using SkyPass.Core.Coordinates;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;

namespace SkyPass.Tests.Reference;

/// <summary>
/// 暴力過境參考解（驗收 #9 的比對基準）。
/// <para>
/// 刻意與正式程式碼的 <c>SkyPass.Core.Passes</c> 互相獨立：只共用傳播與座標轉換（<see cref="Observation"/>），
/// 演算法則完全不同——從視窗起點逐秒取樣仰角，以二分法細化 AOS/LOS，以黃金分割法細化最大仰角。
/// 不使用仰角變化率，也不依賴任何取樣格點對齊。
/// </para>
/// </summary>
public static class BruteForcePassFinder
{
    public sealed record RefPass(
        double AosSec, double LosSec, double MaxSec, double MaxElevationDeg,
        bool StartedBeforeWindow, bool EndsAfterWindow);

    /// <summary>回傳相對於 <paramref name="start"/> 的秒數。</summary>
    public static List<RefPass> Find(Sgp4Propagator prop, ObserverSite site, JulianDate start, double durationSec, double maskDeg)
    {
        double F(double s)
        {
            var err = Observation.Compute(prop, site, start.AddSeconds(s), out var o);
            if (err != Sgp4Error.None)
            {
                throw new InvalidOperationException($"參考解遇到傳播錯誤 {err}（t = {s} s）");
            }

            return o.Look.ElevationDeg - maskDeg;
        }

        var times = new List<double>();
        for (var s = 0.0; s < durationSec; s += 1.0)
        {
            times.Add(s);
        }

        times.Add(durationSec);
        var values = times.Select(F).ToArray();

        var passes = new List<RefPass>();
        var i = 0;
        while (i < times.Count)
        {
            if (values[i] < 0)
            {
                i++;
                continue;
            }

            var startedBefore = i == 0;
            var aos = startedBefore ? 0.0 : Bisect(F, times[i - 1], times[i]);
            var j = i;
            while (j + 1 < times.Count && values[j + 1] >= 0)
            {
                j++;
            }

            var endsAfter = j == times.Count - 1;
            var los = endsAfter ? durationSec : Bisect(F, times[j], times[j + 1]);

            var best = i;
            for (var k = i; k <= j; k++)
            {
                if (values[k] > values[best])
                {
                    best = k;
                }
            }

            var lo = Math.Max(aos, times[best] - 1.0);
            var hi = Math.Min(los, times[best] + 1.0);
            var maxSec = GoldenMax(F, lo, hi);
            var maxVal = F(maxSec);
            if (values[best] > maxVal)
            {
                maxSec = times[best];
                maxVal = values[best];
            }

            passes.Add(new RefPass(aos, los, maxSec, maxVal + maskDeg, startedBefore, endsAfter));
            i = j + 1;
        }

        return passes;
    }

    private static double Bisect(Func<double, double> f, double below, double above)
    {
        // f(below) < 0 ≤ f(above) 或相反；以符號判斷
        var fLo = f(below);
        var lo = below;
        var hi = above;
        while (hi - lo > 1e-5)
        {
            var mid = 0.5 * (lo + hi);
            if ((f(mid) < 0) == (fLo < 0))
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return 0.5 * (lo + hi);
    }

    private static double GoldenMax(Func<double, double> f, double a, double b)
    {
        var invPhi = (Math.Sqrt(5) - 1) / 2;
        var c = b - invPhi * (b - a);
        var d = a + invPhi * (b - a);
        double fc = f(c), fd = f(d);
        while (b - a > 1e-4)
        {
            if (fc > fd)
            {
                b = d;
                d = c;
                fd = fc;
                c = b - invPhi * (b - a);
                fc = f(c);
            }
            else
            {
                a = c;
                c = d;
                fc = fd;
                d = a + invPhi * (b - a);
                fd = f(d);
            }
        }

        return 0.5 * (a + b);
    }
}
