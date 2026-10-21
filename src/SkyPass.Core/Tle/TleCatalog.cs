using SkyPass.Core.Time;

namespace SkyPass.Core.Tle;

/// <summary>
/// 軌道資料目錄（不可變）。同一 NORAD 依 epoch 由新到舊保留最多 <see cref="MaxHistory"/> 組，
/// 預設使用最新的一組，可釘選歷史中的任一組。
/// </summary>
public sealed class TleCatalog
{
    public const int MaxHistory = 5;

    public static readonly TleCatalog Empty = new([]);

    private TleCatalog(IReadOnlyList<CatalogEntry> entries)
    {
        Entries = entries;
    }

    /// <summary>依 NORAD 編號遞增排序。</summary>
    public IReadOnlyList<CatalogEntry> Entries { get; }

    public static TleCatalog FromEntries(IEnumerable<CatalogEntry> entries) =>
        new(entries.OrderBy(e => e.NoradId).ToList());

    public CatalogEntry? Find(int noradId) => Entries.FirstOrDefault(e => e.NoradId == noradId);

    public (TleCatalog Catalog, ImportSummary Summary) Import(IEnumerable<TleSet> sets)
    {
        var byId = Entries.ToDictionary(e => e.NoradId, e => (History: e.History.ToList(), e.PinnedEpoch));
        int added = 0, replaced = 0, discarded = 0;

        foreach (var set in sets)
        {
            if (!byId.TryGetValue(set.NoradId, out var slot))
            {
                slot = ([], null);
                byId[set.NoradId] = slot;
            }

            var history = slot.History;
            var sameEpoch = history.FindIndex(h => h.Epoch == set.Epoch);
            if (sameEpoch >= 0)
            {
                history[sameEpoch] = set;
                replaced++;
                continue;
            }

            if (history.Count >= MaxHistory && set.Epoch.MinutesSince(history[^1].Epoch) < 0)
            {
                discarded++;
                continue;
            }

            history.Add(set);
            history.Sort((a, b) => b.Epoch.MinutesSince(a.Epoch).CompareTo(0.0));
            added++;
            if (history.Count > MaxHistory)
            {
                history.RemoveAt(history.Count - 1);
                discarded++;
            }
        }

        var entries = byId
            .Select(kv =>
            {
                var pinned = kv.Value.PinnedEpoch;
                if (pinned is not null && !kv.Value.History.Any(h => h.Epoch == pinned.Value))
                {
                    pinned = null;
                }

                return new CatalogEntry(kv.Key, kv.Value.History, pinned);
            })
            .ToList();

        var catalog = FromEntries(entries);
        var unsupported = catalog.Entries.Count(e => !e.IsSupported);
        return (catalog, new ImportSummary(added, replaced, discarded, unsupported));
    }

    public TleCatalog Pin(int noradId, JulianDate epoch)
    {
        var entry = Find(noradId) ?? throw new ArgumentException($"目錄中沒有 NORAD {noradId}。", nameof(noradId));
        if (!entry.History.Any(h => h.Epoch == epoch))
        {
            throw new ArgumentException($"NORAD {noradId} 的歷史中沒有 epoch {epoch.ToUtc():u} 的軌道資料。", nameof(epoch));
        }

        return Replace(entry with { PinnedEpoch = epoch });
    }

    public TleCatalog Unpin(int noradId)
    {
        var entry = Find(noradId) ?? throw new ArgumentException($"目錄中沒有 NORAD {noradId}。", nameof(noradId));
        return Replace(entry with { PinnedEpoch = null });
    }

    public TleCatalog Remove(int noradId) => new(Entries.Where(e => e.NoradId != noradId).ToList());

    private TleCatalog Replace(CatalogEntry entry) =>
        new(Entries.Select(e => e.NoradId == entry.NoradId ? entry : e).ToList());
}

/// <param name="History">由新到舊排序，至少一組。</param>
/// <param name="PinnedEpoch">使用者釘選的 epoch；null 表示使用最新一組。</param>
public sealed record CatalogEntry(int NoradId, IReadOnlyList<TleSet> History, JulianDate? PinnedEpoch)
{
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(14);

    public TleSet Latest => History[0];

    public TleSet Active => PinnedEpoch is { } pinned ? History.First(h => h.Epoch == pinned) : Latest;

    public string Name => Latest.Name;

    /// <summary>深空軌道（週期 ≥ 225 分鐘）不支援預測。</summary>
    public bool IsSupported => !Active.IsDeepSpace;

    /// <summary>預測時間與目前使用組的 epoch 相差超過 14 天。</summary>
    public bool IsStaleAt(JulianDate time) => Math.Abs(time.MinutesSince(Active.Epoch)) > StaleThreshold.TotalMinutes;
}

/// <param name="Added">新增的軌道資料組數。</param>
/// <param name="Replaced">epoch 相同而被取代的組數。</param>
/// <param name="DiscardedOlder">因超過歷史上限而捨棄的較舊組數。</param>
/// <param name="Unsupported">匯入後目錄中標示為不支援（深空）的衛星數。</param>
public sealed record ImportSummary(int Added, int Replaced, int DiscardedOlder, int Unsupported);
