using System.Diagnostics;
using SkyPass.Core.Coordinates;
using SkyPass.Core.Tle;
using SkyPass.Presenters;
using SkyPass.Tests.Support;
using Xunit.Abstractions;

namespace SkyPass.Tests.Performance;

/// <summary>驗收 #17：即時追蹤單次更新耗時 ≤ 1 ms。</summary>
[Trait("Category", "Performance")]
[Collection(PerformanceCollection.Name)]
public class LiveTrackingPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Single_update_takes_at_most_one_millisecond()
    {
        var reference = TestData.ReadCsv("live_tracking.csv").First();
        var iss = TleParser.Parse($"{reference[1]}\n{reference[2]}").Sets.Single();
        var clock = new FakeTimeProvider(TestData.Utc(reference[0]));
        var p = new LiveTrackingPresenter(clock);
        p.Configure(iss, new Geodetic(25.0330, 121.5654, 0.010), 10, 145.8e6);
        await p.WhenIdleAsync();
        for (var i = 0; i < 1000; i++)
        {
            p.Tick(); // 暖機（JIT）
        }

        var samples = new List<double>();
        for (var i = 0; i < 5000; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var start = Stopwatch.GetTimestamp();
            p.Tick();
            samples.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        var p99 = samples[(int)(samples.Count * 0.99)];
        var p999 = samples[(int)(samples.Count * 0.999)];
        output.WriteLine($"即時追蹤單次更新（5000 次）：中位數 {median * 1000:F1} µs，P99 {p99 * 1000:F1} µs，P99.9 {p999 * 1000:F1} µs，最大 {samples[^1] * 1000:F1} µs");
        Assert.True(p999 <= 1.0, $"P99.9 {p999:F3} ms 超過 1 ms");
    }
}
