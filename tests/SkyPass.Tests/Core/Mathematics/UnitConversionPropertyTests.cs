using SkyPass.Core.Coordinates;
using SkyPass.Core.Doppler;
using SkyPass.Core.Mathematics;
using SkyPass.Core.Time;
using SkyPass.Tests.Support;

namespace SkyPass.Tests.Core.Mathematics;

/// <summary>驗收 #21「單位換算」：度↔弧度、角度正規化、秒↔分鐘、公尺↔公里、Hz 與距離變化率。</summary>
public class UnitConversionPropertyTests
{
    [Fact]
    public void Property_unit_conversions_are_consistent()
    {
        Property.Run("單位換算", seed: 21001, iterations: 5000, rng =>
        {
            var deg = rng.NextDouble() * 2000 - 1000;

            // 度 ↔ 弧度 往返
            Assert.Equal(deg, deg * Angles.DegToRad * Angles.RadToDeg, 1e-9);

            // 正規化後在 [0, 360) 且與原角度同方向
            var n = Angles.Normalize360Deg(deg);
            Assert.InRange(n, 0, 360 - 1e-12);
            Assert.Equal(Math.Cos(deg * Angles.DegToRad), Math.Cos(n * Angles.DegToRad), 1e-9);
            Assert.Equal(Math.Sin(deg * Angles.DegToRad), Math.Sin(n * Angles.DegToRad), 1e-9);

            // 角度差在 [−180, 180) 且 a + diff ≡ b
            var other = rng.NextDouble() * 720 - 360;
            var diff = Angles.DifferenceDeg(deg, other);
            Assert.InRange(diff, -180, 180 - 1e-12);
            Assert.Equal(0, Angles.DifferenceDeg(Angles.Normalize360Deg(deg + diff), Angles.Normalize360Deg(other)), 1e-7);

            // 時間：秒 ↔ 分鐘 ↔ 儒略日
            var t0 = JulianDate.FromParts(2451544.5 + rng.Next(0, 40000), rng.NextDouble());
            var seconds = rng.NextDouble() * 2e6 - 1e6;
            var t1 = t0.AddSeconds(seconds);
            Assert.Equal(seconds, t1.SecondsSince(t0), 1e-4);
            Assert.Equal(t1.SecondsSince(t0) / 60.0, t1.MinutesSince(t0), 1e-9);

            // 高度：公尺（介面）↔ 公里（內部）
            var heightM = rng.NextDouble() * 9000 - 500;
            var site = new Geodetic(rng.NextDouble() * 180 - 90, rng.NextDouble() * 360 - 180, heightM / 1000.0);
            Assert.Equal(heightM, Wgs84.FromEcef(Wgs84.ToEcef(site)).HeightKm * 1000.0, 1e-5);

            // 都卜勒：頻移與距離變化率成正比、反號
            var rate = rng.NextDouble() * 16 - 8;
            var f0 = 1e6 + rng.NextDouble() * 1e10;
            var shift = DopplerCalculator.ReceivedHz(f0, rate) - f0;
            Assert.Equal(-f0 * rate / DopplerCalculator.SpeedOfLightKmPerSec, shift, Math.Abs(f0) * 1e-15 + 1e-6);
        });
    }
}
