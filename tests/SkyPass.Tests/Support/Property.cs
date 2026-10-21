namespace SkyPass.Tests.Support;

/// <summary>
/// 以固定種子執行隨機性質測試。每個案例使用由 (seed, iteration) 衍生的獨立種子，
/// 失敗時例外訊息會列出可直接重現的種子。
/// </summary>
public static class Property
{
    public static void Run(string name, int seed, int iterations, Action<Random> check)
    {
        for (var i = 0; i < iterations; i++)
        {
            var caseSeed = CaseSeed(seed, i);
            try
            {
                check(new Random(caseSeed));
            }
            catch (Exception ex)
            {
                throw new PropertyFailedException(
                    $"性質 '{name}' 在第 {i} 次失敗。重現：Property.Replay(caseSeed: {caseSeed})（seed={seed}）。原因：{ex.Message}", ex);
            }
        }
    }

    /// <summary>單獨重跑一個失敗案例。</summary>
    public static void Replay(int caseSeed, Action<Random> check) => check(new Random(caseSeed));

    public static int CaseSeed(int seed, int iteration) => unchecked(seed * 1_000_003 + iteration);
}

public sealed class PropertyFailedException(string message, Exception inner) : Exception(message, inner);
