using System.Text;
using SkyPass.Core.Tle;
using SkyPass.Tests.Support;
using static SkyPass.Tests.Support.TleText;

namespace SkyPass.Tests.Core.Tle;

public class TleParserTests
{
    [Fact]
    public void Parses_all_fields_of_two_line_set()
    {
        var result = TleParser.Parse($"{IssLine1}\n{IssLine2}\n");

        Assert.Empty(result.Errors);
        var tle = Assert.Single(result.Sets);
        Assert.Equal(25544, tle.NoradId);
        Assert.Equal('U', tle.Classification);
        Assert.Equal("98067A", tle.InternationalDesignator);
        Assert.Equal(2008, tle.EpochYear);
        Assert.Equal(264.51782528, tle.EpochDayOfYear);
        Assert.Equal(-0.00002182, tle.MeanMotionDot);
        Assert.Equal(0.0, tle.MeanMotionDdot);
        Assert.Equal(-0.11606 * Math.Pow(10, -4), tle.BStar);
        Assert.Equal(0, tle.EphemerisType);
        Assert.Equal(292, tle.ElementSetNumber);
        Assert.Equal(51.6416, tle.InclinationDeg);
        Assert.Equal(247.4627, tle.RaanDeg);
        Assert.Equal(0.0006703, tle.Eccentricity);
        Assert.Equal(130.5360, tle.ArgOfPerigeeDeg);
        Assert.Equal(325.0288, tle.MeanAnomalyDeg);
        Assert.Equal(15.72125391, tle.MeanMotionRevPerDay);
        Assert.Equal(56353, tle.RevolutionNumber);
        Assert.Equal("25544", tle.Name);
        Assert.Equal(IssLine1, tle.Line1);
        Assert.Equal(IssLine2, tle.Line2);
    }

    [Fact]
    public void Implied_decimal_eccentricity_and_positive_bstar()
    {
        var tle = Assert.Single(TleParser.Parse($"{VanguardLine1}\r\n{VanguardLine2}").Sets);

        Assert.Equal(0.1859667, tle.Eccentricity);
        Assert.Equal(0.28098 * Math.Pow(10, -4), tle.BStar);
        Assert.Equal(0.00000023, tle.MeanMotionDot);
        Assert.Equal(2000, tle.EpochYear);
    }

    [Fact]
    public void Second_derivative_with_mantissa_sign_and_positive_exponent()
    {
        var line1 = Set(IssLine1, 44, "-12345+1");

        var tle = Assert.Single(TleParser.Parse($"{line1}\n{IssLine2}").Sets);

        Assert.Equal(-0.12345 * Math.Pow(10, 1), tle.MeanMotionDdot);
    }

    [Fact]
    public void Positive_sign_character_is_accepted()
    {
        var line1 = Set(Set(IssLine1, 33, "+.00002182"), 53, "+11606-4");

        var tle = Assert.Single(TleParser.Parse($"{line1}\n{IssLine2}").Sets);

        Assert.Equal(0.00002182, tle.MeanMotionDot);
        Assert.Equal(0.11606e-4, tle.BStar, 1e-20);
    }

    [Theory]
    [InlineData("57", 1957)]
    [InlineData("99", 1999)]
    [InlineData("00", 2000)]
    [InlineData("56", 2056)]
    [InlineData("08", 2008)]
    public void Two_digit_epoch_year_pivots_at_57(string yy, int expected)
    {
        var tle = Assert.Single(TleParser.Parse($"{Set(IssLine1, 18, yy)}\n{IssLine2}").Sets);

        Assert.Equal(expected, tle.EpochYear);
    }

