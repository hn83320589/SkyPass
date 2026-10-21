using SkyPass.Core.Coordinates;
using SkyPass.Core.Propagation;
using SkyPass.Core.Sun;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;
using SkyPass.Core.Visibility;
using SkyPass.Tests.Support;
using Xunit.Abstractions;

namespace SkyPass.Tests.Core.Sun;

/// <summary>
/// 擦邊地影：β 角接近臨界值時，衛星只會擦過圓柱陰影邊緣，陰影可能短於取樣步長（10 秒）。
/// 以幾何方式構造此類軌道，確認陰影搜尋不會漏判。
/// </summary>
public class ShortShadowTests(ITestOutputHelper output)
{
    private static readonly TleSet Base = PassScenario.Satellites.First(s => s.NoradId == 25544) with
    {
        NoradId = 99001,
        Name = "GRAZING",
        InclinationDeg = 98.0,
        Eccentricity = 0.0001,
        MeanMotionRevPerDay = 14.5,
        BStar = 0,
        MeanMotionDot = 0,
        MeanMotionDdot = 0,
    };

    /// <summary>觀察兩圈（約 3.3 小時）。</summary>
    private const double WindowSeconds = 2 * 86400 / 14.5;

    /// <summary>陰影深度（km）；持續時間約與深度的平方根成正比，對應約 1–8 秒。</summary>
    public static TheoryData<double> DepthsKm => [0.0002, 0.001, 0.005, 0.015];

    [Theory]
    [MemberData(nameof(DepthsKm))]
    public void Short_grazing_shadow_is_found_with_ten_second_step(double depthKm)
    {
        var prop = Sgp4Propagator.Create(GrazingTle(depthKm));
        var start = prop.Epoch;
        var reference = BruteForceEvents(prop, start, WindowSeconds);
        var shortest = ShortestShadowSeconds(reference);
        output.WriteLine($"深度 {depthKm} km：參考事件 {reference.Count} 個，最短陰影 {shortest:F2} s");
        Assert.True(shortest < ShadowSearch.DefaultStepSeconds, $"情境構造失敗：最短陰影 {shortest:F2} s 不短於取樣步長");

        var timeline = ShadowSearch.Find(prop, start, start.AddSeconds(WindowSeconds));

        Assert.Equal(reference.Count, timeline.Events.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.Equal(reference[i].Entering, timeline.Events[i].Entering);
            var diff = timeline.Events[i].Time.SecondsSince(start) - reference[i].Seconds;
            Assert.True(Math.Abs(diff) <= 0.1, $"事件 {i} 差 {diff:F4} s");
        }
    }

    [Fact]
    public void Visible_interval_is_split_by_short_shadow()
    {
        var prop = Sgp4Propagator.Create(GrazingTle(0.005));
        var start = prop.Epoch;
        var reference = BruteForceEvents(prop, start, WindowSeconds);
        var enter = reference.First(e => e.Entering);
        var exit = reference.First(e => !e.Entering && e.Seconds > enter.Seconds);
        Assert.True(exit.Seconds - enter.Seconds < ShadowSearch.DefaultStepSeconds);

        // 觀測點放在對日點：整段時間太陽仰角遠低於 −6°，只剩日照條件影響可目視
        var mid = start.AddSeconds(enter.Seconds);
        var antiSolar = Wgs84.FromEcef(-1.0 * Frames.RotateTemeToEcef(SunPosition.UnitVectorTeme(mid), Gmst.ComputeRad(mid)) * 6378.0);
        var site = new ObserverSite(antiSolar with { HeightKm = 0 });
        var from = start.AddSeconds(enter.Seconds - 120);
        var to = start.AddSeconds(exit.Seconds + 120);
        Assert.True(SunPosition.ElevationDeg(site, from) < -60 && SunPosition.ElevationDeg(site, to) < -60);

        var intervals = VisibilityCalculator.ComputeIntervals(prop, site, from, to);

        Assert.Equal(2, intervals.Count);
        Assert.True(Math.Abs(intervals[0].End.SecondsSince(start) - enter.Seconds) <= 0.1);
        Assert.True(Math.Abs(intervals[1].Start.SecondsSince(start) - exit.Seconds) <= 0.1);
    }

