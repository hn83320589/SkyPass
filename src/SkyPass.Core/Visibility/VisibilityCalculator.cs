using SkyPass.Core.Coordinates;
using SkyPass.Core.Propagation;
using SkyPass.Core.Sun;
using SkyPass.Core.Time;

namespace SkyPass.Core.Visibility;

/// <summary>
/// 可目視：衛星受日照（不在圓柱陰影內）且觀測點太陽仰角 &lt; −6°。
/// 在過境期間每 <see cref="StepSeconds"/> 取樣兩個連續函數（陰影邊界距離、太陽仰角 − 門檻），
/// 各自變號處以二分法求根，再依各子區間中點判斷是否可目視並合併。總長不足 1 秒視為不可目視。
/// </summary>
public static class VisibilityCalculator
{
    public const double SunElevationThresholdDeg = -6.0;
    public const double MinimumVisibleSeconds = 1.0;

    private const double StepSeconds = 10.0;
    private const double RootToleranceSeconds = 1e-3;

    public static IReadOnlyList<TimeInterval> ComputeIntervals(Sgp4Propagator propagator, ObserverSite site, JulianDate from, JulianDate to)
    {
        var total = to.SecondsSince(from);
        if (total <= 0)
        {
            return [];
        }

        var boundaries = new List<double> { 0.0, total };
        var prev = Evaluate(propagator, site, from, 0.0);
        if (prev is null)
        {
            return [];
        }

        for (var k = 1; ; k++)
        {
            var offset = Math.Min(k * StepSeconds, total);
            var next = Evaluate(propagator, site, from, offset);
            if (next is null)
            {
                total = prev.Value.Offset;
                break;
            }

            if ((prev.Value.ShadowMargin < 0) != (next.Value.ShadowMargin < 0))
            {
                boundaries.Add(Bisect(propagator, site, from, prev.Value, next.Value, static e => e.ShadowMargin));
            }

            if ((prev.Value.SunMargin < 0) != (next.Value.SunMargin < 0))
            {
                boundaries.Add(Bisect(propagator, site, from, prev.Value, next.Value, static e => e.SunMargin));
            }

            prev = next.Value;
            if (offset >= total)
            {
                break;
            }
        }

        boundaries.Sort();
        var intervals = new List<TimeInterval>();
        double? openStart = null;
        for (var i = 0; i + 1 < boundaries.Count && boundaries[i] < total; i++)
        {
            var a = boundaries[i];
            var b = Math.Min(boundaries[i + 1], total);
            if (b <= a)
            {
                continue;
            }

            var mid = Evaluate(propagator, site, from, 0.5 * (a + b));
            var visible = mid is { ShadowMargin: >= 0, SunMargin: < 0 };
            if (visible)
            {
                openStart ??= a;
            }
            else if (openStart is { } s)
            {
                intervals.Add(new TimeInterval(from.AddSeconds(s), from.AddSeconds(a)));
                openStart = null;
            }
        }

        if (openStart is { } last)
        {
            intervals.Add(new TimeInterval(from.AddSeconds(last), from.AddSeconds(total)));
        }

        return intervals.Sum(iv => iv.DurationSeconds) >= MinimumVisibleSeconds ? intervals : [];
    }

    /// <param name="ShadowMargin">陰影邊界距離（km），≥ 0 表示受日照。</param>
    /// <param name="SunMargin">太陽仰角 − 門檻（度），&lt; 0 表示天色夠暗。</param>
    private readonly record struct Eval(double Offset, double ShadowMargin, double SunMargin);

    private static Eval? Evaluate(Sgp4Propagator propagator, ObserverSite site, JulianDate from, double offset)
    {
        var t = from.AddSeconds(offset);
        if (propagator.Propagate(t, out var state) != Sgp4Error.None)
        {
            return null;
        }

        var sun = SunPosition.UnitVectorTeme(t);
        return new Eval(offset, Shadow.Margin(state.PositionKm, sun), SunPosition.ElevationDeg(site, t) - SunElevationThresholdDeg);
    }

    private static double Bisect(Sgp4Propagator propagator, ObserverSite site, JulianDate from, Eval lo, Eval hi, Func<Eval, double> g)
    {
        var loNegative = g(lo) < 0;
        while (hi.Offset - lo.Offset > RootToleranceSeconds)
        {
            var mid = Evaluate(propagator, site, from, 0.5 * (lo.Offset + hi.Offset));
            if (mid is null)
            {
                break;
            }

            if ((g(mid.Value) < 0) == loNegative)
            {
                lo = mid.Value;
            }
            else
            {
                hi = mid.Value;
            }
        }

        return 0.5 * (lo.Offset + hi.Offset);
    }
}
