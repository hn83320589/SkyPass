using SkyPass.Core.Coordinates;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;

namespace SkyPass.Core.Passes;

/// <summary>
/// 單一衛星的過境搜尋。
/// <list type="number">
/// <item>取樣時刻對齊全域格點（1970-01-01 起每 <see cref="SampleStepSeconds"/> 秒），再加上時間範圍的起訖點；
/// 因此把時間範圍拆成兩段預測時，兩段用到的樣本與一次預測相同。</item>
/// <item>每個樣本計算 f = 仰角 − 遮罩，以及解析的仰角變化率 f'。</item>
/// <item>f 變號 → AOS／LOS，以二分法求到 1 毫秒。</item>
/// <item>f' 由正轉負 → 區間內有仰角極大值，二分求 f' 的根。即使區間兩端都在遮罩之下，
/// 只要極大值 ≥ 遮罩就是一次掠過過境——這是不漏掉掠過過境的關鍵。
/// 只有在一個取樣間隔內同時出現極大與極小值時才可能漏判，而近地軌道相鄰仰角極值相隔遠大於 30 秒。</item>
/// <item>傳播失敗時二分出失敗時刻（0.5 秒內），在該時刻停止並產生診斷。</item>
/// </list>
/// </summary>
public static class PassFinder
{
    public const double SampleStepSeconds = 30.0;

    private const double RootToleranceSeconds = 1e-3;
    private const double DecayToleranceSeconds = 0.5;
    private const int CancellationCheckInterval = 256;

