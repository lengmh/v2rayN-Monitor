namespace V2rayNMonitor.Core;
public sealed record AppSettings(string V2rayNPath, string TimeZoneId, bool Demo = false);
public static class MonitorStartup
{
    public static string Command(string executable, string dataDirectory, bool scopeMode = false, bool probeMode = false) =>
        $"\"{executable}\" --background{(scopeMode || probeMode ? " --scope-ledger" : "")}{(probeMode ? " --probe-ledger" : "")} --data-dir \"{dataDirectory}\"";
}
public sealed record ProbeCollectionStatus(bool HistoryEnabled, string State, string Message);
public sealed record CollectorStatus(string State, string Message, string? CurrentNode = null, DateTimeOffset? LastSample = null, string? UploadPerSecond = null, string? DownloadPerSecond = null, bool Demo = false,
    ProxyCollectionScope? Scope = null, string CollectionBasis = "legacy_proxy", ProbeCollectionStatus? Probes = null)
{
    public IReadOnlyList<string>? HistoryBases { get; init; }
}
