namespace V2rayNMonitor.Core;
public sealed record AppSettings(string V2rayNPath, string TimeZoneId, bool Demo = false);
public static class MonitorStartup
{
    public static string Command(string executable, string dataDirectory, bool scopeMode = false) =>
        $"\"{executable}\" --background{(scopeMode ? " --scope-ledger" : "")} --data-dir \"{dataDirectory}\"";
}
public sealed record CollectorStatus(string State, string Message, string? CurrentNode = null, DateTimeOffset? LastSample = null, string? UploadPerSecond = null, string? DownloadPerSecond = null, bool Demo = false,
    ProxyCollectionScope? Scope = null, string CollectionBasis = "legacy_proxy");
