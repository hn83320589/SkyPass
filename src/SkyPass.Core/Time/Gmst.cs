namespace SkyPass.Core.Time;

/// <summary>格林威治平恆星時（IAU-82），UT1 近似為 UTC。</summary>
public static class Gmst
{
    private const double TwoPi = 2.0 * Math.PI;
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>回傳 GMST（弧度），範圍 [0, 2π)。</summary>
    public static double ComputeRad(in JulianDate jdUt1)
    {
        var t = ((jdUt1.Day - 2451545.0) + jdUt1.Fraction) / 36525.0;
        var thetaSec = -6.2e-6 * t * t * t + 0.093104 * t * t
                       + (876600.0 * 3600 + 8640184.812866) * t + 67310.54841;
        var rad = (thetaSec * DegToRad / 240.0) % TwoPi;
        return rad < 0.0 ? rad + TwoPi : rad;
    }
}
