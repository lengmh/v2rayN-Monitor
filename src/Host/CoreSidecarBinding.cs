using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Host;

// A retained discovery baseline is rechecked against the actual standard files after the core stops.
// No synthetic process identity is created for the stopped core, and no page may supply this baseline.
internal sealed class CoreSidecarBinding
{
    private readonly CoreComponent approvedCore;
    private readonly XraySnapshot? baseline;
    private readonly SidecarPlan? confirmed;
    internal bool RestoreOnly => confirmed is null;
    internal SidecarPlan Confirmed => confirmed ??
        throw new CoreComponentInstaller.Rejected(CoreInstallState.UnsupportedTarget);
    internal CoreComponent Component { get; }

    private CoreSidecarBinding(CoreComponent core, CoreFileIdentity installed)
    {
        approvedCore = core;
        Component = core with
        {
            Id = "sing-box-sidecar", FileName = "monitor-stats.json",
            Sha256 = installed.Sha256, ExpectedBytes = installed.Bytes
        };
    }

    internal CoreSidecarBinding(CoreComponent core, XraySnapshot baseline, SidecarPlan confirmed)
        : this(core, new(Encoding.UTF8.GetByteCount(confirmed.Json),
            XrayParser.Hash(Encoding.UTF8.GetBytes(confirmed.Json))))
    {
        ArgumentNullException.ThrowIfNull(baseline);
        this.baseline = baseline;
        this.confirmed = confirmed;
    }

    internal static CoreSidecarBinding ForRestore(CoreComponent core, CoreFileIdentity installed) => new(core, installed);

    internal async Task<IDisposable> AcquireAsync(string sidecarPath, bool exclusiveCore, CancellationToken token)
    {
        var lease = new Inputs();
        try
        {
            if (approvedCore.Id != "sing-box-statistics" || approvedCore.FileName != "sing-box-client.exe")
                throw new CoreComponentInstaller.Rejected(CoreInstallState.UnsupportedTarget);
            var coreDirectory = Path.GetDirectoryName(sidecarPath)!;
            var root = Path.GetDirectoryName(Path.GetDirectoryName(coreDirectory)!)!;
            var corePath = Path.Combine(coreDirectory, approvedCore.FileName);
            var core = lease.Open(corePath);
            if ((await CoreComponents.VerifyStreamAsync(approvedCore, core, token)).State != CoreFileState.IdentityMatched)
                throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidCandidate);
            // A restore undoes the recorded file operation even when its core cannot reload.
            // New writes still require the original running-core discovery and configuration.
            if (baseline is not null && confirmed is not null)
            {
                if (baseline.InstanceId != XrayParser.Hash(root.ToUpperInvariant()))
                    throw new CoreComponentInstaller.Rejected(CoreInstallState.PlanChanged);
                var config = Read(lease.Open(Path.Combine(root, "binConfigs", "config.json")), 4 * 1024 * 1024);
                var gui = Read(lease.Open(Path.Combine(root, "guiConfigs", "guiNConfig.json")), 1024 * 1024);
                var database = Path.Combine(root, "guiConfigs", "guiNDB.db");
                lease.Open(database); // Refuse an open write handle and deny new writers during replacement.
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                {
                    CoreComponentInstaller.PlainPath(database + suffix, directory: false, allowMissing: true);
                    if (File.Exists(database + suffix)) lease.Open(database + suffix);
                }
                if (!XrayParser.TryReadSelection(gui, out var selection) || selection!.EnableTun ||
                    !selection.StatisticsRequested || selection.IndexId != baseline.NodeId ||
                    confirmed.InstanceId != baseline.InstanceId || confirmed.NodeId != baseline.NodeId ||
                    confirmed.ProcessIdentity != baseline.ProcessIdentity || confirmed.Generation != baseline.Generation ||
                    confirmed.ConfigurationHash != XrayParser.Hash(config))
                    throw new CoreComponentInstaller.Rejected(CoreInstallState.PlanChanged);
                using var document = JsonDocument.Parse(confirmed.Json);
                var listen = document.RootElement.GetProperty("listen").GetString()!;
                var prepared = CoreCapabilityPreparation.PrepareSidecar(baseline, config, null, listen);
                if (prepared.Plan?.Json != confirmed.Json)
                    throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidConfiguration);
                WindowsDiscovery.ReadProfile(database, selection.IndexId, gui, config, out _,
                    Encoding.UTF8.GetBytes(confirmed.Json), allowMemberStatistics: true);
            }
            // A held file can still have its parent directory renamed. Recheck every directory entry.
            foreach (var file in lease.Files)
            {
                CoreComponentInstaller.PlainPath(file.Name, directory: false);
                var identity = await CoreComponentInstaller.IdentifyAsync(file, token);
                if (!await CoreComponentInstaller.SameFileAtPathAsync(file, file.Name, identity, token))
                    throw new CoreComponentInstaller.Rejected(CoreInstallState.PlanChanged);
            }
            if (exclusiveCore)
            {
                // Unix's path comparison reopens the file and would conflict with our own flock.
                // Take the exclusive lease last, then verify the approved bytes again through it.
                lease.Files.Remove(core);
                core.Dispose();
                core = lease.Open(corePath, FileShare.None);
                if ((await CoreComponents.VerifyStreamAsync(approvedCore, core, token)).State != CoreFileState.IdentityMatched)
                    throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidCandidate);
                if (OperatingSystem.IsWindows() && !await CoreComponentInstaller.SameFileAtPathAsync(core, corePath,
                    new(core.Length, approvedCore.Sha256), token))
                    throw new CoreComponentInstaller.Rejected(CoreInstallState.PlanChanged);
            }
            return lease;
        }
        catch (Exception error)
        {
            lease.Dispose();
            if (error is WindowsDiscovery.DiscoveryException or SqliteException or JsonException or KeyNotFoundException or InvalidOperationException)
                throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidConfiguration);
            throw;
        }
    }

    internal Socket ReservePort(string json)
    {
        if (!SingboxParser.TryReadMemberStatisticsSettings(Encoding.UTF8.GetBytes(json), out _, out _))
            throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidConfiguration);
        using var document = JsonDocument.Parse(json);
        var endpoint = IPEndPoint.Parse(document.RootElement.GetProperty("listen").GetString()!);
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Bind(endpoint);
            return socket; // Hold through commit; release before the caller asks the user to reload.
        }
        catch { socket.Dispose(); throw; }
    }

    internal void ValidatePrevious(FileStream? file)
    {
        if (file is null) return;
        file.Position = 0;
        if (!SingboxParser.TryReadMemberStatisticsSettings(Read(file, 4096), out _, out _))
            throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidConfiguration);
    }

    private static byte[] Read(FileStream input, int maximum)
    {
        if (input.Length is <= 0 || input.Length > maximum)
            throw new CoreComponentInstaller.Rejected(CoreInstallState.InvalidConfiguration);
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private sealed class Inputs : IDisposable
    {
        internal List<FileStream> Files { get; } = [];
        internal FileStream Open(string path, FileShare share = FileShare.Read)
        {
            CoreComponentInstaller.PlainPath(path, directory: false);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, share);
            Files.Add(file);
            return file;
        }
        public void Dispose() { foreach (var file in Files) file.Dispose(); }
    }
}
