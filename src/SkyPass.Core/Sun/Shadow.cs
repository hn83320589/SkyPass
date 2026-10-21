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

    /// <summary>
    /// <see cref="Margin"/> 的時間變化率（km/s），視太陽方向為定值：r·ŝ ≥ 0 側為 r·v/|r|，
    /// 否則為 r⊥·v⊥/|r⊥|（v⊥ = v − (v·ŝ)ŝ）。兩式在 r·ŝ = 0 處相等。只用來偵測區間內是否有極值。
    /// </summary>
    public static double MarginRateKmPerSec(in Vector3d rTemeKm, in Vector3d vTemeKmPerSec, in Vector3d sunUnit)
    {
        var d = rTemeKm.Dot(sunUnit);
        if (d >= 0.0)
        {
            return rTemeKm.Dot(vTemeKmPerSec) / rTemeKm.Norm;
        }

        var rPerp = rTemeKm - sunUnit * d;
        var vPerp = vTemeKmPerSec - sunUnit * vTemeKmPerSec.Dot(sunUnit);
        return rPerp.Dot(vPerp) / rPerp.Norm;
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

/// <summary>
/// 陰影進出時刻搜尋，做法與過境搜尋相同：
/// <list type="number">
/// <item>每 <see cref="DefaultStepSeconds"/> 秒取樣陰影邊界函數 m 與其變化率 m'。</item>
/// <item>m 變號 → 進入或離開陰影，二分法求到 1e-4 秒。</item>
/// <item>m 不變號但 m' 變號 → 區間內有極值。以黃金分割在 m 本身求出極值；若極小值 &lt; 0（兩端受日照、中間擦過地影），
/// 或極大值 ≥ 0（兩端在陰影、中間短暫受日照），就在極值兩側各求一次根。因此短於取樣步長的擦邊陰影也不會漏判。</item>
/// </list>
/// 只有在一個取樣間隔內同時出現極大與極小值時才可能漏判；近地軌道 m 的相鄰極值相隔約半圈（數十分鐘）。
/// </summary>
public static class ShadowSearch
{
    /// <summary>預設取樣步長（秒）。</summary>
    public const double DefaultStepSeconds = 10.0;

    private const double TimeToleranceSeconds = 1e-4;

    public static ShadowTimeline Find(Sgp4Propagator propagator, JulianDate start, JulianDate end, double stepSeconds = DefaultStepSeconds)
    {
        var total = end.SecondsSince(start);
        var events = new List<ShadowEvent>();
        var first = Evaluate(propagator, start, 0);
        if (first.Error != Sgp4Error.None)
        {
            return new ShadowTimeline(false, events, first.Error, start);
        }

        var inShadowAtStart = first.Margin < 0;
        var a = first;
        while (a.Offset < total)
        {
            var b = Evaluate(propagator, start, Math.Min(a.Offset + stepSeconds, total));
            if (b.Error != Sgp4Error.None)
            {
                return new ShadowTimeline(inShadowAtStart, events, b.Error, start.AddSeconds(a.Offset));
            }

            if ((a.Margin < 0) != (b.Margin < 0))
            {
                events.Add(new ShadowEvent(start.AddSeconds(Bisect(propagator, start, a, b)), b.Margin < 0));
            }
            else if (a.Rate < 0 && b.Rate > 0 && a.Margin >= 0)
            {
                // 兩端受日照、中間有極小值：可能是短暫的擦邊陰影
                var min = Golden(propagator, start, a.Offset, b.Offset, minimize: true);
                if (min.Margin < 0)
                {
                    events.Add(new ShadowEvent(start.AddSeconds(Bisect(propagator, start, a, min)), true));
                    events.Add(new ShadowEvent(start.AddSeconds(Bisect(propagator, start, min, b)), false));
                }
            }
            else if (a.Rate > 0 && b.Rate < 0 && a.Margin < 0)
            {
                // 兩端在陰影、中間有極大值：可能短暫受日照
                var max = Golden(propagator, start, a.Offset, b.Offset, minimize: false);
                if (max.Margin >= 0)
                {
                    events.Add(new ShadowEvent(start.AddSeconds(Bisect(propagator, start, a, max)), false));
                    events.Add(new ShadowEvent(start.AddSeconds(Bisect(propagator, start, max, b)), true));
                }
            }

            a = b;
        }

        return new ShadowTimeline(inShadowAtStart, events, Sgp4Error.None, null);
    }

    private readonly record struct Sample(double Offset, double Margin, double Rate, Sgp4Error Error);

    /// <summary>lo 與 hi 的 m 一負一非負；回傳跨越點的偏移秒數。</summary>
    private static double Bisect(Sgp4Propagator propagator, JulianDate start, Sample lo, Sample hi)
    {
        var loNegative = lo.Margin < 0;
        double l = lo.Offset, h = hi.Offset;
        while (h - l > TimeToleranceSeconds)
        {
            var mid = Evaluate(propagator, start, 0.5 * (l + h));
            if (mid.Error != Sgp4Error.None)
            {
                break;
            }

            if ((mid.Margin < 0) == loNegative)
            {
                l = mid.Offset;
            }
            else
            {
                h = mid.Offset;
            }
        }

        return 0.5 * (l + h);
    }

    /// <summary>以黃金分割在 m 本身求 [a, b] 內的極小（或極大）值。</summary>
    private static Sample Golden(Sgp4Propagator propagator, JulianDate start, double a, double b, bool minimize)
    {
        var sign = minimize ? 1.0 : -1.0;
        var g = (Math.Sqrt(5) - 1) / 2;
        var c = Evaluate(propagator, start, b - g * (b - a));
        var d = Evaluate(propagator, start, a + g * (b - a));
        while (b - a > TimeToleranceSeconds)
        {
            if (sign * c.Margin < sign * d.Margin)
            {
                b = d.Offset;
                d = c;
                c = Evaluate(propagator, start, b - g * (b - a));
            }
            else
            {
                a = c.Offset;
                c = d;
                d = Evaluate(propagator, start, a + g * (b - a));
            }
        }

        return Evaluate(propagator, start, 0.5 * (a + b));
    }

    private static Sample Evaluate(Sgp4Propagator propagator, JulianDate start, double offsetSeconds)
    {
        var time = start.AddSeconds(offsetSeconds);
        var err = propagator.Propagate(time, out var state);
        if (err != Sgp4Error.None)
        {
            return new Sample(offsetSeconds, double.NaN, double.NaN, err);
        }

        var sun = SunPosition.UnitVectorTeme(time);
        return new Sample(offsetSeconds, Shadow.Margin(state.PositionKm, sun), Shadow.MarginRateKmPerSec(state.PositionKm, state.VelocityKmPerSec, sun), err);
    }
}
