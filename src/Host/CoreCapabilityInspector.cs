using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Host;

internal static class CoreCapabilityInspector
{
    [SupportedOSPlatform("windows")]
    internal static Task<CoreCapabilityInspection> InspectWindowsAsync(string directory, CancellationToken token) =>
        Task.Run(async () =>
        {
            if (string.IsNullOrWhiteSpace(directory)) return Report("not_selected", CoreComponents.Catalog);
            try
            {
                token.ThrowIfCancellationRequested();
                return await InspectAsync(ObserveWindows(directory), CoreComponents.Catalog, token).ConfigureAwait(false);
            }
            catch (Exception error) when (Expected(error)) { return Report("unknown_target", CoreComponents.Catalog); }
        }, token);

    [SupportedOSPlatform("windows")]
    internal static CoreInstallObservation ObserveWindows(string directory)
    {
        var processes = WindowsDiscovery.ReadProcesses();
        var clients = processes.Where(x => x.Name.Equals("v2rayN.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (clients.Length != 1 || clients[0].Failure is not null ||
            !Path.IsPathFullyQualified(clients[0].ExecutablePath))
            throw new CoreComponentInstaller.Rejected(CoreInstallState.UnknownTarget);
        var clientPath = clients[0].ExecutablePath;
        CoreComponentInstaller.PlainPath(clientPath, directory: false);
        return new(clientPath, directory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "v2rayN"),
            FileVersionInfo.GetVersionInfo(clientPath).ProductVersion ?? "",
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64" : "unsupported", processes);
    }

    // Only Host-owned observations and the compiled catalog reach here. Never accepts page-supplied paths/descriptors.
    internal static async Task<CoreCapabilityInspection> InspectAsync(CoreInstallObservation observed,
        IReadOnlyList<CoreComponent> components, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(observed.DataRoot)) return Report("not_selected", components);
        try
        {
            var clients = observed.Processes.Where(x => x.Name.Equals("v2rayN.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!observed.Complete || observed.Processes.Any(x => x.Failure is not null || x.StartedAt is null ||
                    !Path.IsPathFullyQualified(x.ExecutablePath)) ||
                clients.Length != 1 || clients[0].Pid <= 0 ||
                !ProcessBinding.SamePath(clients[0].ExecutablePath, observed.ClientPath))
                return Report("unknown_target", components);
            CoreComponentInstaller.PlainPath(observed.ClientPath, directory: false);
            CoreComponentInstaller.PlainPath(observed.DataRoot, directory: true);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(observed.DataRoot));
            if ((!ProcessBinding.SamePath(Path.GetDirectoryName(observed.ClientPath)!, root) &&
                 !ProcessBinding.SamePath(root, observed.LocalAppDataRoot)) ||
                !Directory.Exists(Path.Combine(root, "guiConfigs")) || !Directory.Exists(Path.Combine(root, "binConfigs")))
                return Report("unknown_target", components);
            if (components.Any(x => CoreComponents.ValidateTarget(x, observed.ClientVersion, observed.Runtime) is not null))
                return Report("unsupported_target", components);
            var rows = new List<CoreComponentFileStatus>();
            foreach (var component in components)
            {
                token.ThrowIfCancellationRequested();
                var state = await ReadFileAsync(component, root, observed, token).ConfigureAwait(false);
                rows.Add(Row(component, state));
            }
            return new("identified", rows.AsReadOnly());
        }
        catch (Exception error) when (Expected(error)) { return Report("unknown_target", components); }
    }

    private static async Task<string> ReadFileAsync(CoreComponent component, string root,
        CoreInstallObservation observed, CancellationToken token)
    {
        var folder = component.Id switch
        {
            "sing-box-statistics" when component.FileName == "sing-box-client.exe" => "sing_box",
            "xray-probes" when component.FileName == "xray.exe" => "xray",
            _ => null
        };
        if (folder is null) return "not_checked";
        try
        {
            var path = root;
            foreach (var part in new[] { "bin", folder })
            {
                path = Path.Combine(path, part);
                CoreComponentInstaller.PlainPath(path, directory: true, allowMissing: true);
                if (!Directory.Exists(path)) return "missing";
            }
            path = Path.Combine(path, component.FileName);
            CoreComponentInstaller.PlainPath(path, directory: false, allowMissing: true);
            var check = await CoreComponents.VerifyFileAsync(component, path, observed.ClientVersion, observed.Runtime, token).ConfigureAwait(false);
            return check.State switch
            {
                CoreFileState.IdentityMatched => "identity_matched",
                CoreFileState.Missing => "missing",
                CoreFileState.SizeMismatch => "size_mismatch",
                CoreFileState.DigestMismatch => "digest_mismatch",
                _ => "unreadable"
            };
        }
        catch (Exception error) when (Expected(error)) { return "unreadable"; }
    }

    private static CoreCapabilityInspection Report(string state, IReadOnlyList<CoreComponent> components) =>
        new(state, components.Select(x => Row(x, "not_checked")).ToArray());
    private static CoreComponentFileStatus Row(CoreComponent component, string state) =>
        new(component.Id, component.Id == "sing-box-statistics" ? "sing-box 统计支持" : "Xray 探测扩展",
            component.Version, state, CoreComponentDownloads.PublishedAsset(component.Id) is null ? "asset_unavailable" : "available", component.License);
    private static bool Expected(Exception error) => error is CoreComponentInstaller.Rejected or IOException or
        UnauthorizedAccessException or ArgumentException or NotSupportedException or
        System.Management.ManagementException or System.ComponentModel.Win32Exception;
}
