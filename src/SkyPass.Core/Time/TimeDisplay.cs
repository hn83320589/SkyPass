using System.Globalization;

namespace SkyPass.Core.Time;

/// <summary>把 UTC 時刻轉成指定 UTC 偏移的顯示文字（四捨五入到秒）。Core 內部一律使用 UTC。</summary>
public static class TimeDisplay
{
    public const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss";
    public const string TimeFormat = "HH:mm:ss";

    public static DateTime ToLocal(JulianDate time, TimeSpan utcOffset)
    {
        var utc = time.ToUtc();
        var rounded = new DateTime((utc.Ticks + TimeSpan.TicksPerSecond / 2) / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Unspecified);
        return rounded + utcOffset;
    }

    public static string Format(JulianDate time, TimeSpan utcOffset, string format = DateTimeFormat) =>
        ToLocal(time, utcOffset).ToString(format, CultureInfo.InvariantCulture);

    public static string OffsetLabel(TimeSpan utcOffset)
    {
        var sign = utcOffset < TimeSpan.Zero ? "-" : "+";
        var abs = utcOffset.Duration();
        return $"UTC{sign}{abs.Hours:00}:{abs.Minutes:00}";
    }
}
