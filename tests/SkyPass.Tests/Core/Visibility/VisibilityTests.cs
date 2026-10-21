using SkyPass.Core.Coordinates;
using SkyPass.Core.Passes;
using SkyPass.Core.Propagation;
using SkyPass.Core.Sun;
using SkyPass.Core.Time;
using SkyPass.Core.Visibility;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Visibility;

/// <summary>驗收 #14：日照與陰影區段、太陽仰角門檻、可目視起訖、整段陰影／整段白天、陰影邊界在 AOS/LOS 前後。</summary>
public class VisibilityTests
{
    private static readonly Lazy<List<(Sgp4Propagator Prop, ObserverSite Site, SatellitePass Pass)>> Passes = new(CollectPasses);

    /// <summary>獨立判斷某時刻是否可目視（直接用定義，不經過區段搜尋）。</summary>
    private static bool VisibleAt(Sgp4Propagator prop, ObserverSite site, JulianDate t)
    {
        Assert.Equal(Sgp4Error.None, prop.Propagate(t, out var s));
        return !Shadow.IsInShadow(s.PositionKm, SunPosition.UnitVectorTeme(t)) && SunPosition.ElevationDeg(site, t) < VisibilityCalculator.SunElevationThresholdDeg;
    }

    [Fact]
    public void Visible_intervals_agree_with_pointwise_definition()
    {
        var checkedPasses = 0;
        foreach (var (prop, site, pass) in Passes.Value)
        {
            var intervals = pass.VisibleIntervals;
            var visibleSamples = 0;
            for (var t = 0.0; t <= pass.DurationSeconds; t += 1.0)
            {
                var time = pass.Aos.AddSeconds(t);
                var visible = VisibleAt(prop, site, time);
                if (intervals.Count == 0)
                {
                    visibleSamples += visible ? 1 : 0;
                    continue;
                }

                var inside = intervals.Any(iv => time.SecondsSince(iv.Start) > 0.01 && iv.End.SecondsSince(time) > 0.01);
                var nearEdge = intervals.Any(iv => Math.Abs(time.SecondsSince(iv.Start)) <= 0.01 || Math.Abs(time.SecondsSince(iv.End)) <= 0.01);
                if (!nearEdge)
                {
                    Assert.Equal(inside, visible);
                }
            }

            // 沒有可目視區段時，逐秒取樣最多只可能碰到一個（總長不足 1 秒的）可目視時刻
            Assert.True(visibleSamples <= 1, $"過境 {pass.Aos.ToUtc():O} 有 {visibleSamples} 秒可目視卻沒有區段");

            foreach (var iv in intervals)
            {
                Assert.True(iv.DurationSeconds > 0);
                if (Math.Abs(iv.Start.SecondsSince(pass.Aos)) > 1e-6)
                {
                    Assert.False(VisibleAt(prop, site, iv.Start.AddSeconds(-0.01)));
                    Assert.True(VisibleAt(prop, site, iv.Start.AddSeconds(0.01)));
                }

                if (Math.Abs(pass.Los.SecondsSince(iv.End)) > 1e-6)
                {
                    Assert.True(VisibleAt(prop, site, iv.End.AddSeconds(-0.01)));
                    Assert.False(VisibleAt(prop, site, iv.End.AddSeconds(0.01)));
                }
            }

            checkedPasses++;
        }

        Assert.True(checkedPasses > 50);
    }

    [Fact]
    public void Pass_entirely_in_shadow_is_not_visible()
    {
        var (prop, site, pass) = Passes.Value.First(x => AllSamples(x, (p, s, t) => p.InShadow(t)));

        Assert.Empty(pass.VisibleIntervals);
    }

    [Fact]
    public void Pass_entirely_in_daylight_is_not_visible()
    {
        var (_, _, pass) = Passes.Value.First(x => AllSamples(x, (p, s, t) => SunPosition.ElevationDeg(s, t) > 0));

        Assert.Empty(pass.VisibleIntervals);
    }

    [Fact]
    public void Shadow_entry_during_pass_ends_visible_interval()
    {
        // 天色已暗、衛星在過境途中進入地影：可目視區段結束於進入陰影的時刻
        var found = Passes.Value.First(x =>
            x.Pass.VisibleIntervals.Count == 1 &&
            x.Pass.VisibleIntervals[0].Start == x.Pass.Aos &&
            x.Pass.Los.SecondsSince(x.Pass.VisibleIntervals[0].End) > 5);
        var end = found.Pass.VisibleIntervals[0].End;

        Assert.False(found.Prop.InShadow(end.AddSeconds(-0.01)));
        Assert.True(found.Prop.InShadow(end.AddSeconds(0.01)));
    }

