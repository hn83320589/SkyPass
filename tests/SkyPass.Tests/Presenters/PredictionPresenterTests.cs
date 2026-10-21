using SkyPass.Core.Coordinates;
using SkyPass.Core.Passes;
using SkyPass.Presenters;
using SkyPass.Tests.Core.Passes;

namespace SkyPass.Tests.Presenters;

public class PredictionPresenterTests
{
    private static PredictionRequest Request(int sats, double days)
    {
        var list = PassPredictorTests.Constellation(sats);
        return new PredictionRequest(list, new Geodetic(25.03, 121.56, 0.01), list[0].Epoch, list[0].Epoch.AddSeconds(days * 86400), 10);
    }

    [Fact]
    public async Task Completed_run_publishes_result_and_full_progress()
    {
        var presenter = new PredictionPresenter();

        await presenter.StartAsync(Request(10, 1));

        var state = presenter.State;
        Assert.Equal(PredictionStatus.Completed, state.Status);
        Assert.NotNull(state.Result);
        Assert.Equal(1.0, presenter.Progress);
        Assert.Equal(1, state.Version);
    }

    [Fact]
    public async Task Cancel_keeps_previous_result_and_never_publishes_partial_one()
    {
        var presenter = new PredictionPresenter();
        await presenter.StartAsync(Request(5, 1));
        var previous = presenter.State.Result;

        var running = presenter.StartAsync(Request(200, 7));
        while (presenter.Progress < 0.02)
        {
            await Task.Delay(1);
        }

        presenter.Cancel();
        await running;

        Assert.Equal(PredictionStatus.Cancelled, presenter.State.Status);
        Assert.Same(previous, presenter.State.Result);
        Assert.Equal(1, presenter.State.Version);
    }

    [Fact]
    public async Task Newer_request_supersedes_running_one()
    {
        var presenter = new PredictionPresenter();

        var first = presenter.StartAsync(Request(200, 7));
        var second = presenter.StartAsync(Request(3, 1));
        await Task.WhenAll(first, second);

        Assert.Equal(PredictionStatus.Completed, presenter.State.Status);
        Assert.Equal(3, presenter.State.Request!.Satellites.Count);
    }

    [Fact]
    public async Task Progress_observed_during_run_never_decreases()
    {
        var presenter = new PredictionPresenter();
        var seen = new List<double>();

        var run = presenter.StartAsync(Request(60, 3));
        while (!run.IsCompleted)
        {
            seen.Add(presenter.Progress);
            await Task.Delay(1);
        }

        await run;
        seen.Add(presenter.Progress);
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i] >= seen[i - 1]);
        }

        Assert.Equal(1.0, seen[^1]);
    }

    [Fact]
    public async Task Failure_is_reported_without_throwing()
    {
        var presenter = new PredictionPresenter();
        var bad = Request(2, 1) with { End = Request(2, 1).Start.AddSeconds(-10) };

        await presenter.StartAsync(bad);

        Assert.Equal(PredictionStatus.Failed, presenter.State.Status);
        Assert.Contains("終點早於起點", presenter.State.Message);
    }
}
