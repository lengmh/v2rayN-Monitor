using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace V2rayNMonitor.Host;

internal sealed record CoreAssetFile(string Name, long Bytes, string Sha256);
internal sealed record CoreComponentAsset(Uri Url, long Bytes, string Sha256, IReadOnlyList<CoreAssetFile> Files);
internal enum CorePreparationState
{
    ExistingFileMatched, DownloadVerified, UnknownComponent, UnsupportedTarget, InvalidDescriptor,
    AssetUnavailable, Cancelled, TimedOut, DownloadFailed, InvalidArchive, StorageFailed
}

// A verified file is only a preparation result. Installation must recheck it and the target.
// A failure can retain a staging directory if cleanup was denied; it is never a usable result.
internal sealed record CorePreparation(CorePreparationState State, string? FilePath = null,
    string? StagingDirectory = null, CoreFileState? FileState = null)
{
    private Func<string?>? cleanup;
    private string? remainder;

    internal CorePreparation OwnStaging(Func<string?> release)
    {
        cleanup = release;
        return this;
    }

    internal string? ReleaseStaging()
    {
        if (Interlocked.Exchange(ref cleanup, null) is { } release) remainder = release();
        return remainder ?? (State == CorePreparationState.DownloadVerified ? null : StagingDirectory);
    }
}

internal static class CoreComponentDownloads
{
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const string ReleasePrefix = "/lengmh/v2rayN-Monitor/releases/download/";

    // Reviewed independent assets only. Never resolve "latest", full bundles or a remote manifest.
    private const string ComponentRelease = "https://github.com/lengmh/v2rayN-Monitor/releases/download/v0.2.0-preview.2-components.1/";
    internal static CoreComponentAsset? PublishedAsset(string componentId) => componentId switch
    {
        "sing-box-statistics" => new(new(ComponentRelease +
            "v2rayn-monitor-component-sing-box-statistics-1.14.1-monitor-test.5-windows-x64.zip"),
            30342198, "e3d9ea251c794b404499c08bc6407e2da77bbe6bac28b02880a10eb2bc991066",
            Array.AsReadOnly(new CoreAssetFile[]
            {
                new("COPYING", 35149, "3972dc9744f6499f0f9b2dbf76696f2ae7ad8af9b23dde66d6af86c9dfb36986"),
                new("LICENSE", 791, "650d5e3b99a446fb38e820fa87a49562e0c79eab868fff58618ac487a58e554c"),
                new("NOTICE.txt", 791, "650d5e3b99a446fb38e820fa87a49562e0c79eab868fff58618ac487a58e554c"),
                new("README.md", 10806, "b158fb5169ede7e7a4099168a334003a04da69de80bf37ecb557342f541a0f96"),
                new("build-ci.ps1", 5311, "99b4ff16ebe32374fa8aec928888522b7007b6dba44dc48ee370ac1a8ddfdd59"),
                new("build-provenance.json", 1813, "dfb75227d6ac737a0b193bb5d062f8d9844586368f840dc7a046efde5473f031"),
                new("component.json", 519, "aaed2bc4c3f84501fc0aad973021612928088997ea85c5634e58ec343d7fc884"),
                new("member-sidecar.patch", 3699, "83a9d155cedda96195090eff7eced8767215baf1f184cbdb4da692d48cc9a205"),
                new("member-stats.patch", 18942, "d63aaf45f78214481195c3599278e540b65d29ec0562a2cb10138603591b62a5"),
                new("monitor.patch", 6009, "741eb4b643e5e501d072bc22525766352c2ec1b553b9caf2f9edfbde2bdf4877"),
                new("probe-stats.patch", 9593, "3f6c83e829dcbc3ec0f2f3ccf7c5aaa7511dac94ba00a2d1894a2540ae70bb5e"),
                new("sing-box-client.exe", 81990144, "595b4b3fede70fc23ca645dcf7b5a4ab6f91e5e7938db0ca77d50eb0f8376695"),
                new("sing-box-fixed-source.tar.gz", 1982381, "8420c7723828a8d9d062c3fafee28c7b7e0d20a4c904fa7ff283f6e884edd537")
            })),
        "xray-probes" => new(new(ComponentRelease +
            "v2rayn-monitor-component-xray-probes-26.7.28-monitor-test.1-windows-x64.zip"),
            13218492, "5b71cc923bc64de44bf6f82a92ab5cad944bad8ef0dc68a3f8316fec4514400c",
            Array.AsReadOnly(new CoreAssetFile[]
            {
                new("LICENSE", 16725, "1f256ecad192880510e84ad60474eab7589218784b9a50bc7ceee34c2b91f1d5"),
                new("README.md", 2891, "f65264b909d8e30577f912fcf4829fdbdd4c45b501670c0764f809491a5b55d7"),
                new("build-ci.ps1", 5297, "569c832b93a235d3287172d9e9b10ec2d2077f86535c156b4b45bd5ff355ac60"),
                new("component.json", 492, "8807b966b7931017b0985b409331a493c36f7d33b47381f3cb3b9949436c414b"),
                new("probe-stats.go", 4491, "a16a9d0d8cfba37c551522f76d384c0adf142a498595dd4175e77ab9f0639d87"),
                new("probe-stats.patch", 927, "cfd5003e20bc2d05ee58cb8c39e85bad6168d20a139891ffef13472e6a40bcf7"),
                new("probe-stats_test.go", 3986, "512eca4d4b71e6ed8341ffa747046b106c3dd0168e2e07c078a8eb420ea7ffee"),
                new("provenance.json", 1153, "2788e7dee2e411e2addd34843c7651b5fbe577dc40ce4a31fc1c5a89a33dcb7a"),
                new("xray-fixed-source.tar.gz", 932611, "45de3ead5186fea442b04c662c554a1ffd1d7bd9093a83410f2edabe74fb766e"),
                new("xray.exe", 32780800, "fafb97663a3d7f2831b86409d8bc949394103c987e0d36c889bc6a6e4f20fa8e")
            })),
        _ => null
    };

