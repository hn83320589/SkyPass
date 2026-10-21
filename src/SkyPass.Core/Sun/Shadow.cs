using SkyPass.Core.Mathematics;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;

namespace SkyPass.Core.Sun;

/// <summary>圓柱陰影模型。</summary>
public static class Shadow
{
    public const double EarthRadiusKm = 6378.137;

    /// <summary>r·ŝ &lt; 0 且 |r − (r·ŝ)ŝ| &lt; 地球半徑時在陰影內。</summary>
    public static bool IsInShadow(in Vector3d rTemeKm, in Vector3d sunUnit)
    {
        var d = rTemeKm.Dot(sunUnit);
        return d < 0.0 && (rTemeKm - sunUnit * d).Norm < EarthRadiusKm;
    }

    /// <summary>
    /// 連續的陰影邊界函數（km），負值表示在陰影內。在 r·ŝ &lt; 0 側為 |r⊥| − Re，否則為 |r| − Re；
    /// 兩者在 r·ŝ = 0 處相等，因此函數連續，可用來求根。衛星在地球外（|r| &gt; Re）時與 <see cref="IsInShadow"/> 一致。
    /// </summary>
    public static double Margin(in Vector3d rTemeKm, in Vector3d sunUnit)
    {
        var d = rTemeKm.Dot(sunUnit);
        return d >= 0.0
            ? rTemeKm.Norm - EarthRadiusKm
            : (rTemeKm - sunUnit * d).Norm - EarthRadiusKm;
    }
}

public readonly record struct ShadowEvent(JulianDate Time, bool Entering);

/// <param name="InShadowAtStart">搜尋起點時是否在陰影內。</param>
/// <param name="Events">依時間排序的進入／離開事件。</param>
/// <param name="StopError">傳播失敗（例如衰減）時的錯誤碼；搜尋在 <paramref name="StoppedAt"/> 停止。</param>
public sealed record ShadowTimeline(bool InShadowAtStart, IReadOnlyList<ShadowEvent> Events, Sgp4Error StopError, JulianDate? StoppedAt)
{
    /// <summary>把事件轉成 [start, end] 內的陰影區間。</summary>
    public IReadOnlyList<TimeInterval> ShadowIntervals(JulianDate start, JulianDate end)
    {
        var result = new List<TimeInterval>();
        var limit = StoppedAt ?? end;
        JulianDate? open = InShadowAtStart ? start : null;
        foreach (var e in Events)
        {
            if (e.Entering)
            {
                open = e.Time;
            }
            else if (open is { } s)
            {
                result.Add(new TimeInterval(s, e.Time));
                open = null;
            }
        }

        if (open is { } last)
        {
            result.Add(new TimeInterval(last, limit));
        }

        return result;
    }
}

public static class ShadowSearch
{
    /// <summary>預設取樣步長（秒）。短於此長度的陰影區段可能漏判（已知限制）。</summary>
    public const double DefaultStepSeconds = 10.0;

    private const double TimeToleranceSeconds = 1e-4;

    public static ShadowTimeline Find(Sgp4Propagator propagator, JulianDate start, JulianDate end, double stepSeconds = DefaultStepSeconds)
    {
        var total = end.SecondsSince(start);
        var events = new List<ShadowEvent>();
        if (MarginAt(propagator, start, 0, out var m0) is var err0 && err0 != Sgp4Error.None)
        {
            return new ShadowTimeline(false, events, err0, start);
        }

        var inShadowAtStart = m0 < 0;
        var t = 0.0;
        var m = m0;
        while (t < total)
        {
            var t2 = Math.Min(t + stepSeconds, total);
            var err = MarginAt(propagator, start, t2, out var m2);
            if (err != Sgp4Error.None)
            {
                return new ShadowTimeline(inShadowAtStart, events, err, start.AddSeconds(t));
            }

            if ((m < 0) != (m2 < 0))
            {
                var root = Refine(propagator, start, t, m, t2);
                events.Add(new ShadowEvent(start.AddSeconds(root), m2 < 0));
            }

            t = t2;
            m = m2;
        }

        return new ShadowTimeline(inShadowAtStart, events, Sgp4Error.None, null);
    }

    private static double Refine(Sgp4Propagator propagator, JulianDate start, double lo, double mLo, double hi)
    {
        while (hi - lo > TimeToleranceSeconds)
        {
            var mid = 0.5 * (lo + hi);
            if (MarginAt(propagator, start, mid, out var mMid) != Sgp4Error.None)
            {
                break;
            }

            if ((mMid < 0) == (mLo < 0))
            {
                lo = mid;
                mLo = mMid;
            }
            else
            {
                hi = mid;
            }
        }

        return 0.5 * (lo + hi);
    }

    private static Sgp4Error MarginAt(Sgp4Propagator propagator, JulianDate start, double offsetSeconds, out double margin)
    {
        var time = start.AddSeconds(offsetSeconds);
        var err = propagator.Propagate(time, out var state);
        margin = err == Sgp4Error.None ? Shadow.Margin(state.PositionKm, SunPosition.UnitVectorTeme(time)) : double.NaN;
        return err;
    }
}
