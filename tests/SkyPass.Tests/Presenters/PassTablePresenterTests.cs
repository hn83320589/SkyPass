using SkyPass.Core.Passes;
using SkyPass.Core.Time;
using SkyPass.Presenters;

namespace SkyPass.Tests.Presenters;

public class PassTablePresenterTests
{
    private static readonly JulianDate T0 = JulianDate.FromUtc(new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc));

    private static SatellitePass Pass(int norad, string name, double aosMin, double maxEl, bool visible = false) => new()
    {
        NoradId = norad,
        SatelliteName = name,
        Aos = T0.AddSeconds(aosMin * 60),
        AosAzimuthDeg = 10,
        MaxElevationTime = T0.AddSeconds(aosMin * 60 + 300),
        MaxElevationDeg = maxEl,
        MaxElevationAzimuthDeg = 90,
        Los = T0.AddSeconds(aosMin * 60 + 600),
        LosAzimuthDeg = 170,
        VisibleIntervals = visible ? [new TimeInterval(T0.AddSeconds(aosMin * 60 + 100), T0.AddSeconds(aosMin * 60 + 400))] : [],
    };

    private static readonly IReadOnlyList<SatellitePass> Passes =
    [
        Pass(25544, "ISS (ZARYA)", 0, 45, visible: true),
        Pass(40069, "METEOR-M 2", 30, 12),
        Pass(48274, "CSS (TIANHE)", 60, 70),
        Pass(25544, "ISS (ZARYA)", 95, 20),
    ];

    private static PassTablePresenter Presenter()
    {
        var p = new PassTablePresenter();
        p.SetPasses(Passes, TimeSpan.FromHours(8));
        return p;
    }

    [Fact]
    public void Default_sort_is_by_aos_and_rows_are_formatted_in_display_offset()
    {
        var rows = Presenter().Rows;

        Assert.Equal([0, 30, 60, 95], rows.Select(r => (int)Math.Round(r.Pass.Aos.SecondsSince(T0) / 60)));
        Assert.Equal("2026-10-21 20:00:00", rows[0].Aos);
        Assert.Equal("45.0°", rows[0].MaxElevation);
        Assert.Equal("10:00", rows[0].Duration);
        Assert.Equal("20:01:40–20:06:40", rows[0].Visible);
    }

    [Theory]
    [InlineData(PassSortColumn.Satellite, false, new[] { "CSS (TIANHE)", "ISS (ZARYA)", "ISS (ZARYA)", "METEOR-M 2" })]
    [InlineData(PassSortColumn.MaxElevation, true, new[] { "CSS (TIANHE)", "ISS (ZARYA)", "ISS (ZARYA)", "METEOR-M 2" })]
    [InlineData(PassSortColumn.MaxElevation, false, new[] { "METEOR-M 2", "ISS (ZARYA)", "ISS (ZARYA)", "CSS (TIANHE)" })]
    [InlineData(PassSortColumn.Aos, true, new[] { "ISS (ZARYA)", "CSS (TIANHE)", "METEOR-M 2", "ISS (ZARYA)" })]
    public void Sorting(PassSortColumn column, bool descending, string[] expectedNames)
    {
        var p = Presenter();

        p.SortBy(column, descending);

        Assert.Equal(expectedNames, p.Rows.Select(r => r.Satellite));
    }

    [Fact]
    public void Sorting_same_column_twice_toggles_direction()
    {
        var p = Presenter();

        p.ToggleSort(PassSortColumn.MaxElevation);
        var first = p.Rows[0].Pass.MaxElevationDeg;
        p.ToggleSort(PassSortColumn.MaxElevation);

        Assert.Equal(12, first);
        Assert.Equal(70, p.Rows[0].Pass.MaxElevationDeg);
    }

    [Fact]
    public void Filtering_by_name_min_elevation_and_visibility()
    {
        var p = Presenter();

        p.SetFilter(new PassFilter("iss", 0, false));
        Assert.Equal(2, p.Rows.Count);

        p.SetFilter(new PassFilter(null, 25, false));
        Assert.Equal(["ISS (ZARYA)", "CSS (TIANHE)"], p.Rows.Select(r => r.Satellite));

        p.SetFilter(new PassFilter(null, 0, true));
        Assert.Single(p.Rows);

        p.SetFilter(new PassFilter("25544", 0, false));
        Assert.Equal(2, p.Rows.Count);
    }

    [Fact]
    public void Filtering_by_time_range_keeps_overlapping_passes()
    {
        var p = Presenter();

        // 過境 AOS 為 0、30、60、95 分鐘，各持續 10 分鐘；範圍 [41, 65] 分鐘只與第 3 個重疊
        p.SetFilter(PassFilter.None with { From = T0.AddSeconds(41 * 60), To = T0.AddSeconds(65 * 60) });
        Assert.Equal(["CSS (TIANHE)"], p.Rows.Select(r => r.Satellite));

        // 邊界只碰到（LOS 恰為範圍起點）也算重疊
        p.SetFilter(PassFilter.None with { From = T0.AddSeconds(40 * 60) });
        Assert.Equal(3, p.Rows.Count);

        p.SetFilter(PassFilter.None with { To = T0.AddSeconds(29 * 60) });
        Assert.Single(p.Rows);
    }

    [Fact]
    public void Selection_survives_sorting_and_is_cleared_when_filtered_out()
    {
        var p = Presenter();
        var css = p.Rows.Single(r => r.Satellite.StartsWith("CSS"));

        p.Select(css);
        var version = p.SelectionVersion;
        p.SortBy(PassSortColumn.Satellite, false);

        Assert.Same(css.Pass, p.SelectedPass);
        Assert.True(p.Rows.Single(r => r.Pass == css.Pass).IsSelected);
        Assert.Equal(version, p.SelectionVersion);

        p.SetFilter(new PassFilter("METEOR", 0, false));
        Assert.Null(p.SelectedPass);
        Assert.True(p.SelectionVersion > version);
    }

    [Fact]
    public void Selection_is_kept_across_new_prediction_when_same_pass_exists()
    {
        var p = Presenter();
        p.Select(p.Rows[2]);

        var recomputed = Passes.Select(x => x with { MaxElevationDeg = x.MaxElevationDeg + 1e-9 }).ToList();
        p.SetPasses(recomputed, TimeSpan.FromHours(8));

        Assert.NotNull(p.SelectedPass);
        Assert.Equal(48274, p.SelectedPass!.NoradId);
    }

    [Fact]
    public void Boundary_flags_are_labelled()
    {
        var p = new PassTablePresenter();
        p.SetPasses([Pass(1, "A", 0, 30) with { StartedBeforeWindow = true }, Pass(2, "B", 10, 30) with { EndsAfterWindow = true }], TimeSpan.Zero);

        Assert.Equal("起點前已開始", p.Rows[0].Flags);
        Assert.Equal("終點後才結束", p.Rows[1].Flags);
    }
}
