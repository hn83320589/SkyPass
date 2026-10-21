using SkyPass.Core.Coordinates;
using SkyPass.Core.Passes;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Passes;

public class PassFinderTests
{
    private static readonly TleSet Iss = PassScenario.Satellites.First(s => s.NoradId == 25544);
    private static readonly ObserverSite Taipei = new(new Geodetic(25.0330, 121.5654, 0.01));

    private static (Sgp4Propagator Prop, JulianDate Start, JulianDate End, SatellitePass Pass) FirstIssPass()
    {
        var prop = Sgp4Propagator.Create(Iss);
        var start = prop.Epoch;
        var end = start.AddSeconds(2 * 86400);
        var pass = PassFinder.Find(prop, Taipei, start, end, 10).Passes.First(p => !p.StartedBeforeWindow && p.MaxElevationDeg > 20);
        return (prop, start, end, pass);
    }

    [Fact]
    public void Finds_passes_with_all_fields()
    {
        var (_, _, _, pass) = FirstIssPass();

        Assert.Equal(25544, pass.NoradId);
        Assert.Equal("ISS (ZARYA)", pass.SatelliteName);
        Assert.False(pass.StartedBeforeWindow);
        Assert.False(pass.EndsAfterWindow);
        Assert.InRange(pass.DurationSeconds, 60, 900);
    }

    [Fact]
    public void Pass_crossing_window_start_is_flagged()
    {
        var (prop, _, _, pass) = FirstIssPass();
        var start = pass.MaxElevationTime;

        var result = PassFinder.Find(prop, Taipei, start, start.AddSeconds(3600), 10).Passes;

        var first = result[0];
        Assert.True(first.StartedBeforeWindow);
        Assert.Equal(start, first.Aos);
        Assert.Equal(pass.Los.ToUtc(), first.Los.ToUtc(), TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void Pass_crossing_window_end_is_flagged()
    {
        var (prop, start, _, pass) = FirstIssPass();
        var end = pass.MaxElevationTime;

        var last = PassFinder.Find(prop, Taipei, start, end, 10).Passes[^1];

        Assert.True(last.EndsAfterWindow);
        Assert.Equal(end, last.Los);
        Assert.Equal(pass.Aos.ToUtc(), last.Aos.ToUtc(), TimeSpan.FromMilliseconds(10));
    }

    [Fact]
    public void Window_shorter_than_pass_yields_single_pass_flagged_both_ways()
    {
        var (prop, _, _, pass) = FirstIssPass();
        var start = pass.Aos.AddSeconds(30);
        var end = pass.Los.AddSeconds(-30);

        var result = PassFinder.Find(prop, Taipei, start, end, 10).Passes;

        var only = Assert.Single(result);
        Assert.True(only.StartedBeforeWindow);
        Assert.True(only.EndsAfterWindow);
        Assert.Equal(pass.MaxElevationDeg, only.MaxElevationDeg, 1e-6);
    }

    [Fact]
    public void Aos_exactly_on_window_start()
    {
        var (prop, _, _, pass) = FirstIssPass();

        var result = PassFinder.Find(prop, Taipei, pass.Aos, pass.Aos.AddSeconds(3600), 10).Passes;

        // AOS 恰在起點：仰角 ≈ 遮罩，可能被視為「起點前已開始」或在起點後瞬間開始，兩者都正確
        var first = result[0];
        Assert.True(Math.Abs(first.Aos.SecondsSince(pass.Aos)) <= 0.01);
        Assert.Equal(pass.MaxElevationDeg, first.MaxElevationDeg, 1e-6);
    }

    [Fact]
    public void Los_exactly_on_window_end()
    {
        var (prop, start, _, pass) = FirstIssPass();

        var last = PassFinder.Find(prop, Taipei, start, pass.Los, 10).Passes[^1];

        Assert.True(Math.Abs(last.Los.SecondsSince(pass.Los)) <= 0.01);
        Assert.Equal(pass.MaxElevationDeg, last.MaxElevationDeg, 1e-6);
    }

    [Fact]
    public void Empty_or_reversed_window_returns_no_passes()
    {
        var prop = Sgp4Propagator.Create(Iss);

        Assert.Empty(PassFinder.Find(prop, Taipei, prop.Epoch, prop.Epoch, 10).Passes);
        Assert.Throws<ArgumentException>(() => PassFinder.Find(prop, Taipei, prop.Epoch.AddSeconds(10), prop.Epoch, 10));
    }

    [Fact]
    public void Decay_stops_prediction_and_produces_diagnostic()
    {
        // SGP4-VER 28872 約在 epoch 後 50–55 分鐘衰減
        var tle = Sgp4ReferenceRow.Load("sgp4_published.csv").First(r => r.Case == "VER-28872").Tle;
        var prop = Sgp4Propagator.Create(tle);
        var site = new ObserverSite(new Geodetic(0, 0, 0));

        var result = PassFinder.Find(prop, site, prop.Epoch, prop.Epoch.AddSeconds(86400), 0);

        var diag = Assert.IsType<PredictionDiagnostic>(result.Diagnostic);
        Assert.Equal(DiagnosticKind.Decayed, diag.Kind);
        Assert.Equal(Sgp4Error.Decayed, diag.Error);
        var decaySec = diag.Time!.Value.SecondsSince(prop.Epoch);
        Assert.InRange(decaySec, 50 * 60, 55 * 60);
        Assert.Equal(Sgp4Error.None, prop.Propagate(diag.Time.Value.AddSeconds(-1.5), out _));
        Assert.NotEqual(Sgp4Error.None, prop.Propagate(diag.Time.Value.AddSeconds(1.5), out _));
        Assert.All(result.Passes, p => Assert.True(p.Los.SecondsSince(diag.Time.Value) <= 0));
        Assert.Contains("衰減", diag.Message);
    }

    [Fact]
    public void Deep_space_satellite_produces_unsupported_diagnostic()
    {
        var prop = Sgp4Propagator.Create(Iss with { MeanMotionRevPerDay = 1.0027 });

        var result = PassFinder.Find(prop, Taipei, prop.Epoch, prop.Epoch.AddSeconds(86400), 10);

        Assert.Empty(result.Passes);
        Assert.Equal(DiagnosticKind.Unsupported, result.Diagnostic!.Kind);
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        var prop = Sgp4Propagator.Create(Iss);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            PassFinder.Find(prop, Taipei, prop.Epoch, prop.Epoch.AddSeconds(7 * 86400), 10, cts.Token));
    }
}
