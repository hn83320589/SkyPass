using System.Diagnostics;
using SkyPass.Core.Coordinates;
using SkyPass.Core.Passes;
using SkyPass.Tests.Core.Passes;
using Xunit.Abstractions;

namespace SkyPass.Tests.Performance;

/// <summary>驗收 #20：100 顆 × 7 天 × 遮罩 10° 在 8 核心機器上 ≤ 2 秒。</summary>
[Trait("Category", "Performance")]
[Collection(PerformanceCollection.Name)]
public class PredictionPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void Hundred_satellites_seven_days_within_budget()
    {
        var sats = PassPredictorTests.Constellation(100);
        var request = new PredictionRequest(sats, new Geodetic(25.0330, 121.5654, 0.01), sats[0].Epoch, sats[0].Epoch.AddSeconds(7 * 86400), 10.0)
        {
            MaxDegreeOfParallelism = 8,
        };
        PassPredictor.Predict(request with { Satellites = sats.Take(4).ToList() }); // 暖機（JIT）

        var sw = Stopwatch.StartNew();
        var result = PassPredictor.Predict(request);
        sw.Stop();

        // 驗收基準為 8 核心；核心數較少的機器（如 CI runner）依比例放寬並記錄實測值
        var cores = Math.Min(Environment.ProcessorCount, 8);
        var budget = TimeSpan.FromSeconds(2.0 * 8 / cores);
        output.WriteLine($"100 顆 × 7 天：{sw.Elapsed.TotalMilliseconds:F0} ms，{result.Passes.Count} 個過境，" +
                         $"使用 {cores} 執行緒（邏輯處理器 {Environment.ProcessorCount}），預算 {budget.TotalMilliseconds:F0} ms");
        Assert.True(sw.Elapsed <= budget, $"耗時 {sw.Elapsed.TotalMilliseconds:F0} ms 超過預算 {budget.TotalMilliseconds:F0} ms");
        Assert.True(result.Passes.Count > 1000);
    }
}
