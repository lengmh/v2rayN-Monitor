namespace V2rayNMonitor.Core;

/// <summary>Identity comes from verified discovery, never from a display name or an RPC counter name.</summary>
public sealed record MeasurementScope(string Basis, string IdentityKind, string IdentityValue, string Name,
    string? NodeId = null, string? OutboundTag = null);

/// <summary>Supply either a complete counter pair or a diagnostic category, not guessed zeroes.</summary>
public sealed record ScopeReading(MeasurementScope Scope, long? Upload = null, long? Download = null,
    string? UnavailableReason = null, bool InvalidateContinuity = false, bool PartialCoverage = false);

/// <summary>Readings are the complete current directory; all omitted cursors must be explicitly retired.</summary>
public sealed record ScopeBatch(string InstanceId, string SessionId, string Generation, DateTimeOffset At,
    IReadOnlyList<ScopeReading> Readings, IReadOnlyList<long> RetiredScopeIds);

public sealed record ScopeCommit(string Status, IReadOnlyDictionary<long, string> Scopes);
public sealed record ScopeInfo(long Id, string InstanceId, MeasurementScope Scope);
public sealed record ScopeQuery(string Basis, DateTimeOffset Start, DateTimeOffset End, string TimeZoneId,
    string Bucket = "day", long? ScopeId = null, int Page = 0, int PageSize = 50)
{
    public string? ScopeName { get; init; }
}
public sealed record ScopeTrafficRow(string Basis, long ScopeId, string Name, string Start, string End,
    string Upload, string Download, string Quality, bool PartialCoverage);
public sealed record ScopeTotal(long ScopeId, string Name, string Upload, string Download,
    string RecoveredUpload, string RecoveredDownload);
public sealed record ScopeGap(long? ScopeId, string Origin, string Start, string End, string Reason, bool IsOpen);
public sealed record ScopeHistory(string Basis, string Upload, string Download,
    string RecoveredUpload, string RecoveredDownload, string PartialUpload, string PartialDownload,
    string UnbucketedUpload, string UnbucketedDownload, IReadOnlyList<SeriesPoint> Series,
    IReadOnlyList<ScopeTotal> Scopes, IReadOnlyList<ScopeTrafficRow> Records, long RecordCount,
    IReadOnlyList<ScopeGap> Gaps);
