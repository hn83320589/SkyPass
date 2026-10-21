using SkyPass.Core.Coordinates;
using SkyPass.Core.Storage;

namespace SkyPass.Presenters;

/// <summary>設定頁：觀測點（最多 5 個）、遮罩、時區顯示偏移、下行頻率、預報天數。每次變更都驗證並立即儲存。</summary>
public sealed class SettingsPresenter(ISettingsStore store, DiagnosticsPresenter diagnostics)
{
    private const string Source = "設定";

    public AppSettings Current { get; private set; } = AppSettings.Default;

    /// <summary>每次設定變更遞增，供其他畫面判斷是否需要重新計算。</summary>
    public int Version { get; private set; }

    /// <summary>最近一次被拒絕的操作原因；成功時清除。</summary>
    public string? LastError { get; private set; }

    public void Load()
    {
        var result = store.Load();
        Current = result.Settings;
        diagnostics.SetSource(Source, result.Warnings.Select(w => new DiagnosticItem(DiagnosticSeverity.Warning, Source, w)));
        Version++;
    }

    public bool AddSite(string name, double latitudeDeg, double longitudeDeg, double heightMeters)
    {
        if (Current.Sites.Count >= AppSettings.MaxSites)
        {
            return Reject($"觀測點最多 {AppSettings.MaxSites} 個，請先刪除一個。");
        }

        var site = new NamedSite(name.Trim(), new Geodetic(latitudeDeg, longitudeDeg, heightMeters / 1000.0));
        if (!AppSettings.IsValidSite(site))
        {
            return Reject("觀測點不合法：名稱不可空白，緯度需介於 −90° 與 90°，經度介於 −180° 與 180°，高度介於 −1000 m 與 10000 m。");
        }

        return Apply(Current with { Sites = [.. Current.Sites, site] });
    }

    public bool UpdateSite(int index, string name, double latitudeDeg, double longitudeDeg, double heightMeters)
    {
        if (index < 0 || index >= Current.Sites.Count)
        {
            return Reject("觀測點不存在。");
        }

        var site = new NamedSite(name.Trim(), new Geodetic(latitudeDeg, longitudeDeg, heightMeters / 1000.0));
        if (!AppSettings.IsValidSite(site))
        {
            return Reject("觀測點不合法：名稱不可空白，緯度需介於 −90° 與 90°，經度介於 −180° 與 180°，高度介於 −1000 m 與 10000 m。");
        }

        var sites = Current.Sites.ToList();
        sites[index] = site;
        return Apply(Current with { Sites = sites });
    }

    public bool RemoveSite(int index)
    {
        if (Current.Sites.Count <= 1)
        {
            return Reject("至少需要保留一個觀測點。");
        }

        if (index < 0 || index >= Current.Sites.Count)
        {
            return Reject("觀測點不存在。");
        }

        var sites = Current.Sites.Where((_, i) => i != index).ToList();
        var selected = Current.SelectedSiteIndex;
        if (selected == index)
        {
            selected = 0;
        }
        else if (selected > index)
        {
            selected--;
        }

        return Apply(Current with { Sites = sites, SelectedSiteIndex = selected });
    }

    public bool SelectSite(int index) =>
        index >= 0 && index < Current.Sites.Count ? Apply(Current with { SelectedSiteIndex = index }) : Reject("觀測點不存在。");

    public bool SetMask(double maskDeg) =>
        maskDeg is >= 0 and < 90 ? Apply(Current with { MaskDeg = maskDeg }) : Reject("遮罩仰角需介於 0° 與 90°。");

    public bool SetDisplayOffset(TimeSpan offset) =>
        AppSettings.IsValidOffset(offset) ? Apply(Current with { DisplayUtcOffset = offset }) : Reject("時區偏移需介於 UTC−12 與 UTC+14，且為 15 分鐘的倍數。");

    public bool SetCsvOffset(TimeSpan offset) =>
        AppSettings.IsValidOffset(offset) ? Apply(Current with { CsvUtcOffset = offset }) : Reject("時區偏移需介於 UTC−12 與 UTC+14，且為 15 分鐘的倍數。");

    public bool SetDownlinkHz(double hz) =>
        hz is > 0 and < 1e12 ? Apply(Current with { DownlinkHz = hz }) : Reject("下行頻率需大於 0。");

    public bool SetPredictionDays(double days) =>
        days is > 0 and <= 14 ? Apply(Current with { PredictionDays = days }) : Reject("預報天數需介於 0 與 14 天。");

    private bool Apply(AppSettings settings)
    {
        try
        {
            store.Save(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new DiagnosticItem(DiagnosticSeverity.Error, Source, $"設定無法儲存：{ex.Message}"));
            return Reject($"設定無法儲存：{ex.Message}");
        }

        Current = settings;
        LastError = null;
        Version++;
        return true;
    }

    private bool Reject(string message)
    {
        LastError = message;
        return false;
    }
}
