using SkyPass.Core.Coordinates;
using SkyPass.Core.Layout;
using SkyPass.Core.Passes;
using SkyPass.Core.Sun;
using SkyPass.Core.Time;
using SkyPass.Tests.Core.Passes;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Layout;

/// <summary>驗收 #15（時間軸與 DPI）：區段不重疊、時間到座標映射、DPI 四捨五入無累積誤差。</summary>
public class TimelineGeometryTests
{
    private static readonly JulianDate Start = JulianDate.FromUtc(new DateTime(2026, 10, 21, 16, 0, 0, DateTimeKind.Utc));
    private static readonly TimelineLayout Layout = new(Start, DurationSeconds: 86400, X0: 100, Width: 960);

    [Fact]
    public void Time_maps_linearly_to_x()
    {
        Assert.Equal(100, Layout.XOf(Start), 9);
        Assert.Equal(1060, Layout.XOf(Start.AddSeconds(86400)), 9);
        Assert.Equal(580, Layout.XOf(Start.AddSeconds(43200)), 9);
        Assert.Equal(100 + 960.0 / 24, Layout.XOf(Start.AddSeconds(3600)), 9);
    }

    [Fact]
    public void Property_x_to_time_inverts_time_to_x()
    {
        Property.Run("時間軸映射往返", seed: 15101, iterations: 2000, rng =>
        {
            var t = Start.AddSeconds(rng.NextDouble() * 86400);

            var back = Layout.TimeOf(Layout.XOf(t));

            Assert.True(Math.Abs(back.SecondsSince(t)) < 1e-4);
        });
    }

