using System.Text;
using SkyPass.Core.Coordinates;
using SkyPass.Core.Storage;
using SkyPass.Core.Tle;
using SkyPass.Presenters;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Presenters;

public class SettingsPresenterTests
{
    [Fact]
    public void Loads_settings_and_reports_warnings_to_diagnostics()
    {
        var diagnostics = new DiagnosticsPresenter();
        var p = new SettingsPresenter(new InMemorySettingsStore(warnings: ["設定檔損毀"]), diagnostics);

        p.Load();

        Assert.Equal(10, p.Current.MaskDeg);
        Assert.Contains(diagnostics.Items, d => d.Message == "設定檔損毀" && d.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void At_most_five_sites()
    {
        var store = new InMemorySettingsStore();
        var p = new SettingsPresenter(store, new DiagnosticsPresenter());
        p.Load();

        for (var i = 0; i < 4; i++)
        {
            Assert.True(p.AddSite($"站 {i}", 20 + i, 120, 50));
        }

        Assert.False(p.AddSite("第六個", 10, 10, 0));
        Assert.Contains("最多 5 個", p.LastError);
        Assert.Equal(5, store.Saved.Sites.Count);
    }

    [Fact]
    public void Height_is_entered_in_metres_and_stored_in_km()
    {
        var store = new InMemorySettingsStore();
        var p = new SettingsPresenter(store, new DiagnosticsPresenter());
        p.Load();

        p.AddSite("山頂", 23.47, 120.96, 3952);

        Assert.Equal(3.952, store.Saved.Sites[^1].Location.HeightKm, 12);
    }

    [Theory]
    [InlineData(95, 0, 0)]
    [InlineData(0, 200, 0)]
    [InlineData(0, 0, 20000)]
    public void Invalid_site_is_rejected_with_message(double lat, double lon, double heightM)
    {
        var p = new SettingsPresenter(new InMemorySettingsStore(), new DiagnosticsPresenter());
        p.Load();

        Assert.False(p.AddSite("x", lat, lon, heightM));
        Assert.NotNull(p.LastError);
    }

    [Fact]
    public void Mask_offset_and_selection_changes_are_validated_and_saved()
    {
        var store = new InMemorySettingsStore();
        var p = new SettingsPresenter(store, new DiagnosticsPresenter());
        p.Load();
        var version = p.Version;

        Assert.True(p.SetMask(5));
        Assert.False(p.SetMask(-1));
        Assert.True(p.SetDisplayOffset(TimeSpan.FromHours(-3.5)));
        Assert.False(p.SetDisplayOffset(TimeSpan.FromMinutes(7)));
        Assert.True(p.AddSite("B", 1, 2, 3));
        Assert.True(p.SelectSite(1));
        Assert.False(p.SelectSite(7));

        Assert.Equal(5, store.Saved.MaskDeg);
        Assert.Equal(TimeSpan.FromHours(-3.5), store.Saved.DisplayUtcOffset);
        Assert.Equal(1, store.Saved.SelectedSiteIndex);
        Assert.True(p.Version > version);
    }

    [Fact]
    public void Removing_selected_site_keeps_a_valid_selection_and_last_site_cannot_be_removed()
    {
        var store = new InMemorySettingsStore();
        var p = new SettingsPresenter(store, new DiagnosticsPresenter());
        p.Load();
        p.AddSite("B", 1, 2, 3);
        p.SelectSite(1);

        Assert.True(p.RemoveSite(1));
        Assert.Equal(0, p.Current.SelectedSiteIndex);
        Assert.False(p.RemoveSite(0));
    }
}

public class CatalogPresenterTests
{
    private static (CatalogPresenter Presenter, InMemoryCatalogStore Store, DiagnosticsPresenter Diagnostics, FakeTimeProvider Clock) Create(StoreStatus status = StoreStatus.Ok)
    {
        var store = new InMemoryCatalogStore(status);
        var diagnostics = new DiagnosticsPresenter();
        var clock = new FakeTimeProvider(SampleTle.SnapshotTime);
        var p = new CatalogPresenter(store, diagnostics, clock);
        p.Load();
        return (p, store, diagnostics, clock);
    }

    [Fact]
    public async Task Import_reports_errors_with_line_numbers_and_imports_the_rest()
    {
        var (p, store, diagnostics, _) = Create();
        var text = "BROKEN\n1 25544U 98067A   08264.51782528 -.00002182  00000-0 -11606-4 0  2920\n" +
                   "2 25544  51.6416 247.4627 0006703 130.5360 325.0288 15.72125391563537\n" + SampleTle.SnapshotText;

        await p.ImportAsync("mixed.tle", Encoding.UTF8.GetBytes(text));

        Assert.Equal(14, p.Rows.Count);
        Assert.Equal(14, store.Saved.Entries.Count);
        var import = p.LastImport!;
        Assert.Equal("mixed.tle", import.FileName);
        Assert.Contains(import.Errors, e => e.LineNumber == 2 && e.Kind == TleErrorKind.Checksum);
        Assert.Contains(diagnostics.Items, d => d.Message.Contains("第 2 行"));
    }

    [Fact]
    public async Task Rows_show_support_staleness_and_history()
    {
        var (p, _, _, clock) = Create();
        var geo = TleText.Set(TleText.Set(TleText.IssLine2, 2, "40000"), 52, " 1.00270000");
        var geo1 = TleText.Set(TleText.IssLine1, 2, "40000");
        await p.ImportAsync("a.tle", Encoding.UTF8.GetBytes(SampleTle.SnapshotText + $"GEO\n{geo1}\n{geo}\n"));

        var issRow = p.Rows.Single(r => r.NoradId == 25544);
        Assert.Equal(CatalogRowStatus.Ok, issRow.Status);
        Assert.Equal(CatalogRowStatus.Unsupported, p.Rows.Single(r => r.NoradId == 40000).Status);

        clock.Advance(TimeSpan.FromDays(20));
        p.Refresh();
        Assert.Equal(CatalogRowStatus.Stale, p.Rows.Single(r => r.NoradId == 25544).Status);
        Assert.Contains("陳舊", p.Rows.Single(r => r.NoradId == 25544).StatusText);
    }

    [Fact]
    public async Task Pin_history_and_selection_for_prediction()
    {
        var (p, store, _, _) = Create();
        var iss = PassScenario.Satellites.First(s => s.NoradId == 25544);
        var (l1, l2) = TleFormatter.FormatLines(iss with { EpochDayOfYear = iss.EpochDayOfYear - 3 });
        var older = TleParser.Parse($"ISS OLD\n{l1}\n{l2}").Sets.Single();
        await p.ImportAsync("a.tle", Encoding.UTF8.GetBytes(SampleTle.SnapshotText + $"ISS OLD\n{l1}\n{l2}\n"));
        var version = p.Version;

        p.Pin(25544, older.Epoch);

        Assert.Equal(older.Epoch, p.ActiveSets().Single(s => s.NoradId == 25544).Epoch);
        Assert.Equal(older.Epoch, store.Saved.Find(25544)!.PinnedEpoch);
        Assert.True(p.Version > version);

        p.SetSelected(25544, false);
        Assert.DoesNotContain(p.ActiveSets(), s => s.NoradId == 25544);
        Assert.Equal(13, p.ActiveSets().Count);
    }

    [Fact]
    public async Task Unsupported_satellites_are_excluded_from_prediction()
    {
        var (p, _, _, _) = Create();
        var geo = TleText.Set(TleText.Set(TleText.IssLine2, 2, "40000"), 52, " 1.00270000");
        var geo1 = TleText.Set(TleText.IssLine1, 2, "40000");

        await p.ImportAsync("a.tle", Encoding.UTF8.GetBytes($"GEO\n{geo1}\n{geo}\n"));

        Assert.Empty(p.ActiveSets());
    }

    [Fact]
    public async Task Read_only_store_shows_message_and_import_still_works_in_memory()
    {
        var (p, store, diagnostics, _) = Create(StoreStatus.TooNew);

        await p.ImportAsync("a.tle", Encoding.UTF8.GetBytes(SampleTle.SnapshotText));

        Assert.Contains(diagnostics.Items, d => d.Severity == DiagnosticSeverity.Error && d.Message.Contains("TooNew"));
        Assert.Equal(14, p.Rows.Count);
        Assert.Equal(0, store.SaveCount);
        Assert.Contains(diagnostics.Items, d => d.Message.Contains("無法寫入"));
    }
}

public class DiagnosticsPresenterTests
{
    [Fact]
    public void Sources_are_replaced_independently()
    {
        var d = new DiagnosticsPresenter();

        d.SetSource("import", [new DiagnosticItem(DiagnosticSeverity.Warning, "import", "a")]);
        d.SetSource("prediction", [new DiagnosticItem(DiagnosticSeverity.Error, "prediction", "b")]);
        d.SetSource("import", [new DiagnosticItem(DiagnosticSeverity.Info, "import", "c")]);

        Assert.Equal(["b", "c"], d.Items.Select(i => i.Message).Order(StringComparer.Ordinal));
        Assert.Equal(1, d.CountOf(DiagnosticSeverity.Error));
        Assert.Equal(DiagnosticSeverity.Error, d.Items[0].Severity); // 依嚴重度排序
    }
}
