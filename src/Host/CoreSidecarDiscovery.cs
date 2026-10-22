using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Host;

internal sealed record SidecarDiscovery(XraySnapshot Baseline, SidecarPreparation Preparation);

// Preparation evidence only: the prospective listener is NOT an attested statistics interface.
// The baseline comes from the actual running core; C2 retains it after the user stops that core.
internal static class CoreSidecarDiscovery
{
    internal static async Task<SidecarDiscovery> CaptureAsync(CoreInstallObservation observed, CoreComponent component,
        Func<string, string[]?> arguments, CancellationToken token)
    {
        if (component.Id != "sing-box-statistics") throw new CoreComponentInstaller.Rejected(CoreInstallState.UnsupportedTarget);
        var inspected = await CoreCapabilityInspector.InspectAsync(observed, [component], token);
        if (inspected.TargetState != "identified" || inspected.Components.Single().FileState != "identity_matched")
            throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidCandidate);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(observed.DataRoot));
        var parent = observed.Processes.Single(x => x.Name.Equals("v2rayN.exe", StringComparison.OrdinalIgnoreCase));
        var cores = observed.Processes.Where(x => x.ParentPid == parent.Pid &&
            (x.Name.Equals("sing-box-client.exe", StringComparison.OrdinalIgnoreCase) ||
             x.Name.Equals("sing-box.exe", StringComparison.OrdinalIgnoreCase) ||
             x.Name.Equals("xray.exe", StringComparison.OrdinalIgnoreCase))).ToArray();
        var corePath = Path.Combine(root, "bin", "sing_box", component.FileName);
        if (cores.Length != 1 || !cores[0].Name.Equals(component.FileName, StringComparison.OrdinalIgnoreCase) ||
            !ProcessBinding.TryBindCore(parent, cores[0], corePath, arguments(cores[0].CommandLine), out _))
            throw new CoreComponentInstaller.Rejected(CoreInstallState.UnknownTarget);
        var core = cores[0];
        var guiPath = Path.Combine(root, "guiConfigs", "guiNConfig.json");
        var configPath = Path.Combine(root, "binConfigs", "config.json");
        var database = Path.Combine(root, "guiConfigs", "guiNDB.db");
        var sidecarPath = Path.Combine(root, "bin", "sing_box", "monitor-stats.json");
        using var guiFile = Open(guiPath);
        using var configFile = Open(configPath);
        var gui = Read(guiFile, 1024 * 1024);
        var config = Read(configFile, 4 * 1024 * 1024);
        var written = new DateTimeOffset(File.GetLastWriteTimeUtc(configPath), TimeSpan.Zero);
        if (written > core.StartedAt || !XrayParser.TryReadSelection(gui, out var selected) ||
            selected!.EnableTun || !selected.StatisticsRequested || !SingboxParser.HasAnyTlsProxy(config))
            throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidConfiguration);
        CoreComponentInstaller.PlainPath(sidecarPath, directory: false, allowMissing: true);
        byte[]? previous = null;
        string listen;
        if (File.Exists(sidecarPath))
        {
            using var file = Open(sidecarPath);
            previous = Read(file, 4096);
            if (!SingboxParser.TryReadMemberStatisticsSettings(previous, out var endpoint, out _))
                throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidConfiguration);
            listen = endpoint!.Authority;
        }
        else
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            listen = ((IPEndPoint)listener.LocalEndpoint).ToString();
        }
        var prospective = JsonSerializer.SerializeToUtf8Bytes(new { listen });
        CoreComponentInstaller.PlainPath(database, directory: false);
        var name = WindowsDiscovery.ReadProfile(database, selected.IndexId, gui, config, out var configuration,
            prospective, allowMemberStatistics: true);
        var process = XrayParser.Hash(string.Join('|', core.Pid, core.StartedAt!.Value.UtcTicks,
            core.ExecutablePath.ToUpperInvariant(), parent.Pid, parent.StartedAt!.Value.UtcTicks,
            parent.ExecutablePath.ToUpperInvariant()));
        var baseline = new XraySnapshot(XrayParser.Hash(root.ToUpperInvariant()), selected.IndexId, name, process,
            XrayParser.Hash(config) + ":" + written.UtcTicks.ToString(CultureInfo.InvariantCulture),
            core.StartedAt.Value, written, "", configuration!.MetricsEndpoint, "sing-box", configuration.Scope);
        token.ThrowIfCancellationRequested();
        var prepared = CoreCapabilityPreparation.PrepareSidecar(baseline, config,
            previous is null ? (ReadOnlyMemory<byte>?)null : previous.AsMemory(), listen);
        return new(baseline, prepared);
    }

    private static FileStream Open(string path)
    {
        CoreComponentInstaller.PlainPath(path, directory: false);
        return new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    internal static string PreviousText(string path, string? hash)
    {
        CoreComponentInstaller.PlainPath(path, directory: false, allowMissing: true);
        if (!File.Exists(path))
        {
            if (hash is null) return "不存在";
            throw new CoreComponentInstaller.Rejected(CoreInstallState.PlanChanged);
        }
        using var file = Open(path);
        var bytes = Read(file, 4096);
        if (XrayParser.Hash(bytes) != hash) throw new CoreComponentInstaller.Rejected(CoreInstallState.PlanChanged);
        return Encoding.UTF8.GetString(bytes);
    }
    private static byte[] Read(FileStream file, int maximum)
    {
        if (file.Length is <= 0 || file.Length > maximum)
            throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidConfiguration);
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
}
