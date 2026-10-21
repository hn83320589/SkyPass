using System.Globalization;
using SkyPass.Core.Coordinates;
using SkyPass.Core.Doppler;
using SkyPass.Core.Mathematics;
using SkyPass.Core.Propagation;
using SkyPass.Core.Sun;
using SkyPass.Core.Time;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core;

/// <summary>核對 docs/hand-calculations.md 每個逐步手算範例的最終值。</summary>
public class HandCalculationTests
{
    public static TheoryData<string, string> Examples
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var c in File.ReadLines(TestData.PathOf("hand_examples.csv")).Skip(1).Select(l => l.Split('|')))
            {
                data.Add(c[0], c[1]);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Examples))]
    public void Example_matches_program(string section, string id)
    {
        var row = File.ReadLines(TestData.PathOf("hand_examples.csv")).Skip(1).Select(l => l.Split('|')).Single(c => c[0] == section && c[1] == id);
        var input = Parse(row[2]);
        var expected = Parse(row[3]);

        switch (section)
        {
            case "time":
                var jd = JulianDate.FromUtc(TestData.Utc(input["utc"]));
                Assert.Equal(D(expected["jd_day"]), jd.Day);
                Assert.Equal(D(expected["jd_frac"]), jd.Fraction, 1e-15);
                break;

            case "gmst":
                Assert.Equal(D(expected["gmst_rad"]), Gmst.ComputeRad(JulianDate.FromParts(D(input["jd_day"]), D(input["jd_frac"]))), 1e-9);
                break;

            case "coord":
                var teme = new TemeState(new Vector3d(D(input["rx"]), D(input["ry"]), D(input["rz"])), new Vector3d(D(input["vx"]), D(input["vy"]), D(input["vz"])));
                var ecef = Frames.TemeToEcef(teme, D(input["gmst"]));
                var site = new ObserverSite(new Geodetic(D(input["lat"]), D(input["lon"]), D(input["h"])));
                var look = site.Observe(ecef);
                Assert.Equal(D(expected["ex"]), ecef.PositionKm.X, 1e-6);
                Assert.Equal(D(expected["ey"]), ecef.PositionKm.Y, 1e-6);
                Assert.Equal(D(expected["evx"]), ecef.VelocityKmPerSec.X, 1e-9);
                Assert.Equal(D(expected["evy"]), ecef.VelocityKmPerSec.Y, 1e-9);
                Assert.Equal(D(expected["ox"]), site.EcefKm.X, 1e-6);
                Assert.Equal(D(expected["oz"]), site.EcefKm.Z, 1e-6);
                Assert.Equal(0, Angles.DifferenceDeg(D(expected["az"]), look.AzimuthDeg), 1e-6);
                Assert.Equal(D(expected["el"]), look.ElevationDeg, 1e-6);
                Assert.Equal(D(expected["range"]), look.RangeKm, 1e-6);
                Assert.Equal(D(expected["rate"]), look.RangeRateKmPerSec, 1e-9);
                break;

            case "doppler":
                Assert.Equal(D(expected["rx"]), DopplerCalculator.ReceivedHz(D(input["f0"]), D(input["rate"])), 1e-6);
                break;

            case "shadow":
                var sun = SunPosition.UnitVectorTeme(JulianDate.FromParts(D(input["jd_day"]), D(input["jd_frac"])));
                Assert.Equal(D(expected["sx"]), sun.X, 1e-12);
                Assert.Equal(D(expected["sy"]), sun.Y, 1e-12);
                Assert.Equal(D(expected["sz"]), sun.Z, 1e-12);
                Assert.Equal(expected["in_shadow"] == "1", Shadow.IsInShadow(new Vector3d(D(input["rx"]), D(input["ry"]), D(input["rz"])), sun));
                break;

            default:
                Assert.Fail($"未知的範例類別 {section}");
                break;
        }
    }

    [Fact]
    public void At_least_ten_examples_per_section()
    {
        var counts = File.ReadLines(TestData.PathOf("hand_examples.csv")).Skip(1).GroupBy(l => l.Split('|')[0]).ToDictionary(g => g.Key, g => g.Count());

        Assert.All(new[] { "time", "gmst", "coord", "doppler", "shadow" }, s => Assert.True(counts.GetValueOrDefault(s) >= 10, s));
    }

    private static Dictionary<string, string> Parse(string kv) =>
        kv.Split(';').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1].Trim('\''));

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
