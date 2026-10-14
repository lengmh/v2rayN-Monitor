using System.Globalization;
using Microsoft.Data.Sqlite;

namespace V2rayNMonitor.Core;

public sealed record ScopeMigrationResult(string HistoryPath, string BackupPath, long LegacyScopes);

/// <summary>Explicit offline migration. The caller must stop all writers and obtain authorization first.</summary>
public static class ScopeLedgerMigration
{
    private static readonly Dictionary<string, string> Columns = new()
    {
        ["nodes"] = "node_key:TEXT:1|instance_id:TEXT:0|node_id:TEXT:0|name:TEXT:0",
        ["cursors"] = "instance_id:TEXT:1|node_key:TEXT:0|session_id:TEXT:0|generation:TEXT:0|at_ticks:INTEGER:0|upload:INTEGER:0|download:INTEGER:0|interrupted:INTEGER:0",
        ["minutes"] = "start_ticks:INTEGER:1|node_key:TEXT:2|upload:INTEGER:0|download:INTEGER:0|approximate:INTEGER:0|synthetic:INTEGER:0",
        ["unallocated"] = "id:INTEGER:1|start_ticks:INTEGER:0|end_ticks:INTEGER:0|node_key:TEXT:0|upload:INTEGER:0|download:INTEGER:0|quality:TEXT:0",
        ["gaps"] = "id:INTEGER:1|start_ticks:INTEGER:0|end_ticks:INTEGER:0|node_key:TEXT:0|reason:TEXT:0|is_open:INTEGER:0"
    };
    private static readonly Dictionary<string, string> Mapping = new()
    {
        ["nodes"] = """
            SELECT n.node_key,n.instance_id,n.node_id,s.name FROM measurement_scopes s JOIN nodes n ON n.node_key=s.node_key
            WHERE s.basis='legacy_proxy' AND s.identity_kind='legacy_node' AND s.identity_value=s.node_key
                AND s.instance_id=n.instance_id AND s.outbound_tag IS NULL ORDER BY n.node_key
            """,
        ["cursors"] = """
            SELECT s.instance_id,s.node_key,c.session_id,c.generation,c.at_ticks,c.upload,c.download,c.interrupted
            FROM scope_cursors c JOIN measurement_scopes s USING(scope_id) ORDER BY s.instance_id
            """,
        ["minutes"] = """
            SELECT m.start_ticks,s.node_key,m.upload,m.download,m.approximate,m.synthetic
            FROM scope_minutes m JOIN measurement_scopes s USING(scope_id) ORDER BY m.start_ticks,s.node_key
            """,
        ["unallocated"] = """
            SELECT u.id,u.start_ticks,u.end_ticks,s.node_key,u.upload,u.download,u.quality
            FROM scope_unallocated u JOIN measurement_scopes s USING(scope_id) ORDER BY u.id
            """,
        ["gaps"] = """
            SELECT g.id,g.start_ticks,g.end_ticks,s.node_key,g.reason,g.is_open
            FROM scope_gaps g LEFT JOIN measurement_scopes s USING(scope_id) ORDER BY g.id
            """
    };
    private static readonly Dictionary<string, string> Ordering = new()
    {
        ["nodes"] = "node_key", ["cursors"] = "instance_id", ["minutes"] = "start_ticks,node_key",
        ["unallocated"] = "id", ["gaps"] = "id"
    };

