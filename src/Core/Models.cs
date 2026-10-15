namespace V2rayNMonitor.Core;

public sealed record CounterSample(string InstanceId, string NodeId, string NodeName, string SessionId, string Generation, DateTimeOffset At, long Upload, long Download,
    bool PartialCoverage = false);
public sealed record NodeInfo(string Key, string Name);
public sealed record TrafficRow(string Start, string End, string NodeKey, string NodeName, string Upload, string Download, string Quality,
    bool PartialCoverage = false);
public sealed record SeriesPoint(string Label, string Upload, string Download, string RecoveredUpload = "0", string RecoveredDownload = "0");
public sealed record NodeTotal(string Key, string Name, string Upload, string Download, string RecoveredUpload = "0", string RecoveredDownload = "0");
public sealed record GapInfo(string Start, string End, string Reason, bool IsOpen, string? Origin = null);
public sealed record HistoryResult(string Start, string End, string Upload, string Download, string UnallocatedUpload, string UnallocatedDownload, int GapCount, IReadOnlyList<SeriesPoint> Series, IReadOnlyList<NodeTotal> Nodes, IReadOnlyList<TrafficRow> Records, int RecordCount)
{
    public string Basis { get; init; } = "legacy_proxy";
    public string RecoveredUpload { get; init; } = "0";
    public string RecoveredDownload { get; init; } = "0";
    public string PartialUpload { get; init; } = "0";
    public string PartialDownload { get; init; } = "0";
    public string UnbucketedUpload { get; init; } = "0";
    public string UnbucketedDownload { get; init; } = "0";
    public IReadOnlyList<GapInfo> Gaps { get; init; } = [];
    public bool PartialCoverage { get; init; }
}
public sealed record QuerySpec(DateTimeOffset Start, DateTimeOffset End, string TimeZoneId, string Bucket = "day", string? NodeKey = null, int Page = 0, int PageSize = 50)
{
    public string? NodeName { get; init; }
    public bool NonZeroRecords { get; init; }
}
public sealed record ProviderRecord(string At, string NodeName, string Rate, string RawBytes, string BilledBytes);
public sealed record ProviderPoint(string Label, string RawBytes, string BilledBytes);
public sealed record ProviderNode(string Name, string RawBytes, string BilledBytes);
public sealed record ProviderHistory(string Source, string RawBytes, string BilledBytes, IReadOnlyList<ProviderPoint> Series,
    IReadOnlyList<ProviderNode> Nodes, IReadOnlyList<ProviderRecord> Records, int RecordCount);
