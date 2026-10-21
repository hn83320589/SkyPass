using SkyPass.Core.Coordinates;
using SkyPass.Core.Passes;
using SkyPass.Core.Sun;
using SkyPass.Core.Time;

namespace SkyPass.Core.Layout;

/// <summary>時間軸：時間 → x 的線性映射。</summary>
public sealed record TimelineLayout(JulianDate Start, double DurationSeconds, double X0, double Width)
{
    public JulianDate End => Start.AddSeconds(DurationSeconds);

    public double XOf(JulianDate t) => X0 + Width * t.SecondsSince(Start) / DurationSeconds;

    public JulianDate TimeOf(double x) => Start.AddSeconds((x - X0) / Width * DurationSeconds);
}

public sealed record TimelineSegment(SatellitePass Pass, double X0, double X1);

public sealed record TimelineRow(int NoradId, string Name, IReadOnlyList<TimelineSegment> Segments);

public enum DaylightKind
{
    Day,
    CivilTwilight,
    NauticalTwilight,
    AstronomicalTwilight,
    Night,
}

public sealed record DaylightBand(DaylightKind Kind, JulianDate Start, JulianDate End, double X0, double X1);

public readonly record struct HourTick(double X, string Label);

public static class DaylightClassifier
{
    /// <summary>太陽仰角 ≥ 0° 白天；[−6°, 0°) 民用曙暮光；[−12°, −6°) 航海；[−18°, −12°) 天文；&lt; −18° 夜晚。</summary>
    public static DaylightKind Classify(double sunElevationDeg) => sunElevationDeg switch
    {
        >= 0 => DaylightKind.Day,
        >= -6 => DaylightKind.CivilTwilight,
        >= -12 => DaylightKind.NauticalTwilight,
        >= -18 => DaylightKind.AstronomicalTwilight,
        _ => DaylightKind.Night,
    };
}

public static class TimelineGeometry
{
    private const double SunSampleSeconds = 60.0;
    private const double BoundaryToleranceSeconds = 0.01;

    /// <summary>每顆衛星一列（依 NORAD 排序），過境區段裁切到時間軸範圍內；同列重疊的區段合併為不重疊。</summary>
    public static IReadOnlyList<TimelineRow> BuildRows(IEnumerable<SatellitePass> passes, TimelineLayout layout)
    {
        var x0 = layout.XOf(layout.Start);
        var x1 = layout.XOf(layout.End);
        return passes
            .Where(p => p.Los.SecondsSince(layout.Start) > 0 && layout.End.SecondsSince(p.Aos) > 0)
            .GroupBy(p => p.NoradId)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var segments = new List<TimelineSegment>();
                foreach (var p in g.OrderBy(p => p.Aos.SecondsSince(layout.Start)))
                {
                    var a = Math.Max(x0, layout.XOf(p.Aos));
                    var b = Math.Min(x1, layout.XOf(p.Los));
                    if (segments.Count > 0 && a < segments[^1].X1)
                    {
                        a = segments[^1].X1;
                    }

                    if (b >= a)
                    {
                        segments.Add(new TimelineSegment(p, a, b));
                    }
                }

                return new TimelineRow(g.Key, g.First().SatelliteName, segments);
            })
            .ToList();
    }

    /// <summary>依觀測點太陽仰角把時間軸分成白天、曙暮光、夜晚區段，連續且不重疊。</summary>
    public static IReadOnlyList<DaylightBand> BuildDaylightBands(ObserverSite site, TimelineLayout layout)
    {
        DaylightKind KindAt(double offset) => DaylightClassifier.Classify(SunPosition.ElevationDeg(site, layout.Start.AddSeconds(offset)));

        var bands = new List<DaylightBand>();
        var total = layout.DurationSeconds;
        var bandStart = 0.0;
        var kind = KindAt(0);
        var prevOffset = 0.0;
        for (var offset = SunSampleSeconds; ; offset = Math.Min(offset + SunSampleSeconds, total))
        {
            var k = KindAt(offset);
            if (k != kind)
            {
                // 二分到分類改變處（每分鐘太陽仰角變化遠小於 6°，一個取樣間隔最多跨一個門檻）
                double lo = prevOffset, hi = offset;
                while (hi - lo > BoundaryToleranceSeconds)
                {
                    var mid = 0.5 * (lo + hi);
                    if (KindAt(mid) == kind)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                var boundary = 0.5 * (lo + hi);
                bands.Add(Band(layout, kind, bandStart, boundary));
                bandStart = boundary;
                kind = KindAt(Math.Min(total, boundary + BoundaryToleranceSeconds));
            }

            prevOffset = offset;
            if (offset >= total)
            {
                break;
            }
        }

        bands.Add(Band(layout, kind, bandStart, total));
        return bands;
    }

    /// <summary>以顯示時區的整點產生刻度，標籤為兩位數小時。</summary>
    public static IReadOnlyList<HourTick> HourTicks(TimelineLayout layout, TimeSpan displayOffset)
    {
        var startLocal = layout.Start.ToUtc() + displayOffset;
        var firstHour = new DateTime(startLocal.Year, startLocal.Month, startLocal.Day, startLocal.Hour, 0, 0, DateTimeKind.Unspecified);
        if (firstHour < startLocal)
        {
            firstHour = firstHour.AddHours(1);
        }

        var ticks = new List<HourTick>();
        for (var local = firstHour; ; local = local.AddHours(1))
        {
            var t = JulianDate.FromUtc(DateTime.SpecifyKind(local - displayOffset, DateTimeKind.Utc));
            if (t.SecondsSince(layout.End) > 1e-6)
            {
                break;
            }

            ticks.Add(new HourTick(layout.XOf(t), local.Hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture)));
        }

        return ticks;
    }

    private static DaylightBand Band(TimelineLayout layout, DaylightKind kind, double from, double to)
    {
        var start = from == 0 ? layout.Start : layout.Start.AddSeconds(from);
        var end = to == layout.DurationSeconds ? layout.End : layout.Start.AddSeconds(to);
        return new DaylightBand(kind, start, end, layout.XOf(start), layout.XOf(end));
    }
}

/// <summary>
/// DPI 縮放：每個座標都由精確的邏輯座標乘上倍率後各自四捨五入成整數像素，
/// 不累加四捨五入後的間距，因此沒有累積誤差，相鄰區段共用同一條邊。
/// </summary>
public readonly record struct DpiScaler(double Scale)
{
    public int ToPixels(double logical) => (int)Math.Round(logical * Scale, MidpointRounding.AwayFromZero);

    public PixelSpan SnapSpan(double logicalStart, double logicalEnd) => new(ToPixels(logicalStart), ToPixels(logicalEnd));
}

public readonly record struct PixelSpan(int Start, int End);
