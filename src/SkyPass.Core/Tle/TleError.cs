namespace SkyPass.Core.Tle;

public enum TleErrorKind
{
    /// <summary>行長度不是 69 字元。</summary>
    LineLength,

    /// <summary>行首的行號不是預期的 1 或 2。</summary>
    LineNumber,

    /// <summary>校驗碼不符。</summary>
    Checksum,

    /// <summary>欄位格式錯誤（含應為空白的欄位）。</summary>
    FieldFormat,

    /// <summary>欄位數值超出合理範圍。</summary>
    FieldRange,

    /// <summary>兩行的衛星編號不一致。</summary>
    SatelliteNumberMismatch,

    /// <summary>缺少配對的第 1 行或第 2 行。</summary>
    MissingLine,

    /// <summary>名稱行之後沒有軌道資料。</summary>
    MissingElements,
}

/// <param name="LineNumber">檔案中的實際行號（1 起算）。</param>
public sealed record TleError(int LineNumber, TleErrorKind Kind, string Message)
{
    public override string ToString() => $"第 {LineNumber} 行：{Message}";
}

public sealed record TleParseResult(IReadOnlyList<TleSet> Sets, IReadOnlyList<TleError> Errors);
