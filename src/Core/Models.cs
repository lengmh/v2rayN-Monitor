namespace V2rayNMonitor.Core;

public sealed record CounterSample(string InstanceId, string NodeId, string NodeName, string SessionId, string Generation, DateTimeOffset At, long Upload, long Download);
public sealed record NodeInfo(string Key, string Name);
public sealed record TrafficRow(string Start, string End, string NodeKey, string NodeName, string Upload, string Download, string Quality);
public sealed record SeriesPoint(string Label, string Upload, string Download);
public sealed record NodeTotal(string Key, string Name, string Upload, string Download);
public sealed record HistoryResult(string Start, string End, string Upload, string Download, string UnallocatedUpload, string UnallocatedDownload, int GapCount, IReadOnlyList<SeriesPoint> Series, IReadOnlyList<NodeTotal> Nodes, IReadOnlyList<TrafficRow> Records, int RecordCount);
public sealed record QuerySpec(DateTimeOffset Start, DateTimeOffset End, string TimeZoneId, string Bucket = "day", string? NodeKey = null, int Page = 0, int PageSize = 50);
