using SkyPass.Core.Export;
using SkyPass.Core.Passes;
using SkyPass.Core.Storage;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;

namespace SkyPass.Presenters;

/// <summary>
/// 組合各畫面並負責聯動。UI 每幀呼叫 <see cref="Update"/>：比對各 Presenter 的版本號，
/// 需要時重新預報、把新結果交給過境表與時間軸、把選取的過境交給天空圖與即時追蹤。
/// 所有跨畫面的傳遞都在呼叫 Update 的執行緒上進行，背景工作只發佈快照。
/// </summary>
public sealed class MainPresenter
{
    private const string PredictionSource = "預報";

    private readonly TimeProvider _clock;
    private Task _predictionTask = Task.CompletedTask;
    private (int CatalogVersion, Core.Coordinates.Geodetic Site, double Mask, double Days)? _requestKey;
    private (int PredictionVersion, PredictionStatus Status, TimeSpan Offset, int SettingsVersion)? _shown;
    private int _selectionSeen = -1;
    private TleSet? _liveSatellite;

    public MainPresenter(ITleCatalogStore catalogStore, ISettingsStore settingsStore, TimeProvider clock)
    {
        _clock = clock;
        Diagnostics = new DiagnosticsPresenter();
        Settings = new SettingsPresenter(settingsStore, Diagnostics);
        Catalog = new CatalogPresenter(catalogStore, Diagnostics, clock);
        LiveTracking = new LiveTrackingPresenter(clock);
        Prediction = new PredictionPresenter(clock);
    }

    public DiagnosticsPresenter Diagnostics { get; }

    public SettingsPresenter Settings { get; }

    public CatalogPresenter Catalog { get; }

    public PredictionPresenter Prediction { get; }

    public PassTablePresenter PassTable { get; } = new();

    public SkyPlotPresenter SkyPlot { get; } = new();

    public TimelinePresenter Timeline { get; } = new();

    public LiveTrackingPresenter LiveTracking { get; }

    public void Initialize()
    {
        Settings.Load();
        Catalog.Load();
    }

    public void Update()
    {
        var settings = Settings.Current;
        var site = settings.SelectedSite.Location;

        // 1. 預報輸入改變（目錄、觀測點、遮罩、天數）→ 重新預報
        var key = (Catalog.Version, site, settings.MaskDeg, settings.PredictionDays);
        if (_requestKey != key)
        {
            RequestPrediction();
        }

        // 2. 新的預報結果或顯示時區改變 → 更新過境表、時間軸、診斷
        var state = Prediction.State;
        var shown = (state.Version, state.Status, settings.DisplayUtcOffset, Settings.Version);
        if (_shown != shown)
        {
            _shown = shown;
            ShowPrediction(state, settings);
        }

        // 3. 選取改變 → 天空圖、時間軸、即時追蹤聯動
        var selected = PassTable.SelectedPass;
        var selectedTle = selected is null ? null : TleOf(state.Request, selected.NoradId);
        if (PassTable.SelectionVersion != _selectionSeen)
        {
            _selectionSeen = PassTable.SelectionVersion;
            Timeline.SetSelected(selected);
            if (selectedTle is not null)
            {
                _liveSatellite = selectedTle;
            }
        }

        SkyPlot.SetPass(selected, selectedTle, site, settings.MaskDeg);

        // 4. 即時追蹤跟著目錄更新（同一顆衛星有新的軌道資料時換用新的一組）
        if (_liveSatellite is not null)
        {
            _liveSatellite = Catalog.Catalog.Find(_liveSatellite.NoradId)?.Active ?? _liveSatellite;
        }

        LiveTracking.Configure(_liveSatellite, site, settings.MaskDeg, settings.DownlinkHz);
    }

    /// <summary>以目前的目錄與設定重新預報（自現在起 <see cref="AppSettings.PredictionDays"/> 天）。</summary>
    public void RequestPrediction()
    {
        var settings = Settings.Current;
        _requestKey = (Catalog.Version, settings.SelectedSite.Location, settings.MaskDeg, settings.PredictionDays);
        var sats = Catalog.ActiveSets();
        if (sats.Count == 0)
        {
            return;
        }

        var nowUtc = _clock.GetUtcNow().UtcDateTime;
        var start = JulianDate.FromUtc(new DateTime(nowUtc.Ticks / TimeSpan.TicksPerMinute * TimeSpan.TicksPerMinute, DateTimeKind.Utc));
        var request = new PredictionRequest(sats, settings.SelectedSite.Location, start, start.AddSeconds(settings.PredictionDays * 86400), settings.MaskDeg);
        _predictionTask = Prediction.StartAsync(request);
    }

    public void SelectLiveSatellite(int noradId) => _liveSatellite = Catalog.Catalog.Find(noradId)?.Active;

    public async Task ImportTleFileAsync(string path)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Diagnostics.Add(new DiagnosticItem(DiagnosticSeverity.Error, "匯入", $"無法讀取 {path}：{ex.Message}"));
            return;
        }

        await Catalog.ImportAsync(Path.GetFileName(path), bytes).ConfigureAwait(false);
    }

    /// <summary>匯出目前過境表（已套用篩選與排序）為 UTF-8 with BOM 的 CSV，時區依設定的 CSV 輸出時區。</summary>
    public void ExportCsv(Stream stream) =>
        PassCsvExporter.Write(stream, PassTable.Rows.Select(r => r.Pass), Settings.Current.CsvUtcOffset);

    /// <summary>反覆 Update 並等待背景工作，直到沒有待處理的工作（測試與啟動使用）。</summary>
    public async Task UpdateUntilIdleAsync()
    {
        for (var i = 0; i < 50; i++)
        {
            Update();
            var pending = new[] { _predictionTask, LiveTracking.WhenIdleAsync() };
            if (pending.All(t => t.IsCompleted))
            {
                Update();
                if (_predictionTask.IsCompleted && LiveTracking.WhenIdleAsync().IsCompleted)
                {
                    return;
                }
            }

            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    private void ShowPrediction(PredictionState state, AppSettings settings)
    {
        var passes = state.Result?.Passes ?? [];
        PassTable.SetPasses(passes, settings.DisplayUtcOffset);
        if (state.Request is { } req)
        {
            Timeline.SetData(passes, req.Observer, settings.DisplayUtcOffset, req.Start, req.End);
        }

        var items = (state.Result?.Diagnostics ?? [])
            .Select(d => new DiagnosticItem(
                d.Kind is DiagnosticKind.Decayed or DiagnosticKind.PropagationError ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                PredictionSource, d.Message))
            .ToList();
        if (state.Message is not null)
        {
            items.Add(new DiagnosticItem(state.Status == PredictionStatus.Failed ? DiagnosticSeverity.Error : DiagnosticSeverity.Info, PredictionSource, state.Message));
        }

        if (state is { Status: PredictionStatus.Completed, Elapsed: { } elapsed, Request: { } r })
        {
            items.Add(new DiagnosticItem(DiagnosticSeverity.Info, PredictionSource,
                $"預報完成：{r.Satellites.Count} 顆衛星、{passes.Count} 個過境，耗時 {elapsed.TotalMilliseconds:F0} ms。"));
        }

        Diagnostics.SetSource(PredictionSource, items);
    }

    private static TleSet? TleOf(PredictionRequest? request, int noradId) =>
        request?.Satellites.FirstOrDefault(s => s.NoradId == noradId);
}
