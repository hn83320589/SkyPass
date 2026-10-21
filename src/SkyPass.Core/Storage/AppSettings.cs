using SkyPass.Core.Coordinates;

namespace SkyPass.Core.Storage;

public sealed record NamedSite(string Name, Geodetic Location);

/// <summary>使用者設定。Core 內部一律 UTC；<see cref="DisplayUtcOffset"/> 只影響畫面與匯出。</summary>
public sealed record AppSettings
{
    public const int MaxSites = 5;
    public const double DefaultMaskDeg = 10.0;
    public const double DefaultDownlinkHz = 145.8e6;
    public static readonly TimeSpan DefaultUtcOffset = TimeSpan.FromHours(8);
    public static readonly TimeSpan MinUtcOffset = TimeSpan.FromHours(-12);
    public static readonly TimeSpan MaxUtcOffset = TimeSpan.FromHours(14);

    public static readonly AppSettings Default = new();

    public IReadOnlyList<NamedSite> Sites { get; init; } = [new NamedSite("社團屋頂（台北）", new Geodetic(25.0330, 121.5654, 0.010))];

    public int SelectedSiteIndex { get; init; }

    public double MaskDeg { get; init; } = DefaultMaskDeg;

    public TimeSpan DisplayUtcOffset { get; init; } = DefaultUtcOffset;

    public TimeSpan CsvUtcOffset { get; init; } = DefaultUtcOffset;

    public double DownlinkHz { get; init; } = DefaultDownlinkHz;

    /// <summary>預報時間範圍（天）。</summary>
    public double PredictionDays { get; init; } = 2.0;

    public NamedSite SelectedSite => Sites[SelectedSiteIndex];

    /// <summary>把不合法的欄位換回預設值，並回傳說明。</summary>
    public (AppSettings Settings, IReadOnlyList<string> Warnings) Sanitize()
    {
        var warnings = new List<string>();
        var s = this;

        var sites = (Sites ?? []).Where(IsValidSite).ToList();
        if (sites.Count != (Sites?.Count ?? 0))
        {
            warnings.Add("部分觀測點的座標不合法，已略過。");
        }

        if (sites.Count > MaxSites)
        {
            warnings.Add($"觀測點最多 {MaxSites} 個，已只保留前 {MaxSites} 個。");
            sites = sites.Take(MaxSites).ToList();
        }

        if (sites.Count == 0)
        {
            warnings.Add("沒有可用的觀測點，已使用預設觀測點。");
            sites = Default.Sites.ToList();
        }

        s = s with { Sites = sites };
        if (s.SelectedSiteIndex < 0 || s.SelectedSiteIndex >= sites.Count)
        {
            warnings.Add("選定的觀測點不存在，已改為第一個。");
            s = s with { SelectedSiteIndex = 0 };
        }

        if (!(s.MaskDeg is >= 0 and < 90))
        {
            warnings.Add($"遮罩仰角 {s.MaskDeg} 不合法（需介於 0° 與 90°），已改為 {DefaultMaskDeg}°。");
            s = s with { MaskDeg = DefaultMaskDeg };
        }

        if (!IsValidOffset(s.DisplayUtcOffset))
        {
            warnings.Add("畫面時區偏移不合法，已改為 UTC+8。");
            s = s with { DisplayUtcOffset = DefaultUtcOffset };
        }

        if (!IsValidOffset(s.CsvUtcOffset))
        {
            warnings.Add("CSV 時區偏移不合法，已改為 UTC+8。");
            s = s with { CsvUtcOffset = DefaultUtcOffset };
        }

        if (!(s.DownlinkHz is > 0 and < 1e12))
        {
            warnings.Add("下行頻率不合法，已改為 145.800 MHz。");
            s = s with { DownlinkHz = DefaultDownlinkHz };
        }

        if (!(s.PredictionDays is > 0 and <= 14))
        {
            warnings.Add("預報天數不合法（需介於 0 與 14 天），已改為 2 天。");
            s = s with { PredictionDays = 2.0 };
        }

        return (s, warnings);
    }

    public static bool IsValidSite(NamedSite? site) =>
        site is not null && !string.IsNullOrWhiteSpace(site.Name) &&
        site.Location.LatitudeDeg is >= -90 and <= 90 &&
        site.Location.LongitudeDeg is >= -180 and <= 180 &&
        site.Location.HeightKm is >= -1 and <= 10;

    public static bool IsValidOffset(TimeSpan offset) =>
        offset >= MinUtcOffset && offset <= MaxUtcOffset && offset.Ticks % TimeSpan.FromMinutes(15).Ticks == 0;
}

public sealed record SettingsLoadResult(AppSettings Settings, IReadOnlyList<string> Warnings);

public interface ISettingsStore
{
    /// <summary>讀取設定；檔案不存在時回傳預設值，損毀時回傳預設值並附警告。不擲例外。</summary>
    SettingsLoadResult Load();

    void Save(AppSettings settings);
}
