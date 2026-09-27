using System.Globalization;
using HuorongAce.Core.Configuration;
using HuorongAce.Core.Diagnostics;
using Microsoft.Data.Sqlite;

namespace HuorongAce.Core.Monitoring;

/// <summary>
/// Reads the threats Huorong processed from its quarantine database.
/// </summary>
/// <remarks>
/// <para>
/// Huorong stores processed threats in <c>QuarantineEx.db</c>, table
/// <c>FilesV3_60</c>, where <c>vn</c> holds the virus name and <c>fn</c> the
/// file path. Two properties of that database drive the whole design:
/// </para>
/// <list type="bullet">
///   <item>
///     It runs in WAL mode and is held open by the Huorong service, so it
///     cannot be opened read-only in place. Every read therefore works on a
///     copied snapshot that includes the <c>-wal</c> and <c>-shm</c> files.
///   </item>
///   <item>
///     A single incident produces several rows (one per scan / extraction).
///     Two cursors plus a de-duplication window keep that from turning into a
///     stream of pop-ups.
///   </item>
/// </list>
/// <para>
/// C# counterpart of the Go <c>logDB</c> type. The behaviour is identical; the
/// main structural change is that the reader is disposable and every cursor is
/// guarded by a monitor instead of a <c>sync.Mutex</c>.
/// </para>
/// </remarks>
public sealed class HuorongQuarantineReader : IDisposable
{
    /// <summary>Preferred table name in the quarantine database.</summary>
    public const string QuarantineTableName = "FilesV3_60";

    /// <summary>
    /// How long the same threat stays suppressed.
    /// </summary>
    /// <remarks>
    /// Three minutes covers the bursts Huorong writes while a sample is being
    /// unzipped and rescanned, while still allowing a deliberate re-test
    /// shortly afterwards.
    /// </remarks>
    private static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(3);

    private readonly string _databasePath;
    private readonly AppConfig _config;
    private readonly IAppLog _log;
    private readonly object _sync = new();

    private string _tableName = QuarantineTableName;
    private List<string> _columns = new();
    private long _lastRowId;
    private long _watermarkTimestamp;
    private readonly Dictionary<string, DateTimeOffset> _recentlyFired = new(StringComparer.Ordinal);
    private bool _isOpen;

    public HuorongQuarantineReader(string databasePath, AppConfig config, IAppLog? log = null)
    {
        _databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? new NullAppLog();
    }

    /// <summary>Resolved table name, useful for diagnostics.</summary>
    public string TableName
    {
        get
        {
            lock (_sync)
            {
                return _tableName;
            }
        }
    }

    /// <summary>
    /// Opens the database, resolves the threat table and parks both cursors at
    /// the newest row so historical records never alert on start-up.
    /// </summary>
    /// <exception cref="FileNotFoundException">The configured database is missing.</exception>
    /// <exception cref="InvalidOperationException">The database has no readable table.</exception>
    public void Open()
    {
        if (!File.Exists(_databasePath))
        {
            throw new FileNotFoundException("日志文件不存在: " + _databasePath, _databasePath);
        }

        using var snapshot = DatabaseSnapshot.Create(_databasePath);
        using var connection = snapshot.OpenConnection();

        var table = ResolveTableName(connection)
                    ?? throw new InvalidOperationException("未找到日志表");
        var columns = ReadColumns(connection, table);

        lock (_sync)
        {
            _tableName = table;
            _columns = columns;
            _lastRowId = QueryMaxRowId(connection, table);
            _watermarkTimestamp = QueryMaxTimestamp(connection, table, columns);
            _isOpen = true;
            _log.Info($"已打开 {_databasePath} (表 {table}, 列 [{string.Join(",", columns)}], " +
                      $"起始游标 rowid={_lastRowId} ts={_watermarkTimestamp})");
        }
    }

