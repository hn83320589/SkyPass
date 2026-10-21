namespace SkyPass.Core.Mathematics;

/// <summary>角度工具。公開 API 以度為單位；名稱以 Deg / Rad 標示單位。</summary>
public static class Angles
{
    public const double DegToRad = Math.PI / 180.0;
    public const double RadToDeg = 180.0 / Math.PI;

    /// <summary>正規化到 [0, 360)。</summary>
    public static double Normalize360Deg(double deg)
    {
        var r = deg % 360.0;
        if (r < 0)
        {
            r += 360.0;
        }

        if (r >= 360.0)
        {
            r -= 360.0;
        }

        return r + 0.0; // 去除 −0
    }

    /// <summary>由 a 到 b 的最短角度差（b − a），範圍 [−180, 180)。</summary>
    public static double DifferenceDeg(double aDeg, double bDeg)
    {
        var d = Normalize360Deg(bDeg - aDeg);
        return d >= 180.0 ? d - 360.0 : d;
    }
}
