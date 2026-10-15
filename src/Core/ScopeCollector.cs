using System.Text.Json;

namespace V2rayNMonitor.Core;

/// <summary>Projects a verified complete configuration directory, not the group's current selection.</summary>
public static class ScopeCollector
{
    public static ScopeBatch Project(ScopeCapture capture, V2RayMemberStatistics statistics, DateTimeOffset at,
        bool invalidateContinuity = false)
    {
        var snapshot = capture.Snapshot;
        var catalog = capture.Catalog ?? throw new ArgumentException("A verified sing-box member catalog is required.");
        if (snapshot.CoreName != "sing-box" || catalog.InstanceId != snapshot.InstanceId ||
            catalog.Generation != snapshot.Generation || catalog.ProcessIdentity != snapshot.ProcessIdentity ||
            snapshot.Generation.Length < 65 || snapshot.Generation[64] != ':')
            throw new ArgumentException("Member catalog and capture identities conflict.");
        var enabled = capture.EnabledMembers.ToHashSet(StringComparer.Ordinal);
        if (enabled.Count != capture.EnabledMembers.Count || enabled.Any(x => !catalog.Candidates.ContainsKey(x)))
            throw new ArgumentException("Enabled members must be distinct supported independent outbounds.");
        var digest = snapshot.Generation[..64];
        var partial = catalog.UnavailableOutbounds.Values.Any(x => x is not ("group_not_member" or "excluded_outbound"));
        var readings = new List<ScopeReading>();
        foreach (var member in catalog.Candidates.Values.OrderBy(x => x.OutboundTag, StringComparer.Ordinal))
        {
            var scope = member.IndexId is { } id
                ? new MeasurementScope("actual_member", "index_id", id, snapshot.NodeName, id, member.OutboundTag)
                : new MeasurementScope("actual_member", "outbound", JsonSerializer.Serialize(new[] { digest, member.OutboundTag }),
                    member.DisplayName ?? member.OutboundTag, OutboundTag: member.OutboundTag);
            var available = enabled.Contains(member.OutboundTag) && statistics.Members.TryGetValue(member.OutboundTag, out _);
            var reason = !enabled.Contains(member.OutboundTag) ? "member_statistics_disabled"
                : statistics.UnavailableMembers.GetValueOrDefault(member.OutboundTag, "missing_counter");
            var counter = available ? statistics.Members[member.OutboundTag] : null;
            readings.Add(new(scope, counter?.Upload, counter?.Download, available ? null : reason,
                invalidateContinuity, partial));
        }
        foreach (var basis in new[] { "business", "excluded", "unknown" })
        {
            var scope = new MeasurementScope(basis, "aggregate", basis, basis);
            var counter = statistics.Totals.GetValueOrDefault(basis);
            readings.Add(new(scope, counter?.Upload, counter?.Download,
                counter is null ? statistics.UnavailableTotals.GetValueOrDefault(basis, "missing_counter") : null,
                invalidateContinuity));
        }
        return new(snapshot.InstanceId, snapshot.ProcessIdentity, snapshot.Generation, at, readings, []);
    }

    /// <summary>Projects one complete business/probe batch for an explicitly initialized v3 ledger.</summary>
    public static ScopeBatch ProjectWithProbes(ScopeCapture capture, V2RayMemberStatistics business,
        V2RayProbeStatistics probes, DateTimeOffset at, bool invalidateContinuity = false)
    {
        ArgumentNullException.ThrowIfNull(probes);
        var batch = Project(capture, business, at, invalidateContinuity);
        var enabled = capture.EnabledMembers.ToHashSet(StringComparer.Ordinal);
        var readings = batch.Readings.ToList();
        foreach (var member in batch.Readings.Where(x => x.Scope.Basis == "actual_member"))
        {
            var tag = member.Scope.OutboundTag!;
            var scope = member.Scope with { Basis = "probe_urltest_member" };
            var counter = enabled.Contains(tag) ? probes.Members.GetValueOrDefault(tag) : null;
            readings.Add(new(scope, counter?.Upload, counter?.Download, counter is not null ? null :
                !enabled.Contains(tag) ? "probe_statistics_disabled" : probes.UnavailableMembers.GetValueOrDefault(tag, "missing_counter"),
                invalidateContinuity, member.PartialCoverage));
        }
        foreach (var total in new[] { "business", "excluded", "unknown" })
        {
            var basis = "probe_urltest_" + total;
            var counter = probes.Totals.GetValueOrDefault(total);
            readings.Add(new(new(basis, "aggregate", basis, basis), counter?.Upload, counter?.Download,
                counter is not null ? null : probes.UnavailableTotals.GetValueOrDefault(total, "missing_counter"),
                invalidateContinuity));
        }
        return batch with { Readings = readings };
    }

    public static ScopeBatch ProjectLegacy(XraySnapshot snapshot, XrayCounters counter, DateTimeOffset at, bool invalidateContinuity)
    {
        var key = Uri.EscapeDataString(snapshot.InstanceId) + "/" + Uri.EscapeDataString(snapshot.NodeId);
        return new(snapshot.InstanceId, snapshot.ProcessIdentity, snapshot.Generation, at,
            [new(new("legacy_proxy", "legacy_node", key, snapshot.NodeName, snapshot.NodeId),
                counter.Upload, counter.Download, InvalidateContinuity: invalidateContinuity)], []);
    }

    public static ScopeCommit Commit(ScopeLedger ledger, ScopeBatch completeDirectory)
    {
        var active = completeDirectory.Readings.Select(x => (x.Scope.Basis, x.Scope.IdentityKind, x.Scope.IdentityValue)).ToHashSet();
        var retired = ledger.Bases
            .SelectMany(ledger.GetScopes).Where(x => x.InstanceId == completeDirectory.InstanceId &&
                !active.Contains((x.Scope.Basis, x.Scope.IdentityKind, x.Scope.IdentityValue))).Select(x => x.Id).Order().ToArray();
        return ledger.Accept(completeDirectory with { RetiredScopeIds = retired });
    }
}
