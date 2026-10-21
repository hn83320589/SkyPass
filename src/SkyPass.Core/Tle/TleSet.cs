using SkyPass.Core.Time;

namespace SkyPass.Core.Tle;

/// <summary>
/// 一組已驗證的 TLE。數值皆為 TLE 欄位的原始單位（度、每日圈數），轉換到 SGP4 內部單位由傳播器負責。
/// </summary>
public sealed record TleSet
{
    /// <summary>週期達此值（分鐘）以上視為深空軌道。</summary>
    public const double DeepSpacePeriodMinutes = 225.0;

    public required string Name { get; init; }

    public required int NoradId { get; init; }

    public char Classification { get; init; } = 'U';

    public string InternationalDesignator { get; init; } = "";

    /// <summary>四位數年份。</summary>
    public required int EpochYear { get; init; }

    /// <summary>該年第幾日加小數，1.0 = 1 月 1 日 00:00 UTC。</summary>
    public required double EpochDayOfYear { get; init; }

    /// <summary>平均運動一階導數除以 2（圈/日²）。</summary>
    public double MeanMotionDot { get; init; }

    /// <summary>平均運動二階導數除以 6（圈/日³）。</summary>
    public double MeanMotionDdot { get; init; }

    /// <summary>BSTAR 拖曳項（1/地球半徑）。</summary>
    public double BStar { get; init; }

    public int EphemerisType { get; init; }

    public int ElementSetNumber { get; init; }

    public required double InclinationDeg { get; init; }

    public required double RaanDeg { get; init; }

    public required double Eccentricity { get; init; }

    public required double ArgOfPerigeeDeg { get; init; }

    public required double MeanAnomalyDeg { get; init; }

    public required double MeanMotionRevPerDay { get; init; }

    public int RevolutionNumber { get; init; }

    /// <summary>匯入時的原始第 1 行（由 <see cref="TleFormatter"/> 建立者為格式化結果）。</summary>
    public string Line1 { get; init; } = "";

    public string Line2 { get; init; } = "";

    public JulianDate Epoch => JulianDate.FromYearAndDayOfYear(EpochYear, EpochDayOfYear);

    public DateTime EpochUtc => Epoch.ToUtc();

    public double PeriodMinutes => 1440.0 / MeanMotionRevPerDay;

    public bool IsDeepSpace => PeriodMinutes >= DeepSpacePeriodMinutes;
}
