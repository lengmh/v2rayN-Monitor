namespace V2rayNMonitor.Core;

public sealed record CollectorReading(CounterSample? Sample, string State, string Message, string? CurrentNode,
    bool InvalidateContinuity = false, string CoreName = "Xray", ProxyCollectionScope? Scope = null);
public sealed record XrayCounters(long Upload, long Download);
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
