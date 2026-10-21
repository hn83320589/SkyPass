# SkyPass 過境預報台 — 系統分析與架構設計（v1.0）

> 狀態：第 9 節決策已確認並依 M0–M10 實作完成。實作細節與實測數據以 README 為準；
> 與本文件的差異：即時追蹤快取涵蓋「現在 −1 h 至 +24 h」、天空圖軌跡在 UI 執行緒計算（< 1 ms）、
> 指令列多了 `--data/--import/--now/--screenshot` 以重現截圖。

---

## 1. 需求分析摘要

### 1.1 使用情境
| 角色 | 情境 | 系統行為 |
|---|---|---|
| 社員 | 從 CelesTrak 等網站下載 TLE 檔 | 匯入、逐組驗證，錯誤組指出行號與原因，其餘照常匯入 |
| 社員 | 週末要收 NOAA / ISS | 選觀測點、時間範圍、遮罩 → 背景預報 → 過境表、天空圖、24h 時間軸 |
| 操作天線者 | 衛星正在過境 | 即時追蹤頁每秒更新方位、仰角、距離、距離變化率、都卜勒頻率、AOS 倒數 |
| 目視觀測者 | 想用肉眼看 ISS | 過境表標示可目視起訖時間（衛星受日照且太陽仰角 < −6°） |
| 任何人 | 匯出給其他社員 | CSV（UTF-8 BOM），可選輸出時區 |

### 1.2 非功能需求重點
- **離線**：完全不呼叫網路服務。
- **正確性**：SGP4 對 Vallado 參考實作誤差 ≤ 1e-6 km / 1e-9 km/s → 必須「逐行忠實移植」，連浮點運算順序都要一致。
- **不漏過境**：最大仰角 ≥ 遮罩 + 0.05° 的過境一個都不能漏。
- **效能**：100 顆 × 7 天 ≤ 2 秒（8 核）；即時追蹤單次更新 ≤ 1 ms；SGP4 熱路徑零配置、執行緒安全。
- **可測性**：所有計算、版面、座標放在不引用 UI 的函式庫；「現在」一律 `TimeProvider`。

---

## 2. 解決方案結構

```
SkyPass.sln
├─ src/
│  ├─ SkyPass.Core         (net9.0)  純計算：TLE、SGP4、時間座標、太陽陰影、過境、都卜勒、天空圖/時間軸幾何、CSV 格式化
│  ├─ SkyPass.Data         (net9.0)  SQLite（Microsoft.Data.Sqlite 9.0.x）、設定檔 JSON
│  ├─ SkyPass.Presenters   (net9.0)  各畫面狀態、背景計算協調、取消、進度
│  └─ SkyPass.App          (net9.0, win-x64)  Silk.NET + ImGui.NET 繪製與事件轉送（composition root）
├─ tests/
│  └─ SkyPass.Tests        (xUnit)   單元 / 性質 / 效能測試、暴力過境參考解、參考資料檔
├─ tools/
│  └─ refgen/              Python 腳本（離線產生 SGP4 / GMST / 座標參考資料，pinned 版本）
├─ samples/                30+ 組 TLE、預期過境表
├─ docs/                   設計文件、逐步手算範例
└─ .github/workflows/ci.yml
```

### 2.1 相依方向

```
            ┌──────────────┐
            │  SkyPass.App │  (Silk.NET, ImGui.NET)
            └──┬───────┬───┘
               │       │
     ┌─────────▼──┐  ┌─▼────────────┐
     │ Presenters │  │     Data     │  (Microsoft.Data.Sqlite)
     └─────┬──────┘  └──────┬───────┘
           │                │
           └──────┬─────────┘
           ┌──────▼──────┐
           │    Core     │  (無任何外部套件)
           └─────────────┘
```

