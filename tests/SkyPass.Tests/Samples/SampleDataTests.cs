using System.Globalization;
using SkyPass.Core.Coordinates;
using SkyPass.Core.Passes;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;

namespace SkyPass.Tests.Samples;

/// <summary>交付物：samples/ 的範例 TLE 與「預期過境表」（由 tools/refgen/gen_samples.py 以逐秒暴力搜尋獨立產生）。</summary>
public class SampleDataTests
{
    private static string SamplePath(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", name);

    private static readonly JulianDate Start = JulianDate.FromUtc(new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc));
    private static readonly Geodetic Taipei = new(25.0330, 121.5654, 0.010);

    [Fact]
    public void Sample_file_has_at_least_30_near_earth_sets()
    {
        var result = TleParser.Parse(File.ReadAllBytes(SamplePath("skypass-samples.tle")));

        Assert.Empty(result.Errors);
        Assert.True(result.Sets.Count(s => !s.IsDeepSpace) >= 30);
        Assert.Contains(result.Sets, s => s.IsDeepSpace);
    }

    [Fact]
    public void Prediction_matches_expected_pass_table()
    {
        var sets = TleParser.Parse(File.ReadAllBytes(SamplePath("skypass-samples.tle"))).Sets;
        var expected = File.ReadLines(SamplePath("expected-passes.csv")).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(',')).ToList();

        var result = PassPredictor.Predict(new PredictionRequest(sets, Taipei, Start, Start.AddSeconds(86400), 10.0) { ComputeVisibility = false });

        // 最大仰角與遮罩相差 < 0.05° 的邊界過境，兩邊是否納入都可接受
        var clear = expected.Where(e => double.Parse(e[5], CultureInfo.InvariantCulture) - 10.0 >= 0.05).ToList();
        foreach (var e in clear)
        {
            var norad = int.Parse(e[1], CultureInfo.InvariantCulture);
            var aos = JulianDate.FromUtc(DateTime.Parse(e[2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
            var los = JulianDate.FromUtc(DateTime.Parse(e[6], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
            var max = JulianDate.FromUtc(DateTime.Parse(e[4], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
            var actual = result.Passes.SingleOrDefault(p => p.NoradId == norad && Math.Abs(p.Aos.SecondsSince(aos)) < 60);

            Assert.True(actual is not null, $"缺少 {e[0]} AOS {e[2]}");
            Assert.True(Math.Abs(actual.Aos.SecondsSince(aos)) <= 0.1, $"{e[0]} AOS 差 {actual.Aos.SecondsSince(aos):F3} s");
            Assert.True(Math.Abs(actual.Los.SecondsSince(los)) <= 0.1 || actual.EndedByDecay, $"{e[0]} LOS 差 {actual.Los.SecondsSince(los):F3} s");
            Assert.True(Math.Abs(actual.MaxElevationTime.SecondsSince(max)) <= 1.0, $"{e[0]} 最大仰角時刻差");
            Assert.Equal(double.Parse(e[5], CultureInfo.InvariantCulture), actual.MaxElevationDeg, 0.01);
            Assert.Equal(e[8].Contains("起點前已開始"), actual.StartedBeforeWindow);
            Assert.Equal(e[8].Contains("終點後才結束"), actual.EndsAfterWindow);
        }

        Assert.True(result.Passes.Count(p => p.MaxElevationDeg - 10 >= 0.05) == clear.Count, "預報多出預期表沒有的過境");
        Assert.True(clear.Count > 100);
        Assert.Contains(result.Diagnostics, d => d.NoradId == 90014 && d.Kind is DiagnosticKind.PropagationError or DiagnosticKind.Decayed);
        Assert.Contains(result.Diagnostics, d => d.NoradId == 90017 && d.Kind == DiagnosticKind.Unsupported);
    }
}