    [Fact]
    public void Epoch_day_one_is_january_first_midnight()
    {
        var tle = Assert.Single(TleParser.Parse($"{Set(IssLine1, 18, "24001.00000000")}\n{IssLine2}").Sets);

        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), tle.Epoch.ToUtc());
    }

    [Fact]
    public void Checksum_counts_minus_sign_as_one()
    {
        // ISS 第 1 行有兩個負號；若負號計 0，校驗碼會是 5 而不是 7
        Assert.Equal(7, TleParser.ComputeChecksum(IssLine1));
        Assert.Empty(TleParser.Parse($"{IssLine1}\n{IssLine2}").Errors);
    }

    [Theory]
    [InlineData("0 ISS (ZARYA)", "ISS (ZARYA)")]
    [InlineData("ISS (ZARYA)", "ISS (ZARYA)")]
    [InlineData("NOAA 19   ", "NOAA 19")]
    public void Three_line_format_reads_name(string nameLine, string expected)
    {
        var tle = Assert.Single(TleParser.Parse($"{nameLine}\n{IssLine1}\n{IssLine2}").Sets);

        Assert.Equal(expected, tle.Name);
    }

    [Fact]
    public void Utf8_bytes_with_and_without_bom()
    {
        var text = $"國際太空站\r\n{IssLine1}\r\n{IssLine2}\r\n";
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
        var withoutBom = Encoding.UTF8.GetBytes(text);

        Assert.Equal("國際太空站", Assert.Single(TleParser.Parse(withBom).Sets).Name);
        Assert.Equal("國際太空站", Assert.Single(TleParser.Parse(withoutBom).Sets).Name);
    }

    [Fact]
    public void Mixed_two_and_three_line_sets_with_blank_lines()
    {
        var text = $"ISS\n{IssLine1}\n{IssLine2}\n\n{VanguardLine1}\n{VanguardLine2}\n";

        var result = TleParser.Parse(text);

        Assert.Empty(result.Errors);
        Assert.Equal(["ISS", "5"], result.Sets.Select(s => s.Name));
    }

    [Fact]
    public void Wrong_line_length_reports_line_number()
    {
        var text = $"BAD\n{IssLine1[..60]}\n{IssLine2}\nGOOD\n{VanguardLine1}\n{VanguardLine2}";

        var result = TleParser.Parse(text);

        var error = Assert.Single(result.Errors);
        Assert.Equal(2, error.LineNumber);
        Assert.Equal(TleErrorKind.LineLength, error.Kind);
        Assert.Equal("GOOD", Assert.Single(result.Sets).Name);
    }

    [Fact]
    public void Wrong_line_number_reports_line_number()
    {
        var text = $"{IssLine1}\n{Set(IssLine2, 0, "3")}";

        var error = Assert.Single(TleParser.Parse(text).Errors);

        Assert.Equal(2, error.LineNumber);
        Assert.Equal(TleErrorKind.LineNumber, error.Kind);
    }

    [Fact]
    public void Bad_checksum_reports_line_and_expected_value()
    {
        var bad = IssLine2[..68] + "0";

        var result = TleParser.Parse($"\n\n{IssLine1}\n{bad}");

        var error = Assert.Single(result.Errors);
        Assert.Equal(4, error.LineNumber);
        Assert.Equal(TleErrorKind.Checksum, error.Kind);
        Assert.Contains("7", error.Message);
        Assert.Empty(result.Sets);
    }

    [Theory]
    [InlineData(1, 7, "X", TleErrorKind.FieldFormat)]             // 分類
    [InlineData(1, 20, "ABC.51782528", TleErrorKind.FieldFormat)] // epoch 日
    [InlineData(1, 20, "000.51782528", TleErrorKind.FieldRange)]  // epoch 日 < 1
    [InlineData(1, 20, "367.51782528", TleErrorKind.FieldRange)]  // epoch 日超出該年
    [InlineData(1, 33, "-0.0002182", TleErrorKind.FieldFormat)]   // 一階導數格式
    [InlineData(1, 44, " 0000A-0", TleErrorKind.FieldFormat)]     // 二階導數尾數
    [InlineData(1, 53, "-11606*4", TleErrorKind.FieldFormat)]     // BSTAR 指數符號
    [InlineData(1, 17, "X", TleErrorKind.FieldFormat)]            // 應為空白
    [InlineData(2, 8, "190.0000", TleErrorKind.FieldRange)]       // 傾角
    [InlineData(2, 8, " 5x.6416", TleErrorKind.FieldFormat)]
    [InlineData(2, 17, "360.0000", TleErrorKind.FieldRange)]      // 升交點赤經
    [InlineData(2, 26, "00A6703", TleErrorKind.FieldFormat)]      // 偏心率
    [InlineData(2, 34, "400.0000", TleErrorKind.FieldRange)]      // 近地點引數
    [InlineData(2, 43, "360.5000", TleErrorKind.FieldRange)]      // 平近點角
    [InlineData(2, 52, " 0.00000000", TleErrorKind.FieldRange)]   // 平均運動
    [InlineData(2, 2, "25545", TleErrorKind.SatelliteNumberMismatch)]
    public void Field_errors_are_reported(int lineNo, int index, string text, TleErrorKind kind)
    {
        var l1 = lineNo == 1 ? Set(IssLine1, index, text) : IssLine1;
        var l2 = lineNo == 2 ? Set(IssLine2, index, text) : IssLine2;

        var result = TleParser.Parse($"ISS\n{l1}\n{l2}");

        Assert.Empty(result.Sets);
        Assert.Contains(result.Errors, e => e.Kind == kind && e.LineNumber == lineNo + 1);
    }

    [Fact]
    public void Leap_year_allows_day_366()
    {
        var tle = Assert.Single(TleParser.Parse($"{Set(IssLine1, 18, "24366.50000000")}\n{IssLine2}").Sets);
        Assert.Equal(new DateTime(2024, 12, 31, 12, 0, 0, DateTimeKind.Utc), tle.Epoch.ToUtc());

        var nonLeap = TleParser.Parse($"{Set(IssLine1, 18, "23366.50000000")}\n{IssLine2}");
        Assert.Contains(nonLeap.Errors, e => e.Kind == TleErrorKind.FieldRange);
    }

    [Fact]
    public void Missing_line_two_and_orphans_are_reported_and_others_imported()
    {
        var text = string.Join('\n',
            "LONELY NAME",          // 1: 名稱行後沒有軌道資料
            "ISS",                  // 2
            IssLine1,               // 3: 缺第 2 行
            "VANGUARD",             // 4
            VanguardLine1,          // 5
            VanguardLine2,          // 6
            IssLine2);              // 7: 孤立的第 2 行

        var result = TleParser.Parse(text);

        Assert.Equal("VANGUARD", Assert.Single(result.Sets).Name);
        Assert.Contains(result.Errors, e => e.LineNumber == 1 && e.Kind == TleErrorKind.MissingElements);
        Assert.Contains(result.Errors, e => e.LineNumber == 3 && e.Kind == TleErrorKind.MissingLine);
        Assert.Contains(result.Errors, e => e.LineNumber == 7 && e.Kind == TleErrorKind.MissingLine);
        Assert.All(result.Errors, e => Assert.False(string.IsNullOrWhiteSpace(e.Message)));
    }

    [Theory]
    [InlineData("A0000", 100000)]
    [InlineData("E8493", 148493)]
    [InlineData("H9999", 179999)]
    [InlineData("J0000", 180000)]   // 跳過 I
    [InlineData("P0001", 230001)]   // 跳過 O
    [InlineData("Z9999", 339999)]
    public void Alpha5_satellite_numbers(string field, int expected)
    {
        var l1 = Set(IssLine1, 2, field);
        var l2 = Set(IssLine2, 2, field);

        var result = TleParser.Parse($"ALPHA5\n{l1}\n{l2}");

        Assert.Empty(result.Errors);
        Assert.Equal(expected, Assert.Single(result.Sets).NoradId);
    }

    [Theory]
    [InlineData("I0001")]   // I 與 O 不使用，避免與 1、0 混淆
    [InlineData("O0001")]
    [InlineData("a0001")]   // 必須大寫
    [InlineData("AB001")]
    public void Invalid_alpha5_numbers_are_field_errors(string field)
    {
        var result = TleParser.Parse($"X\n{Set(IssLine1, 2, field)}\n{Set(IssLine2, 2, field)}");

        Assert.Empty(result.Sets);
        Assert.Contains(result.Errors, e => e.Kind == TleErrorKind.FieldFormat && e.LineNumber == 2);
    }

    [Theory]
    [InlineData(0, "00000")]
    [InlineData(99999, "99999")]
    [InlineData(100000, "A0000")]
    [InlineData(148493, "E8493")]
    [InlineData(180000, "J0000")]
    [InlineData(230001, "P0001")]
    [InlineData(339999, "Z9999")]
    public void Satellite_number_encoding(int norad, string expected)
    {
        Assert.Equal(expected, TleFormatter.FormatSatelliteNumber(norad));
        Assert.Equal(norad, TleParser.ParseSatelliteNumber(expected));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(340000)]
    public void Satellite_number_out_of_alpha5_range_fails_fast(int norad)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TleFormatter.FormatSatelliteNumber(norad));
    }

    [Fact]
    public void Trailing_whitespace_is_tolerated()
    {
        var result = TleParser.Parse($"{IssLine1}   \n{IssLine2}\t\n");

        Assert.Empty(result.Errors);
        Assert.Single(result.Sets);
    }

    [Fact]
    public void Derived_period_and_deep_space_flag()
    {
        var iss = Assert.Single(TleParser.Parse($"{IssLine1}\n{IssLine2}").Sets);
        Assert.Equal(1440 / 15.72125391, iss.PeriodMinutes, 12);
        Assert.False(iss.IsDeepSpace);

        // 平均運動 6.4 圈/日 → 週期 225 分鐘，恰為深空門檻
        var geoLike = Assert.Single(TleParser.Parse($"{IssLine1}\n{Set(IssLine2, 52, " 6.40000000")}").Sets);
        Assert.Equal(225.0, geoLike.PeriodMinutes, 12);
        Assert.True(geoLike.IsDeepSpace);

        var justBelow = Assert.Single(TleParser.Parse($"{IssLine1}\n{Set(IssLine2, 52, " 6.40000100")}").Sets);
        Assert.False(justBelow.IsDeepSpace);
    }
}
