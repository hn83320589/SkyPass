using SkyPass.Core.Propagation;
using SkyPass.Core.Tle;
using SkyPass.Tests.Support;
using static SkyPass.Tests.Support.TleText;

namespace SkyPass.Tests.Core.Propagation;

public class Sgp4PropagatorTests
{
    private static readonly TleSet Iss = TleParser.Parse($"{IssLine1}\n{IssLine2}").Sets[0];

    [Fact]
    public void Deep_space_orbit_is_rejected_without_exception()
    {
        var geo = Iss with { MeanMotionRevPerDay = 1.00271 };

        var prop = Sgp4Propagator.Create(geo);

        Assert.Equal(Sgp4Error.DeepSpaceNotSupported, prop.InitError);
        Assert.Equal(Sgp4Error.DeepSpaceNotSupported, prop.Propagate(0, out _));
    }

    [Fact]
    public void Decaying_orbit_returns_error_code_instead_of_throwing()
    {
        // SGP4-VER 的 28872：約 50 分鐘後衰減
        var rows = Sgp4ReferenceRow.Load("sgp4_published.csv").Where(r => r.Case == "VER-28872").ToList();
        var prop = Sgp4Propagator.Create(rows[0].Tle);

        Assert.Equal(Sgp4Error.None, prop.Propagate(50.0, out _));
        Assert.Equal(Sgp4Error.Decayed, prop.Propagate(60.0, out var state));
        Assert.Equal(default, state);
    }

    [Fact]
    public void Propagate_by_time_uses_minutes_since_epoch()
    {
        var prop = Sgp4Propagator.Create(Iss);
        var t = Iss.Epoch.AddSeconds(5400);

        prop.Propagate(90.0, out var byMinutes);
        prop.Propagate(t, out var byTime);

        Assert.Equal(byMinutes.PositionKm.X, byTime.PositionKm.X, 1e-6);
        Assert.Equal(byMinutes.VelocityKmPerSec.Z, byTime.VelocityKmPerSec.Z, 1e-9);
    }

    [Fact]
    public void Propagate_does_not_allocate()
    {
        var prop = Sgp4Propagator.Create(Iss);
        var sum = 0.0;

        var allocated = Allocations.MinimumAllocatedBytes(() =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                prop.Propagate(i * 0.5, out var s);
                sum += s.PositionKm.X;
            }
        });

        Assert.Equal(0, allocated);
        Assert.NotEqual(0, sum);
    }

    [Fact]
    public void Concurrent_propagation_gives_identical_results()
    {
        var prop = Sgp4Propagator.Create(Iss);
        var times = Enumerable.Range(0, 20_000).Select(i => i * 0.37 - 3000).ToArray();
        var sequential = times.Select(t => { prop.Propagate(t, out var s); return s; }).ToArray();

        var parallel = new TemeState[times.Length];
        Parallel.For(0, times.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => prop.Propagate(times[i], out parallel[i]));

        Assert.Equal(sequential, parallel);
    }
}
