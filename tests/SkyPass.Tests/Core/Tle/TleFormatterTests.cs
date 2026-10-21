using SkyPass.Core.Tle;
using SkyPass.Tests.Support;
using static SkyPass.Tests.Support.TleText;

namespace SkyPass.Tests.Core.Tle;

public class TleFormatterTests
{
    [Theory]
    [InlineData(IssLine1, IssLine2)]
    [InlineData(VanguardLine1, VanguardLine2)]
    public void Canonical_lines_roundtrip_exactly(string line1, string line2)
    {
        var tle = Assert.Single(TleParser.Parse($"{line1}\n{line2}").Sets);

        var (f1, f2) = TleFormatter.FormatLines(tle);

        Assert.Equal(line1, f1);
        Assert.Equal(line2, f2);
    }

    [Theory]
    [InlineData(0.0, " 00000-0")]
    [InlineData(-0.11606e-4, "-11606-4")]
    [InlineData(0.28098e-4, " 28098-4")]
    [InlineData(1.2345, " 12345+1")]
    [InlineData(0.1, " 10000+0")]
    [InlineData(0.99999e-9, " 99999-9")]
    public void Exponential_field_format(double value, string expected)
    {
        Assert.Equal(expected, TleFormatter.FormatExponential(value));
    }

    [Fact]
    public void Exponential_out_of_range_fails_fast()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TleFormatter.FormatExponential(1e-12));
        Assert.Throws<ArgumentOutOfRangeException>(() => TleFormatter.FormatExponential(1e10));
    }

    [Fact]
    public void Property_parse_format_parse_preserves_fields()
    {
        Property.Run("TLE 往返（格式化→解析→格式化）", seed: 7102, iterations: 3000, rng =>
        {
            var original = RandomTle(rng);

            var (l1, l2) = TleFormatter.FormatLines(original);
            var parsed = TleParser.Parse($"{original.Name}\n{l1}\n{l2}");

            Assert.Empty(parsed.Errors);
            var tle = Assert.Single(parsed.Sets);
            Assert.Equal(original with { Line1 = l1, Line2 = l2 }, tle);
            Assert.Equal((l1, l2), TleFormatter.FormatLines(tle));
        });
    }

    /// <summary>在 TLE 欄位可表達的離散格點上產生隨機元素，確保往返無損。</summary>
    internal static TleSet RandomTle(Random rng)
    {
        var year = 1957 + rng.Next(0, 100);
        var daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;
        return new TleSet
        {
            Name = $"SAT-{rng.Next(1, 99999)}",
            NoradId = rng.Next(4) == 0 ? rng.Next(100000, 340000) : rng.Next(1, 100000), // 含 Alpha-5 範圍
            Classification = "UCS"[rng.Next(3)],
            InternationalDesignator = $"{year % 100:00}{rng.Next(1, 999):000}{(char)('A' + rng.Next(26))}",
            EpochYear = year,
            EpochDayOfYear = Grid(rng, 1, daysInYear + 1 - 1e-8, 1e-8),
            MeanMotionDot = Grid(rng, -0.99999999, 0.99999999, 1e-8),
            MeanMotionDdot = Exp(rng),
            BStar = Exp(rng),
            EphemerisType = rng.Next(0, 10),
            ElementSetNumber = rng.Next(0, 10000),
            InclinationDeg = Grid(rng, 0, 180, 1e-4),
            RaanDeg = Grid(rng, 0, 359.9999, 1e-4),
            Eccentricity = Grid(rng, 0, 0.9999999, 1e-7),
            ArgOfPerigeeDeg = Grid(rng, 0, 359.9999, 1e-4),
            MeanAnomalyDeg = Grid(rng, 0, 359.9999, 1e-4),
            MeanMotionRevPerDay = Grid(rng, 0.00000001, 99.99999999, 1e-8),
            RevolutionNumber = rng.Next(0, 100000),
        };
    }

    private static double Grid(Random rng, double min, double max, double step)
    {
        var steps = (long)Math.Round((max - min) / step);
        var k = rng.NextInt64(0, steps + 1);
        var digits = (int)Math.Round(-Math.Log10(step));
        return Math.Round(min + k * step, digits);
    }

    private static double Exp(Random rng)
    {
        if (rng.Next(10) == 0)
        {
            return 0.0;
        }

        var mantissa = rng.Next(10000, 100000);
        var exponent = rng.Next(-9, 10);
        var sign = rng.Next(2) == 0 ? "" : "-";
        return double.Parse($"{sign}0.{mantissa}", System.Globalization.CultureInfo.InvariantCulture) * Math.Pow(10.0, exponent);
    }
}
