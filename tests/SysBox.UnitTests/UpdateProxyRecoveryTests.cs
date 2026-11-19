// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using SysBox.Core;

namespace SysBox.UnitTests;

public sealed class UpdateProxyRecoveryTests
{
    private const string Latest = "https://api.github.com/repos/Johnson-Tang2019/AbsSystemBox/releases/latest";
    private const string Installer = "https://github.com/Johnson-Tang2019/AbsSystemBox/releases/download/v0.3.8/AbsSystemBox-0.3.8-win-x64-setup.exe";
    private static readonly WebProxy Loopback = new("http://127.0.0.1:12450/");

    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.ResponseEnded)]
    public async Task LocalProxyTransportFailureRetriesTheOfficialRequestDirectly(HttpRequestError error)
    {
        using var normal = new HttpClient(new Handler(_ => throw Failure(error)));
        var directCalls = 0;
        using var direct = new HttpClient(new Handler(request =>
        {
            directCalls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(Latest, request.RequestUri?.AbsoluteUri);
            Assert.Equal("AbsSystemBox-Update/1.0", request.Headers.UserAgent.ToString());
            Assert.Equal("application/vnd.github+json", request.Headers.Accept.ToString());
            return LatestRelease();
        }));
        var updater = new GitHubReleaseUpdates(normal, direct, Loopback);

        Assert.Null(await updater.CheckAsync(new Version(0, 3, 7), CancellationToken.None));
        Assert.Equal(1, directCalls);
    }

    [Fact]
    public async Task AWorkingProxyRemainsThePreferredRoute()
    {
        using var normal = new HttpClient(new Handler(_ => LatestRelease()));
        using var direct = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var updater = new GitHubReleaseUpdates(normal, direct, Loopback);

        Assert.Null(await updater.CheckAsync(new Version(0, 3, 7), CancellationToken.None));
    }

    [Fact]
    public async Task DoesNotBypassANonLocalProxy()
    {
        using var normal = new HttpClient(new Handler(_ => throw Failure(HttpRequestError.SecureConnectionError)));
        using var direct = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var updater = new GitHubReleaseUpdates(normal, direct, new WebProxy("http://proxy.example.test:8080/"));

        await Assert.ThrowsAsync<HttpRequestException>(() => updater.CheckAsync(new Version(0, 3, 7), CancellationToken.None));
    }

    [Fact]
    public async Task DoesNotRetryDirectlyAfterARejectedCertificate()
    {
        using var normal = new HttpClient(new Handler(_ => throw new HttpRequestException(
            HttpRequestError.SecureConnectionError, "Certificate validation failed.", new AuthenticationException())));
        using var direct = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var updater = new GitHubReleaseUpdates(normal, direct, Loopback);

        await Assert.ThrowsAsync<HttpRequestException>(() => updater.CheckAsync(new Version(0, 3, 7), CancellationToken.None));
    }

    [Fact]
    public async Task DoesNotBypassAProxyAuthenticationResponse()
    {
        using var normal = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)));
        using var direct = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var updater = new GitHubReleaseUpdates(normal, direct, Loopback);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            updater.CheckAsync(new Version(0, 3, 7), CancellationToken.None));
        Assert.Equal(HttpStatusCode.ProxyAuthenticationRequired, error.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectInstallerDownloadStillChecksTheOfficialAssetAndHash(bool tampered)
    {
        var asset = new Uri("https://release-assets.githubusercontent.com/update-fixture");
        var expected = "expected"u8.ToArray();
        var payload = tampered ? "tampered"u8.ToArray() : expected;
        using var normal = new HttpClient(new Handler(_ => throw Failure(HttpRequestError.SecureConnectionError)));
        using var direct = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri?.AbsoluteUri == Installer)
                return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = asset } };
            Assert.Equal(asset, request.RequestUri);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var updater = new GitHubReleaseUpdates(normal, direct, Loopback);
        var update = new ReleaseUpdate(new Version(0, 3, 8), new Uri(Installer), expected.Length,
            Convert.ToHexString(SHA256.HashData(expected)));
        using var target = new MemoryStream();

        if (tampered)
            await Assert.ThrowsAsync<InvalidDataException>(() => updater.DownloadAsync(update, target, null, CancellationToken.None));
        else
        {
            await updater.DownloadAsync(update, target, null, CancellationToken.None);
            Assert.Equal(expected, target.ToArray());
        }
    }

    private static HttpResponseMessage LatestRelease() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"draft":false,"prerelease":false,"tag_name":"v0.3.7"}""")
    };

    private static HttpRequestException Failure(HttpRequestError error) =>
        new(error, "Connection closed before the response.", new IOException());

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
