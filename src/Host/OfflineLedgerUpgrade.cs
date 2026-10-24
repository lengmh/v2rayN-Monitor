using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Host;

internal enum OfflineLedgerState
{
    Prepared, Upgraded, AuthorizationRequired, InvalidTarget, UnsupportedLedger,
    WriterActive, PlanChanged, MigrationFailed, VerificationRequired
}
internal sealed record OfflineLedgerPlan(string Directory, LedgerUpgradeStep Step, int Version,
    string FileIdentity, string HistoryHash, string? WalHash);
internal sealed record OfflineLedgerResult(OfflineLedgerState State, OfflineLedgerPlan? Plan = null,
    string? BackupPath = null, int? ObservedVersion = null);

/// <summary>One explicitly authorized offline step; never called by normal ledger startup.</summary>
internal static class OfflineLedgerUpgrade
{
    private sealed class Rejected(OfflineLedgerState state) : Exception
    {
        internal OfflineLedgerState State => state;
    }

    // Shared with Program.Main. Keep creation, use and release on one thread, without awaits.
    internal static string DirectoryIdentity(string directory) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())))[..24];
    internal static string DirectoryMutexName(string directory) => @"Local\V2rayNMonitor-" + DirectoryIdentity(directory);

    // Prepared binds the offline input, not a claim that migration/schema validation has passed.
    // The existing migration performs its full read-only schema preflight before creating a backup.
    internal static OfflineLedgerResult Prepare(string directory, LedgerUpgradeStep step)
    {
        try
        {
            var root = CheckedDirectory(directory);
            using var mutex = new Mutex(true, DirectoryMutexName(root), out var created);
            if (!created) return new(OfflineLedgerState.WriterActive);
            try
            {
                using var directoryLease = GuardDirectory(root);
                using var history = OpenHistory(root);
                var version = Version(Path.Combine(root, "history.db"));
                RequireStep(version, step);
                return new(OfflineLedgerState.Prepared, Capture(root, step, version, history));
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Rejected e) { return new(e.State); }
        catch (SqliteException e) { return new(IsBusy(e) ? OfflineLedgerState.WriterActive : OfflineLedgerState.UnsupportedLedger); }
        catch (Exception e) when (StorageError(e)) { return new(OfflineLedgerState.InvalidTarget); }
    }

    internal static OfflineLedgerResult Execute(OfflineLedgerPlan plan, bool authorized, Action? ownershipAcquired = null)
    {
        if (!authorized) return new(OfflineLedgerState.AuthorizationRequired);
        string? backup = null;
        var migrationStarted = false;
        try
        {
            var root = CheckedDirectory(plan.Directory);
            using var mutex = new Mutex(true, DirectoryMutexName(root), out var created);
            if (!created) return new(OfflineLedgerState.WriterActive);
            try
            {
                using var directoryLease = GuardDirectory(root);
                // Deny replacement/deletion of the history file while SQLite performs the step.
                using var history = OpenHistory(root);
                var path = Path.Combine(root, "history.db");
                var current = Capture(root, plan.Step, Version(path), history);
                if (current != plan) return new(OfflineLedgerState.PlanChanged);
                RequireStep(current.Version, plan.Step);
                CheckedDirectory(root);
                CheckPlainPath(path, directory: false);
                backup = Path.Combine(root, "history-before-" + plan.Step + "-" + Guid.NewGuid().ToString("N") + ".db");
                migrationStarted = true;
                var migrated = ScopeLedgerMigration.MigrateOffline(path, backup, plan.Version, () =>
                {
                    // The migration owns the version check and keeps this same connection
                    // exclusive through backup, application and verification.
                    if (Capture(root, plan.Step, plan.Version, history) != plan)
                        throw new Rejected(OfflineLedgerState.PlanChanged);
                    ownershipAcquired?.Invoke();
                });
                var version = migrated.HistoryVersion;
                return new(version == plan.Version + 1 ? OfflineLedgerState.Upgraded : OfflineLedgerState.VerificationRequired,
                    BackupPath: backup, ObservedVersion: version);
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Rejected e) { return new(e.State); }
        catch (Exception e) when (e is SqliteException or InvalidOperationException || StorageError(e))
        {
            var hasBackup = backup is not null && File.Exists(backup);
            // A migration can commit before a subsequent check/open fails. Never claim rollback
            // from an exception alone, and never overwrite newer history with the saved backup.
            int? version = null;
            try { version = Version(Path.Combine(CheckedDirectory(plan.Directory), "history.db")); }
            catch (Exception readError) when (readError is Rejected or SqliteException or InvalidOperationException || StorageError(readError)) { }
            var state = hasBackup
                ? version == plan.Version ? OfflineLedgerState.MigrationFailed : OfflineLedgerState.VerificationRequired
                : e is SqliteException busy && IsBusy(busy) ? OfflineLedgerState.WriterActive
                : migrationStarted && e is InvalidOperationException ? OfflineLedgerState.UnsupportedLedger
                : OfflineLedgerState.InvalidTarget;
            return new(state, BackupPath: hasBackup ? backup : null, ObservedVersion: version);
        }
    }

    private static OfflineLedgerPlan Capture(string root, LedgerUpgradeStep step, int version, FileStream history)
    {
        var identity = Identity(history, Path.Combine(root, "history.db"));
        using (var directory = GuardDirectory(root))
        {
            if (directory is not null)
            {
                if (!GetFileInformationByHandle(directory, out var info))
                    throw new Rejected(OfflineLedgerState.InvalidTarget);
                identity += $"/{info.Volume}:{info.IndexHigh}:{info.IndexLow}";
            }
        }
        history.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(history));
        var walPath = Path.Combine(root, "history.db-wal");
        string? wal = null;
        if (File.Exists(walPath))
        {
            CheckPlainPath(walPath, directory: false);
            using var file = new FileStream(walPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Identity(file, walPath);
            wal = Convert.ToHexString(SHA256.HashData(file));
        }
        return new(root, step, version, identity, hash, wal);
    }

    private static int Version(string path)
    {
        using var db = Open(path, SqliteOpenMode.ReadOnly);
        using var command = db.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false, Cache = SqliteCacheMode.Private, DefaultTimeout = 1
        }.ToString());
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }

    private static void RequireStep(int version, LedgerUpgradeStep step)
    {
        if (!((version == 1 && step == LedgerUpgradeStep.MultiScope) ||
              (version == 2 && step == LedgerUpgradeStep.Probes)))
            throw new Rejected(OfflineLedgerState.UnsupportedLedger);
    }

    private static string CheckedDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(root) || root == Path.GetPathRoot(root))
            throw new Rejected(OfflineLedgerState.InvalidTarget);
        for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            CheckPlainPath(parent.FullName, directory: true);
        // Reject alternate Windows directory spellings (e.g. 8.3 names) that would bypass
        // the existing Monitor's textual mutex identity. Do not silently choose another lock.
        if (OperatingSystem.IsWindows())
        {
            using var handle = GuardDirectory(root);
        }
        return root;
    }

    private static SafeFileHandle? GuardDirectory(string root)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var handle = CreateFileW(root, 0, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (!handle.IsInvalid && GetFileInformationByHandle(handle, out var info) &&
            (info.Attributes & 0x410) == 0x10 && SameCanonicalPath(handle, root)) return handle;
        handle.Dispose();
        throw new Rejected(OfflineLedgerState.InvalidTarget);
    }

    private static FileStream OpenHistory(string root)
    {
        var path = Path.Combine(root, "history.db");
        CheckPlainPath(path, directory: false);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            var side = path + suffix;
            if (!File.Exists(side)) continue;
            CheckPlainPath(side, directory: false);
            using var file = new FileStream(side, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Identity(file, side);
        }
        return new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    }

    private static void CheckPlainPath(string path, bool directory)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes & FileAttributes.Directory) != 0) != directory ||
            (OperatingSystem.IsWindows() && path.AsSpan(Path.GetPathRoot(path)!.Length).Contains(':')))
            throw new Rejected(OfflineLedgerState.InvalidTarget);
    }

    private static string Identity(FileStream file, string path)
    {
        if (!OperatingSystem.IsWindows()) return File.GetCreationTimeUtc(path).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!GetFileInformationByHandle(file.SafeFileHandle, out var info) || info.Links != 1 ||
            (info.Attributes & 0x410) != 0 || !SameCanonicalPath(file.SafeFileHandle, path))
            throw new Rejected(OfflineLedgerState.InvalidTarget);
        return $"{info.Volume}:{info.IndexHigh}:{info.IndexLow}";
    }

    private static bool SameCanonicalPath(SafeFileHandle handle, string path)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return false;
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        return StringComparer.OrdinalIgnoreCase.Equals(actual, path);
    }

    private static bool IsBusy(SqliteException e) => e.SqliteErrorCode is 5 or 6;
    private static bool StorageError(Exception e) => e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeIdentity
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
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeIdentity identity);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