- **Repository 介面（`ITleCatalogStore`、`ISettingsStore`）定義在 Core**，Data 實作，App 組裝。Presenters 只依賴 Core 介面 → Presenter 測試用 in-memory fake，不碰 SQLite。
- App 只做：讀 Presenter 快照 → ImGui 繪製；輸入事件 → 呼叫 Presenter 方法。不含任何計算。

---

## 3. Core 模組設計

### 3.1 基本型別（全部 `readonly struct`，熱路徑零配置）

| 型別 | 內容 | 說明 |
|---|---|---|
| `Vector3d` | X, Y, Z (double) | 運算子多載、Dot、Cross、Norm |
| `JulianDate` | `Day`（整數 + .5）、`Fraction` | **兩段式儒略日**。單一 double 的 JD 解析度約 40 µs，會讓 GMST 誤差達 ~3e-9 rad，超過 1e-9 rad 的驗收門檻；兩段式與 Vallado/python-sgp4 的 `jd, fr` 一致 |
| `TemeState` / `EcefState` | 位置 km、速度 km/s | 用型別區分座標系，避免混用 |
| `Geodetic` | `LatitudeDeg`, `LongitudeDeg`, `HeightKm` | 公開 API 角度一律「度」，命名帶單位 |
| `Topocentric` | `AzimuthDeg`, `ElevationDeg`, `RangeKm`, `RangeRateKmPerSec` | |

命名規則：公開 API 角度一律 `...Deg`；內部弧度變數以 `...Rad` 結尾。

### 3.2 TLE（`SkyPass.Core.Tle`）
- `TleParser.Parse(string text) → TleParseResult { IReadOnlyList<TleSet> Sets; IReadOnlyList<TleError> Errors }`
  - 前處理：去 BOM、CRLF/LF 正規化、記錄原始行號。
  - 分組：遇到以 `1 ` 開頭且下一行以 `2 ` 開頭 → 兩行組；前一行若非 `1 `/`2 ` 開頭則視為名稱行（三行組）。
  - 逐組驗證：行長度（69）、行號、校驗碼（數字取值、`-` 計 1）、兩行 NORAD 一致、各欄位格式與範圍（傾角 0–180、RAAN 0–360、偏心率 0–1、平均運動 > 0…）。
  - `TleError { int LineNumber; TleErrorKind Kind; string Message }`，錯誤組跳過，其他照常。
- 隱含小數點偏心率、`±NNNNN±E` 指數格式、epoch 年份 57 分界，依假設事項。
- `TleFormatter.Format(TleSet)`：可往返（性質測試：解析→格式化→解析不變）。
- 衍生屬性：`PeriodMinutes = 1440 / MeanMotion`、`IsDeepSpace = Period ≥ 225`。

### 3.3 目錄規則（`TleCatalog`，純邏輯，不碰資料庫）
- 同 NORAD 多組：依 epoch 排序，保留最新 5 組（同 epoch 去重）；「目前使用」預設為最新，使用者可釘選歷史中任一組。
- 深空軌道：標示 `Unsupported`，不進預測。
- 陳舊：`|預測時間 − epoch| > 14 天` → `StaleWarning`。

### 3.4 SGP4（`SkyPass.Core.Propagation`）
- **逐行移植 Vallado `sgp4unit.cpp`（2006 版 + 後續修正）的近地分支**：`initl`、`sgp4init`（method = 'n'）、`sgp4`。WGS-72、opsmode 'i'、近地點 < 220 km 時 `isimp = 1`。
- `Sgp4Propagator`：**不可變 sealed class**，建構時完成 `sgp4init`（只在初始化配置一次）。
- 熱路徑 API：
  ```csharp
  Sgp4Error Propagate(double tsinceMinutes, out TemeState state);  // 無配置、無例外、執行緒安全
  ```
  Vallado 原碼在 `sgp4()` 中會寫回 satrec 欄位（`am, em, mm…`），移植時全部改為區域變數 → 物件不可變 → 執行緒安全。
