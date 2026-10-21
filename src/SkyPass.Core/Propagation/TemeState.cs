using SkyPass.Core.Mathematics;

namespace SkyPass.Core.Propagation;

/// <summary>TEME 座標系的位置（km）與速度（km/s）。</summary>
public readonly record struct TemeState(Vector3d PositionKm, Vector3d VelocityKmPerSec);

/// <summary>SGP4 錯誤碼；1–6 與 Vallado 參考實作相同。</summary>
public enum Sgp4Error
{
    None = 0,

    /// <summary>平均偏心率 ≥ 1 或 &lt; −0.001。</summary>
    MeanEccentricity = 1,

    /// <summary>平均運動 ≤ 0。</summary>
    MeanMotion = 2,

    /// <summary>攝動後偏心率超出 [0, 1]（僅深空會發生）。</summary>
    PerturbedEccentricity = 3,

    /// <summary>半正焦弦 &lt; 0。</summary>
    SemiLatusRectum = 4,

    /// <summary>衛星已衰減（軌道半徑小於地球半徑）。</summary>
    Decayed = 6,

    /// <summary>深空軌道（週期 ≥ 225 分鐘），本程式不支援。</summary>
    DeepSpaceNotSupported = 100,
}
