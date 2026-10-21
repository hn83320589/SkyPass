namespace SkyPass.Tests.Performance;

/// <summary>效能測試不與其他測試平行執行，避免互搶 CPU 影響量測。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Performance";
}
