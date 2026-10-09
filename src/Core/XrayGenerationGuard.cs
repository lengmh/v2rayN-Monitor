namespace V2rayNMonitor.Core;

/// <summary>Fails closed on races or a changed node/configuration in an existing process.</summary>
public sealed class XrayGenerationGuard
{
    private string? processIdentity;
    private string? binding;
    private bool poisoned;

    public bool Observe(XraySnapshot snapshot, out string reason)
    {
        reason = "mapping_changed";
        if (snapshot.ConfigurationWrittenAt > snapshot.ProcessStartedAt)
        {
            processIdentity = snapshot.ProcessIdentity;
            binding = null;
            poisoned = true;
            reason = "waiting_for_core";
            return false;
        }
        var nextBinding = string.Join('|', snapshot.InstanceId, snapshot.NodeId, snapshot.Generation, snapshot.ListenerIdentity);
        if (snapshot.ProcessIdentity != processIdentity)
        {
            processIdentity = snapshot.ProcessIdentity;
            binding = nextBinding;
            poisoned = false;
        }
        else if (binding != nextBinding) poisoned = true;
        if (poisoned) return false;
        reason = "ready";
        return true;
    }

    public bool Accept(XraySnapshot before, XraySnapshot after, out string reason)
    {
        var beforeValid = Observe(before, out var beforeReason);
        var afterValid = Observe(after, out var afterReason);
        if (before != after)
        {
            reason = "snapshot_changed";
            return false;
        }
        reason = beforeValid ? afterReason : beforeReason;
        return beforeValid && afterValid;
    }
}
