namespace V2rayNMonitor.Core;

/// <summary>Allows active-IndexId attribution boundaries, but never a changed core/config binding.</summary>
public sealed class XrayGenerationGuard
{
    private string? processIdentity;
    private string? binding;
    private (string Instance, string Node)? selection;
    private bool poisoned;

    // Cleared only after the caller has durably committed a baseline or invalidated cursor.
    // In particular, observing B before a failed HTTP read must survive a later return to A.
    public bool RequiresBaseline { get; private set; }
    public void InvalidateContinuity() => RequiresBaseline = true;
    public void AcknowledgePersistence() => RequiresBaseline = false;

    public bool Observe(XraySnapshot snapshot, out string reason)
    {
        reason = "mapping_changed";
        var nextSelection = (snapshot.InstanceId, snapshot.NodeId);
        if (selection.HasValue && selection.Value != nextSelection) InvalidateContinuity();
        selection = nextSelection;
        if (snapshot.ConfigurationWrittenAt > snapshot.ProcessStartedAt)
        {
            InvalidateContinuity();
            processIdentity = snapshot.ProcessIdentity;
            binding = null;
            poisoned = true;
            reason = "waiting_for_core";
            return false;
        }
        // NodeId is the chosen accounting convention, not independent core identity proof.
        var nextBinding = string.Join('|', snapshot.InstanceId, snapshot.Generation, snapshot.ListenerIdentity, snapshot.MetricsEndpoint);
        if (snapshot.ProcessIdentity != processIdentity)
        {
            if (processIdentity is not null) InvalidateContinuity();
            processIdentity = snapshot.ProcessIdentity;
            binding = nextBinding;
            poisoned = false;
        }
        else if (binding != nextBinding) { poisoned = true; InvalidateContinuity(); }
        if (poisoned) return false;
        reason = "ready";
        return true;
    }

    public bool Accept(XraySnapshot before, XraySnapshot after, out string reason)
    {
        var beforeValid = Observe(before, out var beforeReason);
        var afterValid = Observe(after, out var afterReason);
        // Names are display-only; renaming a stable IndexId does not interrupt its history.
        if (before with { NodeName = after.NodeName } != after)
        {
            InvalidateContinuity();
            reason = "snapshot_changed";
            return false;
        }
        reason = beforeValid ? afterReason : beforeReason;
        return beforeValid && afterValid;
    }
}
