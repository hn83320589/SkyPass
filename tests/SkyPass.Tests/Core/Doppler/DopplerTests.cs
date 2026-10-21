using SkyPass.Core.Coordinates;
using SkyPass.Core.Doppler;
using SkyPass.Core.Passes;
using SkyPass.Core.Propagation;
using SkyPass.Core.Tle;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Doppler;

/// <summary>驗收 #13：符號、距離變化率為零時頻移為零、與參考資料誤差 ≤ 1 Hz（145.8 MHz）。</summary>
public class DopplerTests
{
    private const double F0 = 145.8e6;

    [Fact]
    public void Matches_reference_within_1_hz()
    {
        var count = 0;
        foreach (var g in TestData.ReadCsv("doppler.csv").GroupBy(c => c[0]))
        {
            var first = g.First();
            var prop = Sgp4Propagator.Create(TleParser.Parse($"{first[1]}\n{first[2]}").Sets.Single());
            var site = new ObserverSite(new Geodetic(TestData.D(first[3]), TestData.D(first[4]), TestData.D(first[5])));
            foreach (var c in g)
            {
                var t = prop.Epoch.AddSeconds(TestData.D(c[6]) * 60);

                var sample = DopplerCalculator.At(prop, site, t, F0);

                Assert.NotNull(sample);
                Assert.True(Math.Abs(sample.Value.ReceivedHz - TestData.D(c[9])) <= 1.0, $"{g.Key} t={c[6]}: 差 {sample.Value.ReceivedHz - TestData.D(c[9]):F4} Hz");
                count++;
            }
        }

        Assert.True(count > 1000);
    }

    [Theory]
    [InlineData(-7.0, 145.8e6 * (1 + 7.0 / 299792.458))]
    [InlineData(7.0, 145.8e6 * (1 - 7.0 / 299792.458))]
    [InlineData(0.0, 145.8e6)]
    public void Received_frequency_formula(double rangeRate, double expected)
    {
        Assert.Equal(expected, DopplerCalculator.ReceivedHz(F0, rangeRate), 1e-6);
    }

    [Fact]
    public void Approaching_raises_frequency_and_receding_lowers_it()
    {
        Assert.True(DopplerCalculator.ReceivedHz(F0, -5) > F0);
        Assert.True(DopplerCalculator.ReceivedHz(F0, 5) < F0);
    }

    [Fact]
    public void Curve_over_a_pass_goes_from_positive_to_negative_shift_through_zero()
    {
        var (prop, site, pass) = IssPass();

        var curve = DopplerCalculator.Curve(prop, site, pass.Aos, pass.Los, F0, stepSeconds: 5);

        Assert.Equal(pass.Aos, curve[0].Time);
        Assert.Equal(pass.Los, curve[^1].Time);
        Assert.True(curve[0].ShiftHz > 2000, "AOS 時接近中，頻率應升高");
        Assert.True(curve[^1].ShiftHz < -2000, "LOS 時遠離中，頻率應降低");
        for (var i = 1; i < curve.Count; i++)
        {
            var dt = curve[i].Time.SecondsSince(curve[i - 1].Time);
            Assert.True(dt is > 0 and <= 5.0 + 1e-9, $"第 {i} 點間隔 {dt:R} s（共 {curve.Count} 點）");
        }

        var zero = DopplerCalculator.FindZeroShift(prop, site, pass.Aos, pass.Los);
        Assert.NotNull(zero);
        var atZero = DopplerCalculator.At(prop, site, zero.Value, F0)!.Value;
        Assert.True(Math.Abs(atZero.ShiftHz) < 0.01, $"距離變化率為零處頻移應為零，實際 {atZero.ShiftHz} Hz");
        Assert.True(Math.Abs(atZero.RangeRateKmPerSec) < 1e-6);
    }

    internal static (Sgp4Propagator, ObserverSite, SatellitePass) IssPass()
    {
        var prop = Sgp4Propagator.Create(PassScenario.Satellites.First(s => s.NoradId == 25544));
        var site = new ObserverSite(new Geodetic(25.0330, 121.5654, 0.01));
        var pass = PassFinder.Find(prop, site, prop.Epoch, prop.Epoch.AddSeconds(2 * 86400), 10).Passes
            .First(p => !p.StartedBeforeWindow && p.MaxElevationDeg > 30);
        return (prop, site, pass);
    }
}
