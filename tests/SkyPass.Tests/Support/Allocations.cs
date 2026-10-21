namespace SkyPass.Tests.Support;

public static class Allocations
{
    /// <summary>
    /// 量測 <paramref name="body"/> 在目前執行緒配置的位元組數，重複數輪取最小值。
    /// 執行環境偶發的一次性配置（例如 JIT 分層編譯）只會出現在某一輪；
    /// 熱路徑若每次呼叫都配置，每一輪都會是非零，仍會被測出。
    /// </summary>
    public static long MinimumAllocatedBytes(Action body, int rounds = 3)
    {
        body(); // 暖機
        var min = long.MaxValue;
        for (var r = 0; r < rounds; r++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            body();
            min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return min;
    }
}
