using SkyPass.Core.Coordinates;
using SkyPass.Core.Passes;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;
using SkyPass.Tests.Reference;
using SkyPass.Tests.Support;
using Xunit.Abstractions;

namespace SkyPass.Tests.Core.Passes;

public class PassFinderPropertyTests(ITestOutputHelper output)
{
    /// <summary>最大仰角與遮罩相差小於此值的邊界過境，兩邊是否納入都可接受。</summary>
    private const double BorderlineDeg = 0.05;

    [Fact]
    public void Property_matches_brute_force_reference()
    {
        var compared = 0;
        Property.Run("過境集合與暴力參考解一致（驗收 #9）", seed: 9401, iterations: 320, rng =>
        {
            var sc = PassScenario.Random(rng);

            var actual = PassFinder.Find(sc.Propagator, sc.Site, sc.Start, sc.End, sc.MaskDeg).Passes;
            var expected = BruteForcePassFinder.Find(sc.Propagator, sc.Site, sc.Start, sc.DurationSec, sc.MaskDeg);

            AssertSamePasses(sc, expected, actual);
            compared += expected.Count;
        });
        output.WriteLine($"比對過境數：{compared}");
        // 健全性檢查：確保比對不是空轉（320 個情境中多數會有過境）
        Assert.True(compared > 200, $"參考解過境總數過少：{compared}");
    }

    [Fact]
    public void Grazing_passes_just_above_mask_are_all_found()
    {
        // 驗收 #10：先以參考解找出一個過境的最大仰角 E，再把遮罩設為 E − δ（δ ∈ [0.05°, 0.5°]）
        var found = 0;
        Property.Run("掠過過境不漏（驗收 #10）", seed: 9402, iterations: 60, rng =>
        {
            var (sc, target) = GrazingScenario(rng);

            var passes = PassFinder.Find(sc.Propagator, sc.Site, sc.Start, sc.End, sc.MaskDeg).Passes;

            var match = passes.SingleOrDefault(p => Math.Abs(p.MaxElevationTime.SecondsSince(sc.Start) - target.MaxSec) < 60);
            Assert.True(match is not null, $"漏掉掠過過境：最大仰角 {target.MaxElevationDeg:F4}°，遮罩 {sc.MaskDeg:F4}°；{sc}");
            Assert.Equal(target.MaxElevationDeg, match.MaxElevationDeg, 0.01);
            found++;
        });
        Assert.True(found >= 50);
    }

    [Fact]
    public void Property_window_split_invariance()
    {
        Property.Run("視窗拆分不變性（驗收 #11）", seed: 9403, iterations: 200, rng =>
        {
            var sc = PassScenario.Random(rng, minDurationSec: 2 * 3600, maxDurationSec: 24 * 3600);
            var splitSec = Math.Round(rng.NextDouble() * sc.DurationSec, 3);
            var t1 = sc.Start.AddSeconds(splitSec);

            var whole = PassFinder.Find(sc.Propagator, sc.Site, sc.Start, sc.End, sc.MaskDeg).Passes;
            var first = PassFinder.Find(sc.Propagator, sc.Site, sc.Start, t1, sc.MaskDeg).Passes;
            var second = PassFinder.Find(sc.Propagator, sc.Site, t1, sc.End, sc.MaskDeg).Passes;
            var merged = Merge(first, second);

            Assert.Equal(whole.Count, merged.Count);
            for (var i = 0; i < whole.Count; i++)
            {
                AssertClose(whole[i], merged[i], sc);
            }
        });
    }

