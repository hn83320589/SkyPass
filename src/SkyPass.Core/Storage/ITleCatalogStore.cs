using SkyPass.Core.Tle;

namespace SkyPass.Core.Storage;

public enum StoreStatus
{
    /// <summary>正常開啟既有資料庫。</summary>
    Ok,

    /// <summary>資料庫不存在，已建立新的空資料庫。</summary>
    Created,

    /// <summary>資料庫由較新版本的程式建立，無法讀寫。</summary>
    TooNew,

    /// <summary>資料庫檔案損毀或不是資料庫。</summary>
    Corrupt,
}

/// <param name="Warnings">個別資料列的問題（例如無法解析的 TLE），不影響其他資料。</param>
public sealed record CatalogLoadResult(TleCatalog Catalog, StoreStatus Status, string? Message, IReadOnlyList<string> Warnings)
{
    public bool IsWritable => Status is StoreStatus.Ok or StoreStatus.Created;
}

/// <summary>軌道資料目錄的持久化。實作需在 <see cref="CatalogLoadResult.IsWritable"/> 為 false 時拒絕寫入。</summary>
public interface ITleCatalogStore
{
    CatalogLoadResult Load();

    /// <summary>以單一交易覆寫整個目錄。資料庫不可寫時擲出 <see cref="InvalidOperationException"/>。</summary>
    void Save(TleCatalog catalog);
}
