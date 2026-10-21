using SkyPass.Core.Storage;
using SkyPass.Core.Tle;

namespace SkyPass.Tests.Support;

/// <summary>可手動設定「現在」的 TimeProvider。</summary>
public sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public FakeTimeProvider(DateTime utc)
        : this(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void SetUtcNow(DateTime utc) => _now = new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

    public void Advance(TimeSpan delta) => _now += delta;
}

public sealed class InMemoryCatalogStore(StoreStatus status = StoreStatus.Ok, TleCatalog? initial = null) : ITleCatalogStore
{
    public TleCatalog Saved { get; private set; } = initial ?? TleCatalog.Empty;

    public int SaveCount { get; private set; }

    public CatalogLoadResult Load() => status is StoreStatus.Ok or StoreStatus.Created
        ? new CatalogLoadResult(Saved, status, null, [])
        : new CatalogLoadResult(TleCatalog.Empty, status, $"資料庫狀態 {status}", []);

    public void Save(TleCatalog catalog)
    {
        if (status is not (StoreStatus.Ok or StoreStatus.Created))
        {
            throw new InvalidOperationException("不可寫入");
        }

        Saved = catalog;
        SaveCount++;
    }
}

public sealed class InMemorySettingsStore(AppSettings? initial = null, IReadOnlyList<string>? warnings = null) : ISettingsStore
{
    public AppSettings Saved { get; private set; } = initial ?? AppSettings.Default;

    public int SaveCount { get; private set; }

    public SettingsLoadResult Load() => new(Saved, warnings ?? []);

    public void Save(AppSettings settings)
    {
        Saved = settings;
        SaveCount++;
    }
}

public static class SampleTle
{
    /// <summary>CelesTrak 快照的 14 顆衛星（三行格式文字）。</summary>
    public static string SnapshotText => File.ReadAllText(TestData.PathOf("celestrak-snapshot-20261021.tle"));

    /// <summary>快照 epoch 附近的時間（2026-10-21 00:00 UTC）。</summary>
    public static readonly DateTime SnapshotTime = new(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
}
