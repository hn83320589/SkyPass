using SkyPass.Core.Passes;

namespace SkyPass.Presenters;

public enum PredictionStatus
{
    Idle,
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <param name="Result">最近一次「完整完成」的結果；取消或失敗不會改變它（不產出半套結果）。</param>
/// <param name="Version">每次產出新結果時遞增。</param>
public sealed record PredictionState(PredictionStatus Status, PredictionResult? Result, PredictionRequest? Request, string? Message, int Version, TimeSpan? Elapsed);

/// <summary>
/// 背景預報協調：同一時間只有一個執行中的預報，新的請求會取消舊的。
/// 狀態以不可變快照發佈，UI 執行緒每幀讀取即可，不需要跨執行緒事件。
/// </summary>
/// <param name="clock">量測預報耗時用；預設 <see cref="TimeProvider.System"/>。</param>
public sealed class PredictionPresenter(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private PredictionState _state = new(PredictionStatus.Idle, null, null, null, 0, null);
    private CancellationTokenSource? _cts;
    private int _runId;
    private double _progress;

    public PredictionState State => Volatile.Read(ref _state);

    /// <summary>目前執行的進度 [0, 1]，單調遞增；新的執行開始時歸零。</summary>
    public double Progress => Volatile.Read(ref _progress);

    public bool IsRunning => State.Status == PredictionStatus.Running;

    public Task StartAsync(PredictionRequest request)
    {
        CancellationTokenSource cts;
        int runId;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = cts = new CancellationTokenSource();
            runId = ++_runId;
            Volatile.Write(ref _progress, 0.0);
            Publish(State with { Status = PredictionStatus.Running, Request = request, Message = null });
        }

        return Task.Run(() => Run(request, runId, cts));
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _cts?.Cancel();
        }
    }

    private void Run(PredictionRequest request, int runId, CancellationTokenSource cts)
    {
        var sink = new ProgressSink(this, runId);
        var started = _clock.GetTimestamp();
        try
        {
            var result = PassPredictor.Predict(request, sink, cts.Token);
            Finish(runId, s => s with
            {
                Status = PredictionStatus.Completed,
                Result = result,
                Message = null,
                Version = s.Version + 1,
                Elapsed = _clock.GetElapsedTime(started),
            });
        }
        catch (OperationCanceledException)
        {
            Finish(runId, s => s with { Status = PredictionStatus.Cancelled, Message = "預報已取消，保留上一次的完整結果。" });
        }
        catch (ArgumentException ex)
        {
            Finish(runId, s => s with { Status = PredictionStatus.Failed, Message = $"預報失敗：{ex.Message}" });
        }
        finally
        {
            cts.Dispose();
        }
    }

    /// <summary>只有最新的一次執行可以改變狀態；被取代的舊執行結束時什麼都不做。</summary>
    private void Finish(int runId, Func<PredictionState, PredictionState> update)
    {
        lock (_gate)
        {
            if (runId != _runId)
            {
                return;
            }

            if (update(State) is { Status: PredictionStatus.Completed } completed)
            {
                Volatile.Write(ref _progress, 1.0);
                Publish(completed);
            }
            else
            {
                Publish(update(State));
            }

            _cts = null;
        }
    }

    private void Publish(PredictionState state) => Volatile.Write(ref _state, state);

    private sealed class ProgressSink(PredictionPresenter owner, int runId) : IProgress<PredictionProgress>
    {
        public void Report(PredictionProgress value)
        {
            lock (owner._gate)
            {
                if (runId == owner._runId && value.Fraction > owner.Progress)
                {
                    Volatile.Write(ref owner._progress, value.Fraction);
                }
            }
        }
    }
}
