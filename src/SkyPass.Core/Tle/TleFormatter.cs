using System.Globalization;

namespace SkyPass.Core.Tle;

/// <summary>將 <see cref="TleSet"/> 的欄位格式化為標準 TLE 兩行（含校驗碼）。</summary>
public static class TleFormatter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static (string Line1, string Line2) FormatLines(TleSet tle)
    {
        var line1 = string.Concat(
            "1 ",
            FormatSatelliteNumber(tle.NoradId),
            tle.Classification.ToString(),
            " ",
            tle.InternationalDesignator.PadRight(8)[..8],
            " ",
            (tle.EpochYear % 100).ToString("D2", Inv),
            tle.EpochDayOfYear.ToString("000.00000000", Inv),
            " ",
            FormatFirstDerivative(tle.MeanMotionDot),
            " ",
            FormatExponential(tle.MeanMotionDdot),
            " ",
            FormatExponential(tle.BStar),
            " ",
            tle.EphemerisType.ToString(Inv),
            " ",
            tle.ElementSetNumber.ToString(Inv).PadLeft(4));

        var line2 = string.Concat(
            "2 ",
            FormatSatelliteNumber(tle.NoradId),
            " ",
            FormatAngle(tle.InclinationDeg),
            " ",
            FormatAngle(tle.RaanDeg),
            " ",
            ((long)Math.Round(tle.Eccentricity * 1e7)).ToString("D7", Inv),
            " ",
            FormatAngle(tle.ArgOfPerigeeDeg),
            " ",
            FormatAngle(tle.MeanAnomalyDeg),
            " ",
            tle.MeanMotionRevPerDay.ToString("0.00000000", Inv).PadLeft(11),
            tle.RevolutionNumber.ToString(Inv).PadLeft(5));

        return (WithChecksum(line1), WithChecksum(line2));
    }

    /// <summary>衛星編號欄：0–99999 為 5 位數字；100000–339999 為 Alpha-5（例如 148493 → "E8493"）。</summary>
    public static string FormatSatelliteNumber(int noradId)
    {
        if (noradId is < 0 or > 339999)
        {
            throw new ArgumentOutOfRangeException(nameof(noradId), noradId, "衛星編號需介於 0 與 339999（Alpha-5 上限 Z9999）。");
        }

        return noradId < 100000
            ? noradId.ToString("D5", Inv)
            : TleParser.Alpha5Letters[noradId / 10000 - 10] + (noradId % 10000).ToString("D4", Inv);
    }

    /// <summary>格式化為「符號、5 位尾數、符號、1 位指數」，例如 −0.11606e-4 → "-11606-4"；零為 " 00000-0"。</summary>
    public static string FormatExponential(double value)
    {
        if (value == 0.0)
        {
            return " 00000-0";
        }

        var abs = Math.Abs(value);
        var exponent = (int)Math.Floor(Math.Log10(abs)) + 1;
        var mantissa = (long)Math.Round(abs / Math.Pow(10.0, exponent) * 1e5);
        if (mantissa >= 100000)
        {
            exponent++;
            mantissa = (long)Math.Round(abs / Math.Pow(10.0, exponent) * 1e5);
        }
        else if (mantissa < 10000)
        {
            exponent--;
            mantissa = (long)Math.Round(abs / Math.Pow(10.0, exponent) * 1e5);
        }

        if (exponent is < -9 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "數值超出 TLE 指數欄位可表達的範圍（10⁻¹⁰ 至 10⁹）。");
        }

        var sign = value < 0 ? '-' : ' ';
        var expSign = exponent < 0 ? '-' : '+';
        return $"{sign}{mantissa.ToString("D5", Inv)}{expSign}{Math.Abs(exponent).ToString(Inv)}";
    }

    private static string FormatFirstDerivative(double value)
    {
        var text = Math.Abs(value).ToString("0.00000000", Inv);
        if (text[0] != '0')
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "平均運動一階導數的絕對值必須小於 1。");
        }

        return (value < 0 ? "-" : " ") + text[1..];
    }

    private static string FormatAngle(double degrees) => degrees.ToString("0.0000", Inv).PadLeft(8);

    private static string WithChecksum(string first68) => first68 + TleParser.ComputeChecksum(first68).ToString(Inv);
}
