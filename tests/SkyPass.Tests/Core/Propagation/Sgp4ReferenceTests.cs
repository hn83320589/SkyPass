using System.Globalization;
using SkyPass.Core.Propagation;
using SkyPass.Core.Tle;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Propagation;

/// <summary>驗收 #3、#4、#5：SGP4 與參考實作逐點相符（位置 ≤ 1e-6 km、速度 ≤ 1e-9 km/s）。</summary>
public class Sgp4ReferenceTests
{
    private const double PositionTolKm = 1e-6;
    private const double VelocityTolKmPerSec = 1e-9;

    public static TheoryData<string> PublishedCases =>
        [.. Sgp4ReferenceRow.Load("sgp4_published.csv").Select(r => r.Case).Distinct()];

    [Theory]
    [MemberData(nameof(PublishedCases))]
    public void Published_case_matches_reference(string caseId)
    {
        var rows = Sgp4ReferenceRow.Load("sgp4_published.csv").Where(r => r.Case == caseId).ToList();

        AssertMatches(rows);
    }

    [Fact]
    public void Published_cases_cover_required_orbit_classes()
    {
        var rows = Sgp4ReferenceRow.Load("sgp4_published.csv");
        var cases = rows.GroupBy(r => r.Case).Select(g => (Tle: g.First().Tle, Rows: g.ToList())).ToList();

        Assert.True(cases.Count >= 20, $"至少 20 組近地案例，實際 {cases.Count}");
        Assert.All(cases, c => Assert.True(c.Rows.Count >= 3, $"{c.Tle.NoradId} 時間點不足"));
        Assert.Contains(cases, c => c.Tle.Eccentricity < 0.001);                // 近圓
        Assert.Contains(cases, c => c.Tle.Eccentricity > 0.1);                  // 高偏心率
        Assert.Contains(cases, c => c.Tle.InclinationDeg < 30);                 // 低傾角
        Assert.Contains(cases, c => c.Tle.InclinationDeg > 90);                 // 逆行
        Assert.Contains(cases, c => PerigeeKm(c.Tle) < 220);                    // 極低近地點（isimp）
        Assert.Contains(cases, c => c.Rows.Any(r => r.Error == 6));             // 衰減
    }

    [Fact]
    public void Random_reference_orbits_match()
    {
        var rows = Sgp4ReferenceRow.Load("sgp4_random.csv");
        var cases = rows.GroupBy(r => r.Case).ToList();

        Assert.True(cases.Count >= 300, $"至少 300 組隨機軌道，實際 {cases.Count}");
        Assert.All(cases, c => Assert.True(c.Count() >= 5));
        foreach (var c in cases)
        {
            AssertMatches(c.ToList());
        }
    }

    [Fact]
    public void Matches_vallado_tcppver_output_at_printed_precision()
    {
        var tles = LoadVerificationTles();
        var compared = 0;
        string? norad = null;
        foreach (var line in File.ReadLines(TestData.PathOf("tcppver.out")))
        {
            if (line.EndsWith(" xx", StringComparison.Ordinal))
            {
                norad = line[..^3].Trim().PadLeft(5, '0');
                continue;
            }

            if (norad is null || !tles.TryGetValue(norad, out var tle))
            {
                continue;
            }

            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Take(7).Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            var prop = Sgp4Propagator.Create(tle);
            var error = prop.Propagate(f[0], out var state);

            Assert.Equal(Sgp4Error.None, error);
            // tcppver.out 位置印到 1e-8 km、速度印到 1e-9 km/s，容差另加上半個列印位數
            AssertClose(f[1], state.PositionKm.X, PositionTolKm, $"{norad} t={f[0]} rx");
            AssertClose(f[2], state.PositionKm.Y, PositionTolKm, $"{norad} t={f[0]} ry");
            AssertClose(f[3], state.PositionKm.Z, PositionTolKm, $"{norad} t={f[0]} rz");
            AssertClose(f[4], state.VelocityKmPerSec.X, VelocityTolKmPerSec + 5e-10, $"{norad} t={f[0]} vx");
            AssertClose(f[5], state.VelocityKmPerSec.Y, VelocityTolKmPerSec + 5e-10, $"{norad} t={f[0]} vy");
            AssertClose(f[6], state.VelocityKmPerSec.Z, VelocityTolKmPerSec + 5e-10, $"{norad} t={f[0]} vz");
            compared++;
        }

        Assert.True(compared > 100, $"比對筆數過少：{compared}");
    }

    private static Dictionary<string, TleSet> LoadVerificationTles()
    {
        var lines = File.ReadLines(TestData.PathOf("SGP4-VER.TLE")).Where(l => l.StartsWith("1 ") || l.StartsWith("2 ")).ToList();
        var result = new Dictionary<string, TleSet>();
        for (var i = 0; i + 1 < lines.Count; i += 2)
        {
            var tle = TleParser.Parse($"{lines[i][..69]}\n{lines[i + 1][..69]}").Sets.SingleOrDefault();
            if (tle is not null && !tle.IsDeepSpace)
            {
                result.TryAdd(lines[i][2..7], tle);
            }
        }

        return result;
    }

    private static double PerigeeKm(TleSet tle)
    {
        var a = Math.Pow(398600.8 / Math.Pow(tle.MeanMotionRevPerDay * 2 * Math.PI / 86400, 2), 1.0 / 3.0);
        return a * (1 - tle.Eccentricity) - 6378.135;
    }

    internal static void AssertMatches(IReadOnlyList<Sgp4ReferenceRow> rows)
    {
        var prop = Sgp4Propagator.Create(rows[0].Tle);
        foreach (var r in rows)
        {
            var error = prop.Propagate(r.TsinceMin, out var state);

            Assert.True(r.Error == (int)error, $"{r.Case} t={r.TsinceMin}: 錯誤碼應為 {r.Error}，實際 {(int)error}（{error}）");
            if (r.Error != 0)
            {
                continue;
            }

            var label = $"{r.Case} t={r.TsinceMin}";
            AssertClose(r.Rx, state.PositionKm.X, PositionTolKm, label + " rx");
            AssertClose(r.Ry, state.PositionKm.Y, PositionTolKm, label + " ry");
            AssertClose(r.Rz, state.PositionKm.Z, PositionTolKm, label + " rz");
            AssertClose(r.Vx, state.VelocityKmPerSec.X, VelocityTolKmPerSec, label + " vx");
            AssertClose(r.Vy, state.VelocityKmPerSec.Y, VelocityTolKmPerSec, label + " vy");
            AssertClose(r.Vz, state.VelocityKmPerSec.Z, VelocityTolKmPerSec, label + " vz");
        }
    }

    private static void AssertClose(double expected, double actual, double tol, string label) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"{label}: 期望 {expected:R}，實際 {actual:R}，差 {Math.Abs(expected - actual):E3}");
}
