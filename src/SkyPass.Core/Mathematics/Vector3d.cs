namespace SkyPass.Core.Mathematics;

/// <summary>三維向量（值型別，熱路徑不配置記憶體）。單位由使用處決定，通常為 km 或 km/s。</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public static readonly Vector3d Zero = new(0, 0, 0);

    public double Norm => Math.Sqrt(X * X + Y * Y + Z * Z);

    public double Dot(in Vector3d other) => X * other.X + Y * other.Y + Z * other.Z;

    public Vector3d Cross(in Vector3d other) =>
        new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);

    public static Vector3d operator +(in Vector3d a, in Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vector3d operator -(in Vector3d a, in Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vector3d operator -(in Vector3d a) => new(-a.X, -a.Y, -a.Z);

    public static Vector3d operator *(in Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public static Vector3d operator *(double s, in Vector3d a) => new(a.X * s, a.Y * s, a.Z * s);
}
