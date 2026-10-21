using SkyPass.Core.Coordinates;
using SkyPass.Core.Layout;
using SkyPass.Core.Passes;
using SkyPass.Core.Propagation;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Layout;

/// <summary>驗收 #15（天空圖）：北在上、東在左、天頂在圓心、仰角到半徑線性、遮罩圈、軌跡取樣密度。</summary>
public class SkyPlotGeometryTests
{
    private static readonly SkyPlotLayout Layout = new(CenterX: 200, CenterY: 150, Radius: 100);

    [Fact]
    public void Zenith_is_at_center()
    {
        Assert.Equal(new PointD(200, 150), Layout.Project(123.0, 90.0));
    }

    [Fact]
    public void North_is_up_and_east_is_left()
    {
        var north = Layout.Project(0, 0);
        var east = Layout.Project(90, 0);
        var south = Layout.Project(180, 0);
        var west = Layout.Project(270, 0);

        AssertPoint(200, 50, north);   // 螢幕 y 向下，北在上方
        AssertPoint(100, 150, east);   // 東在左（仰望天空的鏡像）
        AssertPoint(200, 250, south);
        AssertPoint(300, 150, west);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(30, 100 * 60.0 / 90)]
    [InlineData(45, 50)]
    [InlineData(60, 100 * 30.0 / 90)]
    [InlineData(90, 0)]
    public void Elevation_maps_linearly_to_radius(double el, double radius)
    {
        Assert.Equal(radius, Layout.RadiusForElevation(el), 12);
        var p = Layout.Project(37, el);
        Assert.Equal(radius, Math.Sqrt(Math.Pow(p.X - 200, 2) + Math.Pow(p.Y - 150, 2)), 9);
    }

    [Fact]
    public void Property_projection_is_linear_in_elevation_and_preserves_azimuth()
    {
        Property.Run("天空圖投影", seed: 15001, iterations: 2000, rng =>
        {
            var az = rng.NextDouble() * 360;
            var el = rng.NextDouble() * 90;
            var p = Layout.Project(az, el);
            var (backAz, backEl) = Layout.Unproject(p);

            Assert.Equal(el, backEl, 9);
            if (el < 89.999)
            {
                Assert.Equal(0, SkyPass.Core.Mathematics.Angles.DifferenceDeg(az, backAz), 9);
            }
        });
    }

    [Fact]
    public void Mask_circle_has_constant_radius()
    {
        var circle = Layout.ElevationCircle(10.0, segments: 72);

        Assert.Equal(73, circle.Count); // 閉合
        Assert.Equal(circle[0], circle[^1]);
        Assert.All(circle, p => Assert.Equal(Layout.RadiusForElevation(10), Math.Sqrt(Math.Pow(p.X - 200, 2) + Math.Pow(p.Y - 150, 2)), 9));
    }

    [Fact]
    public void Fit_centres_largest_circle_in_rectangle_with_margin()
    {
        var l = SkyPlotLayout.Fit(x: 10, y: 20, width: 400, height: 300, margin: 25);

        Assert.Equal(210, l.CenterX);
        Assert.Equal(170, l.CenterY);
        Assert.Equal(125, l.Radius);
    }

    [Fact]
    public void Trajectory_respects_maximum_pixel_gap_and_carries_illumination()
    {
        var (prop, site, pass) = Doppler.DopplerTests.IssPass();
        var visiblePass = pass with { VisibleIntervals = SkyPass.Core.Visibility.VisibilityCalculator.ComputeIntervals(prop, site, pass.Aos, pass.Los) };

        var track = SkyTrack.Build(prop, site, visiblePass, Layout, maxPixelGap: 4.0);

        Assert.Equal(pass.Aos, track[0].Time);
        Assert.Equal(pass.Los, track[^1].Time);
        for (var i = 1; i < track.Count; i++)
        {
            var d = Math.Sqrt(Math.Pow(track[i].Point.X - track[i - 1].Point.X, 2) + Math.Pow(track[i].Point.Y - track[i - 1].Point.Y, 2));
            Assert.True(d <= 4.0 + 1e-9, $"第 {i} 段長 {d:F3} px");
            Assert.True(track[i].Time.SecondsSince(track[i - 1].Time) > 0);
        }

        Assert.All(track, p =>
        {
            prop.Propagate(p.Time, out var s);
            var shadow = SkyPass.Core.Sun.Shadow.IsInShadow(s.PositionKm, SkyPass.Core.Sun.SunPosition.UnitVectorTeme(p.Time));
            Assert.Equal(shadow ? Illumination.Shadow : Illumination.Sunlit, p.Illumination);
            Assert.Equal(visiblePass.VisibleIntervals.Any(iv => p.Time.SecondsSince(iv.Start) >= 0 && iv.End.SecondsSince(p.Time) >= 0), p.Visible);
        });
    }

    [Fact]
    public void Smaller_gap_produces_denser_trajectory()
    {
        var (prop, site, pass) = Doppler.DopplerTests.IssPass();

        var coarse = SkyTrack.Build(prop, site, pass, Layout, maxPixelGap: 10);
        var fine = SkyTrack.Build(prop, site, pass, Layout, maxPixelGap: 2);

        Assert.True(fine.Count > coarse.Count * 3);
    }

    private static void AssertPoint(double x, double y, PointD p)
    {
        Assert.Equal(x, p.X, 9);
        Assert.Equal(y, p.Y, 9);
    }
}
