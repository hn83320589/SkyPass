using SkyPass.Core.Coordinates;
using SkyPass.Core.Mathematics;
using SkyPass.Core.Time;

namespace SkyPass.Core.Sun;

/// <summary>低精度太陽位置（儒略日以 UTC 計）。</summary>
public static class SunPosition
{
    /// <summary>
    /// n = JD − 2451545.0；L = 280.460° + 0.9856474°·n；g = 357.528° + 0.9856003°·n；
    /// λ = L + 1.915°·sin g + 0.020°·sin 2g；ε = 23.439° − 0.0000004°·n；
    /// 回傳 (cos λ, cos ε·sin λ, sin ε·sin λ)，近似為 TEME 方向。
    /// </summary>
    public static Vector3d UnitVectorTeme(in JulianDate jd)
    {
        var n = (jd.Day - 2451545.0) + jd.Fraction;
        var lDeg = Angles.Normalize360Deg(280.460 + 0.9856474 * n);
        var gRad = Angles.Normalize360Deg(357.528 + 0.9856003 * n) * Angles.DegToRad;
        var lambdaRad = Angles.Normalize360Deg(lDeg + 1.915 * Math.Sin(gRad) + 0.020 * Math.Sin(2 * gRad)) * Angles.DegToRad;
        var epsRad = (23.439 - 0.0000004 * n) * Angles.DegToRad;
        var sinLambda = Math.Sin(lambdaRad);
        return new Vector3d(Math.Cos(lambdaRad), Math.Cos(epsRad) * sinLambda, Math.Sin(epsRad) * sinLambda);
    }

    /// <summary>觀測點的太陽（幾何）仰角：ŝ 以 θ = GMST 轉到地固後，與天頂方向內積取 asin。</summary>
    public static double ElevationDeg(ObserverSite site, in JulianDate jd)
    {
        var sunEcef = Frames.RotateTemeToEcef(UnitVectorTeme(jd), Gmst.ComputeRad(jd));
        return Math.Asin(Math.Clamp(sunEcef.Dot(site.ZenithUnit), -1.0, 1.0)) * Angles.RadToDeg;
    }
}
