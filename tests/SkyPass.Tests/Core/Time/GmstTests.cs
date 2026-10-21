using SkyPass.Core.Time;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Time;

public class GmstTests
{
    [Fact]
    public void J2000_noon_is_280_point_46_degrees()
    {
        var gmst = Gmst.ComputeRad(JulianDate.FromParts(2451544.5, 0.5));

        Assert.Equal(280.46061837 * Math.PI / 180, gmst, 1e-9);
    }

    [Fact]
    public void Matches_high_precision_reference_within_1e9_rad()
    {
        var count = 0;
        foreach (var row in TestData.ReadCsv("time_gmst.csv"))
        {
            var jd = JulianDate.FromParts(TestData.D(row[1]), TestData.D(row[2]));
            var expected = TestData.D(row[3]);

            var actual = Gmst.ComputeRad(jd);

            Assert.True(Math.Abs(AngleDiff(actual, expected)) <= 1e-9, $"{row[0]}: expected {expected}, actual {actual}");
            Assert.InRange(actual, 0, 2 * Math.PI);
            count++;
        }
        Assert.True(count >= 100);
    }

    private static double AngleDiff(double a, double b)
    {
        var d = a - b;
        if (d > Math.PI) d -= 2 * Math.PI;
        if (d < -Math.PI) d += 2 * Math.PI;
        return d;
    }
}
