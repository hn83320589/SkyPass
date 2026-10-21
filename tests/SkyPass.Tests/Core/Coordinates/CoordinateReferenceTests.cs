using SkyPass.Core.Coordinates;
using SkyPass.Core.Mathematics;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Coordinates;

/// <summary>驗收 #7：至少 100 組參考案例；角度 ≤ 1e-6°、距離 ≤ 1e-6 km、距離變化率 ≤ 1e-9 km/s。</summary>
public class CoordinateReferenceTests
{
    private const double AngleTolDeg = 1e-6;
    private const double DistTolKm = 1e-6;
    private const double RateTolKmPerSec = 1e-9;

    [Fact]
    public void All_reference_cases_match()
    {
        var count = 0;
        foreach (var c in TestData.ReadCsv("coords.csv"))
        {
            var d = c.Skip(1).Select(TestData.D).ToArray();
            var id = c[0];
            var jd = JulianDate.FromParts(d[0], d[1]);
            var site = new ObserverSite(new Geodetic(d[2], d[3], d[4]));
            var teme = new TemeState(new Vector3d(d[5], d[6], d[7]), new Vector3d(d[8], d[9], d[10]));

            var gmst = Gmst.ComputeRad(jd);
            Close(d[11], gmst, 1e-9, id + " gmst");

            var ecef = Frames.TemeToEcef(teme, gmst);
            Close(d[12], ecef.PositionKm.X, DistTolKm, id + " rx");
            Close(d[13], ecef.PositionKm.Y, DistTolKm, id + " ry");
            Close(d[14], ecef.PositionKm.Z, DistTolKm, id + " rz");
            Close(d[15], ecef.VelocityKmPerSec.X, RateTolKmPerSec, id + " vx");
            Close(d[16], ecef.VelocityKmPerSec.Y, RateTolKmPerSec, id + " vy");
            Close(d[17], ecef.VelocityKmPerSec.Z, RateTolKmPerSec, id + " vz");

            Close(d[18], site.EcefKm.X, DistTolKm, id + " ox");
            Close(d[19], site.EcefKm.Y, DistTolKm, id + " oy");
            Close(d[20], site.EcefKm.Z, DistTolKm, id + " oz");

            var look = site.Observe(ecef);
            Close(0, Angles.DifferenceDeg(d[21], look.AzimuthDeg), AngleTolDeg, id + " az");
            Close(d[22], look.ElevationDeg, AngleTolDeg, id + " el");
            Close(d[23], look.RangeKm, DistTolKm, id + " range");
            Close(d[24], look.RangeRateKmPerSec, RateTolKmPerSec, id + " rate");
            Assert.InRange(look.AzimuthDeg, 0.0, 360.0 - double.Epsilon);
            count++;
        }

        Assert.True(count >= 100, $"至少 100 組，實際 {count}");
    }

    private static void Close(double expected, double actual, double tol, string label) =>
        Assert.True(Math.Abs(expected - actual) <= tol, $"{label}: 期望 {expected:R}，實際 {actual:R}，差 {Math.Abs(expected - actual):E3}");
}
