using SkyPass.Core.Coordinates;
using SkyPass.Core.Propagation;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;

namespace SkyPass.Tests.Support;

/// <summary>過境性質測試的隨機情境：衛星、觀測點、時間範圍、遮罩。</summary>
public sealed record PassScenario(Sgp4Propagator Propagator, ObserverSite Site, JulianDate Start, double DurationSec, double MaskDeg)
{
    public JulianDate End => Start.AddSeconds(DurationSec);

    public override string ToString() =>
        $"NORAD {Propagator.Tle.NoradId}, 觀測點 {Site.Location}, 起點 {Start.ToUtc():O}, 長度 {DurationSec:F0} s, 遮罩 {MaskDeg:F3}°";

    private static readonly Lazy<IReadOnlyList<TleSet>> Pool = new(LoadPool);

    public static IReadOnlyList<TleSet> Satellites => Pool.Value;

    /// <summary>產生一個在整個時間範圍內都能正常傳播的情境。</summary>
    public static PassScenario Random(Random rng, double minDurationSec = 3600, double maxDurationSec = 8 * 3600, double maxMaskDeg = 30)
    {
        while (true)
        {
            var tle = Satellites[rng.Next(Satellites.Count)];
            var prop = Sgp4Propagator.Create(tle);
            var site = new ObserverSite(new Geodetic(rng.NextDouble() * 160 - 80, rng.NextDouble() * 360 - 180, rng.NextDouble() * 2));
            var start = prop.Epoch.AddSeconds(Math.Round(rng.NextDouble() * 4 * 86400 - 86400, 3));
            var duration = minDurationSec + rng.NextDouble() * (maxDurationSec - minDurationSec);
            var mask = rng.NextDouble() * maxMaskDeg;
            if (PropagatesThroughout(prop, start, duration))
            {
                return new PassScenario(prop, site, start, duration, mask);
            }
        }
    }

    public static bool PropagatesThroughout(Sgp4Propagator prop, JulianDate start, double durationSec)
    {
        for (var s = 0.0; s <= durationSec + 60; s += 60)
        {
            if (prop.Propagate(start.AddSeconds(s), out _) != Sgp4Error.None)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<TleSet> LoadPool()
    {
        var snapshot = TleParser.Parse(File.ReadAllText(TestData.PathOf("celestrak-snapshot-20261021.tle"))).Sets;
        var random = Sgp4ReferenceRow.Load("sgp4_random.csv")
            .GroupBy(r => r.Case)
            .Where(g => g.All(r => r.Error == 0))
            .Select(g => g.First().Tle);
        return snapshot.Concat(random).ToList();
    }
}
