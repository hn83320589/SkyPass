namespace SkyPass.Core.Time;

/// <summary>
/// 兩段式儒略日（UTC）：<see cref="Day"/> 為當日 0h 的儒略日（整數 + 0.5），<see cref="Fraction"/> 為日內比例 [0, 1)。
/// 單一 double 的儒略日解析度約 40 微秒，會讓 GMST 誤差超過 1e-9 rad，因此拆成兩段保存。
/// </summary>
public readonly record struct JulianDate
{
    private const double TicksPerDay = TimeSpan.TicksPerDay;

    private JulianDate(double day, double fraction)
    {
        Day = day;
        Fraction = fraction;
    }

    public double Day { get; }

    public double Fraction { get; }

    /// <summary>合併為單一 double（會損失精度，僅供顯示或低精度用途）。</summary>
    public double Value => Day + Fraction;

    public static JulianDate FromParts(double day, double fraction)
    {
        var whole = Math.Floor(day - 0.5) + 0.5;
        fraction += day - whole;
        var carry = Math.Floor(fraction);
        return new JulianDate(whole + carry, fraction - carry);
    }

    public static JulianDate FromUtc(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local)
        {
            throw new ArgumentException("必須是 UTC 時間（DateTimeKind.Utc 或 Unspecified），不可為 Local。", nameof(utc));
        }

        var dayStart = FromCalendarDay(utc.Year, utc.Month, utc.Day);
        return new JulianDate(dayStart, utc.TimeOfDay.Ticks / TicksPerDay);
    }

    /// <summary>TLE epoch：年份（四位數）與「該年第幾日加小數」，1.0 代表 1 月 1 日 00:00:00 UTC。</summary>
    public static JulianDate FromYearAndDayOfYear(int year, double dayOfYear)
    {
        var wholeDays = Math.Floor(dayOfYear);
        return FromParts(FromCalendarDay(year, 1, 1) + (wholeDays - 1), dayOfYear - wholeDays);
    }

    public DateTime ToUtc()
    {
        var (y, m, d) = ToCalendarDay(Day);
        var ticks = (long)Math.Round(Fraction * TicksPerDay);
        return new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Utc).AddTicks(ticks);
    }

    /// <summary>自 <paramref name="epoch"/> 起算的分鐘數（SGP4 的 tsince）。</summary>
    public double MinutesSince(in JulianDate epoch) => ((Day - epoch.Day) + (Fraction - epoch.Fraction)) * 1440.0;

    public double SecondsSince(in JulianDate other) => ((Day - other.Day) + (Fraction - other.Fraction)) * 86400.0;

    public JulianDate AddSeconds(double seconds) => FromParts(Day, Fraction + seconds / 86400.0);

    /// <summary>格里曆日期 → 當日 0h 的儒略日（Fliegel &amp; Van Flandern 整數演算法）。</summary>
    private static double FromCalendarDay(long y, long m, long d)
    {
        var a = (m - 14) / 12;
        var jdn = (1461 * (y + 4800 + a)) / 4
                  + (367 * (m - 2 - 12 * a)) / 12
                  - (3 * ((y + 4900 + a) / 100)) / 4
                  + d - 32075;
        return jdn - 0.5;
    }

    private static (int Year, int Month, int Day) ToCalendarDay(double dayStart)
    {
        var l = (long)(dayStart + 0.5) + 68569;
        var n = 4 * l / 146097;
        l -= (146097 * n + 3) / 4;
        var i = 4000 * (l + 1) / 1461001;
        l = l - 1461 * i / 4 + 31;
        var j = 80 * l / 2447;
        var d = l - 2447 * j / 80;
        l = j / 11;
        var m = j + 2 - 12 * l;
        var y = 100 * (n - 49) + i + l;
        return ((int)y, (int)m, (int)d);
    }
}