    /// <summary>掃描升交點赤經找到「有陰影 → 無陰影」的邊界，再二分到最深處恰為 −depthKm 的軌道。</summary>
    private static TleSet GrazingTle(double depthKm)
    {
        double MinMargin(double raan) => MinimumMargin(Sgp4Propagator.Create(Base with { RaanDeg = raan }));

        double lo = double.NaN, hi = double.NaN;
        var prev = MinMargin(0);
        for (var raan = 2.0; raan < 360; raan += 2.0)
        {
            var cur = MinMargin(raan);
            if (prev < 0 && cur >= 0)
            {
                (lo, hi) = (raan - 2.0, raan);
                break;
            }

            prev = cur;
        }

        Assert.False(double.IsNaN(lo), "找不到陰影消失的 β 角");
        for (var i = 0; i < 40; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (MinMargin(mid) < -depthKm)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return Base with { RaanDeg = 0.5 * (lo + hi) };
    }

    /// <summary>兩圈內陰影邊界函數的最小值：1 秒取樣後以黃金分割細化。</summary>
    private static double MinimumMargin(Sgp4Propagator prop)
    {
        var best = 0.0;
        var bestValue = double.MaxValue;
        for (var s = 0.0; s <= WindowSeconds; s += 1.0)
        {
            var m = MarginAt(prop, prop.Epoch, s);
            if (m < bestValue)
            {
                (best, bestValue) = (s, m);
            }
        }

        double a = best - 1, b = best + 1;
        var g = (Math.Sqrt(5) - 1) / 2;
        while (b - a > 1e-4)
        {
            var c = b - g * (b - a);
            var d = a + g * (b - a);
            if (MarginAt(prop, prop.Epoch, c) < MarginAt(prop, prop.Epoch, d))
            {
                b = d;
            }
            else
            {
                a = c;
            }
        }

        return Math.Min(bestValue, MarginAt(prop, prop.Epoch, 0.5 * (a + b)));
    }

    /// <summary>獨立的參考：每 0.05 秒以定義判斷是否在陰影內，跨越處二分到 1e-5 秒。</summary>
    private static List<(double Seconds, bool Entering)> BruteForceEvents(Sgp4Propagator prop, JulianDate start, double duration)
    {
        bool InShadow(double s)
        {
            var t = start.AddSeconds(s);
            Assert.Equal(Sgp4Error.None, prop.Propagate(t, out var st));
            return Shadow.IsInShadow(st.PositionKm, SunPosition.UnitVectorTeme(t));
        }

        var events = new List<(double, bool)>();
        var prev = InShadow(0);
        for (var s = 0.05; s <= duration; s += 0.05)
        {
            var cur = InShadow(s);
            if (cur != prev)
            {
                double lo = s - 0.05, hi = s;
                while (hi - lo > 1e-5)
                {
                    var mid = 0.5 * (lo + hi);
                    if (InShadow(mid) == prev)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                events.Add((0.5 * (lo + hi), cur));
            }

            prev = cur;
        }

        return events;
    }

    private static double ShortestShadowSeconds(List<(double Seconds, bool Entering)> events)
    {
        var shortest = double.MaxValue;
        for (var i = 0; i + 1 < events.Count; i++)
        {
            if (events[i].Entering && !events[i + 1].Entering)
            {
                shortest = Math.Min(shortest, events[i + 1].Seconds - events[i].Seconds);
            }
        }

        return shortest;
    }

    private static double MarginAt(Sgp4Propagator prop, JulianDate start, double s)
    {
        var t = start.AddSeconds(s);
        prop.Propagate(t, out var st);
        return Shadow.Margin(st.PositionKm, SunPosition.UnitVectorTeme(t));
    }
}
