using SkyPass.Core.Coordinates;
using SkyPass.Core.Layout;
using SkyPass.Core.Passes;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;

namespace SkyPass.Presenters;

public enum TrackStyle
{
    /// <summary>衛星在地影中。</summary>
    Shadow,

    /// <summary>衛星受日照，但天色不夠暗。</summary>
    Sunlit,

    /// <summary>可目視。</summary>
    Visible,
}

public sealed record SkyPlotModel(
    SkyPlotLayout Layout,
    IReadOnlyList<PointD> Horizon,
    IReadOnlyList<PointD> MaskCircle,
    IReadOnlyList<IReadOnlyList<PointD>> ElevationRings,
    IReadOnlyList<(PointD From, PointD To)> Spokes,
    IReadOnlyList<(string Label, PointD Position)> Labels,
    IReadOnlyList<TrackPoint> Track,
    SatellitePass? Pass)
{
    public static TrackStyle StyleOf(TrackPoint p) =>
        p.Visible ? TrackStyle.Visible : p.Illumination == Illumination.Sunlit ? TrackStyle.Sunlit : TrackStyle.Shadow;
}

/// <summary>天空圖：選定過境的軌跡、遮罩圈、方位標示與目前位置。所有座標由 Core 計算，App 只負責畫線。</summary>
public sealed class SkyPlotPresenter
{
    private const double MaxPixelGap = 3.0;
    private const double LabelOffset = 14.0;

    private (double X, double Y, double W, double H) _viewport = (0, 0, 300, 300);
    private SatellitePass? _pass;
    private Sgp4Propagator? _propagator;
    private ObserverSite? _site;
    private double _maskDeg = 10;
    private SkyPlotModel? _model;

    public SkyPlotModel Model => _model ??= Build();

    public void SetViewport(double x, double y, double width, double height)
    {
        if (_viewport != (x, y, width, height))
        {
            _viewport = (x, y, width, height);
            _model = null;
        }
    }

    public void SetPass(SatellitePass? pass, TleSet? tle, Geodetic site, double maskDeg)
    {
        if (ReferenceEquals(pass, _pass) && ReferenceEquals(tle, _propagator?.Tle) && _site?.Location == site && _maskDeg == maskDeg)
        {
            return;
        }

        _pass = pass;
        _propagator = tle is null ? null : Sgp4Propagator.Create(tle);
        _site = new ObserverSite(site);
        _maskDeg = maskDeg;
        _model = null;
    }

    /// <summary>選定衛星此刻的位置；在地平線下或無法傳播時回傳 null。</summary>
    public PointD? CurrentPosition(JulianDate now)
    {
        if (_propagator is null || _site is null || Observation.Compute(_propagator, _site, now, out var o) != Sgp4Error.None || o.Look.ElevationDeg < 0)
        {
            return null;
        }

        return Model.Layout.Project(o.Look.AzimuthDeg, o.Look.ElevationDeg);
    }

    private SkyPlotModel Build()
    {
        var layout = SkyPlotLayout.Fit(_viewport.X, _viewport.Y, _viewport.W, _viewport.H, margin: 24);
        IReadOnlyList<TrackPoint> track = _pass is not null && _propagator is not null && _site is not null
            ? SkyTrack.Build(_propagator, _site, _pass, layout, MaxPixelGap)
            : [];
        return new SkyPlotModel(
            layout,
            layout.ElevationCircle(0),
            layout.ElevationCircle(_maskDeg),
            [layout.ElevationCircle(30), layout.ElevationCircle(60)],
            layout.AzimuthSpokes(30),
            layout.CardinalLabels(LabelOffset),
            track,
            _pass);
    }
}