    [Fact]
    public void Property_pass_invariants()
    {
        Property.Run("過境屬性（驗收 #12）", seed: 9404, iterations: 300, rng =>
        {
            var sc = PassScenario.Random(rng, maxDurationSec: 2 * 86400);

            var passes = PassFinder.Find(sc.Propagator, sc.Site, sc.Start, sc.End, sc.MaskDeg).Passes;

            for (var i = 0; i < passes.Count; i++)
            {
                var p = passes[i];
                Assert.True(p.MaxElevationDeg >= sc.MaskDeg, $"最大仰角低於遮罩；{sc}");
                Assert.True(p.MaxElevationTime.SecondsSince(p.Aos) >= 0 && p.Los.SecondsSince(p.MaxElevationTime) >= 0);
                if (!p.StartedBeforeWindow && !p.EndsAfterWindow)
                {
                    Assert.True(p.MaxElevationTime.SecondsSince(p.Aos) > 0 && p.Los.SecondsSince(p.MaxElevationTime) > 0,
                        $"AOS < 最大仰角時刻 < LOS 不成立；{sc}");
                }

                if (!p.StartedBeforeWindow)
                {
                    Assert.Equal(sc.MaskDeg, ElevationAt(sc, p.Aos), 0.01);
                }
                else
                {
                    Assert.Equal(sc.Start, p.Aos);
                }

                if (!p.EndsAfterWindow)
                {
                    Assert.Equal(sc.MaskDeg, ElevationAt(sc, p.Los), 0.01);
                }
                else
                {
                    Assert.Equal(sc.End, p.Los);
                }

                Assert.InRange(p.AosAzimuthDeg, 0, 360 - 1e-12);
                Assert.InRange(p.LosAzimuthDeg, 0, 360 - 1e-12);
                Assert.InRange(p.MaxElevationAzimuthDeg, 0, 360 - 1e-12);
                Assert.Equal(p.Los.SecondsSince(p.Aos), p.DurationSeconds, 1e-6);
                if (i > 0)
                {
                    Assert.True(p.Aos.SecondsSince(passes[i - 1].Los) > 0, "同一顆衛星的過境重疊或未排序");
                }
            }
        });
    }

    [Fact]
    public void Property_raising_mask_shrinks_pass_set()
    {
        Property.Run("遮罩提高時過境集合單調縮小（驗收 #21）", seed: 9405, iterations: 200, rng =>
        {
            var sc = PassScenario.Random(rng, maxMaskDeg: 20);
            var higher = sc.MaskDeg + rng.NextDouble() * 20;

            var low = PassFinder.Find(sc.Propagator, sc.Site, sc.Start, sc.End, sc.MaskDeg).Passes;
            var high = PassFinder.Find(sc.Propagator, sc.Site, sc.Start, sc.End, higher).Passes;

            Assert.True(high.Count <= low.Count);
            foreach (var h in high)
            {
                Assert.Contains(low, l => h.Aos.SecondsSince(l.Aos) >= -1e-3 && l.Los.SecondsSince(h.Los) >= -1e-3);
            }
        });
    }

    private static (PassScenario Scenario, BruteForcePassFinder.RefPass Target) GrazingScenario(Random rng)
    {
        while (true)
        {
            var sc = PassScenario.Random(rng, minDurationSec: 6 * 3600, maxDurationSec: 6 * 3600, maxMaskDeg: 0);
            var candidates = BruteForcePassFinder.Find(sc.Propagator, sc.Site, sc.Start, sc.DurationSec, 0)
                .Where(p => p is { StartedBeforeWindow: false, EndsAfterWindow: false } && p.MaxElevationDeg > 1)
                .ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var target = candidates[rng.Next(candidates.Count)];
            var delta = 0.05 + rng.NextDouble() * 0.45;
            return (sc with { MaskDeg = target.MaxElevationDeg - delta }, target);
        }
    }

    private static double ElevationAt(PassScenario sc, JulianDate t)
    {
        Assert.Equal(Sgp4Error.None, Observation.Compute(sc.Propagator, sc.Site, t, out var o));
        return o.Look.ElevationDeg;
    }

