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
    public CapturedNodeName? CapturedName { get; init; }
}
// Candidates come from an independently verified configuration, not names returned by the RPC.
public sealed record SingboxMemberCatalog(string InstanceId, string Generation, string ProcessIdentity,
    IReadOnlyDictionary<string, SingboxMemberIdentity> Candidates,
    IReadOnlyDictionary<string, string> UnavailableOutbounds);
public sealed record ScopeCapture(XraySnapshot Snapshot, SingboxMemberCatalog? Catalog,
    IReadOnlyList<string> EnabledMembers)
{
    public XrayOutboundCatalog? XrayOutbounds { get; init; }
}
public sealed record XrayOutboundIdentity(string OutboundTag, string Protocol, CapturedNodeName? CapturedName);
public sealed record XrayOutboundCatalog(string InstanceId, string Generation, string ProcessIdentity,
    IReadOnlyDictionary<string, XrayOutboundIdentity> Candidates,
    IReadOnlyDictionary<string, string> UnavailableOutbounds, int GroupCount);
public sealed record ScopeCollectorReading(ScopeBatch? Batch, string State, string Message,
    string? CurrentNode = null, bool InvalidateContinuity = false, ProbeCollectionStatus? Probes = null,
    ProxyCollectionScope? Scope = null);
public sealed record XrayGuiSelection(string IndexId, bool EnableStatistics, bool DisplayRealTimeSpeed, bool EnableTun)
{
    public bool StatisticsRequested => EnableStatistics || DisplayRealTimeSpeed;
}
public sealed record XrayEffectiveConfiguration(Uri MetricsEndpoint, string Protocol, string ServerIdentity,
    string CoreName = "Xray", ProxyCollectionScope? Scope = null)
{
    public string? PolicyGroupSingleMemberName { get; init; }
}

// This is route-tag coverage, not the selected member of a group or a chain hop.
public sealed record ProxyCollectionScope(int UncollectedOutbounds, int UncollectedGroups,
    string Basis = "routed_proxy")
{
    public int CollectedOutbounds { get; init; }
    public int CollectedGroups { get; init; }
    public bool HasSeparateProbeCounters { get; init; }
    public bool IsPartial => UncollectedOutbounds > 0 || UncollectedGroups > 0;
    public string Description => Basis == "xray_outbound_members"
        ? $"采集 {CollectedOutbounds} 个 Xray 独立代理出站自身的累计字节，包含 {CollectedGroups} 个策略组选择实际出站后的连接；不叠加策略组，排除直连和阻断。"
            + (IsPartial ? $" 另有 {UncollectedOutbounds} 个代理出站未覆盖，流量保持未知。" : "")
            + (HasSeparateProbeCounters
                ? " 内部 HTTP 探测另列；原出站累计仍可能包含探测，两种口径不能相加或直接相减。"
                : " 内部探测尚未单独区分，不能仅凭出站计数判断流量用途。")
        : Basis == "xray_outbound_proxy"
        ? IsPartial
            ? $"仅采集 Xray proxy 出站自身的累计字节，包含策略组选中该出站的连接；其他 {UncollectedOutbounds} 个代理出站未采集，配置含 {UncollectedGroups} 个策略组。当前合计不是全部代理流量，内部探测未单独区分。"
            : "采集 Xray proxy 出站自身的累计字节，排除其他出站；内部探测未单独区分。"
        : IsPartial
        ? $"仅采集路由直接匹配 proxy 的字节。其他 {UncollectedOutbounds} 个出站（含 {UncollectedGroups} 个策略组）未采集，其流量未知；当前合计不是全部代理流量。"
        : "采集路由直接匹配 proxy 的原始字节，排除 direct。";
}

// All identity components are obtained independently of the metrics response. No credentials or raw
// configurations are retained here. ProcessIdentity includes PID AND creation time, not PID alone.
public sealed record XraySnapshot(
    string InstanceId, string NodeId, string NodeName, string ProcessIdentity,
    string Generation, DateTimeOffset ProcessStartedAt, DateTimeOffset ConfigurationWrittenAt,
    string ListenerIdentity, Uri MetricsEndpoint, string CoreName = "Xray", ProxyCollectionScope? Scope = null)
{
    public bool XrayProbeExtensionVerified { get; init; }
    public bool SelectedPolicyGroup { get; init; }
    public string? PolicyGroupSingleMemberName { get; init; }
}
