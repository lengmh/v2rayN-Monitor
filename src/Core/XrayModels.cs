namespace V2rayNMonitor.Core;

public sealed record CollectorReading(CounterSample? Sample, string State, string Message, string? CurrentNode,
    bool InvalidateContinuity = false, string CoreName = "Xray");
public sealed record XrayCounters(long Upload, long Download);
public sealed record XrayGuiSelection(string IndexId, bool EnableStatistics, bool DisplayRealTimeSpeed, bool EnableTun)
{
    public bool StatisticsRequested => EnableStatistics || DisplayRealTimeSpeed;
}
public sealed record XrayEffectiveConfiguration(Uri MetricsEndpoint, string Protocol, string ServerIdentity, string CoreName = "Xray");

// All identity components are obtained independently of the metrics response. No credentials or raw
// configurations are retained here. ProcessIdentity includes PID AND creation time, not PID alone.
public sealed record XraySnapshot(
    string InstanceId, string NodeId, string NodeName, string ProcessIdentity,
    string Generation, DateTimeOffset ProcessStartedAt, DateTimeOffset ConfigurationWrittenAt,
    string ListenerIdentity, Uri MetricsEndpoint, string CoreName = "Xray");