    private static readonly JulianDate GridAnchor = JulianDate.FromUtc(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc));

    public static PassSearchResult Find(
        Sgp4Propagator propagator, ObserverSite site, JulianDate start, JulianDate end, double maskDeg,
        CancellationToken cancellationToken = default)
    {
        var duration = end.SecondsSince(start);
        if (duration < 0)
        {
            throw new ArgumentException($"時間範圍終點早於起點（{start.ToUtc():O} → {end.ToUtc():O}）。", nameof(end));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var tle = propagator.Tle;
        if (!propagator.IsSupported)
        {
            return new PassSearchResult([], new PredictionDiagnostic(tle.NoradId, tle.Name, DiagnosticKind.Unsupported, null,
                Sgp4Error.DeepSpaceNotSupported, $"{tle.Name}（NORAD {tle.NoradId}）週期 {tle.PeriodMinutes:F1} 分鐘，屬深空軌道，不支援預測。"));
        }

        var scan = new Scan(propagator, site, maskDeg, start);
        return scan.Run(end, duration, cancellationToken);
    }

    /// <summary>單一時刻的取樣結果。</summary>
    private readonly record struct Sample(JulianDate Time, double F, double FDot, double AzimuthDeg, Sgp4Error Error);

    private sealed class Scan(Sgp4Propagator propagator, ObserverSite site, double maskDeg, JulianDate start)
    {
        private readonly List<SatellitePass> _passes = [];
        private OpenPass? _open;

        public PassSearchResult Run(JulianDate end, double duration, CancellationToken ct)
        {
            var first = Eval(start);
            if (first.Error != Sgp4Error.None)
            {
                return new PassSearchResult([], Diagnose(first.Error, start));
            }

            if (first.F >= 0)
            {
                _open = new OpenPass(first, startedBeforeWindow: true);
            }

            if (duration == 0)
            {
                if (_open is not null)
                {
                    Close(first, endsAfterWindow: true);
                }

                return new PassSearchResult(_passes, null);
            }

            var prev = first;
            var gridIndex = (long)Math.Floor(start.SecondsSince(GridAnchor) / SampleStepSeconds) + 1;
            var count = 0;
            while (true)
            {
                if (++count % CancellationCheckInterval == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                var t = GridAnchor.AddSeconds(gridIndex * SampleStepSeconds);
                var isEnd = end.SecondsSince(t) <= 0;
                if (isEnd)
                {
                    t = end;
                }

                var next = Eval(t);
                if (next.Error != Sgp4Error.None)
                {
                    var lastGood = FindLastGood(prev, next);
                    if (_open is not null)
                    {
                        _open.Consider(lastGood);
                        _passes.Add(_open.Build(propagator.Tle, lastGood, maskDeg, endsAfterWindow: false, endedByDecay: true));
                    }

                    return new PassSearchResult(_passes, Diagnose(next.Error, lastGood.Time));
                }

                ProcessInterval(prev, next);
                prev = next;
                gridIndex++;
                if (isEnd)
                {
                    break;
                }
            }

            if (_open is not null)
            {
                Close(prev, endsAfterWindow: true);
            }

            return new PassSearchResult(_passes, null);
        }

        private void ProcessInterval(Sample a, Sample b)
        {
            Sample? max = a.FDot > 0 && b.FDot <= 0 ? RefineMax(a, b) : null;

            if (_open is null)
            {
                if (b.F >= 0)
                {
                    // AOS 在 (a, b]；若區間內有極大值，極大值必 ≥ f(b) ≥ 0，AOS 在 (a, max]
                    var aos = RefineCrossing(a, max ?? b);
                    _open = new OpenPass(aos, startedBeforeWindow: false);
                    _open.Consider(max);
                    _open.Consider(b);
                }
                else if (max is { F: >= 0 } m)
                {
                    // 兩端都在遮罩下、中間短暫升起的掠過過境
                    _open = new OpenPass(RefineCrossing(a, m), startedBeforeWindow: false);
                    _open.Consider(m);
                    Close(RefineCrossing(m, b), endsAfterWindow: false);
                }

                return;
            }

            _open.Consider(max);
            if (b.F < 0)
            {
                Close(RefineCrossing(max ?? a, b), endsAfterWindow: false);
            }
            else
            {
                _open.Consider(b);
            }
        }

        private void Close(Sample los, bool endsAfterWindow)
        {
            _open!.Consider(los);
            _passes.Add(_open.Build(propagator.Tle, los, maskDeg, endsAfterWindow, endedByDecay: false));
            _open = null;
        }

        /// <summary>f(lo) 與 f(hi) 一個 &lt; 0、一個 ≥ 0；回傳跨越點。</summary>
        private Sample RefineCrossing(Sample lo, Sample hi)
        {
            var loAbove = lo.F >= 0;
            while (hi.Time.SecondsSince(lo.Time) > RootToleranceSeconds)
            {
                var mid = Eval(Mid(lo.Time, hi.Time));
                if (mid.Error != Sgp4Error.None)
                {
                    break;
                }

                if ((mid.F >= 0) == loAbove)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            return Eval(Mid(lo.Time, hi.Time));
        }

        /// <summary>f'(lo) &gt; 0 ≥ f'(hi)；回傳極大值。</summary>
        private Sample RefineMax(Sample lo, Sample hi)
        {
            while (hi.Time.SecondsSince(lo.Time) > RootToleranceSeconds)
            {
                var mid = Eval(Mid(lo.Time, hi.Time));
                if (mid.Error != Sgp4Error.None)
                {
                    break;
                }

                if (mid.FDot > 0)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            var m = Eval(Mid(lo.Time, hi.Time));
            return m.F >= lo.F && m.F >= hi.F ? m : lo.F >= hi.F ? lo : hi;
        }

        private Sample FindLastGood(Sample good, Sample bad)
        {
            var badTime = bad.Time;
            while (badTime.SecondsSince(good.Time) > DecayToleranceSeconds)
            {
                var mid = Eval(Mid(good.Time, badTime));
                if (mid.Error == Sgp4Error.None)
                {
                    good = mid;
                }
                else
                {
                    badTime = mid.Time;
                }
            }

            return good;
        }

        private static JulianDate Mid(JulianDate a, JulianDate b) => a.AddSeconds(0.5 * b.SecondsSince(a));

        private Sample Eval(JulianDate t)
        {
            var error = Observation.Compute(propagator, site, t, out var o);
            return error == Sgp4Error.None
                ? new Sample(t, o.Look.ElevationDeg - maskDeg, o.Look.ElevationRateDegPerSec, o.Look.AzimuthDeg, error)
                : new Sample(t, double.NaN, double.NaN, double.NaN, error);
        }

        private PredictionDiagnostic Diagnose(Sgp4Error error, JulianDate time)
        {
            var tle = propagator.Tle;
            var kind = error == Sgp4Error.Decayed ? DiagnosticKind.Decayed : DiagnosticKind.PropagationError;
            var what = kind == DiagnosticKind.Decayed ? "衰減"
                : error == Sgp4Error.MeanEccentricity ? "傳播失敗（平均偏心率超出範圍，通常表示阻力已使軌道衰減）"
                : "傳播失敗";
            return new PredictionDiagnostic(tle.NoradId, tle.Name, kind, time, error,
                $"{tle.Name}（NORAD {tle.NoradId}）於 {time.ToUtc():yyyy-MM-dd HH:mm:ss} UTC {what}（SGP4 錯誤碼 {(int)error}：{error}），預測在此停止。");
        }

        private sealed class OpenPass(Sample aos, bool startedBeforeWindow)
        {
            private readonly Sample _aos = aos;
            private Sample _max = aos;

            public void Consider(Sample? s)
            {
                if (s is { } v && v.F > _max.F)
                {
                    _max = v;
                }
            }

            public SatellitePass Build(Tle.TleSet tle, Sample los, double maskDeg, bool endsAfterWindow, bool endedByDecay) => new()
            {
                NoradId = tle.NoradId,
                SatelliteName = tle.Name,
                Aos = _aos.Time,
                AosAzimuthDeg = _aos.AzimuthDeg,
                MaxElevationTime = _max.Time,
                MaxElevationDeg = _max.F + maskDeg,
                MaxElevationAzimuthDeg = _max.AzimuthDeg,
                Los = los.Time,
                LosAzimuthDeg = los.AzimuthDeg,
                StartedBeforeWindow = startedBeforeWindow,
                EndsAfterWindow = endsAfterWindow,
                EndedByDecay = endedByDecay,
            };
        }
    }
}