    /// <summary>
    /// Returns threats recorded since the previous call and advances the cursors.
    /// </summary>
    public IReadOnlyList<ThreatInfo> Poll()
    {
        lock (_sync)
        {
            if (!_isOpen)
            {
                return Array.Empty<ThreatInfo>();
            }
        }

        var results = new List<ThreatInfo>();

        using (var snapshot = DatabaseSnapshot.Create(_databasePath))
        using (var connection = snapshot.OpenConnection())
        {
            string table;
            long cursorRowId;
            long watermark;

            lock (_sync)
            {
                table = _tableName;
                cursorRowId = _lastRowId;
                watermark = _watermarkTimestamp;
            }

            var maxRowId = cursorRowId;
            var maxTimestamp = watermark;

            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT rowid, * FROM \"{table}\" WHERE rowid > $cursor ORDER BY rowid ASC";
            command.Parameters.AddWithValue("$cursor", cursorRowId);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = ReadRow(reader);
                var rowId = row.GetValueOrDefault("rowid").ParseLong();
                if (rowId > maxRowId)
                {
                    maxRowId = rowId;
                }

                var timestamp = ParseTimestamp(row.GetValueOrDefault("ts"));
                if (timestamp > maxTimestamp)
                {
                    maxTimestamp = timestamp;
                }

                // Time cursor: anything at or before the watermark has already
                // been accounted for, including every duplicate of an older
                // incident.
                if (watermark > 0 && timestamp > 0 && timestamp <= watermark)
                {
                    continue;
                }

                if (!IsThreat(row))
                {
                    continue;
                }

                // De-duplication state is shared, so the check and the update
                // have to happen together.
                var key = DedupKey(row);
                lock (_sync)
                {
                    if (_recentlyFired.TryGetValue(key, out var firedAt) &&
                        DateTimeOffset.UtcNow - firedAt < DedupWindow)
                    {
                        continue;
                    }

                    _recentlyFired[key] = DateTimeOffset.UtcNow;
                    PruneRecentlyFired();
                }

                results.Add(ToThreatInfo(row, timestamp));
            }

            lock (_sync)
            {
                _lastRowId = maxRowId;
                _watermarkTimestamp = maxTimestamp;
            }
        }

