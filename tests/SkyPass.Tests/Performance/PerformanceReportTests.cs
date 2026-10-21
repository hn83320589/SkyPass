using System.Diagnostics;
using SkyPass.Core.Coordinates;
using SkyPass.Core.Passes;
using SkyPass.Core.Propagation;
using SkyPass.Tests.Core.Passes;
using SkyPass.Tests.Support;
using Xunit.Abstractions;

namespace SkyPass.Tests.Performance;

/// <summary>產生 README 的效能數據（以 Release 建置執行：dotnet test -c Release --filter Category=Performance）。</summary>
[Trait("Category", "Performance")]
[Collection(PerformanceCollection.Name)]
public class PerformanceReportTests(ITestOutputHelper output)
{
    [Fact]
    public void Thread_scaling_report()
    {
        var sats = PassPredictorTests.Constellation(100);
        var request = new PredictionRequest(sats, new Geodetic(25.0330, 121.5654, 0.01), sats[0].Epoch, sats[0].Epoch.AddSeconds(7 * 86400), 10.0);
        PassPredictor.Predict(request with { Satellites = sats.Take(8).ToList() });

        foreach (var threads in new[] { 1, 2, 4, 8, Environment.ProcessorCount }.Distinct())
        {
            var best = double.MaxValue;
            var passes = 0;
            for (var run = 0; run < 2; run++)
            {
                var sw = Stopwatch.StartNew();
                passes = PassPredictor.Predict(request with { MaxDegreeOfParallelism = threads }).Passes.Count;
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
            }

            output.WriteLine($"| {threads} | {best:F0} ms | {passes} |");
        }
    }

    [Fact]
    public void Sgp4_and_observation_cost_report()
    {
        var prop = Sgp4Propagator.Create(PassScenario.Satellites.First(s => s.NoradId == 25544));
        var site = new ObserverSite(new Geodetic(25.0330, 121.5654, 0.01));
        const int n = 2_000_000;
        var sum = 0.0;
        for (var i = 0; i < 10_000; i++)
        {
            prop.Propagate(i * 0.1, out var w);
            sum += w.PositionKm.X;
        }

        var sw = new Stopwatch(); // 先建立，避免把 Stopwatch 本身的配置算進去
        var before = GC.GetAllocatedBytesForCurrentThread();
        sw.Start();
        for (var i = 0; i < n; i++)
        {
            prop.Propagate(i * 0.01, out var s);
            sum += s.PositionKm.X;
        }

        var sgp4Ns = sw.Elapsed.TotalNanoseconds / n;
        sw.Restart();
        for (var i = 0; i < n; i++)
        {
            Observation.Compute(prop, site, prop.Epoch.AddSeconds(i * 0.6), out var o);
            sum += o.Look.ElevationDeg;
        }

        var obsNs = sw.Elapsed.TotalNanoseconds / n;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        output.WriteLine($"SGP4 單次：{sgp4Ns:F0} ns；傳播 + GMST + TEME→地固 + 站心觀測：{obsNs:F0} ns；{2 * n} 次呼叫共配置 {allocated} bytes（checksum {sum:E2}）");
        Assert.Equal(0, allocated);
    }
}
