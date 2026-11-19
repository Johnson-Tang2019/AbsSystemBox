// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SysBox.Core;

namespace SysBox.UnitTests;

public sealed class AppUpdateFeaturesTests
{
    private const string Installer = "https://github.com/Johnson-Tang2019/AbsSystemBox/releases/download/v0.3.6/AbsSystemBox-0.3.6-win-x64-setup.exe";
    private static readonly byte[] Payload = "installer fixture"u8.ToArray();
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Payload));

    [Fact]
    public async Task ReadsStableOfficialMetadataAndSkipsEqualOrOlderReleases()
    {
        using var client = new HttpClient(new Stub(request =>
        {
            Assert.Equal("https://api.github.com/repos/Johnson-Tang2019/AbsSystemBox/releases/latest", request.RequestUri?.AbsoluteUri);
            Assert.NotEmpty(request.Headers.UserAgent);
            return Release();
        }));
        var updates = new GitHubReleaseUpdates(client);
        var candidate = await updates.CheckAsync(new Version(0, 3, 5), CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.Equal(new Version(0, 3, 6), candidate.Version);
        Assert.Equal(Digest, candidate.Sha256);
        Assert.Equal(Payload.Length, candidate.Size);
        Assert.Null(await updates.CheckAsync(new Version(0, 3, 6), CancellationToken.None));
        Assert.Null(await updates.CheckAsync(new Version(0, 3, 7), CancellationToken.None));
    }

    [Theory]
    [InlineData(true, false, Installer, true)]
    [InlineData(false, true, Installer, true)]
    [InlineData(false, false, "https://example.test/setup.exe", true)]
    [InlineData(false, false, Installer, false)]
    public async Task RejectsUntrustedOrUnsignedMetadata(bool draft, bool prerelease, string url, bool hasDigest)
    {
        using var client = new HttpClient(new Stub(_ => Release(draft, prerelease, url, hasDigest)));
        var updates = new GitHubReleaseUpdates(client);

        await Assert.ThrowsAsync<InvalidDataException>(() => updates.CheckAsync(new Version(0, 3, 5), CancellationToken.None));
    }

    [Fact]
    public async Task VerifiesInstallerBytesAcrossTheOfficialAssetRedirect()
    {
        var asset = new Uri("https://release-assets.githubusercontent.com/fixture");
        using var client = new HttpClient(new Stub(request => request.RequestUri == new Uri(Installer)
            ? Redirect(asset) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) }));
        var updates = new GitHubReleaseUpdates(client);
        using var output = new MemoryStream();

        await updates.DownloadAsync(Update(), output, null, CancellationToken.None);

        Assert.Equal(Payload, output.ToArray());
    }

    [Fact]
    public async Task RetriesTheOfficialCheckAfterATransportInterruption()
    {
        var calls = 0;
        using var client = new HttpClient(new Stub(_ => ++calls == 1
            ? throw new HttpRequestException("Interrupted.", new IOException()) : Release()));
        var updates = new GitHubReleaseUpdates(client);

        Assert.NotNull(await updates.CheckAsync(new Version(0, 3, 5), CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RestartsAnInterruptedDownloadWithoutKeepingPartialBytes()
    {
        var calls = 0;
        using var client = new HttpClient(new Stub(_ => new(HttpStatusCode.OK)
        {
            Content = ++calls == 1 ? new StreamContent(new BrokenInstaller()) : new ByteArrayContent(Payload)
        }));
        var updates = new GitHubReleaseUpdates(client);
        using var output = new MemoryStream();

        await updates.DownloadAsync(Update(), output, null, CancellationToken.None);

        Assert.Equal(2, calls);
        Assert.Equal(Payload, output.ToArray());
    }

    [Fact]
    public async Task RefusesRedirectsAwayFromGitHubAssets()
    {
        using var client = new HttpClient(new Stub(_ => Redirect(new Uri("https://example.test/payload"))));
        var updates = new GitHubReleaseUpdates(client);
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<HttpRequestException>(() => updates.DownloadAsync(Update(), output, null, CancellationToken.None));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusesMismatchedDigestOrDownloadSize(bool wrongSize)
    {
        var tampered = new byte[Payload.Length + (wrongSize ? 1 : 0)];
        using var client = new HttpClient(new Stub(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(tampered) }));
        var updates = new GitHubReleaseUpdates(client);
        using var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() => updates.DownloadAsync(Update(), output, null, CancellationToken.None));
    }

    private static ReleaseUpdate Update() => new(new Version(0, 3, 6), new Uri(Installer), Payload.Length, Digest);

    private static HttpResponseMessage Release(bool draft = false, bool prerelease = false, string url = Installer, bool hasDigest = true) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(new
            {
                draft, prerelease, tag_name = "v0.3.6",
                assets = new[] { new
                {
                    name = "AbsSystemBox-0.3.6-win-x64-setup.exe", state = "uploaded", size = Payload.Length,
                    browser_download_url = url, digest = hasDigest ? "sha256:" + Digest : null
                } }
            }), Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Redirect(Uri destination) => new(HttpStatusCode.Redirect) { Headers = { Location = destination } };

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class BrokenInstaller() : MemoryStream(new byte[Payload.Length])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Position == 0 ? base.ReadAsync(buffer[..3], cancellationToken) :
                ValueTask.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "Interrupted."));
    }
}
