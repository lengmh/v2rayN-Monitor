using System.Text;
using System.Text.Json;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Host;

internal enum SidecarPreparationState { Prepared, ConfirmationRequired, AlreadyMatched, InvalidEndpoint, InvalidConfiguration, UnsupportedExistingSidecar }
internal sealed record SidecarPlan(string InstanceId, string NodeId, string ProcessIdentity, string Generation,
    string ConfigurationHash, string? PreviousHash, string Json);
internal sealed record SidecarPreparation(SidecarPreparationState State, SidecarPlan? Plan = null);
internal enum LedgerUpgradeStep { MultiScope, Probes }
internal sealed record LedgerPreparation(string StartupFlag, IReadOnlyList<LedgerUpgradeStep> Steps);

/// <summary>In-memory proposals only. No ports, files, processes or ledgers are opened or changed.</summary>
internal static class CoreCapabilityPreparation
{
    // The caller supplies a snapshot independently verified by discovery, never page-supplied identity.
    internal static SidecarPreparation PrepareSidecar(XraySnapshot snapshot, ReadOnlyMemory<byte> configuration,
        ReadOnlyMemory<byte>? previous, string listen)
    {
        var endpointJson = JsonSerializer.SerializeToUtf8Bytes(new { listen });
        if (!SingboxParser.TryReadStatisticsEndpoint(endpointJson, out _))
            return new(SidecarPreparationState.InvalidEndpoint);
        if (string.IsNullOrWhiteSpace(snapshot.NodeId) || string.IsNullOrWhiteSpace(snapshot.ProcessIdentity)
            || !SingboxParser.TryReadConfiguration(configuration, endpointJson, out _, out _, allowMemberStatistics: true)
            || !SingboxParser.TryReadMemberCatalog(snapshot, configuration, out var catalog, out _)
            || !catalog!.Candidates.TryGetValue("proxy", out var anchor) || anchor.IndexId != snapshot.NodeId)
            return new(SidecarPreparationState.InvalidConfiguration);

        // The approved reader rejects unknown fields. Refuse rather than strip them or broaden its contract.
        if (previous is { } old && !SingboxParser.TryReadMemberStatisticsSettings(old, out _, out _))
            return new(SidecarPreparationState.UnsupportedExistingSidecar);
        var members = catalog.Candidates.Keys.Order(StringComparer.Ordinal).ToArray();
        var json = JsonSerializer.Serialize(new { listen, monitor_members = members });
        if (!SingboxParser.TryReadMemberStatisticsSettings(Encoding.UTF8.GetBytes(json), out _, out _))
            return new(SidecarPreparationState.InvalidConfiguration); // Includes the existing 4096-byte limit.
        var plan = new SidecarPlan(snapshot.InstanceId, snapshot.NodeId, snapshot.ProcessIdentity, snapshot.Generation,
            XrayParser.Hash(configuration.Span), previous is { } bytes ? XrayParser.Hash(bytes.Span) : null, json);
        if (previous is { } existing)
        {
            SingboxParser.TryReadMemberStatisticsSettings(existing, out var oldEndpoint, out var oldMembers);
            SingboxParser.TryReadMemberStatisticsSettings(Encoding.UTF8.GetBytes(json), out var endpoint, out _);
            if (oldEndpoint == endpoint && oldMembers.Order(StringComparer.Ordinal).SequenceEqual(members))
                return new(SidecarPreparationState.AlreadyMatched, plan);
            return new(SidecarPreparationState.ConfirmationRequired, plan);
        }
        return new(SidecarPreparationState.Prepared, plan);
    }

    // This is a freshness check, not authorization to write. A future transaction must also check
    // the target/core identity, stopped writer, path safety, port availability, backup and mutex.
    internal static bool IsCurrent(SidecarPlan plan, XraySnapshot snapshot, ReadOnlyMemory<byte> configuration,
        ReadOnlyMemory<byte>? previous) => plan.InstanceId == snapshot.InstanceId
        && snapshot.CoreName == "sing-box" && plan.NodeId == snapshot.NodeId
        && plan.ProcessIdentity == snapshot.ProcessIdentity && plan.Generation == snapshot.Generation
        && plan.ConfigurationHash == XrayParser.Hash(configuration.Span)
        && plan.PreviousHash == (previous is { } bytes ? XrayParser.Hash(bytes.Span) : null);

    // Version must come from an independently validated ledger, not merely PRAGMA user_version.
    // Each step still requires its own explicit offline authorization and fresh backup.
    internal static LedgerPreparation PrepareLedger(int validatedVersion, bool probes) => validatedVersion switch
    {
        1 => new(probes ? "--probe-ledger" : "--scope-ledger", Array.AsReadOnly(probes
            ? new[] { LedgerUpgradeStep.MultiScope, LedgerUpgradeStep.Probes } : new[] { LedgerUpgradeStep.MultiScope })),
        2 => new(probes ? "--probe-ledger" : "--scope-ledger", Array.AsReadOnly(probes
            ? new[] { LedgerUpgradeStep.Probes } : Array.Empty<LedgerUpgradeStep>())),
        3 => new("--probe-ledger", Array.AsReadOnly(Array.Empty<LedgerUpgradeStep>())),
        _ => throw new ArgumentOutOfRangeException(nameof(validatedVersion))
    };
}