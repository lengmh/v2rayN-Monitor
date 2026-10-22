namespace V2rayNMonitor.Core;

// File identity, collection observation and ledger support are deliberately separate.
public sealed record CoreComponentFileStatus(string Id, string Name, string Version,
    string FileState, string DownloadState, string License);
public sealed record CoreCapabilityInspection(string TargetState, IReadOnlyList<CoreComponentFileStatus> Components);
public sealed record CoreCapabilitiesResponse(string TargetState, int LedgerVersion, string StartupMode,
    string CollectorState, DateTimeOffset? LastSample, string CollectionBasis, string ProbeState,
    IReadOnlyList<CoreComponentFileStatus> Components);