- 錯誤碼對應 Vallado：`MeanEccentricity(1)`、`MeanMotion(2)`、`PerturbedEccentricity(3)`、`SemiLatusRectum(4)`、`Decayed(6)`；初始化時若為深空軌道回傳 `DeepSpaceNotSupported`。
- `tsince = ((jd.Day − epoch.Day) + (jd.Fraction − epoch.Fraction)) × 1440`。

### 3.5 時間與座標（`SkyPass.Core.Time`, `SkyPass.Core.Coordinates`）
全部照假設事項公式：
- `JulianDate.FromUtc(DateTime)` / `ToUtc()`（格里曆演算法）。
- `Gmst.ComputeRad(JulianDate)`（IAU-82）。
- `Frames.TemeToEcef(in TemeState, double gmstRad) → EcefState`（含 ω × r 項）。
- `Geodesy.ToEcef(Geodetic)`（WGS-84）。
- `Topocentric.Compute(in EcefState sat, in ObserverSite site)`：`ObserverSite` 預先算好 sinφ、cosφ、sinλ、cosλ 與 ECEF，熱路徑不重算三角函數。
- 仰角時間導數（解析式，供過境搜尋用）：`dEl/dt = (Ż·ρ − Z·ρ̇) / (ρ²·cos El)`，Ż 為天頂方向速度分量。

### 3.6 太陽與陰影（`SkyPass.Core.Sun`）
- `SunPosition.UnitVectorTeme(JulianDate)`（低精度公式）、`SunPosition.ElevationDeg(site, jd)`。
- `Shadow.IsInShadow(r, ŝ)`（圓柱模型）。
- `Shadow.FindTransitions(propagator, t0, t1)`：連續函數 `s(t) = |r⊥| − Re`（僅在 r·ŝ < 0 側有意義）取樣後以 Brent 法求根，容差 1 ms → 滿足 ≤ 0.1 s。

### 3.7 過境搜尋（`SkyPass.Core.Passes`）— 核心演算法

**目標**：不漏 ≥ 遮罩 + 0.05° 的過境、快、可視窗拆分。

1. **全域對齊取樣格點**：取樣時刻 = Unix epoch 起每 Δ 秒（預設 Δ = 30 s）的整數倍，而非從視窗起點起算。→ `[t0,t1] + [t1,t2]` 與 `[t0,t2]` 用到同一批樣本，拆分不變性自然成立。
2. 每個樣本計算 `f(t) = El(t) − mask` 與解析導數 `f'(t)`（同一次 SGP4 呼叫即可得，無額外成本）。
3. **事件偵測**（相鄰樣本 a, b）：
   - `f` 變號 → AOS 或 LOS，Brent 求根（容差 1 ms）。
   - `f'` 由 + 變 − → 區間內有局部極大，Brent 求 `f'` 的根 → 極大時刻與值；若 `f_max ≥ 0` 而兩端 `f < 0`，這就是一個「掠過過境」，再分別在 [a, t_max]、[t_max, b] 求 AOS/LOS。**這一步就是避免漏掉掠過過境的關鍵**：只看 f 的符號會漏掉在兩個樣本之間短暫升起又落下的過境，但看導數變號不會。
   - 漏判的唯一可能是同一 Δ 內出現兩個仰角極值（一個極大 + 一個極小）。LEO 仰角曲線的相鄰極值至少相隔數分鐘，Δ = 30 s 有充足餘裕；此論點以暴力參考解（1 s 取樣）的 300+ 組隨機性質測試與 50+ 組刻意構造的掠過案例驗證。
4. **邊界**：`f(t0) ≥ 0` → AOS = t0 並標記 `StartedBeforeWindow`；`f(t_end) ≥ 0` → LOS = t_end 並標記 `EndsAfterWindow`。最大仰角取「視窗內」的最大值。
5. **衰減**：遇到 SGP4 錯誤 → 在最後成功樣本與失敗樣本間二分出衰減時刻（容差 1 s），停止該衛星並產生 `PredictionDiagnostic`。
6. **效能估算**：100 顆 × 7 天 / 30 s ≈ 2.0M 次（SGP4 + 座標轉換），單次約 1 µs → 單執行緒 ~2 s，8 執行緒 ~0.3 s，留有餘裕。必要時再加「依仰角角速度上界安全跳步」優化（先不做）。

