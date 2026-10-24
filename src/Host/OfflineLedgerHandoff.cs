namespace V2rayNMonitor.Host;

internal sealed record OfflineLedgerHandoffRequest
{
    internal string Directory { get; }
    internal int ExpectedVersion { get; }
    internal LedgerUpgradeStep Step { get; }

    internal OfflineLedgerHandoffRequest(string directory, int expectedVersion, LedgerUpgradeStep step)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The current Monitor data directory must be absolute.", nameof(directory));
        if (expectedVersion is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        if (!((expectedVersion == 1 && step == LedgerUpgradeStep.MultiScope) ||
              (expectedVersion == 2 && step == LedgerUpgradeStep.Probes)))
            throw new ArgumentException("The request must identify one supported upgrade step.", nameof(step));
        Directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        ExpectedVersion = expectedVersion;
        Step = step;
    }
}

/// <summary>
/// Called synchronously after the collector/server stop and the Monitor directory mutex is disposed.
/// Does not stop processes, update startup settings, restart Monitor or run a second upgrade step.
/// </summary>
internal static class OfflineLedgerHandoff
{
    internal static OfflineLedgerResult Run(OfflineLedgerHandoffRequest request, Func<OfflineLedgerPlan, bool> finalConfirm)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(finalConfirm);
        var prepared = OfflineLedgerUpgrade.Prepare(request.Directory, request.Step);
        if (prepared.State != OfflineLedgerState.Prepared || prepared.Plan is not { } plan)
            return prepared;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(plan.Directory, request.Directory, comparison) ||
            plan.Version != request.ExpectedVersion || plan.Step != request.Step)
            return new(OfflineLedgerState.PlanChanged, ObservedVersion: plan.Version);

        // Final consent applies to this freshly captured plan only. Execute rechecks it after
        // the dialog closes, including any history changes or another writer taking ownership.
        bool authorized;
        try { authorized = finalConfirm(plan); }
        catch (OperationCanceledException) { return new(OfflineLedgerState.AuthorizationRequired); }
        return OfflineLedgerUpgrade.Execute(plan, authorized);
    }
}
