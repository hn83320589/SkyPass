using System.Globalization;
using Microsoft.Data.Sqlite;
using SkyPass.Core.Storage;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;

namespace SkyPass.Data;

/// <summary>
/// 以 SQLite 保存軌道資料目錄。TLE 以原文保存、讀回時重新解析，因此往返無損。
/// 資料庫版本以 <c>PRAGMA user_version</c> 記錄；版本過新或檔案損毀時回報狀態並拒絕寫入，不擲例外。
/// </summary>
/// <param name="clock">取得匯入時刻用；預設 <see cref="TimeProvider.System"/>。</param>
public sealed class SqliteTleCatalogStore(string databasePath, TimeProvider? clock = null) : ITleCatalogStore
{
    public const int SchemaVersion = 1;

    private const int SqliteCorrupt = 11;
    private const int SqliteNotADatabase = 26;

    private StoreStatus? _status;

    public string DatabasePath { get; } = databasePath;

    public CatalogLoadResult Load()
    {
        var existed = File.Exists(DatabasePath) && new FileInfo(DatabasePath).Length > 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!);
            using var conn = Open();
            var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > SchemaVersion)
            {
                _status = StoreStatus.TooNew;
                return Unavailable(StoreStatus.TooNew,
                    $"資料庫 {DatabasePath} 由較新版本的 SkyPass 建立（資料庫版本 {version}，本程式支援 {SchemaVersion}）。" +
                    "軌道資料目錄將以唯讀空白狀態開啟，請更新程式。");
            }

            if (version == 0)
            {
                CreateSchema(conn);
            }

            var warnings = new List<string>();
            var catalog = ReadCatalog(conn, warnings);
            _status = existed ? StoreStatus.Ok : StoreStatus.Created;
            return new CatalogLoadResult(catalog, _status.Value, null, warnings);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteCorrupt or SqliteNotADatabase)
        {
            _status = StoreStatus.Corrupt;
            return Unavailable(StoreStatus.Corrupt,
                $"資料庫 {DatabasePath} 已損毀或不是 SQLite 資料庫（{ex.Message}）。" +
                "軌道資料目錄將以唯讀空白狀態開啟；請備份或刪除該檔案後重新啟動。");
        }
    }

    public void Save(TleCatalog catalog)
    {
        if (_status is null)
        {
            Load();
        }

        if (_status is not (StoreStatus.Ok or StoreStatus.Created))
        {
            throw new InvalidOperationException($"資料庫狀態為 {_status}，不允許寫入：{DatabasePath}");
        }

        using var conn = Open();
        using var tx = conn.BeginTransaction();
        Execute(conn, tx, "DELETE FROM tle_set; DELETE FROM satellite;");
        var now = (clock ?? TimeProvider.System).GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        foreach (var entry in catalog.Entries)
        {
            using var sat = conn.CreateCommand();
            sat.Transaction = tx;
            sat.CommandText = "INSERT INTO satellite (norad_id, pinned_epoch_day, pinned_epoch_fraction) VALUES ($id, $day, $frac);";
            sat.Parameters.AddWithValue("$id", entry.NoradId);
            sat.Parameters.AddWithValue("$day", (object?)entry.PinnedEpoch?.Day ?? DBNull.Value);
            sat.Parameters.AddWithValue("$frac", (object?)entry.PinnedEpoch?.Fraction ?? DBNull.Value);
            sat.ExecuteNonQuery();

            foreach (var tle in entry.History)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = """
                    INSERT INTO tle_set (norad_id, epoch_day, epoch_fraction, name, line1, line2, imported_at)
                    VALUES ($id, $day, $frac, $name, $l1, $l2, $at);
                    """;
                cmd.Parameters.AddWithValue("$id", entry.NoradId);
                cmd.Parameters.AddWithValue("$day", tle.Epoch.Day);
                cmd.Parameters.AddWithValue("$frac", tle.Epoch.Fraction);
                cmd.Parameters.AddWithValue("$name", tle.Name);
                cmd.Parameters.AddWithValue("$l1", tle.Line1);
                cmd.Parameters.AddWithValue("$l2", tle.Line2);
                cmd.Parameters.AddWithValue("$at", now);
                cmd.ExecuteNonQuery();
            }
        }

        tx.Commit();
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        conn.Open();
        return conn;
    }

    private static CatalogLoadResult Unavailable(StoreStatus status, string message) =>
        new(TleCatalog.Empty, status, message, []);

    private static void CreateSchema(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        Execute(conn, tx, $"""
            CREATE TABLE IF NOT EXISTS satellite (
              norad_id              INTEGER PRIMARY KEY,
              pinned_epoch_day      REAL NULL,
              pinned_epoch_fraction REAL NULL
            );
            CREATE TABLE IF NOT EXISTS tle_set (
              id             INTEGER PRIMARY KEY AUTOINCREMENT,
              norad_id       INTEGER NOT NULL REFERENCES satellite(norad_id) ON DELETE CASCADE,
              epoch_day      REAL NOT NULL,
              epoch_fraction REAL NOT NULL,
              name           TEXT NOT NULL,
              line1          TEXT NOT NULL,
              line2          TEXT NOT NULL,
              imported_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_tle_set_norad ON tle_set(norad_id);
            PRAGMA user_version = {SchemaVersion};
            """);
        tx.Commit();
    }

    private static TleCatalog ReadCatalog(SqliteConnection conn, List<string> warnings)
    {
        var pins = new Dictionary<int, JulianDate?>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT norad_id, pinned_epoch_day, pinned_epoch_fraction FROM satellite;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                pins[r.GetInt32(0)] = r.IsDBNull(1) ? null : JulianDate.FromParts(r.GetDouble(1), r.GetDouble(2));
            }
        }

        var histories = new Dictionary<int, List<TleSet>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, norad_id, name, line1, line2 FROM tle_set ORDER BY norad_id, epoch_day DESC, epoch_fraction DESC;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var parsed = TleParser.Parse($"{r.GetString(2)}\n{r.GetString(3)}\n{r.GetString(4)}");
                if (parsed.Sets.Count != 1)
                {
                    var reason = parsed.Errors.FirstOrDefault()?.Message ?? "未知原因";
                    warnings.Add($"資料庫中第 {r.GetInt64(0)} 筆軌道資料（NORAD {r.GetInt32(1)}）無法解析，已略過：{reason}");
                    continue;
                }

                var norad = r.GetInt32(1);
                if (!histories.TryGetValue(norad, out var list))
                {
                    histories[norad] = list = [];
                }

                list.Add(parsed.Sets[0]);
            }
        }

        var entries = histories.Select(kv =>
        {
            var pinned = pins.GetValueOrDefault(kv.Key);
            if (pinned is { } p && !kv.Value.Any(t => t.Epoch == p))
            {
                warnings.Add($"NORAD {kv.Key} 釘選的軌道資料已不存在，改用最新一組。");
                pinned = null;
            }

            return new CatalogEntry(kv.Key, kv.Value.Take(TleCatalog.MaxHistory).ToList(), pinned);
        });
        return TleCatalog.FromEntries(entries);
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private static void Execute(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