**平行化**：`Parallel.For` 以「衛星」為單位，每顆的計算是純循序且決定性 → 結果寫進以索引定位的陣列，最後依 (AOS, NORAD) 穩定排序 → 1 與 N 個執行緒逐位元相同。全部完成才一次回傳；取消時丟棄陣列 → 不會有半套結果。

### 3.8 都卜勒與可目視（`SkyPass.Core.Doppler`, `SkyPass.Core.Visibility`）
- `Doppler.ReceivedHz(f0Hz, rangeRateKmPerSec) = f0 × (1 − ṙ/c)`；`Doppler.Curve(pass, f0, step)`。
- 可目視區段 = 過境 [AOS, LOS] ∩ 受日照區段 ∩ 太陽仰角 < −6° 區段；邊界皆以求根細化。

### 3.9 幾何（`SkyPass.Core.Layout`）
- `SkyPlotGeometry`：中心天頂、`r = R·(90 − El)/90`、`x = cx − r·sin Az`（東在左）、`y = cy − r·cos Az`（北在上，螢幕 y 向下）；遮罩圈；軌跡取樣（依角度變化自適應，保證相鄰點螢幕距離 ≤ N px）。
- `TimelineGeometry`：時間 → x 線性映射；各衛星過境區段（同列合併、不重疊）；夜晚／曙暮光區段（太陽仰角 0°、−6°、−12°、−18°）。
- DPI：所有座標由「精確值 × scale 後各自四捨五入」，不做累加 → 無累積誤差。

### 3.10 CSV（`SkyPass.Core.Export`）
- 欄位：衛星名稱、NORAD、AOS、AOS 方位、最大仰角時刻、最大仰角、LOS、LOS 方位、持續秒數、邊界旗標、可目視起訖。
- 時區偏移參數化；UTF-8 with BOM；RFC 4180 跳脫。

---

## 4. Presenters 設計

| Presenter | 職責 |
|---|---|
| `CatalogPresenter` | 匯入（背景）、錯誤清單、歷史選擇、深空/陳舊標示 |
| `PredictionPresenter` | 啟動/取消預報、進度（單調）、結果快照、診斷 |
| `PassTablePresenter` | 排序、篩選、選取列 → 發出 `SelectedPassChanged` |
| `SkyPlotPresenter` | 選定過境的軌跡（含日照/陰影/可目視著色）、目前位置 |
| `TimelinePresenter` | 24h 區段、晝夜區段 |
| `LiveTrackingPresenter` | 每秒 `Tick()`：1 次 SGP4 + 轉換；下一次 AOS 由背景預先算好的過境快取提供；偵測衛星切換、時間回溯、TLE 更新時重建快取 |
| `SettingsPresenter` | 觀測點（最多 5）、遮罩、時區偏移（預設 UTC+8）、頻率 |
| `DiagnosticsPresenter` | 匯入錯誤、衰減診斷、設定/資料庫錯誤訊息 |

**執行緒模型**：
- 背景工作用 `Task.Run` + `CancellationToken`；結果以**不可變快照**透過 `Volatile.Write` 換參考發佈。
- UI 每幀讀快照（ImGui 為 immediate mode，本來就每幀重畫）→ 不需要 SynchronizationContext，Presenter 完全可在測試中同步驅動。
- 進度：`MonotonicProgress` 包裝，以 `Interlocked` 取最大值，保證單調遞增。
- 時間：所有 Presenter 透過注入的 `TimeProvider`；測試用 `FakeTimeProvider`（自寫，避免新增套件）。

---

## 5. Data 設計

