using System.Globalization;
using SkyPass.Core.Storage;
using SkyPass.Core.Time;
using SkyPass.Core.Tle;

namespace SkyPass.Presenters;

public enum CatalogRowStatus
{
    Ok,
    Stale,
    Unsupported,
}

public sealed record CatalogRow(
    int NoradId,
    string Name,
    DateTime ActiveEpochUtc,
    double AgeDays,
    CatalogRowStatus Status,
    string StatusText,
    IReadOnlyList<TleSet> History,
    TleSet Active,
    bool IsPinned,
    bool IsSelected);

public sealed record ImportReport(string FileName, int SetsRead, ImportSummary Summary, IReadOnlyList<TleError> Errors);

/// <summary>軌道資料管理頁：匯入、歷史與釘選、預測用衛星的勾選，以及深空／陳舊標示。</summary>
public sealed class CatalogPresenter(ITleCatalogStore store, DiagnosticsPresenter diagnostics, TimeProvider clock)
{
    private const string StoreSource = "資料庫";
    private const string ImportSource = "匯入";

    private readonly Lock _gate = new();
    private readonly HashSet<int> _deselected = [];
    private TleCatalog _catalog = TleCatalog.Empty;
    private bool _writable;
    private (int Version, long Minute, IReadOnlyList<CatalogRow> Rows)? _rowsCache;

    public int Version { get; private set; }

    public ImportReport? LastImport { get; private set; }

    public TleCatalog Catalog => Volatile.Read(ref _catalog);

    public IReadOnlyList<CatalogRow> Rows
    {
        get
        {
            var minute = clock.GetUtcNow().UtcTicks / TimeSpan.TicksPerMinute;
            var cache = _rowsCache;
            if (cache is { } c && c.Version == Version && c.Minute == minute)
            {
                return c.Rows;
            }

            var rows = BuildRows();
            _rowsCache = (Version, minute, rows);
            return rows;
        }
    }

    public void Load()
    {
        var result = store.Load();
        _writable = result.IsWritable;
        var items = new List<DiagnosticItem>();
        if (result.Message is not null)
        {
            items.Add(new DiagnosticItem(result.IsWritable ? DiagnosticSeverity.Info : DiagnosticSeverity.Error, StoreSource, result.Message));
        }

        items.AddRange(result.Warnings.Select(w => new DiagnosticItem(DiagnosticSeverity.Warning, StoreSource, w)));
        diagnostics.SetSource(StoreSource, items);
        Replace(result.Catalog, save: false);
    }

    /// <summary>重新計算與「現在時間」有關的欄位（陳舊）。</summary>
    public void Refresh() => _rowsCache = null;

    /// <summary>在背景解析並匯入 TLE 檔；錯誤組別列入匯入報告與診斷頁，其餘照常匯入。</summary>
    public async Task ImportAsync(string fileName, byte[] content)
    {
        var (parsed, imported) = await Task.Run(() =>
        {
            var p = TleParser.Parse(content);
            TleCatalog current;
            lock (_gate)
            {
                current = _catalog;
            }

            return (p, current.Import(p.Sets));
        }).ConfigureAwait(false);

        LastImport = new ImportReport(fileName, parsed.Sets.Count, imported.Summary, parsed.Errors);
        var items = parsed.Errors
            .Select(e => new DiagnosticItem(DiagnosticSeverity.Warning, ImportSource, $"{fileName} {e}"))
            .Prepend(new DiagnosticItem(DiagnosticSeverity.Info, ImportSource,
                $"{fileName}：讀入 {parsed.Sets.Count} 組，新增 {imported.Summary.Added}、取代 {imported.Summary.Replaced}、" +
                $"捨棄較舊 {imported.Summary.DiscardedOlder}、錯誤 {parsed.Errors.Count} 組；不支援（深空）衛星 {imported.Summary.Unsupported} 顆。"));
        diagnostics.SetSource(ImportSource, items);
        Replace(imported.Catalog, save: true);
    }

    public void Pin(int noradId, JulianDate epoch) => Replace(Catalog.Pin(noradId, epoch), save: true);

    public void Unpin(int noradId) => Replace(Catalog.Unpin(noradId), save: true);

    public void Remove(int noradId) => Replace(Catalog.Remove(noradId), save: true);

    public void SetSelected(int noradId, bool selected)
    {
        lock (_gate)
        {
            var changed = selected ? _deselected.Remove(noradId) : _deselected.Add(noradId);
            if (changed)
            {
                Version++;
            }
        }
    }

    /// <summary>勾選且支援預測的衛星，各取目前使用的一組（釘選或最新）。</summary>
    public IReadOnlyList<TleSet> ActiveSets()
    {
        lock (_gate)
        {
            return Catalog.Entries.Where(e => e.IsSupported && !_deselected.Contains(e.NoradId)).Select(e => e.Active).ToList();
        }
    }

    private void Replace(TleCatalog catalog, bool save)
    {
        lock (_gate)
        {
            Volatile.Write(ref _catalog, catalog);
            Version++;
        }

        if (!save)
        {
            return;
        }

        if (!_writable)
        {
            diagnostics.Add(new DiagnosticItem(DiagnosticSeverity.Error, StoreSource, "資料庫無法寫入，本次變更只保留在記憶體中，關閉程式後會遺失。"));
            return;
        }

        try
        {
            store.Save(catalog);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Data.Common.DbException)
        {
            diagnostics.Add(new DiagnosticItem(DiagnosticSeverity.Error, StoreSource, $"軌道資料無法寫入資料庫：{ex.Message}"));
        }
    }

    private IReadOnlyList<CatalogRow> BuildRows()
    {
        var now = JulianDate.FromUtc(clock.GetUtcNow().UtcDateTime);
        lock (_gate)
        {
            return Catalog.Entries.Select(e =>
            {
                var age = now.SecondsSince(e.Active.Epoch) / 86400.0;
                var (status, text) = !e.IsSupported
                    ? (CatalogRowStatus.Unsupported, $"不支援（深空軌道，週期 {e.Active.PeriodMinutes.ToString("F0", CultureInfo.InvariantCulture)} 分鐘）")
                    : e.IsStaleAt(now)
                        ? (CatalogRowStatus.Stale, $"軌道資料陳舊（{Math.Abs(age).ToString("F1", CultureInfo.InvariantCulture)} 天）")
                        : (CatalogRowStatus.Ok, "可預測");
                return new CatalogRow(e.NoradId, e.Name, e.Active.EpochUtc, age, status, text, e.History, e.Active,
                    e.PinnedEpoch is not null, !_deselected.Contains(e.NoradId));
            }).ToList();
        }
    }
}
