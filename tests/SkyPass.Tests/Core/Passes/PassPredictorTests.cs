using SkyPass.Core.Coordinates;
using SkyPass.Core.Passes;
using SkyPass.Core.Tle;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Passes;

/// <summary>驗收 #16：結果與執行緒數無關、取消不留半套結果、進度單調遞增。</summary>
public class PassPredictorTests
{
    private static readonly Geodetic Taipei = new(25.0330, 121.5654, 0.01);

    internal static IReadOnlyList<TleSet> Constellation(int count)
    {
        var real = PassScenario.Satellites.Take(14).ToList();
        return Enumerable.Range(0, count).Select(k =>
        {
            var b = real[k % real.Count];
            return k < real.Count ? b : b with
            {
                NoradId = 80000 + k,
                Name = $"{b.Name} #{k}",
                RaanDeg = (b.RaanDeg + 17.0 * k) % 360.0,
                MeanAnomalyDeg = (b.MeanAnomalyDeg + 31.0 * k) % 360.0,
            };
        }).ToList();
    }

    private static PredictionRequest Request(IReadOnlyList<TleSet> sats, double days, int? threads = null) =>
        new(sats, Taipei, sats[0].Epoch, sats[0].Epoch.AddSeconds(days * 86400), 10.0) { MaxDegreeOfParallelism = threads };

    [Fact]
    public void Results_are_bitwise_identical_for_1_and_N_threads()
    {
        var sats = Constellation(30);

        var one = PassPredictor.Predict(Request(sats, 2, threads: 1));
        var many = PassPredictor.Predict(Request(sats, 2, threads: Environment.ProcessorCount));

        Assert.Equal(one.Passes.Count, many.Passes.Count);
        Assert.True(one.Passes.Count > 50);
        for (var i = 0; i < one.Passes.Count; i++)
        {
            var a = one.Passes[i];
            var b = many.Passes[i];
            Assert.Equal(a with { VisibleIntervals = [] }, b with { VisibleIntervals = [] });
            Assert.Equal(a.VisibleIntervals, b.VisibleIntervals);
        }
    }

    [Fact]
    public void Property_result_is_independent_of_thread_count()
    {
        var pool = Constellation(40);
        Property.Run("結果與執行緒數無關（驗收 #16、#21）", seed: 16001, iterations: 12, rng =>
        {
            var sats = pool.OrderBy(_ => rng.Next()).Take(rng.Next(3, 15)).ToList();
            var site = new Geodetic(rng.NextDouble() * 140 - 70, rng.NextDouble() * 360 - 180, 0);
            var start = sats[0].Epoch.AddSeconds(rng.NextDouble() * 86400);
            var request = new PredictionRequest(sats, site, start, start.AddSeconds(86400 * (0.5 + rng.NextDouble())), rng.NextDouble() * 20);

            var one = PassPredictor.Predict(request with { MaxDegreeOfParallelism = 1 });
            var many = PassPredictor.Predict(request with { MaxDegreeOfParallelism = rng.Next(2, 2 * Environment.ProcessorCount + 1) });

            Assert.Equal(one.Passes.Count, many.Passes.Count);
            for (var i = 0; i < one.Passes.Count; i++)
            {
                Assert.Equal(one.Passes[i] with { VisibleIntervals = [] }, many.Passes[i] with { VisibleIntervals = [] });
                Assert.Equal(one.Passes[i].VisibleIntervals, many.Passes[i].VisibleIntervals);
            }

            Assert.Equal(one.Diagnostics, many.Diagnostics);
        });
    }

    [Fact]
    public void Passes_are_sorted_by_aos_then_norad()
    {
        var result = PassPredictor.Predict(Request(Constellation(20), 1));

        for (var i = 1; i < result.Passes.Count; i++)
        {
            var prev = result.Passes[i - 1];
            var cur = result.Passes[i];
            var dt = cur.Aos.SecondsSince(prev.Aos);
            Assert.True(dt > 0 || (dt == 0 && cur.NoradId >= prev.NoradId));
        }
    }

    [Fact]
    public void Progress_is_monotonic_and_completes()
    {
        var reports = new List<PredictionProgress>();
        var sink = new SynchronousProgress<PredictionProgress>(reports.Add);

        PassPredictor.Predict(Request(Constellation(40), 1), sink);

        Assert.NotEmpty(reports);
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].Completed > reports[i - 1].Completed);
        }

        Assert.Equal(40, reports[^1].Completed);
        Assert.Equal(1.0, reports[^1].Fraction);
    }

    [Fact]
    public void Cancellation_throws_and_returns_no_partial_result()
    {
        using var cts = new CancellationTokenSource();
        var sink = new SynchronousProgress<PredictionProgress>(p =>
        {
            if (p.Completed >= 3)
            {
                cts.Cancel();
            }
        });
        PredictionResult? result = null;

        Assert.ThrowsAny<OperationCanceledException>(() => result = PassPredictor.Predict(Request(Constellation(200), 7), sink, cts.Token));
        Assert.Null(result);
    }

    [Fact]
    public void Diagnostics_for_unsupported_decayed_and_stale_satellites()
    {
        var iss = PassScenario.Satellites.First(s => s.NoradId == 25544);
        var decaying = Sgp4ReferenceRow.Load("sgp4_published.csv").First(r => r.Case == "VER-28872").Tle;
        var geo = iss with { NoradId = 40000, Name = "GEO", MeanMotionRevPerDay = 1.0027 };
        var request = new PredictionRequest([iss, geo], Taipei, iss.Epoch.AddSeconds(20 * 86400), iss.Epoch.AddSeconds(21 * 86400), 10);

        var result = PassPredictor.Predict(request);
        var decayResult = PassPredictor.Predict(new PredictionRequest([decaying], Taipei, decaying.Epoch, decaying.Epoch.AddSeconds(86400), 0));

        Assert.Contains(result.Diagnostics, d => d.NoradId == 40000 && d.Kind == DiagnosticKind.Unsupported);
        Assert.Contains(result.Diagnostics, d => d.NoradId == 25544 && d.Kind == DiagnosticKind.StaleElements);
        Assert.DoesNotContain(result.Passes, p => p.NoradId == 40000);
        Assert.Contains(result.Passes, p => p.NoradId == 25544); // 陳舊只警告，仍然預測
        Assert.Contains(decayResult.Diagnostics, d => d.Kind == DiagnosticKind.Decayed);
    }

    [Fact]
    public void Passes_include_visibility_intervals()
    {
        var result = PassPredictor.Predict(Request(Constellation(14), 3));

        Assert.Contains(result.Passes, p => p.VisibleIntervals.Count > 0);
        Assert.All(result.Passes.SelectMany(p => p.VisibleIntervals.Select(iv => (p, iv))), x =>
        {
            Assert.True(x.iv.Start.SecondsSince(x.p.Aos) >= -1e-6);
            Assert.True(x.p.Los.SecondsSince(x.iv.End) >= -1e-6);
        });
    }
}

/// <summary>在呼叫端執行緒同步回報（<see cref="Progress{T}"/> 會非同步排程，順序不保證）。</summary>
public sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
