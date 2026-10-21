using SkyPass.Core.Coordinates;
using SkyPass.Core.Mathematics;
using SkyPass.Core.Propagation;
using SkyPass.Core.Sun;
using SkyPass.Core.Time;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Sun;

/// <summary>驗收 #8：太陽向量、圓柱陰影、觀測點太陽仰角；陰影進出時刻誤差 ≤ 0.1 秒。</summary>
public class SunAndShadowTests
{
    [Fact]
    public void Sun_vector_elevation_and_shadow_flag_match_reference()
    {
        var count = 0;
        foreach (var c in TestData.ReadCsv("sun.csv"))
        {
            var d = c.Skip(1).Select(TestData.D).ToArray();
            var jd = JulianDate.FromParts(d[0], d[1]);

            var s = SunPosition.UnitVectorTeme(jd);
            Assert.Equal(d[4], s.X, 1e-12);
            Assert.Equal(d[5], s.Y, 1e-12);
            Assert.Equal(d[6], s.Z, 1e-12);

            var site = new ObserverSite(new Geodetic(d[2], d[3], 0));
            Assert.Equal(d[7], SunPosition.ElevationDeg(site, jd), 1e-6);

            var r = new Vector3d(d[8], d[9], d[10]);
            Assert.Equal(d[11] == 1, Shadow.IsInShadow(r, s));
            count++;
        }

        Assert.True(count >= 100);
    }

    [Fact]
    public void Sun_at_march_equinox_is_near_vernal_direction()
    {
        var s = SunPosition.UnitVectorTeme(JulianDate.FromUtc(new DateTime(2025, 3, 20, 9, 1, 0, DateTimeKind.Utc)));

        Assert.True(s.X > 0.9999);
        Assert.Equal(1.0, s.Norm, 12);
    }

    [Fact]
    public void Cylindrical_shadow_cases()
    {
        var sun = new Vector3d(1, 0, 0);

        Assert.True(Shadow.IsInShadow(new Vector3d(-7000, 0, 0), sun));            // 正後方
        Assert.True(Shadow.IsInShadow(new Vector3d(-7000, 6378.0, 0), sun));       // 圓柱內緣
        Assert.False(Shadow.IsInShadow(new Vector3d(-7000, 6378.2, 0), sun));      // 圓柱外
        Assert.False(Shadow.IsInShadow(new Vector3d(7000, 0, 0), sun));            // 向陽面
        Assert.False(Shadow.IsInShadow(new Vector3d(0, 6000, 0), sun));            // r·ŝ = 0
    }

    [Fact]
    public void Shadow_entry_and_exit_times_match_reference_within_0_1_s()
    {
        var groups = TestData.ReadCsv("shadow_events.csv").GroupBy(c => c[0]).ToList();
        Assert.True(groups.Count >= 5);

        foreach (var g in groups)
        {
            var first = g.First();
            var prop = Sgp4Propagator.Create(Tles.TleOf(first[2], first[3]));
            var start = prop.Epoch.AddSeconds(TestData.D(first[4]) * 60);
            var end = prop.Epoch.AddSeconds(TestData.D(first[5]) * 60);

            var timeline = ShadowSearch.Find(prop, start, end);

            var expected = g.Select(c => (Entering: c[6] == "enter", Tsince: TestData.D(c[7]))).ToList();
            Assert.Equal(expected.Count, timeline.Events.Count);
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].Entering, timeline.Events[i].Entering);
                var actualSec = timeline.Events[i].Time.SecondsSince(prop.Epoch);
                Assert.True(Math.Abs(actualSec - expected[i].Tsince * 60) <= 0.1,
                    $"{g.Key} 事件 {i}: 期望 {expected[i].Tsince * 60:F4} s，實際 {actualSec:F4} s");
            }
        }
    }

    [Fact]
    public void Shadow_timeline_reports_state_at_start_and_intervals()
    {
        var first = TestData.ReadCsv("shadow_events.csv").First();
        var prop = Sgp4Propagator.Create(Tles.TleOf(first[2], first[3]));
        var start = prop.Epoch;
        var end = prop.Epoch.AddSeconds(86400);

        var timeline = ShadowSearch.Find(prop, start, end);
        var intervals = timeline.ShadowIntervals(start, end);

        // 第一個事件是「離開」→ 起點時在陰影內
        Assert.Equal(!timeline.Events[0].Entering, timeline.InShadowAtStart);
        Assert.All(intervals, iv => Assert.True(iv.End.SecondsSince(iv.Start) > 0));
        for (var i = 1; i < intervals.Count; i++)
        {
            Assert.True(intervals[i].Start.SecondsSince(intervals[i - 1].End) > 0);
        }
    }

    private static class Tles
    {
        public static SkyPass.Core.Tle.TleSet TleOf(string l1, string l2) =>
            SkyPass.Core.Tle.TleParser.Parse($"{l1}\n{l2}").Sets.Single();
    }
}
