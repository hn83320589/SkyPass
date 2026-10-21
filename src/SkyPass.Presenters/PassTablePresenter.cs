using System.Globalization;
using SkyPass.Core.Passes;
using SkyPass.Core.Time;

namespace SkyPass.Presenters;

public enum PassSortColumn
{
    Satellite,
    Aos,
    MaxElevation,
}

/// <param name="Text">比對衛星名稱（不分大小寫）或 NORAD 編號；null 或空白表示不篩選。</param>
public sealed record PassFilter(string? Text, double MinMaxElevationDeg, bool VisibleOnly)
{
    public static readonly PassFilter None = new(null, 0, false);
}

/// <summary>過境表的一列（已依顯示時區格式化）。</summary>
public sealed record PassRow(
    SatellitePass Pass,
    string Satellite,
    string Aos,
    string AosAzimuth,
    string MaxTime,
    string MaxElevation,
    string Los,
    string LosAzimuth,
    string Duration,
    string Flags,
    string Visible,
    bool IsSelected);

/// <summary>過境表：排序、篩選與選取。選取的過境由 <see cref="SelectedPass"/> 提供給天空圖、時間軸與即時追蹤聯動。</summary>
public sealed class PassTablePresenter
{
    private IReadOnlyList<SatellitePass> _passes = [];
    private TimeSpan _offset;
    private IReadOnlyList<PassRow>? _rows;

    public PassSortColumn SortColumn { get; private set; } = PassSortColumn.Aos;

    public bool SortDescending { get; private set; }

    public PassFilter Filter { get; private set; } = PassFilter.None;

    public SatellitePass? SelectedPass { get; private set; }

    /// <summary>選取改變時遞增。</summary>
    public int SelectionVersion { get; private set; }

    public IReadOnlyList<PassRow> Rows => _rows ??= BuildRows();

    public int TotalCount => _passes.Count;

    public void SetPasses(IReadOnlyList<SatellitePass> passes, TimeSpan displayOffset)
    {
        _passes = passes;
        _offset = displayOffset;
        if (SelectedPass is { } old)
        {
            // 重新預報後，以「同一顆衛星且時間重疊」找回原本選取的過境
            var same = passes.FirstOrDefault(p => p.NoradId == old.NoradId && p.Aos.SecondsSince(old.Los) < 0 && old.Aos.SecondsSince(p.Los) < 0);
            SetSelection(same);
        }

        Invalidate();
    }

    public void SortBy(PassSortColumn column, bool descending)
    {
        SortColumn = column;
        SortDescending = descending;
        Invalidate();
    }

    /// <summary>點同一欄切換方向，點新的一欄則遞增排序。</summary>
    public void ToggleSort(PassSortColumn column) => SortBy(column, column == SortColumn && !SortDescending);

    public void SetFilter(PassFilter filter)
    {
        Filter = filter;
        Invalidate();
        if (SelectedPass is not null && !Rows.Any(r => r.Pass == SelectedPass))
        {
            SetSelection(null);
            Invalidate();
        }
    }

    public void Select(PassRow? row)
    {
        SetSelection(row?.Pass);
        Invalidate();
    }

    public void Select(SatellitePass? pass)
    {
        SetSelection(pass);
        Invalidate();
    }

    private void SetSelection(SatellitePass? pass)
    {
        if (!ReferenceEquals(pass, SelectedPass))
        {
            SelectedPass = pass;
            SelectionVersion++;
        }
    }

    private void Invalidate() => _rows = null;

    private IReadOnlyList<PassRow> BuildRows()
    {
        IEnumerable<SatellitePass> query = _passes;
        var f = Filter;
        if (!string.IsNullOrWhiteSpace(f.Text))
        {
            var text = f.Text.Trim();
            query = query.Where(p => p.SatelliteName.Contains(text, StringComparison.OrdinalIgnoreCase)
                                     || p.NoradId.ToString(CultureInfo.InvariantCulture).Contains(text, StringComparison.Ordinal));
        }

        query = query.Where(p => p.MaxElevationDeg >= f.MinMaxElevationDeg);
        if (f.VisibleOnly)
        {
            query = query.Where(p => p.VisibleIntervals.Count > 0);
        }

        IOrderedEnumerable<SatellitePass> sorted = SortColumn switch
        {
            PassSortColumn.Satellite => Order(query, p => p.SatelliteName, StringComparer.Ordinal),
            PassSortColumn.MaxElevation => Order(query, p => p.MaxElevationDeg, Comparer<double>.Default),
            _ => Order(query, p => p.Aos.Value, Comparer<double>.Default),
        };

        return sorted
            .ThenBy(p => p.Aos.Day).ThenBy(p => p.Aos.Fraction).ThenBy(p => p.NoradId)
            .Select(ToRow)
            .ToList();
    }

    private IOrderedEnumerable<SatellitePass> Order<TKey>(IEnumerable<SatellitePass> source, Func<SatellitePass, TKey> key, IComparer<TKey> comparer) =>
        SortDescending ? source.OrderByDescending(key, comparer) : source.OrderBy(key, comparer);

    private PassRow ToRow(SatellitePass p)
    {
        var inv = CultureInfo.InvariantCulture;
        var flags = new List<string>();
        if (p.StartedBeforeWindow)
        {
            flags.Add("起點前已開始");
        }

        if (p.EndsAfterWindow)
        {
            flags.Add("終點後才結束");
        }

        if (p.EndedByDecay)
        {
            flags.Add("衰減");
        }

        var visible = string.Join(" / ", p.VisibleIntervals.Select(iv =>
            $"{TimeDisplay.Format(iv.Start, _offset, TimeDisplay.TimeFormat)}–{TimeDisplay.Format(iv.End, _offset, TimeDisplay.TimeFormat)}"));

        return new PassRow(
            p,
            p.SatelliteName,
            TimeDisplay.Format(p.Aos, _offset),
            $"{p.AosAzimuthDeg.ToString("F0", inv)}°",
            TimeDisplay.Format(p.MaxElevationTime, _offset, TimeDisplay.TimeFormat),
            $"{p.MaxElevationDeg.ToString("F1", inv)}°",
            TimeDisplay.Format(p.Los, _offset),
            $"{p.LosAzimuthDeg.ToString("F0", inv)}°",
            FormatDuration(p.DurationSeconds),
            string.Join("、", flags),
            visible,
            ReferenceEquals(p, SelectedPass));
    }

    internal static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Round(seconds));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }
}
