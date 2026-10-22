using System.Diagnostics;
using System.Runtime.Versioning;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Cryptography;
using System.Text.Json;

namespace V2rayNMonitor.Host;

internal sealed record CoreInstallObservation(string ClientPath, string DataRoot, string LocalAppDataRoot,
    string ClientVersion, string Runtime, IReadOnlyList<ProcessInfo> Processes, bool Complete = true);
internal sealed record CoreFileIdentity(long Bytes, string Sha256);
internal sealed record CoreInstallPlan(string CandidatePath, string TargetPath, int ClientPid,
    DateTimeOffset ClientStartedAt, CoreFileIdentity Client, CoreFileIdentity? Previous);
internal enum CoreInstallState
{
    Ready, AlreadyMatched, InstalledPendingReload, Restored, AlreadyRestored, FailedRestored,
    UnknownTarget, UnsupportedTarget, InvalidCandidate, UnsafePath, CoreRunning, PlanChanged,
    FileBusy, PermissionDenied, Cancelled, StorageFailed, RecoveryRequired
}
internal sealed record CoreInstallPreparation(CoreInstallState State, CoreInstallPlan? Plan = null);
internal sealed record CoreInstallResult(CoreInstallState State, Guid? OperationId = null);
internal sealed record CoreInstallJournal(Guid OperationId, string ComponentId, string Version,
    CoreFileIdentity Client, CoreFileIdentity Installed, CoreFileIdentity? Previous, string State);
internal enum CoreInstallCheckpoint { BackupVerified, BeforeReplace, AfterReplace }

