using System.Globalization;
using SkyPass.Data;

namespace SkyPass.App;

/// <summary>
/// 進入點。參數（皆為選用）：
/// <list type="bullet">
/// <item><c>--data &lt;目錄&gt;</c>：改用指定目錄存放資料庫與設定檔（預設 %LocalAppData%\SkyPass\）。</item>
/// <item><c>--import &lt;TLE 檔&gt;</c>：啟動後匯入。</item>
/// <item><c>--now &lt;ISO 8601 UTC&gt;</c>：以指定時刻作為「現在」（之後照實際經過時間前進），用於重現截圖。</item>
/// <item><c>--screenshot &lt;目錄&gt;</c>：預報完成後依序擷取各頁畫面為 PNG 並結束。</item>
/// </list>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var options = AppOptions.Parse(args);
        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            return 2;
        }

        using var host = new AppHost(options);
        host.Run();
        return 0;
    }
}

internal sealed record AppOptions(AppPaths Paths, string? ImportPath, DateTimeOffset? Now, string? ScreenshotDirectory, string? Error)
{
    public static AppOptions Parse(string[] args)
    {
        var paths = AppPaths.Default;
        string? import = null;
        string? screenshot = null;
        DateTimeOffset? now = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"參數 {args[i]} 缺少值。");
            try
            {
                switch (args[i])
                {
                    case "--data":
                        var dir = Next();
                        paths = new AppPaths(Path.Combine(dir, "skypass.db"), Path.Combine(dir, "settings.json"));
                        break;
                    case "--import":
                        import = Next();
                        break;
                    case "--now":
                        now = DateTimeOffset.Parse(Next(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                        break;
                    case "--screenshot":
                        screenshot = Next();
                        break;
                    default:
                        return new AppOptions(paths, null, null, null, $"未知的參數：{args[i]}");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                return new AppOptions(paths, null, null, null, ex.Message);
            }
        }

        return new AppOptions(paths, import, now, screenshot, null);
    }
}

/// <summary>從指定時刻開始、照實際經過時間前進的時鐘（用於重現截圖）。</summary>
internal sealed class ShiftedTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly long _origin = TimeProvider.System.GetTimestamp();

    public override DateTimeOffset GetUtcNow() => start + TimeProvider.System.GetElapsedTime(_origin);
}