    private static void AssertSamePasses(PassScenario sc, List<BruteForcePassFinder.RefPass> expected, IReadOnlyList<SatellitePass> actual)
    {
        var exp = expected.Where(p => p.MaxElevationDeg - sc.MaskDeg >= BorderlineDeg).ToList();
        var act = actual.Where(p => p.MaxElevationDeg - sc.MaskDeg >= BorderlineDeg).ToList();

        // 邊界過境（差 < 0.05°）兩邊都可能有；只比對明確的過境，並確認雙方沒有多出明確的過境
        foreach (var e in exp)
        {
            var a = actual.SingleOrDefault(p => Overlaps(sc, p, e));
            Assert.True(a is not null, $"漏掉過境：AOS {e.AosSec:F1}s 最大仰角 {e.MaxElevationDeg:F3}°；{sc}");
            Assert.True(Math.Abs(a.Aos.SecondsSince(sc.Start) - e.AosSec) <= 0.1, $"AOS 差 {a.Aos.SecondsSince(sc.Start) - e.AosSec:F4}s；{sc}");
            Assert.True(Math.Abs(a.Los.SecondsSince(sc.Start) - e.LosSec) <= 0.1, $"LOS 差 {a.Los.SecondsSince(sc.Start) - e.LosSec:F4}s；{sc}");
            Assert.True(Math.Abs(a.MaxElevationDeg - e.MaxElevationDeg) <= 0.01, $"最大仰角差 {a.MaxElevationDeg - e.MaxElevationDeg:F5}°；{sc}");
            Assert.True(Math.Abs(a.MaxElevationTime.SecondsSince(sc.Start) - e.MaxSec) <= 1.0, $"最大仰角時刻差 {a.MaxElevationTime.SecondsSince(sc.Start) - e.MaxSec:F3}s；{sc}");
            Assert.Equal(e.StartedBeforeWindow, a.StartedBeforeWindow);
            Assert.Equal(e.EndsAfterWindow, a.EndsAfterWindow);
        }

        foreach (var a in act)
        {
            Assert.True(expected.Any(e => Overlaps(sc, a, e)), $"多出參考解沒有的過境：AOS {a.Aos.ToUtc():O}；{sc}");
        }
    }

    private static bool Overlaps(PassScenario sc, SatellitePass a, BruteForcePassFinder.RefPass e) =>
        a.Aos.SecondsSince(sc.Start) <= e.LosSec + 1 && e.AosSec <= a.Los.SecondsSince(sc.Start) + 1;

    private static List<SatellitePass> Merge(IReadOnlyList<SatellitePass> first, IReadOnlyList<SatellitePass> second)
    {
        var result = first.ToList();
        var rest = second.ToList();
        if (result.Count > 0 && rest.Count > 0 && result[^1].EndsAfterWindow && rest[0].StartedBeforeWindow)
        {
            var a = result[^1];
            var b = rest[0];
            var max = b.MaxElevationDeg > a.MaxElevationDeg ? b : a;
            result[^1] = a with
            {
                Los = b.Los,
                LosAzimuthDeg = b.LosAzimuthDeg,
                EndsAfterWindow = b.EndsAfterWindow,
                MaxElevationDeg = max.MaxElevationDeg,
                MaxElevationTime = max.MaxElevationTime,
                MaxElevationAzimuthDeg = max.MaxElevationAzimuthDeg,
            };
            rest.RemoveAt(0);
        }

        result.AddRange(rest);
        return result;
    }

    private static void AssertClose(SatellitePass expected, SatellitePass actual, PassScenario sc)
    {
        Assert.True(Math.Abs(actual.Aos.SecondsSince(expected.Aos)) <= 0.01, $"AOS 不同；{sc}");
        Assert.True(Math.Abs(actual.Los.SecondsSince(expected.Los)) <= 0.01, $"LOS 不同；{sc}");
        Assert.True(Math.Abs(actual.MaxElevationTime.SecondsSince(expected.MaxElevationTime)) <= 0.1, $"最大仰角時刻不同；{sc}");
        Assert.Equal(expected.MaxElevationDeg, actual.MaxElevationDeg, 1e-6);
        Assert.Equal(expected.StartedBeforeWindow, actual.StartedBeforeWindow);
        Assert.Equal(expected.EndsAfterWindow, actual.EndsAfterWindow);
    }
}
