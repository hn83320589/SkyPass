using SkyPass.Core.Mathematics;

namespace SkyPass.Core.Coordinates;

/// <summary>WGS-84 大地座標（緯度、經度以度為單位，高度 km）。</summary>
public readonly record struct Geodetic(double LatitudeDeg, double LongitudeDeg, double HeightKm);

/// <summary>地固座標（ECEF）的位置（km）與速度（km/s）。</summary>
public readonly record struct EcefState(Vector3d PositionKm, Vector3d VelocityKmPerSec);

public static class Wgs84
{
    public const double EquatorialRadiusKm = 6378.137;
    public const double Flattening = 1.0 / 298.257223563;
    public const double EccentricitySquared = Flattening * (2.0 - Flattening);

    public static Vector3d ToEcef(in Geodetic g)
    {
        var lat = g.LatitudeDeg * Angles.DegToRad;
        var lon = g.LongitudeDeg * Angles.DegToRad;
        var sinLat = Math.Sin(lat);
        var cosLat = Math.Cos(lat);
        var n = EquatorialRadiusKm / Math.Sqrt(1.0 - EccentricitySquared * sinLat * sinLat);
        return new Vector3d(
            (n + g.HeightKm) * cosLat * Math.Cos(lon),
            (n + g.HeightKm) * cosLat * Math.Sin(lon),
            (n * (1.0 - EccentricitySquared) + g.HeightKm) * sinLat);
    }

    /// <summary>地固 → 大地座標（迭代法，對極區穩定）。</summary>
    public static Geodetic FromEcef(in Vector3d r)
    {
        var p = Math.Sqrt(r.X * r.X + r.Y * r.Y);
        var lon = Math.Atan2(r.Y, r.X);
        var lat = Math.Atan2(r.Z, p * (1.0 - EccentricitySquared));
        double h = 0;
        for (var i = 0; i < 10; i++)
        {
            var sinLat = Math.Sin(lat);
            var n = EquatorialRadiusKm / Math.Sqrt(1.0 - EccentricitySquared * sinLat * sinLat);
            h = p * Math.Cos(lat) + r.Z * sinLat - EquatorialRadiusKm * Math.Sqrt(1.0 - EccentricitySquared * sinLat * sinLat);
            lat = Math.Atan2(r.Z + EccentricitySquared * n * sinLat, p);
        }

        return new Geodetic(lat * Angles.RadToDeg, lon * Angles.RadToDeg, h);
    }
}