    [Fact]
    public void Satellite_rows_contain_clipped_non_overlapping_segments()
    {
        var sats = PassPredictorTests.Constellation(14);
        var site = new Geodetic(25.03, 121.56, 0.01);
        var start = sats[0].Epoch;
        var layout = new TimelineLayout(start, 86400, 0, 1000);
        var predicted = PassPredictor.Predict(new PredictionRequest(sats, site, start.AddSeconds(-3600), start.AddSeconds(90000), 10)).Passes;
        var crossingStart = predicted[0] with { NoradId = 1, SatelliteName = "CROSS", Aos = start.AddSeconds(-100), Los = start.AddSeconds(200) };
        var passes = predicted.Prepend(crossingStart).ToList();

        var rows = TimelineGeometry.BuildRows(passes, layout);

        Assert.Equal(passes.Select(p => p.NoradId).Distinct().Count(), rows.Count);
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Segments.Count; i++)
            {
                var s = row.Segments[i];
                Assert.InRange(s.X0, 0, 1000);
                Assert.InRange(s.X1, 0, 1000);
                Assert.True(s.X1 >= s.X0);
                if (i > 0)
                {
                    Assert.True(s.X0 >= row.Segments[i - 1].X1, "同一列的區段重疊");
                }

                var pass = s.Pass;
                if (pass.Aos.SecondsSince(start) >= 0 && start.AddSeconds(86400).SecondsSince(pass.Los) >= 0)
                {
                    Assert.Equal(layout.XOf(pass.Aos), s.X0, 9);
                    Assert.Equal(layout.XOf(pass.Los), s.X1, 9);
                }
            }
        }

        Assert.Contains(rows.SelectMany(r => r.Segments), s => s.X0 == 0);     // 起點前已開始的被裁切
    }

    [Fact]
    public void Daylight_bands_cover_window_without_gaps_and_match_sun_elevation()
    {
        var site = new ObserverSite(new Geodetic(25.03, 121.56, 0.01));

        var bands = TimelineGeometry.BuildDaylightBands(site, Layout);

        Assert.Equal(Start, bands[0].Start);
        Assert.Equal(Start.AddSeconds(86400), bands[^1].End);
        for (var i = 0; i < bands.Count; i++)
        {
            var b = bands[i];
            Assert.True(b.End.SecondsSince(b.Start) > 0);
            if (i > 0)
            {
                Assert.Equal(bands[i - 1].End, b.Start);
                Assert.NotEqual(bands[i - 1].Kind, b.Kind);
                Assert.Equal(bands[i - 1].X1, b.X0);
            }

            var mid = b.Start.AddSeconds(0.5 * b.End.SecondsSince(b.Start));
            Assert.Equal(b.Kind, DaylightClassifier.Classify(SunPosition.ElevationDeg(site, mid)));
        }

        Assert.Contains(bands, b => b.Kind == DaylightKind.Night);
        Assert.Contains(bands, b => b.Kind == DaylightKind.Day);
        Assert.Contains(bands, b => b.Kind == DaylightKind.CivilTwilight);
    }

    [Theory]
    [InlineData(10.0, DaylightKind.Day)]
    [InlineData(0.0, DaylightKind.Day)]
    [InlineData(-0.1, DaylightKind.CivilTwilight)]
    [InlineData(-6.0, DaylightKind.CivilTwilight)]          // 與可目視門檻「< −6°」一致：恰為 −6° 仍屬民用曙暮光
    [InlineData(-6.01, DaylightKind.NauticalTwilight)]
    [InlineData(-12.0, DaylightKind.NauticalTwilight)]
    [InlineData(-12.01, DaylightKind.AstronomicalTwilight)]
    [InlineData(-18.0, DaylightKind.AstronomicalTwilight)]
    [InlineData(-18.01, DaylightKind.Night)]
    [InlineData(-40.0, DaylightKind.Night)]
    public void Daylight_classification_thresholds(double sunEl, DaylightKind expected)
    {
        Assert.Equal(expected, DaylightClassifier.Classify(sunEl));
    }

    [Fact]
    public void Hour_ticks_follow_display_offset()
    {
        // 起點 16:00 UTC = 00:00 UTC+8
        var ticks = TimelineGeometry.HourTicks(Layout, TimeSpan.FromHours(8));

        Assert.Equal(25, ticks.Count);
        Assert.Equal("00", ticks[0].Label);
        Assert.Equal(100, ticks[0].X, 9);
        Assert.Equal("12", ticks[12].Label);

        var halfHour = TimelineGeometry.HourTicks(Layout, TimeSpan.FromHours(5.5));
        Assert.Equal(100 + 960.0 / 48, halfHour[0].X, 9); // 16:00 UTC = 21:30 → 第一個整點在 30 分鐘後
        Assert.Equal("22", halfHour[0].Label);
    }

    [Fact]
    public void Dpi_rounding_has_no_cumulative_error()
    {
        // 每個刻度各自由精確邏輯座標換算後四捨五入，而不是把「四捨五入後的間距」逐一累加
        var scaler = new DpiScaler(1.3);
        const double cell = 960.0 / 24; // 40 邏輯像素 → 52 實體像素
        const double odd = 960.0 / 7;   // 137.14… 邏輯像素，四捨五入有誤差

        for (var i = 0; i <= 7; i++)
        {
            Assert.Equal((int)Math.Round(i * odd * 1.3, MidpointRounding.AwayFromZero), scaler.ToPixels(i * odd));
        }

        Assert.NotEqual(7 * scaler.ToPixels(odd), scaler.ToPixels(7 * odd)); // 累加會產生的誤差
        Assert.Equal(1248, scaler.ToPixels(960));
        Assert.Equal(1248, scaler.ToPixels(24 * cell));
        Assert.Equal(scaler.ToPixels(24 * cell), scaler.ToPixels(960));
    }

    [Fact]
    public void Property_dpi_snapped_adjacent_segments_share_edges()
    {
        Property.Run("DPI 四捨五入後相鄰區段無縫", seed: 15102, iterations: 2000, rng =>
        {
            var scaler = new DpiScaler(1.0 + rng.Next(0, 9) * 0.25);
            var edges = Enumerable.Range(0, 20).Select(_ => rng.NextDouble() * 1000).Order().ToArray();

            var rects = edges.Zip(edges.Skip(1), (a, b) => scaler.SnapSpan(a, b)).ToArray();

            for (var i = 1; i < rects.Length; i++)
            {
                Assert.Equal(rects[i - 1].End, rects[i].Start);
            }

            Assert.All(rects, r => Assert.True(r.End >= r.Start));
            Assert.Equal(scaler.ToPixels(edges[^1]), rects[^1].End);
        });
    }
}