### 5.1 SQLite（`%LocalAppData%\SkyPass\skypass.db`）
```sql
-- PRAGMA user_version = 1
CREATE TABLE satellite (
  norad_id        INTEGER PRIMARY KEY,
  name            TEXT NOT NULL,
  pinned_tle_id   INTEGER NULL REFERENCES tle_set(id)
);
CREATE TABLE tle_set (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  norad_id    INTEGER NOT NULL REFERENCES satellite(norad_id) ON DELETE CASCADE,
  epoch_utc   TEXT    NOT NULL,     -- ISO 8601，僅供查詢排序
  line0       TEXT NULL,
  line1       TEXT NOT NULL,        -- 原文保存 → 往返無損
  line2       TEXT NOT NULL,
  imported_at TEXT NOT NULL,
  UNIQUE(norad_id, line1, line2)
);
```
- 保存 TLE 原文、讀回時重新解析 → 往返無損。
- `user_version` > 支援版本 → 回傳 `StoreStatus.TooNew`，App 顯示明確訊息、以唯讀/空目錄運作；檔案損毀（`SQLITE_NOTADB` / `SQLITE_CORRUPT`）→ `StoreStatus.Corrupt`，不崩潰。
- 匯入在單一交易內執行。

### 5.2 設定檔（`%LocalAppData%\SkyPass\settings.json`，System.Text.Json）
- 觀測點 ×5、目前觀測點、遮罩、時區偏移、下行頻率、CSV 輸出時區。
- 損毀或欄位不合法 → 回傳預設值 + `SettingsLoadWarning`，由診斷頁與提示列顯示。
- 寫入先寫暫存檔再 `File.Replace`，避免寫一半。

---

## 6. App（Silk.NET + ImGui.NET）
- 套件：`Silk.NET.Windowing`、`Silk.NET.Input`、`Silk.NET.OpenGL`、`Silk.NET.OpenGL.Extensions.ImGui`（內含 ImGui.NET 相依），OpenGL 3.3 Core。
- 版面：左側導覽（過境表／天空圖／時間軸／即時追蹤／軌道資料／設定／診斷），過境表與天空圖可同時顯示並聯動。
- 天空圖與時間軸以 `ImDrawList` 畫 Core 算好的折線/矩形，App 不做任何座標計算。
- 中文字型：執行時載入系統字型 `C:\Windows\Fonts\msjh.ttc`（微軟正黑體）；找不到時退回 ImGui 預設字型並改顯示英文標籤（不打包字型檔）。
- 截圖：提供 `--screenshot <path>` 參數，渲染一幀後以 `glReadPixels` 存 PNG，供 README 使用（PNG 編碼自寫最小實作，不加套件）。

---

## 7. 測試策略（對應驗收標準）

| # | 測試 | 資料來源 |
|---|---|---|
| 1, 2 | TLE 解析、目錄規則單元測試 | 手寫案例 |
| 3 | SGP4 已發表案例 | Vallado `SGP4-VER.TLE`（見待決事項 Q1） |
| 4 | SGP4 300+ 隨機近地軌道 × ≥5 時間點 | `tools/refgen` 以 python-sgp4 產生，提交 CSV |
| 5 | 衰減 | 會衰減的構造 TLE + 參考資料（python-sgp4 錯誤碼） |
| 6, 7 | 時間、GMST、座標 ≥100 組 | refgen（GMST 用 `sgp4.propagation.gstime`；座標以 Python 獨立實作） |
| 8, 14 | 太陽、陰影、可目視 | refgen + 手算範例 |
| 9, 11, 12 | 過境 vs 暴力參考解、視窗拆分、過境屬性 | 性質測試，固定種子 |
| 10 | 掠過過境 ≥50 組 | 先用暴力解找一個過境的最大仰角 E，再設遮罩 = E − δ，δ ∈ [0.05°, 0.5°] |
| 13 | 都卜勒 | 手算 + refgen |
| 15 | 天空圖、時間軸幾何 | 單元測試 |
| 16 | 平行／取消／進度 | 1 vs N 執行緒逐位元比較；取消後結果為 null |
| 17, 20 | 效能 | `Stopwatch` + `GC.GetAllocatedBytesForCurrentThread()` |
| 18 | 持久化 | 暫存目錄中的 SQLite／JSON；故意寫壞檔案 |
| 19 | CSV | 單元測試 |