// No UI/API caller yet. Paths and process observations are owned by the Host, never by a page.
// One core file only; sidecar, configuration and ledger handling belong to separate stages.
internal sealed class CoreComponentInstaller(CoreComponent component, Func<CoreInstallObservation> capture,
    Action<CoreInstallCheckpoint>? checkpoint = null)
{
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const string BackupFolder = ".monitor-core-backups";
    private sealed record Target(string Path, ProcessInfo Client, CoreFileIdentity ClientFile);
    private sealed class Rejected(CoreInstallState state) : Exception { internal CoreInstallState State => state; }

    [SupportedOSPlatform("windows")]
    internal static CoreComponentInstaller? ForWindows(string componentId, string clientPath, string dataRoot)
    {
        var selected = CoreComponents.Catalog.SingleOrDefault(x => x.Id == componentId);
        if (selected is null) return null;
        return new(selected, () => new(clientPath, dataRoot,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "v2rayN"),
            FileVersionInfo.GetVersionInfo(clientPath).ProductVersion ?? "",
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
                ? "win-x64" : "unsupported",
            WindowsDiscovery.ReadProcesses()));
    }

    internal async Task<CoreInstallPreparation> PrepareAsync(string candidatePath, CancellationToken token)
    {
        try
        {
            var target = await ReadTargetAsync(token);
            using var previous = OpenExisting(target.Path, FileShare.Read);
            var identity = previous is null ? null : await IdentifyAsync(previous, token);
            if (MatchesComponent(identity)) return new(CoreInstallState.AlreadyMatched);
            PlainPath(candidatePath, directory: false);
            if ((await CoreComponents.VerifyFileAsync(component, candidatePath, component.ClientVersion,
                component.Runtime, token)).State != CoreFileState.IdentityMatched)
                return new(CoreInstallState.InvalidCandidate);
            return new(CoreInstallState.Ready, new(Path.GetFullPath(candidatePath), target.Path,
                target.Client.Pid, target.Client.StartedAt!.Value, target.ClientFile, identity));
        }
        catch (Exception e) when (Expected(e)) { return new(Failure(e)); }
    }

    internal async Task<CoreInstallResult> InstallAsync(CoreInstallPlan plan, CancellationToken token)
    {
        Guid? operation = null;
        CoreInstallJournal? journal = null;
        Target? target = null;
        FileStream? original = null;
        var attempted = false;
        var replaced = false;
        try
        {
            target = await ReadTargetAsync(token);
            RequirePlan(plan, target);
            using var transaction = LockTarget(target.Path);
            // Deny new readers/writers (including a new loader), but permit the atomic replacement.
            original = OpenExisting(target.Path, FileShare.Delete);
            var old = original is null ? null : await IdentifyAsync(original, token);
            if (old != plan.Previous) throw new Rejected(CoreInstallState.PlanChanged);
            if (MatchesComponent(old)) return new(CoreInstallState.AlreadyMatched);
            PlainPath(plan.CandidatePath, directory: false);
            using var candidate = new FileStream(plan.CandidatePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if ((await CoreComponents.VerifyStreamAsync(component, candidate, token)).State != CoreFileState.IdentityMatched)
                throw new Rejected(CoreInstallState.InvalidCandidate);
            operation = Guid.NewGuid();
            var folder = CreateBackup(target.Path, operation.Value);
            journal = new(operation.Value, component.Id, component.Version, target.ClientFile,
                new(candidate.Length, component.Sha256.ToUpperInvariant()), old, "Preparing");
            WriteJournal(target.Path, journal);
            if (original is not null)
            {
                await CopyAsync(original, Path.Combine(folder, "previous.exe"), token);
                if (await IdentifyFileAsync(Path.Combine(folder, "previous.exe"), token) != old)
                    throw new IOException("Backup verification failed.");
            }
            var staged = Path.Combine(folder, "candidate.exe");
            await CopyAsync(candidate, staged, token);
            if (await IdentifyFileAsync(staged, token) != journal.Installed)
                throw new IOException("Staged file changed.");
            WriteJournal(target.Path, journal with { State = "BackupVerified" });
            checkpoint?.Invoke(CoreInstallCheckpoint.BackupVerified);
            checkpoint?.Invoke(CoreInstallCheckpoint.BeforeReplace);
            RequirePlan(plan, await ReadTargetAsync(token));
            PlainPath(target.Path, directory: false, allowMissing: old is null);
            PlainPath(staged, directory: false);
            if (await IdentifyFileAsync(staged, token) != journal.Installed)
                throw new Rejected(CoreInstallState.InvalidCandidate);
            if (original is not null && !await SameFileAtPathAsync(original, target.Path, old!, token))
                throw new Rejected(CoreInstallState.PlanChanged);
            using var checkedBackup = old is null ? null : new FileStream(Path.Combine(folder, "previous.exe"),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            if (checkedBackup is not null && await IdentifyAsync(checkedBackup, token) != old)
                throw new Rejected(CoreInstallState.PlanChanged);
            token.ThrowIfCancellationRequested();
            WriteJournal(target.Path, journal with { State = "Replacing" });
            attempted = true;
            if (original is null) File.Move(staged, target.Path); // Never overwrite a concurrently created file.
            else File.Replace(staged, target.Path, null);
            replaced = true;
            original?.Dispose(); original = null;
            // Once replaced, finish verification/recovery even if the caller cancels.
            checkpoint?.Invoke(CoreInstallCheckpoint.AfterReplace);
            if (await IdentifyFileAsync(target.Path, CancellationToken.None) != journal.Installed)
                throw new IOException("Installed file changed.");
            WriteJournal(target.Path, journal with { State = "InstalledPendingReload" });
            return new(CoreInstallState.InstalledPendingReload, operation);
        }
        catch (Exception e) when (Expected(e))
        {
            original?.Dispose(); original = null;
            if (attempted && journal is not null && target is not null)
            {
                // A failed atomic operation may be partial or may have raced with another writer.
                // Do not roll back somebody else's identical bytes merely because their digest matches.
                if (!replaced)
                {
                    try
                    {
                        var actual = await IdentifyFileAsync(target.Path, CancellationToken.None, allowMissing: true);
                        if (actual != journal.Previous) return new(CoreInstallState.RecoveryRequired, operation);
                        TryWriteJournal(target.Path, journal with { State = "FailedBeforeReplacement" });
                        return new(Failure(e), operation);
                    }
                    catch (Exception inspection) when (Expected(inspection))
                    { return new(CoreInstallState.RecoveryRequired, operation); }
                }
                try
                {
                    using var transaction = LockTarget(target.Path);
                    var outcome = await RestoreLockedAsync(journal, CancellationToken.None);
                    return new(outcome == CoreInstallState.Restored ? CoreInstallState.FailedRestored :
                        outcome == CoreInstallState.AlreadyRestored ? Failure(e) : CoreInstallState.RecoveryRequired, operation);
                }
                catch (Exception recovery) when (Expected(recovery))
                { return new(CoreInstallState.RecoveryRequired, operation); }
            }
            if (journal is not null && target is not null)
                TryWriteJournal(target.Path, journal with { State = "FailedBeforeReplacement" });
            return new(Failure(e), operation);
        }
        finally { original?.Dispose(); }
    }

    // Reload from the bounded operation ID, not a caller-supplied backup or destination path.
    // A restarted Monitor may restore with fresh process evidence and the same client binary.
    internal async Task<CoreInstallResult> RestoreAsync(Guid operationId, CancellationToken token)
    {
        try
        {
            var target = await ReadTargetAsync(token);
            using var transaction = LockTarget(target.Path);
            var path = Path.Combine(BackupPath(target.Path, operationId), "install.json");
            PlainPath(path, directory: false);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > 65536) throw new Rejected(CoreInstallState.UnsafePath);
            var journal = await JsonSerializer.DeserializeAsync<CoreInstallJournal>(input, cancellationToken: token);
            if (journal is null || journal.OperationId != operationId || journal.ComponentId != component.Id ||
                journal.Version != component.Version || journal.Client != target.ClientFile ||
                !MatchesComponent(journal.Installed) || !ValidIdentity(journal.Previous) ||
                (journal.Previous is null && Path.Exists(Path.Combine(BackupPath(target.Path, operationId), "previous.exe"))))
                throw new Rejected(CoreInstallState.PlanChanged);
            input.Dispose();
            return new(await RestoreLockedAsync(journal, token), operationId);
        }
        catch (Exception e) when (Expected(e)) { return new(Failure(e), operationId); }
    }

    private async Task<CoreInstallState> RestoreLockedAsync(CoreInstallJournal journal, CancellationToken token)
    {
        var target = await ReadTargetAsync(token);
        if (target.ClientFile != journal.Client) throw new Rejected(CoreInstallState.PlanChanged);
        using var current = OpenExisting(target.Path, FileShare.Delete);
        var identity = current is null ? null : await IdentifyAsync(current, token);
        if (identity == journal.Previous)
        {
            WriteJournal(target.Path, journal with { State = "Restored" });
            return CoreInstallState.AlreadyRestored;
        }
        if (identity != journal.Installed) throw new Rejected(CoreInstallState.PlanChanged);
        var folder = BackupPath(target.Path, journal.OperationId);
        PlainPath(folder, directory: true);
        string? staged = null;
        if (journal.Previous is not null)
        {
            var backup = Path.Combine(folder, "previous.exe");
            PlainPath(backup, directory: false);
            using var source = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (await IdentifyAsync(source, token) != journal.Previous)
                throw new Rejected(CoreInstallState.PlanChanged);
            staged = Path.Combine(folder, "restore-" + Guid.NewGuid().ToString("N") + ".exe");
            await CopyAsync(source, staged, token);
            if (await IdentifyFileAsync(staged, token) != journal.Previous)
                throw new IOException("Restore staging changed.");
        }
        var finalTarget = await ReadTargetAsync(token);
        if (!SameTarget(target, finalTarget)) throw new Rejected(CoreInstallState.PlanChanged);
        PlainPath(target.Path, directory: false);
        if (!await SameFileAtPathAsync(current!, target.Path, journal.Installed, token))
            throw new Rejected(CoreInstallState.PlanChanged);
        token.ThrowIfCancellationRequested();
        WriteJournal(target.Path, journal with { State = "Restoring" });
        try
        {
            if (staged is null)
            {
                // Preserve the new file while restoring the original absence; do not delete it.
                File.Move(target.Path, Path.Combine(folder, "removed-" + Guid.NewGuid().ToString("N") + ".exe"));
            }
            else File.Replace(staged, target.Path, null);
            current?.Dispose();
            if (await IdentifyFileAsync(target.Path, CancellationToken.None, allowMissing: true) != journal.Previous)
                return CoreInstallState.RecoveryRequired;
            WriteJournal(target.Path, journal with { State = "Restored" });
            return CoreInstallState.Restored;
        }
        catch (Exception e) when (Expected(e)) { return CoreInstallState.RecoveryRequired; }
    }

    private async Task<Target> ReadTargetAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var observed = capture();
        if (!observed.Complete || observed.Processes.Any(x => x.Failure is not null || x.StartedAt is null ||
            !Path.IsPathFullyQualified(x.ExecutablePath))) throw new Rejected(CoreInstallState.UnknownTarget);
        if (CoreComponents.ValidateTarget(component, observed.ClientVersion, observed.Runtime) is not null)
            throw new Rejected(CoreInstallState.UnsupportedTarget);
        var parents = observed.Processes.Where(x => x.Name.Equals("v2rayN.exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (parents.Length != 1 || parents[0].Pid <= 0 || !ProcessBinding.SamePath(parents[0].ExecutablePath, observed.ClientPath))
            throw new Rejected(CoreInstallState.UnknownTarget);
        PlainPath(observed.ClientPath, directory: false);
        PlainPath(observed.DataRoot, directory: true);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(observed.DataRoot));
        if (!ProcessBinding.SamePath(Path.GetDirectoryName(observed.ClientPath)!, root) &&
            !ProcessBinding.SamePath(root, observed.LocalAppDataRoot))
            throw new Rejected(CoreInstallState.UnknownTarget);
        var relative = component.Id switch
        {
            "sing-box-statistics" when component.FileName == "sing-box-client.exe" => Path.Combine("bin", "sing_box", "sing-box-client.exe"),
            "xray-probes" when component.FileName == "xray.exe" => Path.Combine("bin", "xray", "xray.exe"),
            _ => throw new Rejected(CoreInstallState.UnsupportedTarget)
        };
        var target = Path.Combine(root, relative);
        PlainPath(Path.GetDirectoryName(target)!, directory: true);
        PlainPath(target, directory: false, allowMissing: true);
        if (observed.Processes.Any(x => ProcessBinding.SamePath(x.ExecutablePath, target)))
            throw new Rejected(CoreInstallState.CoreRunning);
        if (OperatingSystem.IsWindows() && File.Exists(target))
        {
            var targetIdentity = PhysicalIdentity(target);
            foreach (var process in observed.Processes)
            {
                var processIdentity = PhysicalIdentity(process.ExecutablePath);
                if (targetIdentity == processIdentity) throw new Rejected(CoreInstallState.CoreRunning);
            }
        }
        return new(target, parents[0], (await IdentifyFileAsync(observed.ClientPath, token))!);
    }

    // The old handle permits atomic replacement, so recheck that the path still names that file.
    private static async Task<bool> SameFileAtPathAsync(FileStream held, string path,
        CoreFileIdentity expected, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return await IdentifyFileAsync(path, token) == expected;
        using var current = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
        if (current.IsInvalid || !GetFileInformationByHandle(held.SafeFileHandle, out var before) ||
            !GetFileInformationByHandle(current, out var now)) return false;
        return (now.Attributes & 0x410) == 0 && before.Volume == now.Volume &&
            before.IndexHigh == now.IndexHigh && before.IndexLow == now.IndexLow;
    }
    private static (uint Volume, uint High, uint Low) PhysicalIdentity(string path)
    {
        // Running image paths may be hard links or alternate spellings of the same file.
        using var file = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (file.IsInvalid || !GetFileInformationByHandle(file, out var identity))
            throw new Rejected(CoreInstallState.UnknownTarget);
        return (identity.Volume, identity.IndexHigh, identity.IndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileIdentity
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileIdentity identity);

    private static bool SameTarget(Target a, Target b) => ProcessBinding.SamePath(a.Path, b.Path) &&
        a.Client.Pid == b.Client.Pid && a.Client.StartedAt == b.Client.StartedAt && a.ClientFile == b.ClientFile;
    private static void RequirePlan(CoreInstallPlan plan, Target target)
    {
        if (!ProcessBinding.SamePath(plan.TargetPath, target.Path) || plan.ClientPid != target.Client.Pid ||
            plan.ClientStartedAt != target.Client.StartedAt || plan.Client != target.ClientFile)
            throw new Rejected(CoreInstallState.PlanChanged);
    }
    private bool MatchesComponent(CoreFileIdentity? file) => file is not null && file.Bytes > 0 &&
        file.Sha256.Equals(component.Sha256, StringComparison.OrdinalIgnoreCase) &&
        (component.ExpectedBytes is null || component.ExpectedBytes == file.Bytes);
    private static bool ValidIdentity(CoreFileIdentity? file) => file is null || (file.Bytes is >= 0 and <= MaximumFileBytes &&
        file.Sha256 is { Length: 64 } && file.Sha256.All(char.IsAsciiHexDigit));

    private static FileStream? OpenExisting(string path, FileShare share)
    {
        PlainPath(path, directory: false, allowMissing: true);
        try { return new(path, FileMode.Open, FileAccess.Read, share, 65536, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return null; }
    }
    private static async Task<CoreFileIdentity?> IdentifyFileAsync(string path, CancellationToken token, bool allowMissing = false)
    {
        using var file = OpenExisting(path, FileShare.Read);
        if (file is null)
        {
            if (allowMissing) return null;
            throw new FileNotFoundException();
        }
        return await IdentifyAsync(file, token);
    }
    private static async Task<CoreFileIdentity> IdentifyAsync(Stream file, CancellationToken token)
    {
        var size = file.Length;
        if (size is < 0 or > MaximumFileBytes) throw new Rejected(CoreInstallState.InvalidCandidate);
        file.Position = 0;
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        if (file.Length != size) throw new IOException("File changed.");
        return new(size, digest);
    }
    private static async Task CopyAsync(Stream source, string destination, CancellationToken token)
    {
        PlainPath(destination, directory: false, allowMissing: true);
        source.Position = 0;
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            65536, FileOptions.Asynchronous);
        await source.CopyToAsync(output, token);
        await output.FlushAsync(token);
        output.Flush(flushToDisk: true);
    }
    private static FileStream LockTarget(string target)
    {
        var path = Path.Combine(Path.GetDirectoryName(target)!, ".monitor-install.lock");
        PlainPath(path, directory: false, allowMissing: true);
        return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private static string BackupPath(string target, Guid id)
    {
        if (id == Guid.Empty) throw new Rejected(CoreInstallState.UnsafePath);
        return Path.Combine(Path.GetDirectoryName(target)!, BackupFolder, id.ToString("N"));
    }
    private static string CreateBackup(string target, Guid id)
    {
        var path = BackupPath(target, id);
        var parent = Path.GetDirectoryName(path)!;
        PlainPath(parent, directory: true, allowMissing: true);
        Directory.CreateDirectory(parent);
        PlainPath(parent, directory: true);
        if (Path.Exists(path)) throw new IOException("Backup already exists.");
        Directory.CreateDirectory(path);
        PlainPath(path, directory: true);
        return path;
    }
    private static void WriteJournal(string target, CoreInstallJournal journal)
    {
        var folder = BackupPath(target, journal.OperationId);
        PlainPath(folder, directory: true);
        var path = Path.Combine(folder, "install.json");
        PlainPath(path, directory: false, allowMissing: true);
        var temporary = Path.Combine(folder, "journal-" + Guid.NewGuid().ToString("N") + ".json");
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(output, journal); output.Flush(flushToDisk: true); }
        PlainPath(folder, directory: true);
        File.Move(temporary, path, overwrite: true);
    }
    private static void TryWriteJournal(string target, CoreInstallJournal journal)
    {
        try { WriteJournal(target, journal); }
        catch (Exception e) when (Expected(e)) { /* Retain the last durable state and all backup files. */ }
    }
    private static void PlainPath(string path, bool directory, bool allowMissing = false)
    {
        if (!Path.IsPathFullyQualified(path) || (OperatingSystem.IsWindows() &&
            (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Skip(2).Contains(':'))))
            throw new Rejected(CoreInstallState.UnsafePath);
        var full = Path.GetFullPath(path);
        var leaf = new FileInfo(full);
        for (FileSystemInfo? item = directory ? new DirectoryInfo(full) : leaf; item is not null;)
        {
            item.Refresh();
            if (item.LinkTarget is not null || (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new Rejected(CoreInstallState.UnsafePath);
            item = item is DirectoryInfo d ? d.Parent : new DirectoryInfo(Path.GetDirectoryName(item.FullName)!);
        }
        if (directory ? Directory.Exists(full) : File.Exists(full)) return;
        if (!allowMissing || Path.Exists(full) || !Directory.Exists(Path.GetDirectoryName(full)))
            throw new Rejected(CoreInstallState.UnsafePath);
    }
    private static bool Expected(Exception e) => e is Rejected or IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or OperationCanceledException or JsonException or
        System.Management.ManagementException or System.ComponentModel.Win32Exception;
    private static CoreInstallState Failure(Exception e) => e switch
    {
        Rejected r => r.State,
        OperationCanceledException => CoreInstallState.Cancelled,
        UnauthorizedAccessException => CoreInstallState.PermissionDenied,
        IOException io when (io.HResult & 0xffff) is 32 or 33 => CoreInstallState.FileBusy,
        _ => CoreInstallState.StorageFailed
    };
}
