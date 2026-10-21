using SkyPass.Core.Propagation;
using SkyPass.Core.Time;

namespace SkyPass.Core.Passes;

/// <summary>一次過境。角度以度為單位，方位角範圍 [0, 360)。</summary>
public sealed record SatellitePass
{
    public required int NoradId { get; init; }

    public required string SatelliteName { get; init; }

    /// <summary>仰角升到遮罩的時刻；<see cref="StartedBeforeWindow"/> 時為時間範圍起點。</summary>
    public required JulianDate Aos { get; init; }

    public required double AosAzimuthDeg { get; init; }

    /// <summary>時間範圍內的最大仰角時刻。</summary>
    public required JulianDate MaxElevationTime { get; init; }

    public required double MaxElevationDeg { get; init; }

    public required double MaxElevationAzimuthDeg { get; init; }

    /// <summary>仰角降到遮罩以下的時刻；<see cref="EndsAfterWindow"/> 時為時間範圍終點。</summary>
    public required JulianDate Los { get; init; }

    public required double LosAzimuthDeg { get; init; }

    /// <summary>時間範圍起點時衛星已在遮罩之上（「起點前已開始」）。</summary>
    public bool StartedBeforeWindow { get; init; }

    /// <summary>時間範圍終點時衛星尚未落下（「終點後才結束」）。</summary>
    public bool EndsAfterWindow { get; init; }

    /// <summary>過境途中衛星衰減，LOS 為最後一個可傳播的時刻。</summary>
    public bool EndedByDecay { get; init; }

    /// <summary>可目視區段（衛星受日照且太陽仰角 &lt; −6°）。</summary>
    public IReadOnlyList<TimeInterval> VisibleIntervals { get; init; } = [];

    public double DurationSeconds => Los.SecondsSince(Aos);
}

public enum DiagnosticKind
{
    /// <summary>衛星衰減，預測在衰減時刻停止。</summary>
    Decayed,

    /// <summary>其他傳播錯誤（偏心率超出範圍等），預測在該時刻停止。</summary>
    PropagationError,

    /// <summary>深空軌道，不支援預測。</summary>
    Unsupported,

    /// <summary>預測時間與 epoch 相差超過 14 天。</summary>
    StaleElements,
}

public sealed record PredictionDiagnostic(int NoradId, string SatelliteName, DiagnosticKind Kind, JulianDate? Time, Sgp4Error Error, string Message);

/// <param name="Diagnostic">預測提早停止或無法進行時的原因；正常完成為 null。</param>
public sealed record PassSearchResult(IReadOnlyList<SatellitePass> Passes, PredictionDiagnostic? Diagnostic);