    public static ScopeMigrationResult Migrate(string historyPath, string backupPath)
    {
        var history = CheckedPath(historyPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        var backup = Path.GetFullPath(backupPath);
        if (StringComparer.OrdinalIgnoreCase.Equals(history, backup))
            throw new ArgumentException("History and backup must be different new files.");
        // Preflight is read-only, before creating a backup or changing any persistent setting.
        using (var source = Open(history, SqliteOpenMode.ReadOnly))
        {
            Preflight(source, null);
            CheckedParents(backup);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            CheckedParents(backup);
            using (new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using var destination = Open(backup, SqliteOpenMode.ReadWrite);
            source.BackupDatabase(destination);
        }
        using var snapshot = Open(backup, SqliteOpenMode.ReadOnly);
        Preflight(snapshot, null);
        using var target = Open(history, SqliteOpenMode.ReadWrite);
        // Exclusive locking is connection-local; competing readers/writers must leave before DDL.
        Execute(target, null, "PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA locking_mode=EXCLUSIVE;");
        Apply(target, snapshot, ScopeLedger.SchemaSql);
        return new(history, backup, Scalar(target, null, "SELECT COUNT(*) FROM measurement_scopes;"));
    }

    // SQL is not a public input. Keeping the transactional application separate also allows
    // failure regressions to exercise the real rollback/verification path without real databases.
    internal static void Apply(SqliteConnection target, SqliteConnection snapshot, string sql)
    {
        using var transaction = target.BeginTransaction();
        Preflight(target, transaction);
        CompareOriginal(target, transaction, snapshot);
        Execute(target, transaction, sql);
        CompareOriginal(target, transaction, snapshot);
        foreach (var table in Columns.Keys)
            Compare(target, transaction, Mapping[table], snapshot, $"SELECT * FROM {table} ORDER BY {Ordering[table]};");
        Compare(target, transaction, "SELECT instance_id,at_ticks,fingerprint FROM scope_batches ORDER BY instance_id;",
            snapshot, "SELECT instance_id,MAX(at_ticks),NULL FROM cursors GROUP BY instance_id ORDER BY instance_id;");
        Require(Scalar(target, transaction, "PRAGMA user_version;") == 2, "Migration version was not committed with its schema.");
        Require(Scalar(target, transaction, """
            SELECT (SELECT COUNT(*) FROM measurement_scopes)-(SELECT COUNT(*) FROM nodes)
                +(SELECT COUNT(*) FROM scope_cursors WHERE continuity_valid<>0)
                +(SELECT COUNT(*) FROM scope_gaps WHERE origin<>'legacy');
            """) == 0, "Incomplete mapping or unsafe migrated continuity.");
        CheckIntegrity(target, transaction);
        transaction.Commit();
    }

    private static void Preflight(SqliteConnection db, SqliteTransaction? transaction)
    {
        Require(Scalar(db, transaction, "PRAGMA user_version;") == 1, "Only a supported v1 history ledger can be migrated.");
        using (var command = Command(db, transaction,
            "SELECT name,type FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' AND type<>'index' ORDER BY name;"))
        using (var reader = command.ExecuteReader())
        {
            var names = new List<string>();
            while (reader.Read())
            {
                Require(reader.GetString(1) == "table", "Unsupported history schema objects.");
                names.Add(reader.GetString(0));
            }
            Require(names.SequenceEqual(Columns.Keys.Order(StringComparer.Ordinal)), "Not an unmodified v1 history database.");
        }
        foreach (var (table, expected) in Columns)
        {
            using var command = Command(db, transaction, $"PRAGMA table_info({table});");
            using var reader = command.ExecuteReader();
            var signature = new List<string>();
            while (reader.Read()) signature.Add($"{reader.GetString(1)}:{reader.GetString(2)}:{reader.GetInt64(5)}");
            Require(string.Join('|', signature) == expected, "Unsupported v1 column layout.");
        }
        CheckIntegrity(db, transaction);
        foreach (var table in new[] { "cursors", "minutes", "unallocated" })
            Require(Scalar(db, transaction, $"""
                SELECT COUNT(*) FROM {table} WHERE typeof(upload)<>'integer' OR typeof(download)<>'integer'
                    OR upload<0 OR download<0;
                """) == 0, "Illegal historical integer bytes.");
        Require(Scalar(db, transaction, """
            SELECT COUNT(*) FROM cursors c LEFT JOIN nodes n ON c.node_key=n.node_key
            WHERE n.node_key IS NULL OR c.instance_id<>n.instance_id;
            """) == 0, "Historical cursor instance association is invalid.");
        foreach (var table in new[] { "minutes", "unallocated", "gaps" })
            Require(Scalar(db, transaction, $"""
                SELECT COUNT(*) FROM {table} h LEFT JOIN nodes n ON h.node_key=n.node_key
                WHERE h.node_key IS NOT NULL AND n.node_key IS NULL;
                """) == 0, "Orphan historical association.");
        foreach (var table in Columns.Keys)
        {
            foreach (var column in Columns[table].Split('|').Select(x => x.Split(':')[0]))
            {
                if (column.EndsWith("ticks", StringComparison.Ordinal))
                    Require(Scalar(db, transaction, $"""
                        SELECT COUNT(*) FROM {table} WHERE typeof({column})<>'integer'
                            OR {column}<0 OR {column}>3155378975999999999;
                        """) == 0, "Illegal historical timestamp.");
                else if (column is "interrupted" or "approximate" or "synthetic" or "is_open")
                    Require(Scalar(db, transaction, $"SELECT COUNT(*) FROM {table} WHERE typeof({column})<>'integer' OR {column} NOT IN (0,1);") == 0,
                        "Illegal historical flag.");
                else if (Columns[table].Contains(column + ":TEXT", StringComparison.Ordinal))
                    Require(Scalar(db, transaction, $"SELECT COUNT(*) FROM {table} WHERE typeof({column})<>'text'" +
                        (table == "gaps" && column == "node_key" ? $" AND {column} IS NOT NULL;" : ";")) == 0,
                        "Illegal historical text field.");
            }
            if (table is "unallocated" or "gaps")
                Require(Scalar(db, transaction, $"SELECT COUNT(*) FROM {table} WHERE end_ticks<start_ticks;") == 0, "Inverted historical interval.");
        }
        Require(Scalar(db, transaction, "SELECT COUNT(*) FROM nodes WHERE length(node_key)=0 OR length(instance_id)=0 OR length(node_id)=0;") == 0,
            "Historical identity is empty.");
    }

    private static void CompareOriginal(SqliteConnection target, SqliteTransaction transaction, SqliteConnection snapshot)
    {
        Compare(target, transaction, "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE tbl_name IN ('nodes','cursors','minutes','unallocated','gaps') ORDER BY type,name;",
            snapshot, "SELECT type,name,tbl_name,sql FROM sqlite_master WHERE tbl_name IN ('nodes','cursors','minutes','unallocated','gaps') ORDER BY type,name;");
        foreach (var table in Columns.Keys)
            Compare(target, transaction, $"SELECT * FROM {table} ORDER BY {Ordering[table]};", snapshot,
                $"SELECT * FROM {table} ORDER BY {Ordering[table]};");
    }

    private static void Compare(SqliteConnection target, SqliteTransaction? transaction, string sql,
        SqliteConnection snapshot, string expectedSql)
    {
        using var actualCommand = Command(target, transaction, sql);
        using var expectedCommand = Command(snapshot, null, expectedSql);
        using var actual = actualCommand.ExecuteReader();
        using var expected = expectedCommand.ExecuteReader();
        Require(actual.FieldCount == expected.FieldCount, "History mapping columns changed.");
        while (actual.Read())
        {
            Require(expected.Read(), "Unexpected mapped history row.");
            for (var i = 0; i < actual.FieldCount; i++)
                Require(Equals(actual.GetValue(i), expected.GetValue(i)), "History snapshot or mapped value changed.");
        }
        Require(!expected.Read(), "Historical row was omitted.");
    }
    private static void CheckIntegrity(SqliteConnection db, SqliteTransaction? transaction)
    {
        using (var command = Command(db, transaction, "PRAGMA integrity_check;"))
        using (var reader = command.ExecuteReader())
            Require(reader.Read() && reader.GetString(0) == "ok" && !reader.Read(), "History integrity check failed.");
        using var foreign = Command(db, transaction, "PRAGMA foreign_key_check;");
        using var foreignReader = foreign.ExecuteReader();
        Require(!foreignReader.Read(), "History foreign key check failed.");
    }
    private static string CheckedPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var result = Path.GetFullPath(path);
        if (!File.Exists(result) || (File.GetAttributes(result) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("An existing, non-linked history file is required.");
        CheckedParents(result);
        return result;
    }
    private static void CheckedParents(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Migration paths must not traverse directory links.");
    }
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = mode, Pooling = false, Cache = SqliteCacheMode.Private }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? transaction, string sql)
    {
        var command = db.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command;
    }
    private static long Scalar(SqliteConnection db, SqliteTransaction? transaction, string sql)
    {
        using var command = Command(db, transaction, sql); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
    private static void Execute(SqliteConnection db, SqliteTransaction? transaction, string sql)
    {
        using var command = Command(db, transaction, sql); command.ExecuteNonQuery();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
