using Microsoft.Data.Sqlite;
using SkyPass.Core.Coordinates;
using SkyPass.Core.Storage;
using SkyPass.Core.Tle;
using SkyPass.Data;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Data;

/// <summary>驗收 #18：目錄與歷史往返無損、設定檔損毀用預設值並提示、資料庫過新或損毀顯示明確訊息且不崩潰。</summary>
public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SkyPassTests", Guid.NewGuid().ToString("N"));

    public StorageTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string DbPath => Path.Combine(_dir, "skypass.db");

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private static TleCatalog SampleCatalog()
    {
        var sats = PassScenario.Satellites.Take(14).ToList();
        var history = sats.SelectMany(s => Enumerable.Range(0, 7).Select(k => s with { EpochDayOfYear = s.EpochDayOfYear - k * 0.5 }))
            .Select(s => s with { Line1 = TleFormatter.FormatLines(s).Line1, Line2 = TleFormatter.FormatLines(s).Line2 });
        var (catalog, _) = TleCatalog.Empty.Import(history);
        var pinnedEntry = catalog.Entries[3];
        return catalog.Pin(pinnedEntry.NoradId, pinnedEntry.History[2].Epoch);
    }

    [Fact]
    public void New_database_is_created_empty()
    {
        var result = new SqliteTleCatalogStore(DbPath).Load();

        Assert.Equal(StoreStatus.Created, result.Status);
        Assert.Empty(result.Catalog.Entries);
        Assert.True(result.IsWritable);
        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public void Catalog_with_history_and_pin_roundtrips_losslessly()
    {
        var catalog = SampleCatalog();
        var store = new SqliteTleCatalogStore(DbPath);
        store.Load();

        store.Save(catalog);
        var loaded = new SqliteTleCatalogStore(DbPath).Load();

        Assert.Equal(StoreStatus.Ok, loaded.Status);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(catalog.Entries.Count, loaded.Catalog.Entries.Count);
        for (var i = 0; i < catalog.Entries.Count; i++)
        {
            var expected = catalog.Entries[i];
            var actual = loaded.Catalog.Entries[i];
            Assert.Equal(expected.NoradId, actual.NoradId);
            Assert.Equal(expected.PinnedEpoch, actual.PinnedEpoch);
            Assert.Equal(expected.History, actual.History);
            Assert.Equal(expected.Active, actual.Active);
        }
    }

    [Fact]
    public void Saving_again_replaces_previous_content()
    {
        var store = new SqliteTleCatalogStore(DbPath);
        store.Load();
        store.Save(SampleCatalog());

        store.Save(TleCatalog.Empty.Import([PassScenario.Satellites[0]]).Catalog);

        Assert.Single(new SqliteTleCatalogStore(DbPath).Load().Catalog.Entries);
    }

    [Fact]
    public void Database_from_newer_version_is_reported_and_read_only()
    {
        using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            cmd.ExecuteNonQuery();
        }

        var store = new SqliteTleCatalogStore(DbPath);
        var result = store.Load();

        Assert.Equal(StoreStatus.TooNew, result.Status);
        Assert.False(result.IsWritable);
        Assert.Contains("版本", result.Message);
        Assert.Empty(result.Catalog.Entries);
        Assert.Throws<InvalidOperationException>(() => store.Save(TleCatalog.Empty));
    }

    [Fact]
    public void Corrupt_database_file_is_reported_without_crashing()
    {
        File.WriteAllText(DbPath, "這不是 SQLite 資料庫，只是一段文字。".PadRight(4096, 'x'));

        var store = new SqliteTleCatalogStore(DbPath);
        var result = store.Load();

        Assert.Equal(StoreStatus.Corrupt, result.Status);
        Assert.Contains("損毀", result.Message);
        Assert.Empty(result.Catalog.Entries);
        Assert.Throws<InvalidOperationException>(() => store.Save(TleCatalog.Empty));
    }

    [Fact]
    public void Unparseable_stored_tle_is_skipped_with_warning()
    {
        var store = new SqliteTleCatalogStore(DbPath);
        store.Load();
        store.Save(SampleCatalog());
        using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE tle_set SET line2 = 'garbage' WHERE id = (SELECT MIN(id) FROM tle_set);";
            cmd.ExecuteNonQuery();
        }

        var result = new SqliteTleCatalogStore(DbPath).Load();

        Assert.Equal(StoreStatus.Ok, result.Status);
        Assert.Single(result.Warnings);
        Assert.Equal(SampleCatalog().Entries.Count, result.Catalog.Entries.Count);
    }

    [Fact]
    public void Missing_settings_file_gives_defaults_without_warning()
    {
        var result = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(AppSettings.Default.MaskDeg, result.Settings.MaskDeg);
        Assert.Equal(TimeSpan.FromHours(8), result.Settings.DisplayUtcOffset);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Settings_roundtrip()
    {
        var settings = AppSettings.Default with
        {
            Sites =
            [
                new NamedSite("屋頂", new Geodetic(25.0, 121.5, 0.05)),
                new NamedSite("山上", new Geodetic(24.1, 120.9, 2.1)),
            ],
            SelectedSiteIndex = 1,
            MaskDeg = 5.5,
            DisplayUtcOffset = TimeSpan.FromHours(-3.5),
            CsvUtcOffset = TimeSpan.Zero,
            DownlinkHz = 437.8e6,
            PredictionDays = 7,
        };
        var store = new JsonSettingsStore(SettingsPath);

        store.Save(settings);
        var loaded = store.Load();

        Assert.Empty(loaded.Warnings);
        Assert.Equal(settings.Sites, loaded.Settings.Sites);
        Assert.Equal(settings with { Sites = loaded.Settings.Sites }, loaded.Settings);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"MaskDeg\": \"ten\"}")]
    public void Corrupt_settings_file_falls_back_to_defaults_with_warning(string content)
    {
        File.WriteAllText(SettingsPath, content);

        var result = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(AppSettings.Default.MaskDeg, result.Settings.MaskDeg);
        Assert.Single(result.Settings.Sites);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, w => w.Contains("設定檔"));
    }

    [Fact]
    public void Invalid_setting_values_are_sanitized_individually()
    {
        File.WriteAllText(SettingsPath, """
            {
              "Sites": [
                { "Name": "A", "Location": { "LatitudeDeg": 95, "LongitudeDeg": 0, "HeightKm": 0 } },
                { "Name": "B", "Location": { "LatitudeDeg": 10, "LongitudeDeg": 20, "HeightKm": 0 } },
                { "Name": "C", "Location": { "LatitudeDeg": 11, "LongitudeDeg": 20, "HeightKm": 0 } },
                { "Name": "D", "Location": { "LatitudeDeg": 12, "LongitudeDeg": 20, "HeightKm": 0 } },
                { "Name": "E", "Location": { "LatitudeDeg": 13, "LongitudeDeg": 20, "HeightKm": 0 } },
                { "Name": "F", "Location": { "LatitudeDeg": 14, "LongitudeDeg": 20, "HeightKm": 0 } },
                { "Name": "G", "Location": { "LatitudeDeg": 15, "LongitudeDeg": 20, "HeightKm": 0 } }
              ],
              "SelectedSiteIndex": 9,
              "MaskDeg": 95,
              "DisplayUtcOffset": "05:30:00",
              "CsvUtcOffset": "20:00:00"
            }
            """);

        var result = new JsonSettingsStore(SettingsPath).Load();

        Assert.Equal(["B", "C", "D", "E", "F"], result.Settings.Sites.Select(s => s.Name));
        Assert.Equal(0, result.Settings.SelectedSiteIndex);
        Assert.Equal(10.0, result.Settings.MaskDeg);
        Assert.Equal(TimeSpan.FromHours(5.5), result.Settings.DisplayUtcOffset);
        Assert.Equal(TimeSpan.FromHours(8), result.Settings.CsvUtcOffset);
        Assert.True(result.Warnings.Count >= 4);
    }

    [Fact]
    public void Settings_save_is_atomic_and_creates_directory()
    {
        var nested = Path.Combine(_dir, "a", "b", "settings.json");
        var store = new JsonSettingsStore(nested);

        store.Save(AppSettings.Default with { MaskDeg = 3 });

        Assert.Equal(3, store.Load().Settings.MaskDeg);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(nested)!, "*.tmp"));
    }

    [Fact]
    public void Default_paths_are_under_local_app_data()
    {
        var paths = AppPaths.Default;

        Assert.EndsWith(Path.Combine("SkyPass", "skypass.db"), paths.DatabasePath);
        Assert.EndsWith(Path.Combine("SkyPass", "settings.json"), paths.SettingsPath);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), paths.DatabasePath);
    }
}