        return results;
    }

    /// <summary>
    /// Decides whether a row represents a threat.
    /// </summary>
    /// <remarks>
    /// Rows carrying a non-empty <c>vn</c> come from the quarantine database,
    /// which only ever holds processed threats, so those always count. Keyword
    /// filtering applies only to custom databases, where running it would cause
    /// false negatives (a name like <c>Worm/Generic</c> never matches a keyword
    /// list).
    /// </remarks>
    private bool IsThreat(IReadOnlyDictionary<string, string> row)
    {
        if (!string.IsNullOrWhiteSpace(row.GetValueOrDefault("vn")))
        {
            return true;
        }

        var all = string.Join(' ', row.Values);
        return _config.MatchesKeyword(all);
    }

    private static string DedupKey(IReadOnlyDictionary<string, string> row)
    {
        var virusName = row.GetValueOrDefault("vn")?.Trim();
        if (!string.IsNullOrEmpty(virusName))
        {
            return virusName;
        }

        return string.Join('|', row.Values);
    }

    private void PruneRecentlyFired()
    {
        var cutoff = DateTimeOffset.UtcNow - DedupWindow;
        foreach (var key in _recentlyFired.Where(pair => pair.Value < cutoff).Select(pair => pair.Key).ToList())
        {
            _recentlyFired.Remove(key);
        }
    }

    private static ThreatInfo ToThreatInfo(IReadOnlyDictionary<string, string> row, long timestamp)
    {
        var name = row.GetValueOrDefault("vn")?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            name = "未知威胁";
        }

        var detail = row.GetValueOrDefault("fn")?.Trim() ?? string.Empty;
        if (detail.Length != 0)
        {
            detail = "路径: " + detail;
        }

        var when = timestamp > 0
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime()
            : DateTimeOffset.Now;

        return new ThreatInfo(name, detail, when);
    }

    private static Dictionary<string, string> ReadRow(SqliteDataReader reader)
    {
        var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = reader.GetName(i);
            row[name] = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i).ToString() ?? string.Empty;
        }

        return row;
    }

    private static long ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : 0;
    }

    // ---------------------------------------------------------------------
    // Schema discovery
    // ---------------------------------------------------------------------

    private static string? ResolveTableName(SqliteConnection connection)
    {
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        string[] exact = [QuarantineTableName, "FilesV3", "HrLogV3", "HrLog"];
        foreach (var wanted in exact)
        {
            var match = tables.FirstOrDefault(t => string.Equals(t, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        var prefix = tables.FirstOrDefault(t => t.StartsWith("filesv3", StringComparison.OrdinalIgnoreCase));
        if (prefix is not null)
        {
            return prefix;
        }

        return tables.FirstOrDefault(t => !string.Equals(t, "sqlite_sequence", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ReadColumns(SqliteConnection connection, string table)
    {
        var columns = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info(\"{table}\")";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static long QueryMaxRowId(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MAX(rowid) FROM \"{table}\"";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Newest timestamp already in the table. Returns 0 when the table has no
    /// usable <c>ts</c> column, which disables the time cursor and leaves the
    /// rowid cursor in charge.
    /// </summary>
    private static long QueryMaxTimestamp(SqliteConnection connection, string table, List<string> columns)
    {
        if (!columns.Any(c => string.Equals(c, "ts", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT MAX(\"ts\") FROM \"{table}\"";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        // No handle is kept open between polls: each read opens and closes its
        // own snapshot. Dispose exists so callers can use a `using` block and
        // so future resources have an obvious home.
    }

    // ---------------------------------------------------------------------
    // Test hooks
    // ---------------------------------------------------------------------
    // The cursors are private by design — nothing in the app should move them
    // backwards. The tests do need to, in order to prove that the time cursor
    // really suppresses history, so the reader exposes a few internal helpers
    // that only the test assembly can see.

    /// <summary>Rewinds the rowid cursor so the next poll replays every row.</summary>
    internal void RewindRowIdCursor()
    {
        lock (_sync)
        {
            _lastRowId = 0;
        }
    }

    /// <summary>Rewinds the time cursor so the next poll treats all rows as new.</summary>
    internal void RewindTimeCursor()
    {
        lock (_sync)
        {
            _watermarkTimestamp = 0;
        }
    }

    /// <summary>Drops the de-duplication history.</summary>
    internal void ClearDedupHistory()
    {
        lock (_sync)
        {
            _recentlyFired.Clear();
        }
    }

    /// <summary>Current cursor values, used for diagnostics output in tests.</summary>
    internal (long RowId, long Timestamp) Cursors
    {
        get
        {
            lock (_sync)
            {
                return (_lastRowId, _watermarkTimestamp);
            }
        }
    }

    /// <summary>
    /// A private copy of a WAL database, safe to read while another process
    /// holds the original.
    /// </summary>
    private sealed class DatabaseSnapshot : IDisposable
    {
        private readonly string _directory;

        private DatabaseSnapshot(string directory) => _directory = directory;

        public static DatabaseSnapshot Create(string sourcePath)
        {
            var directory = Directory.CreateTempSubdirectory("huorong-ace-snap-");
            var target = Path.Combine(directory.FullName, "snap.db");

            File.Copy(sourcePath, target, overwrite: true);
            TryCopy(sourcePath + "-wal", target + "-wal");
            TryCopy(sourcePath + "-shm", target + "-shm");

            return new DatabaseSnapshot(directory.FullName);
        }

        public SqliteConnection OpenConnection()
        {
            var connection = new SqliteConnection($"Data Source={Path.Combine(_directory, "snap.db")};Default Timeout=3");
            connection.Open();
            return connection;
        }

        private static void TryCopy(string source, string target)
        {
            try
            {
                if (File.Exists(source))
                {
                    File.Copy(source, target, overwrite: true);
                }
            }
            catch (IOException)
            {
                // Missing or locked side files simply mean slightly older data.
            }
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: temp files are cleaned up by the OS eventually.
            }
        }
    }
}

internal static class RowExtensions
{
    public static string GetValueOrDefault(this IReadOnlyDictionary<string, string> row, string key) =>
        row.TryGetValue(key, out var value) ? value : string.Empty;

    public static long ParseLong(this string? raw) =>
        long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
}
