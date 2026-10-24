using System.Runtime.Versioning;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Host;

internal sealed record CoreActionPrompt(string Title, string Message);

// The page requests a fixed action. Only a fresh native Host confirmation authorizes writing.
internal sealed class CoreComponentActions(
    IReadOnlyList<CoreComponent> catalog,
    Func<string, CoreInstallObservation> capture,
    Func<CoreActionPrompt, CancellationToken, Task<bool>> confirm,
    Func<CoreComponent, string, CoreInstallObservation, CancellationToken, Task<CorePreparation>> prepare,
    Func<string, CoreComponent, CancellationToken, Task<SidecarDiscovery>>? discoverSidecar = null,
    Func<string, string, CancellationToken, Task<CoreRuntimeVerification>>? verifyRuntime = null)
{
    private sealed record PendingSidecar(string Id, string Directory, SidecarDiscovery Discovery, DateTimeOffset Expires);
    private PendingSidecar? pendingSidecar;
    internal void Invalidate() => Interlocked.Exchange(ref pendingSidecar, null);

    [SupportedOSPlatform("windows")]
    internal static CoreComponentActions ForWindows(Func<CoreActionPrompt, CancellationToken, Task<bool>> confirm) =>
        new(CoreComponents.Catalog, CoreCapabilityInspector.ObserveWindows, confirm,
            (component, path, observed, token) => CoreComponentDownloads.PrepareAsync(
                component.Id, path, observed.ClientVersion, observed.Runtime, token),
            (directory, component, token) => CoreSidecarDiscovery.CaptureAsync(
                CoreCapabilityInspector.ObserveWindows(directory), component, WindowsDiscovery.ReadArguments, token),
            async (directory, component, token) =>
            {
                using var discovery = new WindowsDiscovery();
                return await discovery.VerifyRuntimeAsync(directory, component, token).ConfigureAwait(false);
            });

    internal async Task<CoreActionResult> ExecuteAsync(string directory, CoreActionRequest request, CancellationToken token)
    {
        if (!request.IsValid()) return new("InvalidRequest");
        var component = catalog.SingleOrDefault(x => x.Id == request.ComponentId);
        if (component is null) return new("InvalidRequest");
        CorePreparation? downloaded = null;
        CoreActionResult Finish(CoreActionResult result)
        {
            var remainder = downloaded?.ReleaseStaging();
            return remainder is null ? result : result with
            { Details = $"临时组件文件未能完全清理，请保留并核对：{remainder}。安装结果与备份不因此改变。" };
        }
        try
        {
            token.ThrowIfCancellationRequested();
            if (request.Action == "verify-runtime")
            {
                if (verifyRuntime is null) return new("Unavailable", component.Id);
                var runtime = await verifyRuntime(directory, component.Id, token);
                return new("RuntimeChecked", component.Id, Runtime: runtime);
            }
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
            if (request.Action.EndsWith("-sidecar", StringComparison.Ordinal))
                return await SidecarAsync(directory, request, component, Recapture, token);
            if (request.Action == "install")
            {
                downloaded = await prepare(component, target, observed, token);
                if (downloaded.State != CorePreparationState.DownloadVerified || downloaded.FilePath is null)
                    return Finish(new(downloaded.State.ToString(), component.Id));
                var proposed = await installer.PrepareAsync(downloaded.FilePath, token);
                if (proposed.State != CoreInstallState.Ready || proposed.Plan is null)
                    return Finish(new(proposed.State.ToString(), component.Id));
                var plan = proposed.Plan;
                var prompt = new CoreActionPrompt("确认替换配套核心",
                    $"组件：{component.Id} · {component.Version}\n目标：{plan.TargetPath}\n" +
                    $"原文件：{plan.Previous?.Sha256 ?? "不存在"}\n新文件：{component.Sha256}\n" +
                    $"操作编号：{plan.OperationId:N}\n新备份位置：{BackupDirectory(plan.TargetPath, plan.OperationId)}\n\n" +
                    "仅替换此核心文件，不写 sidecar、配置、账本或启动项。目标核心必须已停止；程序不会自动停止或重载代理。\n" +
                    (component.Id == "xray-probes" ? "这是可选实验探测核心，不承诺与原定制核心等价；普通 Xray 统计无需替换。\n" : "") +
                    "确认后将重新核对目标与文件。完成后请在 v2rayN 中手动重载，再核验实际接口。");
                if (!await confirm(prompt, token)) return Finish(new("Cancelled", component.Id));
                token.ThrowIfCancellationRequested();
                var result = await installer.InstallAsync(plan, token);
                return Finish(Result(result, component.Id, plan.TargetPath));
            }
            var operation = Guid.ParseExact(request.OperationId!, "N");
            var restored = await installer.PrepareRestoreAsync(operation, token);
            if (restored.State != CoreInstallState.Ready || restored.TargetPath is null || restored.Journal is null)
                return new(restored.State.ToString(), component.Id, operation.ToString("N"));
            var journal = restored.Journal;
            var restorePrompt = new CoreActionPrompt("确认恢复原核心文件",
                $"组件：{component.Id}\n目标：{restored.TargetPath}\n操作编号：{operation:N}\n" +
                $"备份：{BackupDirectory(restored.TargetPath, operation)}\n" +
                $"当前文件摘要：{journal.Installed.Sha256}\n" +
                $"将恢复：{journal.Previous?.Sha256 ?? "原文件不存在的状态"}\n\n" +
                "只恢复该操作的核心文件，保留备份，不回退账本或设置。目标核心必须已停止；不会自动停止或重载代理。\n" +
                "若当前文件或备份已变化，将拒绝恢复。恢复后请手动重载并核验实际接口。");
            if (!await confirm(restorePrompt, token)) return new("Cancelled", component.Id, operation.ToString("N"));
            token.ThrowIfCancellationRequested();
            return Result(await installer.RestoreAsync(operation, token, journal), component.Id, restored.TargetPath);
        }
        catch (OperationCanceledException) { return Finish(new("Cancelled", component.Id)); }
        catch (CoreComponentInstaller.Rejected rejected) { return Finish(new(rejected.State.ToString(), component.Id)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or System.Management.ManagementException or System.ComponentModel.Win32Exception or
            WindowsDiscovery.DiscoveryException or Microsoft.Data.Sqlite.SqliteException or System.Text.Json.JsonException)
        { return Finish(new(error is UnauthorizedAccessException ? "PermissionDenied" : "UnknownTarget", component.Id)); }
        finally { downloaded?.ReleaseStaging(); }
    }

    private async Task<CoreActionResult> SidecarAsync(string directory, CoreActionRequest request, CoreComponent component,
        Func<CoreInstallObservation> recapture, CancellationToken token)
    {
        if (request.Action == "prepare-sidecar")
        {
            Invalidate();
            if (discoverSidecar is null) return new("Unavailable", component.Id);
            var discovered = await discoverSidecar(directory, component, token);
            if (discovered.Preparation.Plan is not { } plan) return new(discovered.Preparation.State.ToString(), component.Id);
            var ticket = new PendingSidecar(Guid.NewGuid().ToString("N"), Path.GetFullPath(directory),
                discovered, DateTimeOffset.UtcNow.AddMinutes(10));
            Interlocked.Exchange(ref pendingSidecar, ticket);
            return new(discovered.Preparation.State == SidecarPreparationState.AlreadyMatched ? "SidecarAlreadyMatched" : "SidecarPrepared",
                component.Id, PlanId: ticket.Id, Details: "准备仅绑定配置，不代表接口已生效。计划保留 10 分钟；请自行停止目标核心后打开写入确认。");
        }
        if (request.Action == "restore-sidecar")
        {
            Invalidate();
            var operation = Guid.ParseExact(request.OperationId!, "N");
            var recovery = await CoreComponentInstaller.ForSidecarRestoreAsync(component, recapture, operation, token);
            var proposed = await recovery.PrepareRestoreAsync(operation, token);
            if (proposed.State != CoreInstallState.Ready || proposed.TargetPath is null || proposed.Journal is null)
                return new(proposed.State.ToString(), component.Id, operation.ToString("N"));
            var journal = proposed.Journal;
            if (!await confirm(new("确认恢复统计 sidecar",
                $"目标：{proposed.TargetPath}\n操作编号：{operation:N}\n备份：{BackupDirectory(proposed.TargetPath, operation)}\n" +
                $"当前摘要：{journal.Installed.Sha256}\n恢复摘要：{journal.Previous?.Sha256 ?? "恢复为不存在"}\n\n" +
                "只恢复此操作的 sidecar，不改核心、完整配置、路由、账本或启动项。目标核心必须已停止；请手动重载后核验接口。"), token))
                return new("Cancelled", component.Id, operation.ToString("N"));
            token.ThrowIfCancellationRequested();
            var result = await recovery.RestoreAsync(operation, token, journal);
            return Result(result, component.Id, proposed.TargetPath) with
            { State = result.State == CoreInstallState.Restored ? "SidecarRestored" : result.State.ToString() };
        }
        var pending = Interlocked.Exchange(ref pendingSidecar, null);
        if (pending is null || pending.Id != request.PlanId || pending.Expires <= DateTimeOffset.UtcNow ||
            !ProcessBinding.SamePath(pending.Directory, directory))
            return new("PlanChanged", component.Id);
        var confirmed = pending.Discovery.Preparation.Plan!;
        var installer = CoreComponentInstaller.ForSidecar(component, recapture, pending.Discovery.Baseline, confirmed);
        CoreActionResult Retriable(string state)
        {
            if (state is "CoreRunning" or "Cancelled" or "FileBusy" or "PortUnavailable")
            {
                Interlocked.CompareExchange(ref pendingSidecar, pending, null);
                return new(state, component.Id, PlanId: pending.Id);
            }
            return new(state, component.Id);
        }
        var stage = Directory.CreateTempSubdirectory("v2rayn-monitor-sidecar-").FullName;
        var candidate = Path.Combine(stage, "monitor-stats.json");
        try
        {
            await File.WriteAllTextAsync(candidate, confirmed.Json, token);
            var proposed = await installer.PrepareAsync(candidate, token);
            if (proposed.State != CoreInstallState.Ready || proposed.Plan is not { } plan)
                return Retriable(proposed.State == CoreInstallState.AlreadyMatched ? "SidecarAlreadyMatched" : proposed.State.ToString());
            var old = CoreSidecarDiscovery.PreviousText(plan.TargetPath, confirmed.PreviousHash);
            if (!await confirm(new("确认写入统计 sidecar",
                $"目标：{plan.TargetPath}\n操作编号：{plan.OperationId:N}\n备份：{BackupDirectory(plan.TargetPath, plan.OperationId)}\n" +
                $"原摘要：{confirmed.PreviousHash ?? "不存在"}\n新摘要：{XrayParser.Hash(System.Text.Encoding.UTF8.GetBytes(confirmed.Json))}\n原内容：{old}\n新内容：{confirmed.Json}\n\n" +
                "仅写入固定 monitor-stats.json。目标核心须已停止，端口须可用；确认后重验配置、成员、进程与旧文件。\n" +
                "不改完整配置、订阅、路由、账本或启动项，不自动重载。完成后请手动重载并核验实际接口。"), token))
                return Retriable("Cancelled");
            token.ThrowIfCancellationRequested();
            var result = await installer.InstallAsync(plan, token);
            return Result(result, component.Id, plan.TargetPath) with
            { State = result.State == CoreInstallState.InstalledPendingReload ? "SidecarInstalledPendingReload" : result.State.ToString() };
        }
        finally
        {
            // Only this newly-created candidate and empty directory; never recursively remove unknown content.
            try { File.Delete(candidate); Directory.Delete(stage); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string BackupDirectory(string target, Guid? id = null) =>
        Path.Combine(Path.GetDirectoryName(target)!, ".monitor-core-backups", id?.ToString("N") ?? "");
    private static CoreActionResult Result(CoreInstallResult result, string component, string target) =>
        new(result.State.ToString(), component, result.OperationId?.ToString("N"),
            result.OperationId is { } operation ? BackupDirectory(target, operation) : null);
}
