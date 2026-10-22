using System.Security.Cryptography;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Host;

internal sealed record CoreComponent(string Id, string Version, string FileName, string Sha256,
    string ClientVersion, string Runtime, string UpstreamRevision, string License, long? ExpectedBytes = null);

internal enum CoreFileState
{
    IdentityMatched, UnknownComponent, InvalidDescriptor, UnsupportedClient, UnsupportedRuntime,
    Missing, Unreadable, SizeMismatch, DigestMismatch
}

// A file snapshot only: never evidence that a core is installed, running or collecting.
internal sealed record CoreFileCheck(CoreFileState State, long? ActualBytes = null);

internal static class CoreComponents
{
    // These binary digests already gate collection in WindowsDiscovery. Versions, source and
    // licenses are cross-checked against the existing preview.2 release manifest and extension docs.
    // That manifest has archive sizes, not EXE sizes: do not substitute one for the other.
    internal static IReadOnlyList<CoreComponent> Catalog { get; } = Array.AsReadOnly(new[]
    {
        new CoreComponent("sing-box-statistics", "1.14.1-monitor-test.5", "sing-box-client.exe",
            WindowsDiscovery.ApprovedProbeSingboxSha256, SingboxParser.ClientVersion, "win-x64",
            "1ac1a339cb1223e9c70eae14c44411c75033c02d", "GPL-3.0-or-later"),
        new CoreComponent("xray-probes", "26.7.28-monitor-test.1", "xray.exe",
            WindowsDiscovery.ApprovedProbeXraySha256, SingboxParser.ClientVersion, "win-x64",
            "5ca6f4b7d4dc20a881d4330e498892697627ec0c", "MPL-2.0")
    });

    internal static CoreFileCheck Verify(string componentId, string filePath, string clientVersion, string targetRuntime)
    {
        var component = Catalog.SingleOrDefault(x => x.Id == componentId);
        return component is null ? new(CoreFileState.UnknownComponent) :
            VerifyFile(component, filePath, clientVersion, targetRuntime);
    }

    // The descriptor comes from the fixed catalog, never from a browser or a downloaded manifest.
    // Kept separate so synthetic files can exercise the same reader without shipping core binaries.
    internal static CoreFileCheck VerifyFile(CoreComponent component, string filePath,
        string clientVersion, string targetRuntime)
    {
        if (string.IsNullOrWhiteSpace(component.Version) || string.IsNullOrWhiteSpace(component.ClientVersion) ||
            string.IsNullOrWhiteSpace(component.Runtime) || component.Sha256 is not { Length: 64 } digest ||
            !digest.All(char.IsAsciiHexDigit) || component.ExpectedBytes is <= 0)
            return new(CoreFileState.InvalidDescriptor);
        if (clientVersion != component.ClientVersion) return new(CoreFileState.UnsupportedClient);
        if (targetRuntime != component.Runtime) return new(CoreFileState.UnsupportedRuntime);
        try
        {
            using var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var length = file.Length;
            // Bound arbitrary local input; exact SHA256 still identifies the complete binary/version.
            if (length is <= 0 or > 256L * 1024 * 1024 ||
                (component.ExpectedBytes is { } expected && length != expected))
                return new(CoreFileState.SizeMismatch, length);
            var actual = Convert.ToHexString(SHA256.HashData(file));
            if (file.Length != length) return new(CoreFileState.Unreadable);
            return new(actual.Equals(digest, StringComparison.OrdinalIgnoreCase)
                ? CoreFileState.IdentityMatched : CoreFileState.DigestMismatch, length);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        { return new(CoreFileState.Missing); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(CoreFileState.Unreadable); }
    }
}