    [Fact]
    public void Shadow_boundary_just_before_aos_or_after_los_keeps_whole_pass_visible_or_invisible()
    {
        // 陰影邊界落在 AOS 前 60 秒內或 LOS 後 60 秒內：過境期間的日照狀態不變
        var near = Passes.Value.Where(x =>
        {
            var tl = ShadowSearch.Find(x.Prop, x.Pass.Aos.AddSeconds(-60), x.Pass.Los.AddSeconds(60), 1.0);
            return tl.Events.Any(e => e.Time.SecondsSince(x.Pass.Aos) is < 0 and > -60 || e.Time.SecondsSince(x.Pass.Los) is > 0 and < 60);
        }).ToList();

        Assert.NotEmpty(near);
        foreach (var (prop, site, pass) in near)
        {
            var sunlitAtAos = !prop.InShadow(pass.Aos);
            var sunlitAtLos = !prop.InShadow(pass.Los);
            Assert.Equal(sunlitAtAos, sunlitAtLos);
            if (!sunlitAtAos)
            {
                Assert.Empty(pass.VisibleIntervals);
            }
        }
    }

    [Fact]
    public void Sun_elevation_threshold_is_minus_six_degrees()
    {
        Assert.Equal(-6.0, VisibilityCalculator.SunElevationThresholdDeg);
        // 存在受日照、但太陽仰角在 −6° 與 0° 之間（民用曙暮光）而被判為不可目視的時刻
        var any = Passes.Value.Any(x => AllSamples(x, (p, s, t) => !p.InShadow(t) && SunPosition.ElevationDeg(s, t) is > -6 and < 0) && x.Pass.VisibleIntervals.Count == 0);
        Assert.True(any);
    }

    [Fact]
    public void Visibility_shorter_than_one_second_is_discarded()
    {
        Assert.Equal(1.0, VisibilityCalculator.MinimumVisibleSeconds);
        Assert.All(Passes.Value, x => Assert.True(x.Pass.VisibleIntervals.Count == 0 || x.Pass.VisibleIntervals.Sum(i => i.DurationSeconds) >= 1.0));
    }

    private static bool AllSamples((Sgp4Propagator Prop, ObserverSite Site, SatellitePass Pass) x, Func<Sgp4Propagator, ObserverSite, JulianDate, bool> predicate)
    {
        for (var t = 0.0; t <= x.Pass.DurationSeconds; t += 10)
        {
            if (!predicate(x.Prop, x.Site, x.Pass.Aos.AddSeconds(t)))
            {
                return false;
            }
        }

        return predicate(x.Prop, x.Site, x.Pass.Los);
    }

    private static List<(Sgp4Propagator, ObserverSite, SatellitePass)> CollectPasses()
    {
        var result = new List<(Sgp4Propagator, ObserverSite, SatellitePass)>();
        var sites = new[] { new Geodetic(25.03, 121.56, 0.01), new Geodetic(52.0, 0.0, 0.0), new Geodetic(-35.0, 150.0, 0.0), new Geodetic(60.0, -150.0, 0.0) };
        foreach (var tle in PassScenario.Satellites.Take(14))
        {
            var prop = Sgp4Propagator.Create(tle);
            foreach (var g in sites)
            {
                var site = new ObserverSite(g);
                var passes = PassFinder.Find(prop, site, prop.Epoch, prop.Epoch.AddSeconds(3 * 86400), 0).Passes
                    .Where(p => !p.StartedBeforeWindow && !p.EndsAfterWindow);
                foreach (var p in passes)
                {
                    result.Add((prop, site, p with { VisibleIntervals = VisibilityCalculator.ComputeIntervals(prop, site, p.Aos, p.Los) }));
                }
            }
        }

        return result;
    }
}

internal static class PropagatorShadowExtensions
{
    public static bool InShadow(this Sgp4Propagator prop, JulianDate t)
    {
        Assert.Equal(Sgp4Error.None, prop.Propagate(t, out var s));
        return Shadow.IsInShadow(s.PositionKm, SunPosition.UnitVectorTeme(t));
    }
}
