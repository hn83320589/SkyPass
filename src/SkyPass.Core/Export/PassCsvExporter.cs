using System.Globalization;
using System.Text;
using SkyPass.Core.Passes;
using SkyPass.Core.Time;

namespace SkyPass.Core.Export;

/// <summary>過境表 CSV（RFC 4180，CRLF 換行）；寫檔時為 UTF-8 with BOM，讓試算表軟體正確辨識中文。</summary>
public static class PassCsvExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string ToCsv(IEnumerable<SatellitePass> passes, TimeSpan utcOffset)
    {
        var tz = $"({TimeDisplay.OffsetLabel(utcOffset)})";
        var sb = new StringBuilder();
        AppendRow(sb,
        [
            "衛星", "NORAD", $"AOS {tz}", "AOS 方位(°)", $"最大仰角時刻 {tz}", "最大仰角(°)", "最大仰角方位(°)",
            $"LOS {tz}", "LOS 方位(°)", "持續(秒)", "起點前已開始", "終點後才結束",
            $"可目視開始 {tz}", $"可目視結束 {tz}", "可目視時段",
        ]);

        foreach (var p in passes)
        {
            var visible = p.VisibleIntervals;
            AppendRow(sb,
            [
                p.SatelliteName,
                p.NoradId.ToString(Inv),
                TimeDisplay.Format(p.Aos, utcOffset),
                p.AosAzimuthDeg.ToString("F1", Inv),
                TimeDisplay.Format(p.MaxElevationTime, utcOffset),
                p.MaxElevationDeg.ToString("F2", Inv),
                p.MaxElevationAzimuthDeg.ToString("F1", Inv),
                TimeDisplay.Format(p.Los, utcOffset),
                p.LosAzimuthDeg.ToString("F1", Inv),
                Math.Round(p.DurationSeconds).ToString("F0", Inv),
                p.StartedBeforeWindow ? "是" : "否",
                p.EndsAfterWindow ? "是" : "否",
                visible.Count > 0 ? TimeDisplay.Format(visible[0].Start, utcOffset) : "",
                visible.Count > 0 ? TimeDisplay.Format(visible[^1].End, utcOffset) : "",
                string.Join(" / ", visible.Select(iv =>
                    $"{TimeDisplay.Format(iv.Start, utcOffset, TimeDisplay.TimeFormat)}–{TimeDisplay.Format(iv.End, utcOffset, TimeDisplay.TimeFormat)}")),
            ]);
        }

        return sb.ToString();
    }

    public static void Write(Stream stream, IEnumerable<SatellitePass> passes, TimeSpan utcOffset)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), leaveOpen: true);
        writer.Write(ToCsv(passes, utcOffset));
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(Escape(fields[i]));
        }

        sb.Append("\r\n");
    }

    private static string Escape(string field) =>
        field.AsSpan().IndexOfAny(",\"\r\n") >= 0 ? $"\"{field.Replace("\"", "\"\"")}\"" : field;
}
