using SkyPass.Core.Time;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Time;

public class JulianDateTests
{
    public static TheoryData<int, int, int, int, double> KnownDates => new()
    {
        { 2000, 1, 1, 12, 2451545.0 },     // J2000.0
        { 2000, 2, 29, 0, 2451603.5 },     // 2000 為閏年（可被 400 整除）
        { 2000, 3, 1, 0, 2451604.5 },
        { 2100, 2, 28, 0, 2488127.5 },     // 2100 非閏年
        { 2100, 3, 1, 0, 2488128.5 },
        { 1957, 10, 4, 0, 2436115.5 },
        { 1970, 1, 1, 0, 2440587.5 },
        { 2024, 12, 31, 0, 2460675.5 },    // 跨年
        { 2025, 1, 1, 0, 2460676.5 },
        { 2025, 1, 31, 0, 2460706.5 },     // 跨月
        { 2025, 2, 1, 0, 2460707.5 },
    };

    [Theory]
    [MemberData(nameof(KnownDates))]
    public void FromUtc_matches_known_julian_dates(int y, int mo, int d, int h, double expected)
    {
        var jd = JulianDate.FromUtc(new DateTime(y, mo, d, h, 0, 0, DateTimeKind.Utc));

        Assert.Equal(expected, jd.Day + jd.Fraction);
    }

    [Theory]
    [MemberData(nameof(KnownDates))]
    public void ToUtc_inverts_known_julian_dates(int y, int mo, int d, int h, double jdValue)
    {
        var day = Math.Floor(jdValue - 0.5) + 0.5;
        var jd = JulianDate.FromParts(day, jdValue - day);

        Assert.Equal(new DateTime(y, mo, d, h, 0, 0, DateTimeKind.Utc), jd.ToUtc());
    }

    [Fact]
    public void Matches_reference_file()
    {
        var count = 0;
        foreach (var row in TestData.ReadCsv("time_gmst.csv"))
        {
            var utc = TestData.Utc(row[0]);
            var jd = JulianDate.FromUtc(utc);

            Assert.Equal(TestData.D(row[1]), jd.Day);
            Assert.Equal(TestData.D(row[2]), jd.Fraction, 1e-15);
            Assert.Equal(utc, jd.ToUtc());
            count++;
        }
        Assert.True(count >= 100);
    }

    [Fact]
    public void Parts_are_normalized()
    {
        var jd = JulianDate.FromParts(2451544.5, 1.25);
        Assert.Equal(2451545.5, jd.Day);
        Assert.Equal(0.25, jd.Fraction);

        var neg = JulianDate.FromParts(2451544.5, -0.25);
        Assert.Equal(2451543.5, neg.Day);
        Assert.Equal(0.75, neg.Fraction);
    }

    [Fact]
    public void Local_kind_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => JulianDate.FromUtc(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Local)));
    }

    [Fact]
    public void Epoch_from_tle_year_and_day()
    {
        // 第 1.0 日 = 1 月 1 日 00:00:00 UTC
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), JulianDate.FromYearAndDayOfYear(2024, 1.0).ToUtc());
        // 閏年第 366.5 日 = 12 月 31 日 12:00
        Assert.Equal(new DateTime(2024, 12, 31, 12, 0, 0, DateTimeKind.Utc), JulianDate.FromYearAndDayOfYear(2024, 366.5).ToUtc());
        // 2000 年第 60 日 = 2 月 29 日
        Assert.Equal(new DateTime(2000, 2, 29, 6, 0, 0, DateTimeKind.Utc), JulianDate.FromYearAndDayOfYear(2000, 60.25).ToUtc());
    }

    [Fact]
    public void Minutes_since_epoch()
    {
        var epoch = JulianDate.FromYearAndDayOfYear(2025, 100.5);
        var t = JulianDate.FromUtc(new DateTime(2025, 4, 11, 13, 30, 0, DateTimeKind.Utc)); // 第 101 日 13:30

        Assert.Equal(1440.0 + 90.0, t.MinutesSince(epoch), 1e-9);
        Assert.Equal(-(1440.0 + 90.0), epoch.MinutesSince(t), 1e-9);
    }

    [Fact]
    public void AddSeconds_crosses_day_boundary()
    {
        var jd = JulianDate.FromUtc(new DateTime(2024, 12, 31, 23, 59, 30, DateTimeKind.Utc)).AddSeconds(45);

        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 15, DateTimeKind.Utc), jd.ToUtc());
    }

    [Fact]
    public void Property_utc_roundtrip_and_monotonic()
    {
        var min = new DateTime(1900, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var max = new DateTime(2200, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        Property.Run("UTC↔JD 往返與單調性", seed: 6101, iterations: 2000, rng =>
        {
            // 取毫秒整數倍：兩段式儒略日的日內比例解析度遠小於 1 毫秒
            var a = new DateTime(rng.NextInt64(min, max) / 10_000 * 10_000, DateTimeKind.Utc);
            var b = a.AddMilliseconds(rng.Next(1, 10_000_000));

            var ja = JulianDate.FromUtc(a);
            var jb = JulianDate.FromUtc(b);

            Assert.Equal(a, ja.ToUtc());
            Assert.True(jb.MinutesSince(ja) > 0, "時間較晚者的儒略日必須較大");
            Assert.Equal((b - a).TotalMinutes, jb.MinutesSince(ja), 1e-7);
        });
    }
}
