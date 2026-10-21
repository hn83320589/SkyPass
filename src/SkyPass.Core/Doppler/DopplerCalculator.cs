using SkyPass.Core.Coordinates;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;

namespace SkyPass.Core.Doppler;

/// <param name="ReceivedHz">接收頻率 f0 × (1 − ṙ / c)。</param>
public readonly record struct DopplerSample(JulianDate Time, double RangeRateKmPerSec, double TransmitHz, double ReceivedHz)
{
    /// <summary>頻移（接收 − 發射），接近時為正。</summary>
    public double ShiftHz => ReceivedHz - TransmitHz;
}

public static class DopplerCalculator
{
    public const double SpeedOfLightKmPerSec = 299792.458;

    private const double RootToleranceSeconds = 1e-6;

    public static double ReceivedHz(double transmitHz, double rangeRateKmPerSec) =>
        transmitHz * (1.0 - rangeRateKmPerSec / SpeedOfLightKmPerSec);

    /// <summary>指定時刻的頻移；傳播失敗時回傳 null。</summary>
    public static DopplerSample? At(Sgp4Propagator propagator, ObserverSite site, JulianDate time, double transmitHz)
    {
        if (Observation.Compute(propagator, site, time, out var o) != Sgp4Error.None)
        {
            return null;
        }

        return new DopplerSample(time, o.Look.RangeRateKmPerSec, transmitHz, ReceivedHz(transmitHz, o.Look.RangeRateKmPerSec));
    }

    /// <summary>[from, to] 的接收頻率曲線：自 from 起每 <paramref name="stepSeconds"/> 一點，並包含 to。遇到傳播失敗即停止。</summary>
    public static IReadOnlyList<DopplerSample> Curve(
        Sgp4Propagator propagator, ObserverSite site, JulianDate from, JulianDate to, double transmitHz, double stepSeconds)
    {
        if (stepSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepSeconds), stepSeconds, "取樣間隔必須大於 0。");
        }

        var total = to.SecondsSince(from);
        var result = new List<DopplerSample>();
        for (var k = 0; ; k++)
        {
            var offset = Math.Min(k * stepSeconds, total);
            var sample = At(propagator, site, from.AddSeconds(offset), transmitHz);
            if (sample is null)
            {
                break;
            }

            result.Add(sample.Value);
            if (offset >= total)
            {
                break;
            }
        }

        return result;
    }

    /// <summary>在 [from, to] 中距離變化率由負轉正（頻移為零）的時刻；不存在時回傳 null。</summary>
    public static JulianDate? FindZeroShift(Sgp4Propagator propagator, ObserverSite site, JulianDate from, JulianDate to)
    {
        double RangeRate(JulianDate t) =>
            Observation.Compute(propagator, site, t, out var o) == Sgp4Error.None ? o.Look.RangeRateKmPerSec : double.NaN;

        var lo = from;
        var hi = to;
        if (!(RangeRate(lo) < 0 && RangeRate(hi) > 0))
        {
            return null;
        }

        while (hi.SecondsSince(lo) > RootToleranceSeconds)
        {
            var mid = lo.AddSeconds(0.5 * hi.SecondsSince(lo));
            if (RangeRate(mid) < 0)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return lo.AddSeconds(0.5 * hi.SecondsSince(lo));
    }
}
