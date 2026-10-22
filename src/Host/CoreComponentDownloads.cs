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
    string? StagingDirectory = null, CoreFileState? FileState = null);

internal static class CoreComponentDownloads
{
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const string ReleasePrefix = "/lengmh/v2rayN-Monitor/releases/download/";

    // No independent component assets have been published. Do not use full/extended bundles,
    // latest, or a downloaded manifest as a substitute for a reviewed, shipped asset identity.
    internal static CoreComponentAsset? PublishedAsset(string componentId) => null;

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
            return new(CorePreparationState.DownloadVerified, Path.Combine(stage, component.FileName), stage);
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
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return false;
                File.Delete(file);
            }
            Directory.Delete(stage); // Never recurse into files not created by this attempt.
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