- **暴力過境參考解**（`tests/SkyPass.Tests/Reference/BruteForcePassFinder.cs`）：逐秒取樣仰角 → 偵測跨越 → 二分法細化 AOS/LOS（1 ms）→ 在最高的秒附近用三分法找最大值。只共用 `Sgp4Propagator` 與座標轉換，不引用 `SkyPass.Core.Passes` 任何程式碼。
- **性質測試**：自寫 `Property.Run(seed, iterations, Action<Random>)` 小工具，失敗時訊息包含 `seed` 與迭代序號以利重現（不引入 FsCheck）。
- **效能測試**：`[Trait("Category","Performance")]`。2 秒門檻以 8 核為準；CI runner 若核心數 < 8，門檻按比例放寬並在輸出記錄實測值（README 列出本機 24 執行緒與 8 執行緒的實測數據）。

---

## 8. 開發里程碑（每個都能編譯、測試通過、獨立 commit）

| M | 內容 | 驗收 # |
|---|---|---|
| 0 | Solution 骨架、CI（build + test + publish artifact）、.editorconfig | 24（部分） |
| 1 | Vector3d、JulianDate、GMST、TLE 解析/格式化、目錄規則 | 1, 2, 6 |
| 2 | refgen 工具 + SGP4 移植 + 參考資料測試 + 零配置測試 | 3, 4, 5, 20（部分） |
| 3 | 座標轉換、太陽、陰影 | 7, 8 |
| 4 | 暴力參考解 → 過境搜尋 → 性質測試 | 9, 10, 11, 12 |
| 5 | 都卜勒、可目視、多衛星平行預報、取消與進度 | 13, 14, 16, 20 |
| 6 | Data（SQLite、設定檔） | 18 |
| 7 | 幾何（天空圖、時間軸）、CSV | 15, 19 |
| 8 | Presenters（含即時追蹤） | 17, 22 |
| 9 | App（Silk.NET + ImGui） | — |
| 10 | 範例資料、預期過境表、手算範例、README、截圖、效能數據 | 23 |

---

## 9. 已確認決策

| # | 決策 |
|---|---|
| Q1 | 驗收 #3：使用 SGP4-VER 全部近地 TLE + 公開真實 TLE 補足 ≥ 20 組；期望值以 python-sgp4 完整精度產生，另以 `tcppver.out` 印出精度交叉比對 |
| Q2 | `tools/refgen` 使用 Python + `sgp4`（固定版本），僅離線產生參考資料，正式程式與 CI 不需要 Python |
| Q3 | 中文介面，執行時載入系統字型 `msjh.ttc`，缺字型時退回英文 |
| Q4 | GitHub repo：先在本機建 repo 與 CI 設定，推送前再確認 owner/name |
| 其他 | 單一測試專案；自寫性質測試工具與 FakeTimeProvider；可目視「至少 1 秒」以總和計；最大仰角取視窗內最大值 |

## 10. 已知風險
- **SGP4 1e-9 km/s 門檻**：需與參考實作的浮點運算順序完全一致；若 python-sgp4 加速版（C++）與純 Python 版之間本身就有 1e-12 等級差異，以 C++ 版為準。
- **陰影進出時刻**：圓柱模型在 beta 角大時陰影很短；取樣步長需小於最短陰影段，必要時改用解析解（圓柱與軌道交點）。
- **CJK 字型**：依賴系統字型，非中文 Windows 可能缺字 → 退回英文。
