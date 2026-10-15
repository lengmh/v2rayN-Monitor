namespace V2rayNMonitor.Core;

public sealed record CollectorReading(CounterSample? Sample, string State, string Message, string? CurrentNode,
    bool InvalidateContinuity = false, string CoreName = "Xray", ProxyCollectionScope? Scope = null);
public sealed record XrayCounters(long Upload, long Download);
// Route-tag evidence only: no inferred subscription identity, selected member, or summed total.
public sealed record V2RayOutboundStatistics(
    IReadOnlyDictionary<string, XrayCounters> Counters,
    IReadOnlyDictionary<string, string> UnavailableOutbounds)
{
    public string Basis => "routed_outbound";
}
// Member and aggregate dimensions stay separate; no cross-dimension sum or IndexId inference.
public sealed record V2RayMemberStatistics(
    IReadOnlyDictionary<string, XrayCounters> Members,
    IReadOnlyDictionary<string, string> UnavailableMembers,
    IReadOnlyDictionary<string, XrayCounters> Totals,
    IReadOnlyDictionary<string, string> UnavailableTotals)
{
    public string Basis => "actual_member";
}
// URLTest stream bytes are independent of business traffic and provider billing.
public sealed record V2RayProbeStatistics(
    IReadOnlyDictionary<string, XrayCounters> Members,
    IReadOnlyDictionary<string, string> UnavailableMembers,
    IReadOnlyDictionary<string, XrayCounters> Totals,
    IReadOnlyDictionary<string, string> UnavailableTotals)
{
    public string ProbeKind => "urltest";
}
public sealed record V2RayScopeStatistics(V2RayMemberStatistics Members, V2RayProbeStatistics Probes);
public sealed record SingboxMemberIdentity(string OutboundTag, string Protocol, string? IndexId)
{
    public string? DisplayName { get; init; }
}
// Candidates come from an independently verified configuration, not names returned by the RPC.
public sealed record SingboxMemberCatalog(string InstanceId, string Generation, string ProcessIdentity,
    IReadOnlyDictionary<string, SingboxMemberIdentity> Candidates,
    IReadOnlyDictionary<string, string> UnavailableOutbounds);
public sealed record ScopeCapture(XraySnapshot Snapshot, SingboxMemberCatalog? Catalog,
    IReadOnlyList<string> EnabledMembers);
public sealed record ScopeCollectorReading(ScopeBatch? Batch, string State, string Message,
    string? CurrentNode = null, bool InvalidateContinuity = false, ProbeCollectionStatus? Probes = null);
public sealed record XrayGuiSelection(string IndexId, bool EnableStatistics, bool DisplayRealTimeSpeed, bool EnableTun)
{
    public bool StatisticsRequested => EnableStatistics || DisplayRealTimeSpeed;
}
public sealed record XrayEffectiveConfiguration(Uri MetricsEndpoint, string Protocol, string ServerIdentity,
    string CoreName = "Xray", ProxyCollectionScope? Scope = null);

// This is route-tag coverage, not the selected member of a group or a chain hop.
public sealed record ProxyCollectionScope(int UncollectedOutbounds, int UncollectedGroups)
{
    public string Basis => "routed_proxy";
    public bool IsPartial => UncollectedOutbounds > 0;
    public string Description => IsPartial
        ? $"仅采集路由直接匹配 proxy 的字节。其他 {UncollectedOutbounds} 个出站（含 {UncollectedGroups} 个策略组）未采集，其流量未知；当前合计不是全部代理流量。"
        : "采集路由直接匹配 proxy 的原始字节，排除 direct。";
}

// All identity components are obtained independently of the metrics response. No credentials or raw
// configurations are retained here. ProcessIdentity includes PID AND creation time, not PID alone.
public sealed record XraySnapshot(
    string InstanceId, string NodeId, string NodeName, string ProcessIdentity,
    string Generation, DateTimeOffset ProcessStartedAt, DateTimeOffset ConfigurationWrittenAt,
    string ListenerIdentity, Uri MetricsEndpoint, string CoreName = "Xray", ProxyCollectionScope? Scope = null);
