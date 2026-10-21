namespace SkyPass.Presenters;

/// <summary>依嚴重度排序：Error 最前。</summary>
public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

public sealed record DiagnosticItem(DiagnosticSeverity Severity, string Source, string Message);

/// <summary>診斷頁：彙整各來源（設定、資料庫、匯入、預報）的訊息。可從背景執行緒呼叫。</summary>
public sealed class DiagnosticsPresenter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IReadOnlyList<DiagnosticItem>> _bySource = [];
    private IReadOnlyList<DiagnosticItem> _items = [];

    public IReadOnlyList<DiagnosticItem> Items => Volatile.Read(ref _items);

    public int Version { get; private set; }

    /// <summary>以新清單取代某個來源的全部訊息。</summary>
    public void SetSource(string source, IEnumerable<DiagnosticItem> items)
    {
        lock (_gate)
        {
            _bySource[source] = items.ToList();
            Publish();
        }
    }

    public void Add(DiagnosticItem item)
    {
        lock (_gate)
        {
            var existing = _bySource.GetValueOrDefault(item.Source) ?? [];
            _bySource[item.Source] = [.. existing, item];
            Publish();
        }
    }

    public void Clear(string source)
    {
        lock (_gate)
        {
            _bySource.Remove(source);
            Publish();
        }
    }

    public int CountOf(DiagnosticSeverity severity) => Items.Count(i => i.Severity == severity);

    private void Publish()
    {
        var items = _bySource.Values.SelectMany(v => v).OrderBy(i => i.Severity).ThenBy(i => i.Source, StringComparer.Ordinal).ToList();
        Volatile.Write(ref _items, items);
        Version++;
    }
}
