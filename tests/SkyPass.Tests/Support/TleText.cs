namespace SkyPass.Tests.Support;

/// <summary>測試用 TLE 文字工具：修改固定欄位並重算校驗碼。</summary>
public static class TleText
{
    public const string IssLine1 = "1 25544U 98067A   08264.51782528 -.00002182  00000-0 -11606-4 0  2927";
    public const string IssLine2 = "2 25544  51.6416 247.4627 0006703 130.5360 325.0288 15.72125391563537";
    public const string VanguardLine1 = "1 00005U 58002B   00179.78495062  .00000023  00000-0  28098-4 0  4753";
    public const string VanguardLine2 = "2 00005  34.2682 348.7242 1859667 331.7664  19.3264 10.82419157413667";

    public static int Checksum(string line)
    {
        var sum = 0;
        foreach (var c in line.AsSpan(0, 68))
        {
            if (char.IsAsciiDigit(c)) sum += c - '0';
            else if (c == '-') sum += 1;
        }
        return sum % 10;
    }

    /// <summary>以 0 起算的欄位位置覆寫文字，並重算校驗碼。</summary>
    public static string Set(string line, int index, string text) =>
        WithChecksum(string.Concat(line.AsSpan(0, index), text, line.AsSpan(index + text.Length)));

    public static string WithChecksum(string line) => string.Concat(line.AsSpan(0, 68), Checksum(line).ToString());
}
