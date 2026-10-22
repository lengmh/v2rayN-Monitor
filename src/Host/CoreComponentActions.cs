using System.Runtime.Versioning;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Host;

internal sealed record CoreActionPrompt(string Title, string Message);

// The page requests a fixed action. Only a fresh native Host confirmation authorizes writing.
internal sealed class CoreComponentActions(
    IReadOnlyList<CoreComponent> catalog,
    Func<string, CoreInstallObservation> capture,
    Func<CoreActionPrompt, CancellationToken, Task<bool>> confirm,
    Func<CoreComponent, string, CoreInstallObservation, CancellationToken, Task<CorePreparation>> prepare)
{
    [SupportedOSPlatform("windows")]
    internal static CoreComponentActions ForWindows(Func<CoreActionPrompt, CancellationToken, Task<bool>> confirm) =>
        new(CoreComponents.Catalog, CoreCapabilityInspector.ObserveWindows, confirm,
            (component, path, observed, token) => CoreComponentDownloads.PrepareAsync(
                component.Id, path, observed.ClientVersion, observed.Runtime, token));

    internal async Task<CoreActionResult> ExecuteAsync(string directory, CoreActionRequest request, CancellationToken token)
    {
        if (!request.IsValid()) return new("InvalidRequest");
        var component = catalog.SingleOrDefault(x => x.Id == request.ComponentId);
        if (component is null) return new("InvalidRequest");
        try
        {
            token.ThrowIfCancellationRequested();
            var observed = capture(directory);
            var inspection = await CoreCapabilityInspector.InspectAsync(observed, [component], token);
            if (inspection.TargetState != "identified")
                return new(inspection.TargetState == "unsupported_target" ? "UnsupportedTarget" : "UnknownTarget", component.Id);
            // Bind all subsequent captures to this client; never follow a newly selected instance.
            CoreInstallObservation Recapture()
            {
                var current = capture(directory);
                if (!ProcessBinding.SamePath(current.ClientPath, observed.ClientPath) ||
                    !ProcessBinding.SamePath(current.DataRoot, observed.DataRoot))
                    throw new CoreComponentInstaller.Rejected(CoreInstallState.PlanChanged);
                return current;
            }
            var target = Path.Combine(observed.DataRoot, "bin",
                component.Id == "sing-box-statistics" ? "sing_box" : "xray", component.FileName);
            var installer = new CoreComponentInstaller(component, Recapture);
            if (request.Action == "install")
            {
                var downloaded = await prepare(component, target, observed, token);
                if (downloaded.State != CorePreparationState.DownloadVerified || downloaded.FilePath is null)
                    return new(downloaded.State.ToString(), component.Id);
                var proposed = await installer.PrepareAsync(downloaded.FilePath, token);
                if (proposed.State != CoreInstallState.Ready || proposed.Plan is null)
                    return new(proposed.State.ToString(), component.Id);
                var plan = proposed.Plan;
                var prompt = new CoreActionPrompt("确认替换配套核心",
                    $"组件：{component.Id} · {component.Version}\n目标：{plan.TargetPath}\n" +
                    $"原文件：{plan.Previous?.Sha256 ?? "不存在"}\n新文件：{component.Sha256}\n" +
                    $"新备份位置：{BackupDirectory(plan.TargetPath)}\\<新操作编号>\n\n" +
                    "仅替换此核心文件，不写 sidecar、配置、账本或启动项。目标核心必须已停止；程序不会自动停止或重载代理。\n" +
                    (component.Id == "xray-probes" ? "这是可选实验探测核心，不承诺与原定制核心等价；普通 Xray 统计无需替换。\n" : "") +
                    "确认后将重新核对目标与文件。完成后请在 v2rayN 中手动重载，再核验实际接口。");
                if (!await confirm(prompt, token)) return new("Cancelled", component.Id);
                token.ThrowIfCancellationRequested();
                var result = await installer.InstallAsync(plan, token);
                return Result(result, component.Id, plan.TargetPath);
            }
            var operation = Guid.ParseExact(request.OperationId!, "N");
            var restored = await installer.PrepareRestoreAsync(operation, token);
            if (restored.State != CoreInstallState.Ready || restored.TargetPath is null || restored.Journal is null)
                return new(restored.State.ToString(), component.Id, operation.ToString("N"));
            var journal = restored.Journal;
            var restorePrompt = new CoreActionPrompt("确认恢复原核心文件",
                $"组件：{component.Id}\n目标：{restored.TargetPath}\n操作编号：{operation:N}\n" +
                $"备份：{BackupDirectory(restored.TargetPath, operation)}\n" +
                $"将恢复：{journal.Previous?.Sha256 ?? "原文件不存在的状态"}\n\n" +
                "只恢复该操作的核心文件，保留备份，不回退账本或设置。目标核心必须已停止；不会自动停止或重载代理。\n" +
                "若当前文件或备份已变化，将拒绝恢复。恢复后请手动重载并核验实际接口。");
            if (!await confirm(restorePrompt, token)) return new("Cancelled", component.Id, operation.ToString("N"));
            token.ThrowIfCancellationRequested();
            return Result(await installer.RestoreAsync(operation, token, journal), component.Id, restored.TargetPath);
        }
        catch (OperationCanceledException) { return new("Cancelled", component.Id); }
        catch (CoreComponentInstaller.Rejected rejected) { return new(rejected.State.ToString(), component.Id); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Management.ManagementException or System.ComponentModel.Win32Exception)
        { return new(error is UnauthorizedAccessException ? "PermissionDenied" : "UnknownTarget", component.Id); }
    }

    private static string BackupDirectory(string target, Guid? id = null) =>
        Path.Combine(Path.GetDirectoryName(target)!, ".monitor-core-backups", id?.ToString("N") ?? "");
    private static CoreActionResult Result(CoreInstallResult result, string component, string target) =>
        new(result.State.ToString(), component, result.OperationId?.ToString("N"),
            result.OperationId is { } operation ? BackupDirectory(target, operation) : null);
}
