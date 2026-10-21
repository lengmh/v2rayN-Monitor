using System.Globalization;
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
            scope = scope with { CapturedName = member.CapturedName };
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
        return AppendProbes(batch, probes, enabled, invalidateContinuity);
    }

    public static ScopeBatch ProjectXrayWithProbes(ScopeCapture capture, V2RayOutboundStatistics outbounds,
        V2RayProbeStatistics probes, DateTimeOffset at, bool invalidateContinuity = false) =>
        AppendProbes(ProjectXray(capture, outbounds, at, invalidateContinuity), probes, null, invalidateContinuity);

    private static ScopeBatch AppendProbes(ScopeBatch batch, V2RayProbeStatistics probes, HashSet<string>? enabled,
        bool invalidateContinuity)
    {
        ArgumentNullException.ThrowIfNull(probes);
        var readings = batch.Readings.ToList();
        foreach (var member in batch.Readings.Where(x => x.Scope.Basis is "actual_member" or "legacy_proxy"))
        {
            var tag = member.Scope.OutboundTag ?? "proxy";
            var scope = member.Scope.Basis == "legacy_proxy"
                ? new MeasurementScope("probe_urltest_member", "index_id", member.Scope.NodeId!, member.Scope.Name,
                    member.Scope.NodeId, tag) { CapturedName = member.Scope.CapturedName }
                : member.Scope with { Basis = "probe_urltest_member" };
            var active = enabled is null || enabled.Contains(tag);
            var counter = active ? probes.Members.GetValueOrDefault(tag) : null;
            readings.Add(new(scope, counter?.Upload, counter?.Download, counter is not null ? null :
                !active ? "probe_statistics_disabled" : probes.UnavailableMembers.GetValueOrDefault(tag, "missing_counter"),
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
                counter.Upload, counter.Download, InvalidateContinuity: invalidateContinuity,
                PartialCoverage: snapshot.Scope?.IsPartial == true)], []);
    }

    public static ProxyCollectionScope XrayCoverage(XrayOutboundCatalog catalog) =>
        new(catalog.UnavailableOutbounds.Count, 0, "xray_outbound_members")
        { CollectedOutbounds = catalog.Candidates.Count, CollectedGroups = catalog.GroupCount };

    /// <summary>Preserves the legacy proxy cursor and adds independent actual outbound handlers exactly once.</summary>
    public static ScopeBatch ProjectXray(ScopeCapture capture, V2RayOutboundStatistics statistics, DateTimeOffset at,
        bool invalidateContinuity = false)
    {
        var snapshot = capture.Snapshot;
        var catalog = capture.XrayOutbounds ?? throw new ArgumentException("A verified Xray outbound catalog is required.");
        if (snapshot.CoreName != "Xray" || catalog.InstanceId != snapshot.InstanceId || catalog.Generation != snapshot.Generation ||
            catalog.ProcessIdentity != snapshot.ProcessIdentity || snapshot.Generation.Length < 65 || snapshot.Generation[64] != ':' ||
            catalog.Candidates.Count == 0 || (!snapshot.SelectedPolicyGroup && !catalog.Candidates.ContainsKey("proxy")))
            throw new ArgumentException("Outbound catalog and capture identities conflict.");
        var partial = XrayCoverage(catalog).IsPartial;
        var key = Uri.EscapeDataString(snapshot.InstanceId) + "/" + Uri.EscapeDataString(snapshot.NodeId);
        var readings = new List<ScopeReading>();
        foreach (var member in catalog.Candidates.Values.OrderBy(x => x.OutboundTag, StringComparer.Ordinal))
        {
            var tag = member.OutboundTag;
            var scope = tag == "proxy" && !snapshot.SelectedPolicyGroup
                ? new MeasurementScope("legacy_proxy", "legacy_node", key, snapshot.NodeName, snapshot.NodeId)
                : new MeasurementScope("actual_member", "outbound", JsonSerializer.Serialize(new[] { snapshot.Generation[..64], tag }),
                    member.CapturedName is { } name ? $"【{name.GroupLabel}】 {name.OriginalName}" : tag, OutboundTag: tag);
            scope = scope with { CapturedName = member.CapturedName };
            var counter = statistics.Counters.GetValueOrDefault(tag);
            readings.Add(new(scope, counter?.Upload, counter?.Download,
                counter is null ? statistics.UnavailableOutbounds.GetValueOrDefault(tag, "missing_counter") : null,
                invalidateContinuity, partial));
        }
        return new(snapshot.InstanceId, snapshot.ProcessIdentity, snapshot.Generation, at, readings, []);
    }

    public static ScopeCommit Commit(ScopeLedger ledger, ScopeBatch completeDirectory)
    {
        var active = completeDirectory.Readings.Select(x => (x.Scope.Basis, x.Scope.IdentityKind, x.Scope.IdentityValue)).ToHashSet();
        var retired = ledger.Bases
            .SelectMany(ledger.GetScopes).Where(x => x.InstanceId == completeDirectory.InstanceId &&
                !active.Contains((x.Scope.Basis, x.Scope.IdentityKind, x.Scope.IdentityValue))).Select(x => x.Id).Order().ToArray();
        return ledger.Accept(completeDirectory with { RetiredScopeIds = retired });
    }

    /// <summary>Display-only rate between two committed batches, never a source for accounting.</summary>
    public static (string? Upload, string? Download) Rates(ScopeBatch? previous, ScopeBatch current)
    {
        if (previous is null || previous.InstanceId != current.InstanceId || previous.SessionId != current.SessionId ||
            previous.Generation != current.Generation) return (null, null);
        var ticks = (current.At - previous.At).Ticks;
        if (ticks <= 0 || ticks > ScopeLedger.ShortInterval) return (null, null);
        static bool Proxy(ScopeReading reading) => reading.Scope.Basis is "actual_member" or "legacy_proxy";
        static (string, string, string) Identity(ScopeReading reading) =>
            (reading.Scope.Basis, reading.Scope.IdentityKind, reading.Scope.IdentityValue);
        var before = previous.Readings.Where(Proxy).ToDictionary(Identity);
        var members = current.Readings.Where(Proxy).ToArray();
        if (members.Length == 0 || members.Length != before.Count) return (null, null);
        decimal up = 0, down = 0;
        foreach (var reading in members)
        {
            if (!before.TryGetValue(Identity(reading), out var last) || reading.InvalidateContinuity ||
                reading.PartialCoverage || last.PartialCoverage ||
                reading.UnavailableReason is not null || last.UnavailableReason is not null ||
                reading.Upload is not { } upload || reading.Download is not { } download ||
                last.Upload is not { } lastUpload || last.Download is not { } lastDownload ||
                upload < lastUpload || download < lastDownload) return (null, null);
            up += upload - lastUpload;
            down += download - lastDownload;
        }
        var seconds = (decimal)ticks / TimeSpan.TicksPerSecond;
        return (decimal.Floor(up / seconds).ToString(CultureInfo.InvariantCulture),
            decimal.Floor(down / seconds).ToString(CultureInfo.InvariantCulture));
    }
}
