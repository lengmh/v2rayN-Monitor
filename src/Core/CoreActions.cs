namespace V2rayNMonitor.Core;

public sealed record CoreActionRequest(string Action, string ComponentId, string? OperationId = null)
{
    public bool IsValid() => ComponentId is "sing-box-statistics" or "xray-probes" &&
        (Action == "install" && OperationId is null ||
         Action == "restore" && Guid.TryParseExact(OperationId, "N", out var id) && id != Guid.Empty);
}

public sealed record CoreActionResult(string State, string? ComponentId = null,
    string? OperationId = null, string? BackupDirectory = null);
