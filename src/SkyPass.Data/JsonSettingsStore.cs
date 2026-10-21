using System.Text.Encodings.Web;
using System.Text.Json;
using SkyPass.Core.Storage;

namespace SkyPass.Data;

/// <summary>以 JSON 檔保存設定。讀取失敗時回傳預設值與警告；寫入先寫暫存檔再取代，避免寫一半。</summary>
public sealed class JsonSettingsStore(string settingsPath) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string SettingsPath { get; } = settingsPath;

    public SettingsLoadResult Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new SettingsLoadResult(AppSettings.Default, []);
        }

        AppSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Options);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new SettingsLoadResult(AppSettings.Default, [$"設定檔 {SettingsPath} 無法讀取（{ex.Message}），已使用預設值。"]);
        }

        if (settings is null)
        {
            return new SettingsLoadResult(AppSettings.Default, [$"設定檔 {SettingsPath} 內容為空，已使用預設值。"]);
        }

        var (sanitized, warnings) = settings.Sanitize();
        return new SettingsLoadResult(sanitized, warnings);
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(SettingsPath))!;
        Directory.CreateDirectory(dir);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Options));
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}

/// <summary>設定檔與資料庫位置：%LocalAppData%\SkyPass\。</summary>
public sealed record AppPaths(string DatabasePath, string SettingsPath)
{
    public static AppPaths Default
    {
        get
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyPass");
            return new AppPaths(Path.Combine(root, "skypass.db"), Path.Combine(root, "settings.json"));
        }
    }
}
