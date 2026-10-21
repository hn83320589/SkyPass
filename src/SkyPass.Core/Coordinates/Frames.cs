using SkyPass.Core.Mathematics;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;

namespace SkyPass.Core.Coordinates;

public static class Frames
{
    /// <summary>地球自轉角速度 ω（rad/s）。</summary>
    internal const double EarthRotationRadPerSec = 7.2921158553e-5;

    /// <summary>TEME → 地固座標，θ 取 <paramref name="time"/> 的 GMST（IAU-82）。</summary>
    public static EcefState TemeToEcef(in TemeState teme, in JulianDate time) => TemeToEcef(teme, Gmst.ComputeRad(time));

    /// <summary>
    /// TEME → 地固座標：r' = R(θ)·r；v' = R(θ)·v + (ω·y', −ω·x', 0)，θ = GMST（弧度，內部使用）。
    /// 不處理極移與章動。
    /// </summary>
    internal static EcefState TemeToEcef(in TemeState teme, double gmstRad)
    {
        var c = Math.Cos(gmstRad);
        var s = Math.Sin(gmstRad);
        var r = teme.PositionKm;
        var v = teme.VelocityKmPerSec;
        var x = c * r.X + s * r.Y;
        var y = -s * r.X + c * r.Y;
        var vx = c * v.X + s * v.Y;
        var vy = -s * v.X + c * v.Y;
        return new EcefState(
            new Vector3d(x, y, r.Z),
            new Vector3d(vx + EarthRotationRadPerSec * y, vy - EarthRotationRadPerSec * x, v.Z));
    }

    /// <summary>以 θ = GMST 把 TEME 方向向量旋轉到地固座標（無速度項）。</summary>
    internal static Vector3d RotateTemeToEcef(in Vector3d v, double gmstRad)
    {
        var c = Math.Cos(gmstRad);
        var s = Math.Sin(gmstRad);
        return new Vector3d(c * v.X + s * v.Y, -s * v.X + c * v.Y, v.Z);
    }
}
