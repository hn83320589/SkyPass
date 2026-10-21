using SkyPass.Core.Coordinates;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;
using SkyPass.Core.Visibility;

namespace SkyPass.Core.Passes;

public sealed record PredictionRequest(IReadOnlyList<TleSet> Satellites, Geodetic Observer, JulianDate Start, JulianDate End, double MaskDeg)
{
    /// <summary>平行度上限；null 表示由執行環境決定。</summary>
    public int? MaxDegreeOfParallelism { get; init; }

    public bool ComputeVisibility { get; init; } = true;
}

public readonly record struct PredictionProgress(int Completed, int Total)
{
    public double Fraction => Total == 0 ? 1.0 : (double)Completed / Total;
}

/// <param name="Passes">依 AOS、再依 NORAD 編號排序。</param>
public sealed record PredictionResult(IReadOnlyList<SatellitePass> Passes, IReadOnlyList<PredictionDiagnostic> Diagnostics);

/// <summary>
/// 多顆衛星的過境預報。以衛星為單位平行處理；每顆衛星的計算是循序且決定性的，
/// 結果寫入以索引定位的陣列後穩定排序，因此結果與執行緒數無關。
/// 取消時擲出 <see cref="OperationCanceledException"/>，不回傳任何部分結果。
/// </summary>
public static class PassPredictor
{
    public static PredictionResult Predict(PredictionRequest request, IProgress<PredictionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        // 進入平行處理前先驗證，避免例外被包成 AggregateException
        if (request.End.SecondsSince(request.Start) < 0)
        {
            throw new ArgumentException($"時間範圍終點早於起點（{request.Start.ToUtc():O} → {request.End.ToUtc():O}）。", nameof(request));
        }

        if (request.MaskDeg is < -90 or >= 90 || double.IsNaN(request.MaskDeg))
        {
            throw new ArgumentException($"遮罩仰角 {request.MaskDeg} 不合法。", nameof(request));
        }

        var site = new ObserverSite(request.Observer);
        var count = request.Satellites.Count;
        var perSatellite = new (IReadOnlyList<SatellitePass> Passes, List<PredictionDiagnostic> Diagnostics)[count];
        var reporter = new MonotonicReporter(progress, count);
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = request.MaxDegreeOfParallelism ?? -1,
        };

        Parallel.For(0, count, options, i =>
        {
            perSatellite[i] = PredictOne(request, site, request.Satellites[i], cancellationToken);
            reporter.Increment();
        });

        cancellationToken.ThrowIfCancellationRequested();
        var passes = perSatellite.SelectMany(r => r.Passes)
            .OrderBy(p => p.Aos.Day).ThenBy(p => p.Aos.Fraction).ThenBy(p => p.NoradId)
            .ToList();
        var diagnostics = perSatellite.SelectMany(r => r.Diagnostics).ToList();
        return new PredictionResult(passes, diagnostics);
    }

    private static (IReadOnlyList<SatellitePass>, List<PredictionDiagnostic>) PredictOne(
        PredictionRequest request, ObserverSite site, TleSet tle, CancellationToken ct)
    {
        var diagnostics = new List<PredictionDiagnostic>();
        var staleDays = Math.Max(Math.Abs(request.Start.SecondsSince(tle.Epoch)), Math.Abs(request.End.SecondsSince(tle.Epoch))) / 86400.0;
        if (staleDays > CatalogEntry.StaleThreshold.TotalDays && !tle.IsDeepSpace)
        {
            diagnostics.Add(new PredictionDiagnostic(tle.NoradId, tle.Name, DiagnosticKind.StaleElements, null, Sgp4Error.None,
                $"{tle.Name}（NORAD {tle.NoradId}）軌道資料陳舊：預測時間與 epoch（{tle.EpochUtc:yyyy-MM-dd HH:mm} UTC）相差 {staleDays:F1} 天。"));
        }

        var propagator = Sgp4Propagator.Create(tle);
        var result = PassFinder.Find(propagator, site, request.Start, request.End, request.MaskDeg, ct);
        if (result.Diagnostic is not null)
        {
            diagnostics.Add(result.Diagnostic);
        }

        IReadOnlyList<SatellitePass> passes = result.Passes;
        if (request.ComputeVisibility)
        {
            passes = passes
                .Select(p => p with { VisibleIntervals = VisibilityCalculator.ComputeIntervals(propagator, site, p.Aos, p.Los) })
                .ToList();
        }

        return (passes, diagnostics);
    }

    /// <summary>以鎖序列化回報，保證回報的完成數嚴格遞增。</summary>
    private sealed class MonotonicReporter(IProgress<PredictionProgress>? progress, int total)
    {
        private readonly Lock _gate = new();
        private int _completed;

        public void Increment()
        {
            lock (_gate)
            {
                _completed++;
                progress?.Report(new PredictionProgress(_completed, total));
            }
        }
    }
}
