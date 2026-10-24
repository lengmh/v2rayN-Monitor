namespace V2rayNMonitor.Core;

public sealed record CoreActionRequest(string Action, string ComponentId, string? OperationId = null, string? PlanId = null)
{
    public bool IsValid() => ComponentId is "sing-box-statistics" or "xray-probes" &&
        (Action is "install" or "verify-runtime" && OperationId is null && PlanId is null ||
         Action == "restore" && ValidId(OperationId) && PlanId is null ||
         ComponentId == "sing-box-statistics" &&
         (Action == "prepare-sidecar" && OperationId is null && PlanId is null ||
          Action == "apply-sidecar" && OperationId is null && ValidId(PlanId) ||
          Action == "restore-sidecar" && ValidId(OperationId) && PlanId is null));
    private static bool ValidId(string? value) => Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty;
}

public sealed record CoreActionResult(string State, string? ComponentId = null,
    string? OperationId = null, string? BackupDirectory = null, string? PlanId = null,
    string? Details = null, CoreRuntimeVerification? Runtime = null);

public sealed record CoreRuntimeVerification(string State, string? CoreName, bool FileIdentityVerified,
    bool StatisticsAvailable, int MemberCount, string ProbeState, DateTimeOffset CheckedAt);