    internal static Task<CorePreparation> PrepareAsync(string componentId, string? existingFile,
        string clientVersion, string targetRuntime, CancellationToken cancellationToken, IWebProxy? proxy = null)
    {
        var component = CoreComponents.Catalog.SingleOrDefault(x => x.Id == componentId);
        if (component is null) return Task.FromResult(new CorePreparation(CorePreparationState.UnknownComponent));
        return PrepareAsync(component, PublishedAsset(componentId), existingFile, clientVersion, targetRuntime,
            new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
                AutomaticDecompression = DecompressionMethods.None, Proxy = proxy
            }, cancellationToken);
    }

    // Only fixed application records reach this boundary. Injection permits synthetic HTTP tests;
    // no HTTP endpoint, page-supplied URL, destination path or remote catalog is exposed.
    internal static async Task<CorePreparation> PrepareAsync(CoreComponent component, CoreComponentAsset? asset,
        string? existingFile, string clientVersion, string targetRuntime, HttpMessageHandler handler,
        CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
        var token = deadline.Token;
        string? stage = null;
        var created = new List<string>();
        CorePreparation result;
        try
        {
            token.ThrowIfCancellationRequested();
            if (CoreComponents.ValidateTarget(component, clientVersion, targetRuntime) is { } invalid)
                return new(invalid == CoreFileState.InvalidDescriptor ? CorePreparationState.InvalidDescriptor :
                    CorePreparationState.UnsupportedTarget, FileState: invalid);
            if (existingFile is not null)
            {
                var check = await CoreComponents.VerifyFileAsync(component, existingFile, clientVersion, targetRuntime, token);
                token.ThrowIfCancellationRequested();
                if (check.State == CoreFileState.IdentityMatched)
                    return new(CorePreparationState.ExistingFileMatched, existingFile, FileState: check.State);
            }
            if (asset is null) return new(CorePreparationState.AssetUnavailable);
            if (!ValidAsset(component, asset)) return new(CorePreparationState.InvalidDescriptor);

            // Never accept a caller-selected write target. Every attempt owns a new temporary directory.
            stage = Directory.CreateTempSubdirectory("v2rayn-monitor-component-").FullName;
            EnsurePlainDirectory(stage);
            var archivePath = Path.Combine(stage, "download.partial");
            using (var archiveFile = NewFile(archivePath, created))
            {
                using var response = await GetResponseAsync(client, asset.Url, token);
                if (response.Content.Headers.ContentLength is { } length && length != asset.Bytes)
                    throw new InvalidDataException("Archive size mismatch.");
                if (response.Content.Headers.ContentEncoding.Count != 0)
                    throw new InvalidDataException("Encoded archive is not supported.");
                using var input = await response.Content.ReadAsStreamAsync(token);
                await CopyVerifiedAsync(input, archiveFile, asset.Bytes, asset.Sha256, token);
            }
            using (var archive = ZipFile.OpenRead(archivePath))
            {
                // Flat packages keep paths, links and nested extraction out of the write boundary.
                var expected = asset.Files.ToDictionary(x => x.Name, StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (archive.Entries.Count != expected.Count) throw new InvalidDataException("Unexpected file count.");
                foreach (var entry in archive.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                    if (!SafeName(entry.FullName) || !seen.Add(entry.FullName) ||
                        !expected.TryGetValue(entry.FullName, out var file) || entry.Length != file.Bytes ||
                        unixType is not (0 or 0x8000) ||
                        (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                        throw new InvalidDataException("Unexpected archive entry.");
                    EnsurePlainDirectory(stage);
                    using var output = NewFile(Path.Combine(stage, file.Name), created);
                    using var input = entry.Open();
                    await CopyVerifiedAsync(input, output, file.Bytes, file.Sha256, token);
                }
            }
            token.ThrowIfCancellationRequested();
            EnsurePlainDirectory(stage);
            File.Delete(archivePath);
            var ownedStage = stage;
            return new CorePreparation(CorePreparationState.DownloadVerified, Path.Combine(stage, component.FileName), stage)
                .OwnStaging(() => CleanAttempt(ownedStage, created) ? null : ownedStage);
        }
        catch (OperationCanceledException)
        {
            result = new(cancellationToken.IsCancellationRequested ? CorePreparationState.Cancelled : CorePreparationState.TimedOut);
        }
        catch (HttpRequestException) { result = new(CorePreparationState.DownloadFailed); }
        catch (InvalidDataException) { result = new(CorePreparationState.InvalidArchive); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { result = new(CorePreparationState.StorageFailed); }

        if (stage is not null && !CleanAttempt(stage, created)) result = result with { StagingDirectory = stage };
        return result;
    }

    private static bool ValidAsset(CoreComponent component, CoreComponentAsset asset)
    {
        if (!ReleaseUrl(asset.Url) || asset.Bytes is <= 0 or > MaxArchiveBytes || !Digest(asset.Sha256) ||
            !SafeName(component.FileName) || asset.Files.Count is < 1 or > 32) return false;
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "download.partial" };
        foreach (var file in asset.Files)
        {
            if (!SafeName(file.Name) || !names.Add(file.Name) || file.Bytes is <= 0 or > MaxArchiveBytes ||
                !Digest(file.Sha256)) return false;
            total += file.Bytes;
        }
        var binary = asset.Files.SingleOrDefault(x => x.Name == component.FileName);
        return total <= MaxExpandedBytes && binary is not null &&
            binary.Sha256.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase) &&
            (component.ExpectedBytes is null || component.ExpectedBytes == binary.Bytes);
    }

    private static bool Digest(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static bool SafeName(string name)
    {
        if (name.Length is < 1 or > 120 || name is "." or ".." || name.EndsWith('.') ||
            !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')) return false;
        var stem = name.Split('.')[0].ToUpperInvariant();
        return stem is not ("CON" or "PRN" or "AUX" or "NUL" or "CLOCK$") &&
            !(stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9');
    }

    private static bool Https(Uri url) => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps &&
        url.IsDefaultPort && url.UserInfo.Length == 0 && url.Fragment.Length == 0;

    private static bool ReleaseUrl(Uri url)
    {
        if (!Https(url) || url.Host != "github.com" || url.Query.Length != 0 ||
            !url.AbsolutePath.StartsWith(ReleasePrefix, StringComparison.Ordinal)) return false;
        var parts = url.AbsolutePath[ReleasePrefix.Length..].Split('/');
        return parts.Length == 2 && parts[0].StartsWith('v') && SafeName(parts[0]) &&
            SafeName(parts[1]) && parts[1].EndsWith(".zip", StringComparison.Ordinal);
    }

    private static async Task<HttpResponseMessage> GetResponseAsync(HttpClient client, Uri url, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            // Fresh unauthenticated requests: never propagate headers/cookies from a previous hop.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.OK) return response;
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (redirects >= 3 || location is null) throw new HttpRequestException("Invalid redirect.");
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                // GitHub's release asset CDN, not arbitrary githubusercontent subdomains.
                if (!Https(next) || next.Host != "release-assets.githubusercontent.com")
                    throw new HttpRequestException("Untrusted redirect.");
                url = next;
                continue;
            }
            response.Dispose();
            throw new HttpRequestException("Component download failed.");
        }
    }

    private static async Task CopyVerifiedAsync(Stream input, Stream output, long expectedBytes,
        string expectedDigest, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            if (read > expectedBytes - total) throw new InvalidDataException("File exceeds expected size.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            hash.AppendData(buffer, 0, read);
            total += read;
        }
        if (total != expectedBytes ||
            !Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedDigest, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("File identity mismatch.");
    }

    private static FileStream NewFile(string path, List<string> created)
    {
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        created.Add(path);
        return file;
    }

    private static void EnsurePlainDirectory(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Staging path contains a link.");
    }

    private static bool CleanAttempt(string stage, List<string> created)
    {
        try
        {
            EnsurePlainDirectory(stage);
            foreach (var file in created)
            {
                try
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
                    File.Delete(file);
                }
                catch (FileNotFoundException) { } // The verified archive was already removed.
            }
            Directory.Delete(stage); // Never recurse into files not created by this attempt.
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
