using System.Text;
using SkyPass.Core.Export;
using SkyPass.Core.Passes;
using SkyPass.Core.Time;

namespace SkyPass.Tests.Core.Export;

/// <summary>驗收 #19：欄位、時區轉換、UTF-8 with BOM、跨日與跨月。</summary>
public class PassCsvExporterTests
{
    private static JulianDate Utc(int y, int mo, int d, int h, int mi, int s) =>
        JulianDate.FromUtc(new DateTime(y, mo, d, h, mi, s, DateTimeKind.Utc));

    private static SatellitePass Pass(JulianDate aos, double durationSec, string name = "ISS (ZARYA)") => new()
    {
        NoradId = 25544,
        SatelliteName = name,
        Aos = aos,
        AosAzimuthDeg = 213.456,
        MaxElevationTime = aos.AddSeconds(durationSec / 2),
        MaxElevationDeg = 45.678,
        MaxElevationAzimuthDeg = 300.1,
        Los = aos.AddSeconds(durationSec),
        LosAzimuthDeg = 35.04,
    };

    [Fact]
    public void Header_and_columns()
    {
        var csv = PassCsvExporter.ToCsv([Pass(Utc(2026, 10, 21, 10, 0, 0), 600)], TimeSpan.FromHours(8));
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(
            "衛星,NORAD,AOS (UTC+08:00),AOS 方位(°),最大仰角時刻 (UTC+08:00),最大仰角(°),最大仰角方位(°),LOS (UTC+08:00),LOS 方位(°),持續(秒),起點前已開始,終點後才結束,可目視開始 (UTC+08:00),可目視結束 (UTC+08:00),可目視時段",
            lines[0]);
        Assert.Equal("ISS (ZARYA),25544,2026-10-21 18:00:00,213.5,2026-10-21 18:05:00,45.68,300.1,2026-10-21 18:10:00,35.0,600,否,否,,,", lines[1]);
    }

    [Fact]
    public void Utc_offset_conversion_crosses_day_and_month()
    {
        // 10/31 23:55 UTC → UTC+8 為 11/01 07:55；UTC−5 為 10/31 18:55
        var pass = Pass(Utc(2026, 10, 31, 23, 55, 0), 600);

        var plus8 = PassCsvExporter.ToCsv([pass], TimeSpan.FromHours(8)).Split("\r\n")[1].Split(',');
        var minus5 = PassCsvExporter.ToCsv([pass], TimeSpan.FromHours(-5)).Split("\r\n")[1].Split(',');
        var utc = PassCsvExporter.ToCsv([pass], TimeSpan.Zero).Split("\r\n")[1].Split(',');

        Assert.Equal("2026-11-01 07:55:00", plus8[2]);
        Assert.Equal("2026-11-01 08:05:00", plus8[7]);
        Assert.Equal("2026-10-31 18:55:00", minus5[2]);
        Assert.Equal("2026-10-31 23:55:00", utc[2]);
        Assert.Equal("2026-11-01 00:05:00", utc[7]); // UTC 跨日跨月
    }

    [Fact]
    public void Year_boundary_and_half_hour_offset()
    {
        var pass = Pass(Utc(2026, 12, 31, 18, 40, 0), 300);

        var row = PassCsvExporter.ToCsv([pass], TimeSpan.FromHours(5.5)).Split("\r\n");

        Assert.Contains("(UTC+05:30)", row[0]);
        Assert.Equal("2027-01-01 00:10:00", row[1].Split(',')[2]);
    }

    [Fact]
    public void Times_are_rounded_to_nearest_second()
    {
        var pass = Pass(Utc(2026, 10, 21, 10, 0, 0).AddSeconds(0.6), 600);

        var cols = PassCsvExporter.ToCsv([pass], TimeSpan.Zero).Split("\r\n")[1].Split(',');

        Assert.Equal("2026-10-21 10:00:01", cols[2]);
    }

    [Fact]
    public void Visible_window_and_boundary_flags()
    {
        var aos = Utc(2026, 10, 21, 11, 0, 0);
        var pass = Pass(aos, 600) with
        {
            StartedBeforeWindow = true,
            VisibleIntervals = [new TimeInterval(aos.AddSeconds(60), aos.AddSeconds(200)), new TimeInterval(aos.AddSeconds(300), aos.AddSeconds(400))],
        };

        var cols = PassCsvExporter.ToCsv([pass], TimeSpan.FromHours(8)).Split("\r\n")[1].Split(',');

        Assert.Equal("是", cols[10]);
        Assert.Equal("否", cols[11]);
        Assert.Equal("2026-10-21 19:01:00", cols[12]);
        Assert.Equal("2026-10-21 19:06:40", cols[13]);
        Assert.Equal("19:01:00–19:03:20 / 19:05:00–19:06:40", cols[14]);
    }

    [Fact]
    public void Fields_with_commas_and_quotes_are_escaped()
    {
        var csv = PassCsvExporter.ToCsv([Pass(Utc(2026, 10, 21, 10, 0, 0), 600, "SAT, \"A\"")], TimeSpan.Zero);

        Assert.StartsWith("\"SAT, \"\"A\"\"\",25544,", csv.Split("\r\n")[1]);
    }

    [Fact]
    public void Written_file_is_utf8_with_bom()
    {
        using var stream = new MemoryStream();

        PassCsvExporter.Write(stream, [Pass(Utc(2026, 10, 21, 10, 0, 0), 600, "福衛五號")], TimeSpan.FromHours(8));
        var bytes = stream.ToArray();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Contains("福衛五號", text);
        Assert.StartsWith("衛星,", text);
    }

    [Theory]
    [InlineData(8, 0, "UTC+08:00")]
    [InlineData(-3, -30, "UTC-03:30")]
    [InlineData(0, 0, "UTC+00:00")]
    public void Offset_label(int h, int m, string expected)
    {
        Assert.Equal(expected, TimeDisplay.OffsetLabel(new TimeSpan(h, m, 0)));
    }
}
