using SkyPass.Core.Coordinates;
using SkyPass.Core.Mathematics;
using SkyPass.Core.Passes;
using SkyPass.Core.Propagation;
using SkyPass.Core.Sun;
using SkyPass.Core.Time;

namespace SkyPass.Core.Layout;

/// <summary>畫面座標（邏輯像素，y 向下）。</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>
/// 天空圖（極座標）：圓心為天頂，外圈為地平線，半徑與仰角線性對應 r = R·(90 − El)/90。
/// 仰望天空的視角：北在上、東在左，因此 x = cx − r·sin(Az)，y = cy − r·cos(Az)。
/// </summary>
public sealed record SkyPlotLayout(double CenterX, double CenterY, double Radius)
{
    public static SkyPlotLayout Fit(double x, double y, double width, double height, double margin) =>
        new(x + width / 2, y + height / 2, Math.Max(0, Math.Min(width, height) / 2 - margin));

    public double RadiusForElevation(double elevationDeg) => Radius * (90.0 - elevationDeg) / 90.0;

    public PointD Project(double azimuthDeg, double elevationDeg)
    {
        var r = RadiusForElevation(elevationDeg);
        var az = azimuthDeg * Angles.DegToRad;
        return new PointD(CenterX - r * Math.Sin(az), CenterY - r * Math.Cos(az));
    }

    public (double AzimuthDeg, double ElevationDeg) Unproject(PointD p)
    {
        var dx = CenterX - p.X;
        var dy = CenterY - p.Y;
        var r = Math.Sqrt(dx * dx + dy * dy);
        return (Angles.Normalize360Deg(Math.Atan2(dx, dy) * Angles.RadToDeg), 90.0 - r / Radius * 90.0);
    }

    /// <summary>等仰角圓（例如遮罩圈），首尾相同以閉合。</summary>
    public IReadOnlyList<PointD> ElevationCircle(double elevationDeg, int segments = 120)
    {
        var points = new PointD[segments + 1];
        for (var i = 0; i < segments; i++)
        {
            points[i] = Project(360.0 * i / segments, elevationDeg);
        }

        points[segments] = points[0];
        return points;
    }

    /// <summary>自天頂到地平線的方位輔助線。</summary>
    public IReadOnlyList<(PointD From, PointD To)> AzimuthSpokes(double stepDeg = 30) =>
        Enumerable.Range(0, (int)Math.Round(360 / stepDeg))
            .Select(i => (new PointD(CenterX, CenterY), Project(i * stepDeg, 0)))
            .ToList();

    /// <summary>方位標籤位置（在地平線外 <paramref name="offset"/> 像素）。</summary>
    public IReadOnlyList<(string Label, PointD Position)> CardinalLabels(double offset)
    {
        var outer = this with { Radius = Radius + offset };
        return [("N", outer.Project(0, 0)), ("E", outer.Project(90, 0)), ("S", outer.Project(180, 0)), ("W", outer.Project(270, 0))];
    }
}

public enum Illumination
{
    Sunlit,
    Shadow,
}

public readonly record struct TrackPoint(JulianDate Time, double AzimuthDeg, double ElevationDeg, PointD Point, Illumination Illumination, bool Visible);

public static class SkyTrack
{
    private const double InitialStepSeconds = 10.0;
    private const double MinStepSeconds = 0.01;

    /// <summary>
    /// 過境軌跡折線：先每 10 秒取樣，再把畫面上相鄰兩點距離大於 <paramref name="maxPixelGap"/> 的區間對半細分，
    /// 直到全部不超過上限（或時間間隔已小於 0.01 秒）。每點帶日照／陰影與可目視狀態供著色。
    /// </summary>
    public static IReadOnlyList<TrackPoint> Build(Sgp4Propagator propagator, ObserverSite site, SatellitePass pass, SkyPlotLayout layout, double maxPixelGap)
    {
        var total = pass.DurationSeconds;
        var result = new List<TrackPoint>();
        var count = Math.Max(1, (int)Math.Ceiling(total / InitialStepSeconds));
        TrackPoint? prev = null;
        for (var i = 0; i <= count; i++)
        {
            var p = Evaluate(propagator, site, pass, layout, pass.Aos.AddSeconds(total * i / count));
            if (p is null)
            {
                break;
            }

            if (prev is { } a)
            {
                Subdivide(propagator, site, pass, layout, a, p.Value, maxPixelGap, result);
            }

            result.Add(p.Value);
            prev = p;
        }

        return result;
    }

    /// <summary>把 (a, b) 之間需要的細分點依時間順序加入（不含 a、b）。</summary>
    private static void Subdivide(Sgp4Propagator propagator, ObserverSite site, SatellitePass pass, SkyPlotLayout layout,
        TrackPoint a, TrackPoint b, double maxPixelGap, List<TrackPoint> output)
    {
        var dx = b.Point.X - a.Point.X;
        var dy = b.Point.Y - a.Point.Y;
        var dt = b.Time.SecondsSince(a.Time);
        if (dx * dx + dy * dy <= maxPixelGap * maxPixelGap || dt < MinStepSeconds)
        {
            return;
        }

        var mid = Evaluate(propagator, site, pass, layout, a.Time.AddSeconds(dt / 2));
        if (mid is null)
        {
            return;
        }

        Subdivide(propagator, site, pass, layout, a, mid.Value, maxPixelGap, output);
        output.Add(mid.Value);
        Subdivide(propagator, site, pass, layout, mid.Value, b, maxPixelGap, output);
    }

    private static TrackPoint? Evaluate(Sgp4Propagator propagator, ObserverSite site, SatellitePass pass, SkyPlotLayout layout, JulianDate t)
    {
        if (Observation.Compute(propagator, site, t, out var o) != Sgp4Error.None)
        {
            return null;
        }

        var shadow = Shadow.IsInShadow(o.Teme.PositionKm, SunPosition.UnitVectorTeme(t));
        var visible = pass.VisibleIntervals.Any(iv => t.SecondsSince(iv.Start) >= 0 && iv.End.SecondsSince(t) >= 0);
        return new TrackPoint(t, o.Look.AzimuthDeg, o.Look.ElevationDeg, layout.Project(o.Look.AzimuthDeg, o.Look.ElevationDeg),
            shadow ? Illumination.Shadow : Illumination.Sunlit, visible);
    }
}
