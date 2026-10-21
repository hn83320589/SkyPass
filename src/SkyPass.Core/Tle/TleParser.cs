using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SkyPass.Core.Tle;

/// <summary>
/// TLE 解析器：支援兩行／三行格式、CRLF 或 LF、UTF-8 含或不含 BOM。
/// 逐組驗證，錯誤組別記錄行號與原因並跳過，其餘照常匯入。
/// </summary>
public static partial class TleParser
{
    public const int LineLength = 69;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static TleParseResult Parse(ReadOnlySpan<byte> utf8)
    {
        var bom = Encoding.UTF8.Preamble;
        if (utf8.StartsWith(bom))
        {
            utf8 = utf8[bom.Length..];
        }

        return Parse(Encoding.UTF8.GetString(utf8));
    }

    public static TleParseResult Parse(string text)
    {
        var lines = text.TrimStart('﻿').Split('\n').Select(l => l.TrimEnd()).ToArray();
        var sets = new List<TleSet>();
        var errors = new List<TleError>();

        string? pendingName = null;
        var pendingNameLine = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineNo = i + 1;
            if (line.Length == 0)
            {
                continue;
            }

            if (!IsElementLine(line))
            {
                if (pendingName is not null)
                {
                    errors.Add(new TleError(pendingNameLine, TleErrorKind.MissingElements, $"名稱行「{pendingName}」之後沒有軌道資料。"));
                }

                pendingName = CleanName(line);
                pendingNameLine = lineNo;
                continue;
            }

            if (i + 1 < lines.Length && IsElementLine(lines[i + 1]))
            {
                var set = ParseSet(pendingName, line, lineNo, lines[i + 1], lineNo + 1, errors);
                if (set is not null)
                {
                    sets.Add(set);
                }

                i++;
            }
            else
            {
                var expected = line[0] == '2' ? "第 2 行之前缺少第 1 行。" : "第 1 行之後缺少第 2 行。";
                errors.Add(new TleError(lineNo, TleErrorKind.MissingLine, expected));
            }

            pendingName = null;
        }

        if (pendingName is not null)
        {
            errors.Add(new TleError(pendingNameLine, TleErrorKind.MissingElements, $"名稱行「{pendingName}」之後沒有軌道資料。"));
        }

