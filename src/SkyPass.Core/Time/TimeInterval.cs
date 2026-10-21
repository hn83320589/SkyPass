namespace SkyPass.Core.Time;

/// <summary>UTC 時間區間 [Start, End]。</summary>
public readonly record struct TimeInterval(JulianDate Start, JulianDate End)
{
    public double DurationSeconds => End.SecondsSince(Start);
}
