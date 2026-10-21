using SkyPass.Core.Mathematics;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;

namespace SkyPass.Core.Coordinates;

/// <summary>站心觀測量。角度以度為單位，方位角範圍 [0, 360)。</summary>
public readonly record struct LookAngles(
    double AzimuthDeg,
    double ElevationDeg,
    double RangeKm,
    double RangeRateKmPerSec,
    double ElevationRateDegPerSec);

/// <summary>觀測點：預先算好三角函數與地固座標，觀測計算不重算、不配置記憶體。</summary>
public sealed class ObserverSite
{
    private readonly double _sinLat, _cosLat, _sinLon, _cosLon;

    public ObserverSite(Geodetic location)
    {
        Location = location;
        var lat = location.LatitudeDeg * Angles.DegToRad;
        var lon = location.LongitudeDeg * Angles.DegToRad;
        _sinLat = Math.Sin(lat);
        _cosLat = Math.Cos(lat);
        _sinLon = Math.Sin(lon);
        _cosLon = Math.Cos(lon);
        EcefKm = Wgs84.ToEcef(location);
        ZenithUnit = new Vector3d(_cosLat * _cosLon, _cosLat * _sinLon, _sinLat);
    }

    public Geodetic Location { get; }

    public Vector3d EcefKm { get; }

    /// <summary>天頂方向單位向量（地固座標）。</summary>
    public Vector3d ZenithUnit { get; }

    /// <summary>
    /// ρ = r_sat − r_obs；S、E、Z 為南、東、天頂分量；仰角 = asin(Z/|ρ|)，方位角 = atan2(E, −S)；
    /// 距離變化率 = ρ·v / |ρ|（觀測點在地固座標下靜止）。
    /// 另以解析式提供仰角變化率：dEl/dt = (Ż·|ρ| − Z·ρ̇) / (|ρ|²·cos El)。
    /// </summary>
    public LookAngles Observe(in EcefState sat)
    {
        var rho = sat.PositionKm - EcefKm;
        var v = sat.VelocityKmPerSec;
        var s = _sinLat * _cosLon * rho.X + _sinLat * _sinLon * rho.Y - _cosLat * rho.Z;
        var e = -_sinLon * rho.X + _cosLon * rho.Y;
        var z = _cosLat * _cosLon * rho.X + _cosLat * _sinLon * rho.Y + _sinLat * rho.Z;
        var range = rho.Norm;
        var sinEl = z / range;
        var elRad = Math.Asin(sinEl);
        var az = Angles.Normalize360Deg(Math.Atan2(e, -s) * Angles.RadToDeg);
        var rangeRate = rho.Dot(v) / range;

        var zDot = _cosLat * _cosLon * v.X + _cosLat * _sinLon * v.Y + _sinLat * v.Z;
        var cosEl = Math.Cos(elRad);
        var elRateRad = cosEl > 1e-12 ? (zDot * range - z * rangeRate) / (range * range * cosEl) : 0.0;

        return new LookAngles(az, elRad * Angles.RadToDeg, range, rangeRate, elRateRad * Angles.RadToDeg);
    }
}

/// <summary>單一時刻的完整觀測結果（傳播 + 座標轉換 + 站心觀測），熱路徑零配置。</summary>
public readonly record struct Observation(TemeState Teme, EcefState Ecef, LookAngles Look)
{
    public static Sgp4Error Compute(Sgp4Propagator propagator, ObserverSite site, in JulianDate time, out Observation observation)
    {
        var error = propagator.Propagate(time, out var teme);
        if (error != Sgp4Error.None)
        {
            observation = default;
            return error;
        }

        var ecef = Frames.TemeToEcef(teme, Gmst.ComputeRad(time));
        observation = new Observation(teme, ecef, site.Observe(ecef));
        return Sgp4Error.None;
    }
}
