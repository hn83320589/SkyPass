using System.Text;
using SkyPass.Core.Layout;
using SkyPass.Core.Storage;
using SkyPass.Core.Tle;
using SkyPass.Presenters;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Presenters;

/// <summary>各畫面聯動與背景計算協調。</summary>
public class MainPresenterTests
{
    private static async Task<(MainPresenter Main, InMemorySettingsStore Settings, FakeTimeProvider Clock)> StartedAsync()
    {
        var catalog = TleCatalog.Empty.Import(TleParser.Parse(SampleTle.SnapshotText).Sets).Catalog;
        var settings = new InMemorySettingsStore();
        var clock = new FakeTimeProvider(SampleTle.SnapshotTime);
        var main = new MainPresenter(new InMemoryCatalogStore(initial: catalog), settings, clock);
        main.Initialize();
        await main.UpdateUntilIdleAsync();
        return (main, settings, clock);
    }

    [Fact]
    public async Task Startup_loads_catalog_and_runs_prediction()
    {
        var (main, _, _) = await StartedAsync();

        Assert.Equal(PredictionStatus.Completed, main.Prediction.State.Status);
        Assert.NotEmpty(main.PassTable.Rows);
        Assert.All(main.PassTable.Rows, r => Assert.True(r.Pass.MaxElevationDeg >= 10));
        Assert.NotEmpty(main.Timeline.Model.Rows);
        Assert.Equal(14, main.Catalog.Rows.Count);
    }

    [Fact]
    public async Task Selecting_a_pass_links_sky_plot_timeline_and_live_tracking()
    {
        var (main, _, _) = await StartedAsync();
        main.SkyPlot.SetViewport(0, 0, 400, 400);
        var row = main.PassTable.Rows.First(r => r.Pass.MaxElevationDeg > 30);

        main.PassTable.Select(row);
        main.Update();

        Assert.Same(row.Pass, main.SkyPlot.Model.Pass);
        Assert.NotEmpty(main.SkyPlot.Model.Track);
        Assert.Same(row.Pass, main.Timeline.Model.Selected);
        Assert.Equal(row.Pass.NoradId, main.LiveTracking.Satellite!.NoradId);
    }

    [Fact]
    public async Task Clicking_timeline_segment_selects_pass_in_table()
    {
        var (main, _, _) = await StartedAsync();
        main.Timeline.SetViewport(0, 1000);
        var model = main.Timeline.Model;
        var rowIndex = model.Rows.ToList().FindIndex(r => r.Segments.Count > 0);
        var segment = model.Rows[rowIndex].Segments[0];

        var hit = main.Timeline.HitTest(rowIndex, (segment.X0 + segment.X1) / 2);
        main.PassTable.Select(hit);
        main.Update();

        Assert.Same(segment.Pass, main.PassTable.SelectedPass);
    }

    [Fact]
    public async Task Changing_mask_reruns_prediction()
    {
        var (main, settings, _) = await StartedAsync();
        var before = main.PassTable.TotalCount;

        main.Settings.SetMask(30);
        await main.UpdateUntilIdleAsync();

        Assert.True(main.PassTable.TotalCount < before);
        Assert.All(main.PassTable.Rows, r => Assert.True(r.Pass.MaxElevationDeg >= 30));
        Assert.Equal(30, settings.Saved.MaskDeg);
    }

    [Fact]
    public async Task Changing_display_offset_reformats_rows_without_recomputing()
    {
        var (main, _, _) = await StartedAsync();
        var version = main.Prediction.State.Version;
        var aos = main.PassTable.Rows[0].Pass.Aos;

        main.Settings.SetDisplayOffset(TimeSpan.Zero);
        main.Update();

        Assert.Equal(version, main.Prediction.State.Version);
        Assert.Equal(SkyPass.Core.Time.TimeDisplay.Format(aos, TimeSpan.Zero), main.PassTable.Rows[0].Aos);
    }

    [Fact]
    public async Task Csv_export_uses_filtered_rows_and_csv_offset()
    {
        var (main, _, _) = await StartedAsync();
        main.Settings.SetCsvOffset(TimeSpan.FromHours(9));
        main.PassTable.SetFilter(new PassFilter("ISS", 0, false));
        using var stream = new MemoryStream();

        main.ExportCsv(stream);

        var text = Encoding.UTF8.GetString(stream.ToArray());
        Assert.StartsWith("﻿衛星,", text);
        Assert.Contains("UTC+09:00", text);
        var dataLines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        Assert.Equal(main.PassTable.Rows.Count, dataLines.Count);
        Assert.All(dataLines, l => Assert.StartsWith("ISS", l));
    }

    [Fact]
    public async Task Prediction_diagnostics_and_stale_warnings_reach_diagnostics_page()
    {
        var (main, _, clock) = await StartedAsync();

        clock.Advance(TimeSpan.FromDays(20));
        main.RequestPrediction();
        await main.UpdateUntilIdleAsync();

        Assert.Contains(main.Diagnostics.Items, d => d.Message.Contains("陳舊"));
    }

    [Fact]
    public async Task Importing_new_file_triggers_new_prediction()
    {
        var (main, _, _) = await StartedAsync();
        var version = main.Prediction.State.Version;
        var iss = PassScenario.Satellites.First(s => s.NoradId == 25544);
        var (l1, l2) = TleFormatter.FormatLines(iss with { NoradId = 77777, RaanDeg = (iss.RaanDeg + 90) % 360 });

        await main.Catalog.ImportAsync("new.tle", Encoding.UTF8.GetBytes($"NEW SAT\n{l1}\n{l2}\n"));
        await main.UpdateUntilIdleAsync();

        Assert.True(main.Prediction.State.Version > version);
        Assert.Contains(main.PassTable.Rows, r => r.Pass.NoradId == 77777);
    }

    [Fact]
    public void Read_only_database_and_bad_settings_do_not_crash_startup()
    {
        var main = new MainPresenter(new InMemoryCatalogStore(StoreStatus.Corrupt), new InMemorySettingsStore(warnings: ["設定檔損毀，已使用預設值"]),
            new FakeTimeProvider(SampleTle.SnapshotTime));

        main.Initialize();
        main.Update();

        Assert.Contains(main.Diagnostics.Items, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains(main.Diagnostics.Items, d => d.Message.Contains("設定檔損毀"));
        Assert.Equal(PredictionStatus.Idle, main.Prediction.State.Status); // 沒有衛星就不預報
    }

    [Fact]
    public async Task Sky_plot_model_has_mask_circle_and_cardinal_labels()
    {
        var (main, _, _) = await StartedAsync();
        main.SkyPlot.SetViewport(0, 0, 500, 400);
        main.PassTable.Select(main.PassTable.Rows[0]);
        main.Update();

        var model = main.SkyPlot.Model;

        Assert.Equal(model.Layout.RadiusForElevation(10), Distance(model.MaskCircle[0], model.Layout), 9);
        Assert.Equal(["N", "E", "S", "W"], model.Labels.Select(l => l.Label));
        Assert.True(model.Labels[1].Position.X < model.Layout.CenterX); // 東在左
    }

    private static double Distance(PointD p, SkyPlotLayout l) => Math.Sqrt(Math.Pow(p.X - l.CenterX, 2) + Math.Pow(p.Y - l.CenterY, 2));
}
