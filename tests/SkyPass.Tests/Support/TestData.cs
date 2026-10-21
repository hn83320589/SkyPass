using System.Globalization;

namespace SkyPass.Tests.Support;

public static class TestData
{
    public static string PathOf(string fileName) => Path.Combine(AppContext.BaseDirectory, "TestData", fileName);

    /// <summary>讀取簡單 CSV（無引號欄位），略過表頭。</summary>
    public static IEnumerable<string[]> ReadCsv(string fileName) =>
        File.ReadLines(PathOf(fileName)).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(','));

    public static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    public static DateTime Utc(string iso) =>
        DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
}
