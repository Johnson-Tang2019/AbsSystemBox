// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace SysBox.Core;

public sealed record ReleaseUpdate(Version Version, Uri InstallerUri, long Size, string Sha256);

public sealed class GitHubReleaseUpdates(HttpClient client)
{
    private const string Repository = "Johnson-Tang2019/AbsSystemBox";
    private const int MaxReleaseBytes = 512 * 1024;
    private const long MaxInstallerBytes = 384 * 1024 * 1024;
    private static readonly Uri Latest = new($"https://api.github.com/repos/{Repository}/releases/latest");

    public async Task<ReleaseUpdate?> CheckAsync(Version installed, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Latest);
        request.Headers.UserAgent.ParseAdd("AbsSystemBox-Update/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } responseUri && responseUri != Latest)
            throw new InvalidDataException("Release metadata left the official endpoint.");
        if (response.Content.Headers.ContentLength > MaxReleaseBytes)
            throw new InvalidDataException("Release metadata exceeds the size limit.");

        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyAsync(source, buffer, MaxReleaseBytes, null, null, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        var release = json.RootElement;
        if (release.ValueKind != JsonValueKind.Object ||
            !IsFalse(release, "draft") || !IsFalse(release, "prerelease") ||
            Text(release, "tag_name") is not { } tag || !TryParseTag(tag, out var version))
            throw new InvalidDataException("Unexpected release metadata.");
        if (version <= installed)
            return null;

        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Missing official release assets.");
        var name = InstallerName(version);
        var matches = assets.EnumerateArray().Where(item => Text(item, "name") == name).Take(2).ToArray();
        var expectedUri = InstallerUri(version);
        if (matches.Length != 1 || Text(matches[0], "state") != "uploaded" ||
            Text(matches[0], "browser_download_url") != expectedUri.AbsoluteUri ||
            !matches[0].TryGetProperty("size", out var sizeNode) || sizeNode.ValueKind != JsonValueKind.Number ||
            !sizeNode.TryGetInt64(out var size) || size is <= 0 or > MaxInstallerBytes ||
            Text(matches[0], "digest") is not { Length: 71 } digest || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
            !digest.AsSpan(7).ContainsOnlyHex())
            throw new InvalidDataException("Missing or invalid official installer digest.");
        return new ReleaseUpdate(version, expectedUri, size, digest[7..]);
    }

    public async Task DownloadAsync(ReleaseUpdate update, Stream destination, IProgress<double>? progress, CancellationToken token)
    {
        if (update.InstallerUri != InstallerUri(update.Version) || update.Size is <= 0 or > MaxInstallerBytes ||
            update.Sha256.Length != 64 || !update.Sha256.AsSpan().ContainsOnlyHex())
            throw new InvalidDataException("Invalid installer metadata.");

        using var response = await OpenInstallerAsync(update.InstallerUri, token).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } size && size != update.Size)
            throw new InvalidDataException("Installer size differs from the published size.");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var copied = await CopyAsync(source, destination, update.Size, hash, progress, token).ConfigureAwait(false);
        if (copied != update.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
            throw new InvalidDataException("Installer SHA-256 verification failed.");
    }

    private async Task<HttpResponseMessage> OpenInstallerAsync(Uri uri, CancellationToken token)
    {
        for (var redirects = 0; redirects <= 2; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("AbsSystemBox-Update/1.0");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.OK &&
                (response.RequestMessage?.RequestUri is not { } responseUri || responseUri == uri))
                return response;
            using (response)
            {
                var target = response.Headers.Location;
                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308) ||
                    target is not { IsAbsoluteUri: true, Scheme: "https", IsDefaultPort: true, UserInfo: "" } ||
                    target.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                    throw new HttpRequestException("Unable to obtain the official installer.");
                uri = target;
            }
        }
        throw new HttpRequestException("Too many installer redirects.");
    }

    private static async Task<long> CopyAsync(Stream source, Stream target, long limit, IncrementalHash? hash,
        IProgress<double>? progress, CancellationToken token)
    {
        var bytes = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            double lastPercent = -1;
            int count;
            while ((count = await source.ReadAsync(bytes.AsMemory(), token).ConfigureAwait(false)) != 0)
            {
                total += count;
                if (total > limit)
                    throw new InvalidDataException("Download exceeds the declared size.");
                hash?.AppendData(bytes, 0, count);
                await target.WriteAsync(bytes.AsMemory(0, count), token).ConfigureAwait(false);
                var percent = Math.Floor(100d * total / limit);
                if (progress is not null && percent != lastPercent)
                    progress.Report(lastPercent = percent);
            }
            return total;
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }

    private static string? Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool IsFalse(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;

    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version();
        if (tag.Length is < 6 or > 18 || tag[0] != 'v' || !Version.TryParse(tag.AsSpan(1), out var parsed) ||
            parsed.Build is < 0 or > 65535 || parsed.Major > 65535 || parsed.Minor > 65535 || parsed.Revision != -1 ||
            tag != $"v{parsed}")
            return false;
        version = parsed;
        return true;
    }

    public static string InstallerName(Version version) => $"AbsSystemBox-{version}-win-x64-setup.exe";
    private static Uri InstallerUri(Version version) => new($"https://github.com/{Repository}/releases/download/v{version}/{InstallerName(version)}");
}

internal static class UpdateHashText
{
    internal static bool ContainsOnlyHex(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
            if (!char.IsAsciiHexDigit(character))
                return false;
        return true;
    }
}
