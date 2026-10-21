using SkyPass.Core.Coordinates;
using SkyPass.Core.Tle;
using SkyPass.Presenters;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Presenters;

/// <summary>驗收 #17：每秒更新的數值與參考資料一致、單次更新 ≤ 1 ms、衛星改變／時間回溯／軌道資料更新後狀態正確。</summary>
public class LiveTrackingPresenterTests
{
    private static readonly IReadOnlyList<string[]> Reference = TestData.ReadCsv("live_tracking.csv").ToList();
    private static readonly TleSet Iss = TleParser.Parse($"{Reference[0][1]}\n{Reference[0][2]}").Sets.Single();
    private static readonly Geodetic Taipei = new(TestData.D(Reference[0][3]), TestData.D(Reference[0][4]), TestData.D(Reference[0][5]));
    private static readonly TleSet Css = PassScenario.Satellites.First(s => s.NoradId == 48274);

    private static (LiveTrackingPresenter Presenter, FakeTimeProvider Clock) Create(DateTime utc)
    {
        var clock = new FakeTimeProvider(utc);
        var p = new LiveTrackingPresenter(clock);
        p.Configure(Iss, Taipei, 10, 145.8e6);
        return (p, clock);
    }

    [Fact]
    public async Task Each_second_matches_reference()
    {
        var (p, clock) = Create(TestData.Utc(Reference[0][0]));
        await p.WhenIdleAsync();

        foreach (var row in Reference)
        {
            clock.SetUtcNow(TestData.Utc(row[0]));

            var r = p.Tick()!;

            Assert.True(Math.Abs(SkyPass.Core.Mathematics.Angles.DifferenceDeg(TestData.D(row[6]), r.AzimuthDeg)) <= 1e-6, $"{row[0]} az");
            Assert.True(Math.Abs(TestData.D(row[7]) - r.ElevationDeg) <= 1e-6, $"{row[0]} el");
            Assert.True(Math.Abs(TestData.D(row[8]) - r.RangeKm) <= 1e-6, $"{row[0]} range");
            Assert.True(Math.Abs(TestData.D(row[9]) - r.RangeRateKmPerSec) <= 1e-9, $"{row[0]} rate");
            Assert.True(Math.Abs(TestData.D(row[10]) - r.ReceivedHz) <= 1.0, $"{row[0]} freq");
        }
    }

    [Fact]
    public async Task Countdown_to_next_aos_and_los_during_pass()
    {
        var (p, clock) = Create(TestData.Utc(Reference[0][0]));
        await p.WhenIdleAsync();

        var before = p.Tick()!;
        Assert.False(before.AboveMask);
        Assert.NotNull(before.NextPass);
        Assert.Null(before.CurrentPass);
        var untilAos = before.UntilAos!.Value;
        Assert.True(untilAos > TimeSpan.Zero);

        clock.Advance(TimeSpan.FromSeconds(1));
        var next = p.Tick()!.UntilAos!.Value;
        Assert.True(Math.Abs((untilAos - TimeSpan.FromSeconds(1) - next).TotalMilliseconds) < 1, "倒數應每秒減少 1 秒");

        clock.SetUtcNow(before.NextPass!.MaxElevationTime.ToUtc());
        var during = p.Tick()!;
        Assert.True(during.AboveMask);
        Assert.Same(before.NextPass, during.CurrentPass);
        Assert.True(during.UntilLos > TimeSpan.Zero);
        Assert.True(during.NextPass is null || during.NextPass.Aos.SecondsSince(during.CurrentPass!.Los) > 0);
    }

    // 單次更新耗時的效能測試在 Performance/LiveTrackingPerformanceTests（獨立、不平行執行）

    [Fact]
    public async Task Changing_satellite_switches_readings_and_pass_cache()
    {
        var (p, _) = Create(TestData.Utc(Reference[0][0]));
        await p.WhenIdleAsync();
        var issReading = p.Tick()!;

        var cacheVersion = p.CacheVersion;
        p.Configure(Css, Taipei, 10, 145.8e6);
        var pending = p.Tick()!;
        await p.WhenIdleAsync();
        var cssReading = p.Tick()!;

        Assert.True(p.CacheVersion > cacheVersion, "快取完成後版本應遞增，讓 UI 立即更新");

        Assert.Equal(48274, p.Satellite!.NoradId);
        Assert.NotEqual(issReading.RangeKm, cssReading.RangeKm);
        Assert.True(pending.NextPass is null || pending.NextPass.NoradId == 48274, "切換後不可沿用舊衛星的過境");
        Assert.Equal(48274, cssReading.NextPass!.NoradId);
    }

    [Fact]
    public async Task Time_going_backwards_recomputes_next_pass()
    {
        var (p, clock) = Create(TestData.Utc(Reference[0][0]));
        await p.WhenIdleAsync();
        var first = p.Tick()!.NextPass!;

        clock.Advance(TimeSpan.FromDays(-1));
        p.Tick();
        await p.WhenIdleAsync();
        var reading = p.Tick()!;

        Assert.True(reading.NextPass!.Aos.SecondsSince(first.Aos) < -3600, "時間回溯一天後，下一次過境應早於原本的");
        Assert.True(reading.UntilAos > TimeSpan.Zero);
    }

    [Fact]
    public async Task Updated_elements_for_same_satellite_replace_propagator()
    {
        var (p, _) = Create(TestData.Utc(Reference[0][0]));
        await p.WhenIdleAsync();
        var before = p.Tick()!;

        var shifted = Iss with { MeanAnomalyDeg = (Iss.MeanAnomalyDeg + 10) % 360, EpochDayOfYear = Iss.EpochDayOfYear + 0.1 };
        p.Configure(shifted, Taipei, 10, 145.8e6);
        await p.WhenIdleAsync();
        var after = p.Tick()!;

        Assert.Same(shifted, p.Satellite);
        Assert.NotEqual(before.RangeKm, after.RangeKm);
        Assert.NotEqual(before.NextPass!.Aos, after.NextPass!.Aos);
    }

    [Fact]
    public void No_satellite_gives_no_reading()
    {
        var p = new LiveTrackingPresenter(new FakeTimeProvider(SampleTle.SnapshotTime));

        Assert.Null(p.Tick());
    }
}