        return new TleParseResult(sets, errors);
    }

    /// <summary>校驗碼：前 68 字元中數字取其值、負號計 1、其餘為 0，總和取個位數。</summary>
    public static int ComputeChecksum(ReadOnlySpan<char> line)
    {
        var sum = 0;
        foreach (var c in line[..Math.Min(68, line.Length)])
        {
            if (char.IsAsciiDigit(c))
            {
                sum += c - '0';
            }
            else if (c == '-')
            {
                sum += 1;
            }
        }

        return sum % 10;
    }

    /// <summary>Alpha-5 首字元（不含 I、O），依序代表 10–33。</summary>
    internal const string Alpha5Letters = "ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>
    /// 解析 5 字元的衛星編號欄：數字（可有前導空白）或 Alpha-5（首字元 A–Z 不含 I、O 代表 10–33，後接 4 位數字），
    /// 例如 "E8493" = 14 × 10000 + 8493 = 148493，範圍到 "Z9999" = 339999。格式不符時擲出 <see cref="FormatException"/>。
    /// </summary>
    public static int ParseSatelliteNumber(string field) =>
        TryParseSatelliteNumber(field, out var value) ? value : throw new FormatException($"衛星編號格式錯誤：「{field}」。");

    internal static bool TryParseSatelliteNumber(ReadOnlySpan<char> field, out int value)
    {
        value = 0;
        if (field.Length != 5)
        {
            return false;
        }

        var letter = Alpha5Letters.IndexOf(field[0]);
        if (letter >= 0)
        {
            if (!int.TryParse(field[1..], NumberStyles.None, Inv, out var rest))
            {
                return false;
            }

            value = (letter + 10) * 10000 + rest;
            return true;
        }

        var trimmed = field.TrimStart(' ');
        return trimmed.Length > 0 && int.TryParse(trimmed, NumberStyles.None, Inv, out value);
    }

    /// <summary>兩位數年份：57–99 → 1957–1999，00–56 → 2000–2056。</summary>
    public static int ExpandEpochYear(int twoDigitYear) => twoDigitYear >= 57 ? 1900 + twoDigitYear : 2000 + twoDigitYear;

    /// <summary>
    /// 看起來像軌道資料行：「數字 + 空白」開頭，且衛星編號可解析（含 Alpha-5），或長度已接近 69 字元。
    /// 後者讓編號欄寫錯的資料行仍交給欄位驗證，回報精確的行號與原因，而不是被當成名稱行。
    /// </summary>
    private static bool IsElementLine(string line) =>
        line.Length >= 7 && char.IsAsciiDigit(line[0]) && line[1] == ' ' &&
        (line.Length >= 60 || TryParseSatelliteNumber(line.AsSpan(2, 5), out _));

    private static string CleanName(string line)
    {
        var name = line.Trim();
        return name.StartsWith("0 ", StringComparison.Ordinal) ? name[2..].Trim() : name;
    }

    private static TleSet? ParseSet(string? name, string line1, int lineNo1, string line2, int lineNo2, List<TleError> errors)
    {
        var structureOk = CheckStructure(line1, '1', lineNo1, errors) & CheckStructure(line2, '2', lineNo2, errors);
        if (!structureOk)
        {
            return null;
        }

        var errorCount = errors.Count;
        var r1 = new FieldReader(line1, lineNo1, errors);
        r1.Blank(1, 8, 17, 32, 43, 52, 61, 63);
        var norad1 = r1.SatelliteNumber(2);
        var classification = r1.Char(7, "UCS", "分類");
        var designator = line1.Substring(9, 8).Trim();
        var yy = r1.Integer(18, 2, TwoDigitsRegex(), "epoch 年份");
        var epochDay = r1.Decimal(20, 12, EpochDayRegex(), "epoch 日");
        var ndot = r1.Decimal(33, 10, FirstDerivativeRegex(), "平均運動一階導數");
        var nddot = r1.Exponential(44, "平均運動二階導數");
        var bstar = r1.Exponential(53, "BSTAR");
        var ephemerisType = r1.Integer(62, 1, OptionalDigitRegex(), "星曆類型");
        var elementNumber = r1.Integer(64, 4, OptionalDigitsRegex(), "元素組編號");

        var r2 = new FieldReader(line2, lineNo2, errors);
        r2.Blank(1, 7, 16, 25, 33, 42, 51);
        var norad2 = r2.SatelliteNumber(2);
        var inclination = r2.Decimal(8, 8, AngleRegex(), "傾角");
        var raan = r2.Decimal(17, 8, AngleRegex(), "升交點赤經");
        var eccentricity = r2.ImpliedDecimal(26, 7, "偏心率");
        var argPerigee = r2.Decimal(34, 8, AngleRegex(), "近地點引數");
        var meanAnomaly = r2.Decimal(43, 8, AngleRegex(), "平近點角");
        var meanMotion = r2.Decimal(52, 11, MeanMotionRegex(), "平均運動");
        var revolution = r2.Integer(63, 5, OptionalDigitsRegex(), "圈號");

        if (errors.Count > errorCount)
        {
            return null;
        }

        var year = ExpandEpochYear(yy);
        var daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;
        r1.Range(epochDay, 1.0, daysInYear + 1.0, maxInclusive: false, 20, "epoch 日");
        r2.Range(inclination, 0.0, 180.0, maxInclusive: true, 8, "傾角");
        r2.Range(raan, 0.0, 360.0, maxInclusive: false, 17, "升交點赤經");
        r2.Range(argPerigee, 0.0, 360.0, maxInclusive: false, 34, "近地點引數");
        r2.Range(meanAnomaly, 0.0, 360.0, maxInclusive: false, 43, "平近點角");
        if (meanMotion <= 0.0)
        {
            errors.Add(new TleError(lineNo2, TleErrorKind.FieldRange, $"平均運動必須大於 0（第 53–63 欄，實際 {meanMotion.ToString(Inv)}）。"));
        }

        if (norad1 != norad2)
        {
            errors.Add(new TleError(lineNo2, TleErrorKind.SatelliteNumberMismatch, $"兩行的衛星編號不一致（第 1 行 {norad1}，第 2 行 {norad2}）。"));
        }

        if (errors.Count > errorCount)
        {
            return null;
        }

        return new TleSet
        {
            Name = string.IsNullOrEmpty(name) ? norad1.ToString(Inv) : name,
            NoradId = norad1,
            Classification = classification,
            InternationalDesignator = designator,
            EpochYear = year,
            EpochDayOfYear = epochDay,
            MeanMotionDot = ndot,
            MeanMotionDdot = nddot,
            BStar = bstar,
            EphemerisType = ephemerisType,
            ElementSetNumber = elementNumber,
            InclinationDeg = inclination,
            RaanDeg = raan,
            Eccentricity = eccentricity,
            ArgOfPerigeeDeg = argPerigee,
            MeanAnomalyDeg = meanAnomaly,
            MeanMotionRevPerDay = meanMotion,
            RevolutionNumber = revolution,
            Line1 = line1,
            Line2 = line2,
        };
    }

    private static bool CheckStructure(string line, char expectedNumber, int lineNo, List<TleError> errors)
    {
        if (line.Length != LineLength)
        {
            errors.Add(new TleError(lineNo, TleErrorKind.LineLength, $"行長度應為 {LineLength} 字元，實際為 {line.Length}。"));
            return false;
        }

        if (line[0] != expectedNumber)
        {
            errors.Add(new TleError(lineNo, TleErrorKind.LineNumber, $"行號應為 {expectedNumber}，實際為 '{line[0]}'。"));
            return false;
        }

        if (!char.IsAsciiDigit(line[68]))
        {
            errors.Add(new TleError(lineNo, TleErrorKind.Checksum, $"第 69 欄的校驗碼必須是數字，實際為 '{line[68]}'。"));
            return false;
        }

        var expected = ComputeChecksum(line);
        if (line[68] - '0' != expected)
        {
            errors.Add(new TleError(lineNo, TleErrorKind.Checksum, $"校驗碼錯誤：應為 {expected}，實際為 {line[68]}。"));
            return false;
        }

        return true;
    }

    /// <summary>逐欄讀取並記錄錯誤；欄位位置以 0 起算，訊息中轉為 1 起算的欄號。</summary>
    private readonly struct FieldReader(string line, int lineNo, List<TleError> errors)
    {
        public void Blank(params int[] indices)
        {
            foreach (var i in indices)
            {
                if (line[i] != ' ')
                {
                    errors.Add(new TleError(lineNo, TleErrorKind.FieldFormat, $"第 {i + 1} 欄應為空白，實際為 '{line[i]}'。"));
                }
            }
        }

        public int SatelliteNumber(int start)
        {
            if (!TryParseSatelliteNumber(line.AsSpan(start, 5), out var value))
            {
                FormatError(start, 5, "衛星編號");
            }

            return value;
        }

        public char Char(int index, string allowed, string field)
        {
            if (!allowed.Contains(line[index]))
            {
                FormatError(index, 1, field);
            }

            return line[index];
        }

        public int Integer(int start, int length, Regex pattern, string field)
        {
            var text = line.Substring(start, length);
            if (!pattern.IsMatch(text))
            {
                FormatError(start, length, field);
                return 0;
            }

            var trimmed = text.Trim();
            return trimmed.Length == 0 ? 0 : int.Parse(trimmed, NumberStyles.None, Inv);
        }

        public double Decimal(int start, int length, Regex pattern, string field)
        {
            var text = line.Substring(start, length);
            if (!pattern.IsMatch(text))
            {
                FormatError(start, length, field);
                return 0;
            }

            return double.Parse(text.Trim(), NumberStyles.Float, Inv);
        }

        /// <summary>隱含小數點在最前面的數字欄（偏心率）。</summary>
        public double ImpliedDecimal(int start, int length, string field)
        {
            var text = line.Substring(start, length);
            if (!AllDigitsRegex().IsMatch(text))
            {
                FormatError(start, length, field);
                return 0;
            }

            return double.Parse("0." + text, NumberStyles.Float, Inv);
        }

        /// <summary>「符號、5 位尾數（隱含小數點在最前面）、符號、1 位指數」，例如 -11606-4 = −0.11606 × 10⁻⁴。</summary>
        public double Exponential(int start, string field)
        {
            var text = line.Substring(start, 8);
            if (!ExponentialRegex().IsMatch(text))
            {
                FormatError(start, 8, field);
                return 0;
            }

            var sign = text[0] == '-' ? "-" : "";
            var mantissa = double.Parse(sign + "0." + text.Substring(1, 5), NumberStyles.Float, Inv);
            var exponent = int.Parse(text.AsSpan(6, 2), NumberStyles.AllowLeadingSign, Inv);
            return mantissa * Math.Pow(10.0, exponent);
        }

        public void Range(double value, double min, double max, bool maxInclusive, int start, string field)
        {
            var ok = value >= min && (maxInclusive ? value <= max : value < max);
            if (!ok)
            {
                var upper = maxInclusive ? $"≤ {max.ToString(Inv)}" : $"< {max.ToString(Inv)}";
                errors.Add(new TleError(lineNo, TleErrorKind.FieldRange,
                    $"{field}超出範圍（第 {start + 1} 欄起，需 ≥ {min.ToString(Inv)} 且 {upper}，實際 {value.ToString(Inv)}）。"));
            }
        }

        private void FormatError(int start, int length, string field) =>
            errors.Add(new TleError(lineNo, TleErrorKind.FieldFormat,
                $"{field}格式錯誤（第 {start + 1}–{start + length} 欄：「{line.Substring(start, length)}」）。"));
    }


    [GeneratedRegex(@"^\d\d$")]
    private static partial Regex TwoDigitsRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex AllDigitsRegex();

    [GeneratedRegex(@"^[ \d]$")]
    private static partial Regex OptionalDigitRegex();

    [GeneratedRegex(@"^ *\d*$")]
    private static partial Regex OptionalDigitsRegex();

    [GeneratedRegex(@"^[ \d]{2}\d\.\d{8}$")]
    private static partial Regex EpochDayRegex();

    [GeneratedRegex(@"^[ +-]\.\d{8}$")]
    private static partial Regex FirstDerivativeRegex();

    [GeneratedRegex(@"^[ +-]\d{5}[+-]\d$")]
    private static partial Regex ExponentialRegex();

    [GeneratedRegex(@"^[ \d]{2}\d\.\d{4}$")]
    private static partial Regex AngleRegex();

    [GeneratedRegex(@"^[ \d]\d\.\d{8}$")]
    private static partial Regex MeanMotionRegex();
}
