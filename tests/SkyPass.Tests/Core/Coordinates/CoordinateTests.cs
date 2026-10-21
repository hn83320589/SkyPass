using SkyPass.Core.Coordinates;
using SkyPass.Core.Mathematics;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;
using SkyPass.Tests.Support;
using static SkyPass.Tests.Support.TleText;

namespace SkyPass.Tests.Core.Coordinates;

public class CoordinateTests
{
    private const double A = Wgs84.EquatorialRadiusKm;
    private const double B = A * (1 - Wgs84.Flattening);

    [Theory]
    [InlineData(0, 0, 0, A, 0, 0)]
    [InlineData(0, 90, 0, 0, A, 0)]
    [InlineData(0, 180, 0, -A, 0, 0)]
    [InlineData(0, -180, 0, -A, 0, 0)]
    [InlineData(90, 0, 0, 0, 0, B)]
    [InlineData(-90, 123, 0, 0, 0, -B)]
    [InlineData(0, 0, -0.4, A - 0.4, 0, 0)]  // 負高度
    [InlineData(0, -90, 1.5, 0, -(A + 1.5), 0)]
    public void Geodetic_to_ecef_special_points(double lat, double lon, double h, double x, double y, double z)
    {
        var p = Wgs84.ToEcef(new Geodetic(lat, lon, h));

        Assert.Equal(x, p.X, 1e-9);
        Assert.Equal(y, p.Y, 1e-9);
        Assert.Equal(z, p.Z, 1e-9);
    }

    [Fact]
    public void Look_angles_follow_north_east_zenith_convention()
    {
        var site = new ObserverSite(new Geodetic(0, 0, 0));
        var o = site.EcefKm;

        var up = site.Observe(new EcefState(o + new Vector3d(500, 0, 0), Vector3d.Zero));
        var north = site.Observe(new EcefState(o + new Vector3d(0, 0, 1000), Vector3d.Zero));
        var east = site.Observe(new EcefState(o + new Vector3d(0, 1000, 0), Vector3d.Zero));
        var west = site.Observe(new EcefState(o + new Vector3d(0, -1000, 0), Vector3d.Zero));
        var south = site.Observe(new EcefState(o + new Vector3d(0, 0, -1000), Vector3d.Zero));

        Assert.Equal(90.0, up.ElevationDeg, 9);
        Assert.Equal(500.0, up.RangeKm, 9);
        Assert.Equal(0.0, north.AzimuthDeg, 9);
        Assert.Equal(0.0, north.ElevationDeg, 9);
        Assert.Equal(90.0, east.AzimuthDeg, 9);
        Assert.Equal(270.0, west.AzimuthDeg, 9);
        Assert.Equal(180.0, south.AzimuthDeg, 9);
    }

    [Fact]
    public void Range_rate_is_negative_when_approaching()
    {
        var site = new ObserverSite(new Geodetic(10, 20, 0));
        var sat = site.EcefKm + site.ZenithUnit * 800 + new Vector3d(300, 0, 0);
        var towards = (site.EcefKm - sat) * (1.0 / (site.EcefKm - sat).Norm) * 5.0;

        var look = site.Observe(new EcefState(sat, towards));

        Assert.Equal(-5.0, look.RangeRateKmPerSec, 12);
    }

    [Fact]
    public void Property_geodetic_ecef_roundtrip()
    {
        Property.Run("大地↔地固 往返", seed: 8301, iterations: 5000, rng =>
        {
            var g = new Geodetic(rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180, rng.NextDouble() * 10 - 0.5);

            var back = Wgs84.FromEcef(Wgs84.ToEcef(g));

            Assert.Equal(g.LatitudeDeg, back.LatitudeDeg, 1e-9);
            if (Math.Abs(g.LatitudeDeg) < 89.9999)
            {
                Assert.Equal(0.0, Angles.DifferenceDeg(g.LongitudeDeg, back.LongitudeDeg), 1e-9);
            }

            Assert.Equal(g.HeightKm, back.HeightKm, 1e-9);
        });
    }

    [Fact]
    public void Property_teme_to_ecef_preserves_norm_and_z()
    {
        Property.Run("TEME→ECEF 保持長度與 z", seed: 8302, iterations: 2000, rng =>
        {
            var r = new Vector3d(rng.NextDouble() * 2e4 - 1e4, rng.NextDouble() * 2e4 - 1e4, rng.NextDouble() * 2e4 - 1e4);
            var v = new Vector3d(rng.NextDouble() * 16 - 8, rng.NextDouble() * 16 - 8, rng.NextDouble() * 16 - 8);
            var theta = rng.NextDouble() * 2 * Math.PI;

            var e = Frames.TemeToEcef(new TemeState(r, v), theta);

            Assert.Equal(r.Norm, e.PositionKm.Norm, 1e-9);
            Assert.Equal(r.Z, e.PositionKm.Z);
            // 地固速度 = 旋轉後速度 − ω × r
            var omegaCrossR = new Vector3d(0, 0, Frames.EarthRotationRadPerSec).Cross(e.PositionKm);
            Assert.Equal(v.Norm, (e.VelocityKmPerSec + omegaCrossR).Norm, 1e-9);
        });
    }

    [Fact]
    public void Property_elevation_rate_matches_finite_difference()
    {
        var iss = Sgp4Propagator.Create(TleParser.Parse($"{IssLine1}\n{IssLine2}").Sets[0]);
        Property.Run("仰角變化率 ≈ 數值微分", seed: 8303, iterations: 500, rng =>
        {
            var site = new ObserverSite(new Geodetic(rng.NextDouble() * 140 - 70, rng.NextDouble() * 360 - 180, 0));
            var t = iss.Epoch.AddSeconds(rng.NextDouble() * 86400);

            Observation.Compute(iss, site, t, out var o0);
            Observation.Compute(iss, site, t.AddSeconds(-0.01), out var om);
            Observation.Compute(iss, site, t.AddSeconds(0.01), out var op);

            var numeric = (op.Look.ElevationDeg - om.Look.ElevationDeg) / 0.02;
            if (o0.Look.ElevationDeg < 89)
            {
                Assert.Equal(numeric, o0.Look.ElevationRateDegPerSec, 1e-5);
            }
        });
    }

    [Fact]
    public void Observation_compute_does_not_allocate()
    {
        var iss = Sgp4Propagator.Create(TleParser.Parse($"{IssLine1}\n{IssLine2}").Sets[0]);
        var site = new ObserverSite(new Geodetic(25.03, 121.56, 0.01));
        var sum = 0.0;

        var allocated = Allocations.MinimumAllocatedBytes(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                Observation.Compute(iss, site, iss.Epoch.AddSeconds(i), out var o);
                sum += o.Look.ElevationDeg;
            }
        });

        Assert.Equal(0, allocated);
        Assert.NotEqual(0, sum);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(359.5, 0.5, 1)]
    [InlineData(0.5, 359.5, -1)]
    [InlineData(-10, 350, 0)]
    [InlineData(720, 0, 0)]
    public void Angle_difference_wraps(double a, double b, double expected)
    {
        Assert.Equal(expected, Angles.DifferenceDeg(a, b), 12);
    }

    [Theory]
    [InlineData(-0.0, 0.0)]
    [InlineData(-90, 270)]
    [InlineData(360, 0)]
    [InlineData(725, 5)]
    public void Normalize_to_0_360(double input, double expected)
    {
        Assert.Equal(expected, Angles.Normalize360Deg(input), 12);
    }
}
