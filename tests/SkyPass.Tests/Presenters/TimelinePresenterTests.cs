using SkyPass.Core.Coordinates;
using SkyPass.Core.Passes;
using SkyPass.Core.Time;
using SkyPass.Presenters;
using SkyPass.Tests.Core.Passes;

namespace SkyPass.Tests.Presenters;

public class TimelinePresenterTests
{
    private static (TimelinePresenter Presenter, IReadOnlyList<SatellitePass> Passes, JulianDate Start) Create()
    {
        var sats = PassPredictorTests.Constellation(8);
        var site = new Geodetic(25.03, 121.56, 0.01);
        var start = sats[0].Epoch;
        var passes = PassPredictor.Predict(new PredictionRequest(sats, site, start, start.AddSeconds(3 * 86400), 10)).Passes;
        var p = new TimelinePresenter();
        p.SetViewport(50, 1000);
        p.SetData(passes, site, TimeSpan.FromHours(8), start, start.AddSeconds(3 * 86400));
        return (p, passes, start);
    }

    [Fact]
    public void Window_starts_at_local_midnight_of_prediction_start_day()
    {
        var (p, _, start) = Create();

        var local = TimeDisplay.ToLocal(p.Model.Layout.Start, TimeSpan.FromHours(8));

        Assert.Equal(TimeSpan.Zero, local.TimeOfDay);
        Assert.Equal(TimeDisplay.ToLocal(start, TimeSpan.FromHours(8)).Date, local.Date);
        Assert.Equal(86400, p.Model.Layout.DurationSeconds);
        Assert.Equal(50, p.Model.Layout.X0);
        Assert.Equal(1000, p.Model.Layout.Width);
        Assert.Contains(local.ToString("yyyy-MM-dd"), p.Model.DayLabel);
    }

    [Fact]
    public void Day_navigation_is_bounded_by_prediction_range()
    {
        var (p, _, _) = Create();
        var first = p.Model.Layout.Start;

        Assert.False(p.CanGoPrevious);
        p.NextDay();
        Assert.Equal(86400, p.Model.Layout.Start.SecondsSince(first), 6);
        p.NextDay();
        p.NextDay();
        p.NextDay();
        Assert.False(p.CanGoNext);
        p.PreviousDay();
        Assert.True(p.CanGoNext);
    }

    [Fact]
    public void Hit_test_returns_pass_under_cursor_or_null()
    {
        var (p, _, _) = Create();
        p.NextDay(); // 預報起點接近當地午夜，第一天可能沒有過境
        var model = p.Model;
        var rowIndex = model.Rows.ToList().FindIndex(r => r.Segments.Count > 0);
        var seg = model.Rows[rowIndex].Segments[0];

        Assert.Same(seg.Pass, p.HitTest(rowIndex, (seg.X0 + seg.X1) / 2));
        Assert.Null(p.HitTest(rowIndex, seg.X0 - 30));
        Assert.Null(p.HitTest(99, 100));
    }
}
