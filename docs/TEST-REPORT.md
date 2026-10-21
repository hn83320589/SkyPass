# SkyPass 測試報告

| 項目 | 內容 |
|---|---|
| 版本 | `main` 分支，本報告隨同一個 commit 提交 |
| 測試日期 | 2026-10-21 |
| 測試環境 | Windows 11 專業版、Intel Core i7-13700（24 個邏輯處理器）、.NET SDK 9.0.315、Release 建置 |
| 測試框架 | xUnit 2.9.2、Microsoft.NET.Test.Sdk 17.12.0 |
| 指令 | `dotnet build SkyPass.sln -c Release` → `dotnet test SkyPass.sln -c Release --no-build` |
| **結論** | **建置 0 警告、0 錯誤；384 個測試全部通過（0 失敗、0 略過），耗時約 26 秒；GitHub Actions 綠燈。24 條驗收標準均符合** |

## 目錄

1. [測試範圍與方法](#1-測試範圍與方法)
2. [測試結果總表](#2-測試結果總表)
3. [驗收標準逐項檢查](#3-驗收標準逐項檢查)
4. [規格限制檢查](#4-規格限制檢查)
5. [交付物檢查](#5-交付物檢查)
6. [效能量測](#6-效能量測)
7. [本輪檢查發現並修正的問題](#7-本輪檢查發現並修正的問題)（含 [7.1 外部審查意見與處理](#71-外部審查意見與處理)）
8. [CI](#8-ci)
9. [未涵蓋範圍與風險](#9-未涵蓋範圍與風險)
10. [重現方式](#10-重現方式)

---

## 1. 測試範圍與方法

| 類型 | 說明 |
|---|---|
| 單元測試 | Core（TLE、SGP4、時間座標、太陽陰影、過境、都卜勒、可目視、幾何、CSV）、Data（SQLite、設定檔）、Presenters（各畫面狀態與協調） |
| 參考資料比對 | 由 `tools/refgen` 以 python-sgp4 2.27（Vallado C++）與 Python 獨立實作的公式**離線**產生並提交（見 [tools/refgen/README.md](../tools/refgen/README.md)） |
| 性質測試 | 16 組，`Property.Run(名稱, 種子, 次數, 檢查)`。每個案例以 (種子, 序號) 衍生獨立種子；失敗時訊息列出 `Property.Replay(caseSeed)` 可直接重現 |
| 獨立參考解 | `tests/SkyPass.Tests/Reference/BruteForcePassFinder.cs`：逐秒取樣、二分法、黃金分割；只共用傳播與座標轉換，不引用 `SkyPass.Core.Passes` |
| 效能測試 | `Category=Performance`，放在不與其他測試平行的集合（`PerformanceCollection`），避免互搶 CPU |
| 架構測試 | 以反射檢查分層與「公開 API 不得出現弧度」；掃描原始碼檢查「不得直接讀取系統時鐘」 |
| 整合測試 | `MainPresenterTests`：啟動、預報、選取聯動、設定變更重新預報、匯入、CSV 匯出、資料庫損毀時啟動 |
| 畫面驗證 | App 以 `--screenshot` 模式實際啟動、渲染各頁並擷取 PNG（`docs/screenshots/`），人工檢視版面與聯動（非自動化斷言，見 §9） |

## 2. 測試結果總表

| 測試類別 | 案例數 | 失敗 | 耗時 (s) | 對應驗收 |
|---|---:|---:|---:|---|
| Architecture.LayeringTests | 4 | 0 | 0.2 | 技術限制 |
| Core.Tle.TleParserTests | 58 | 0 | 0.0 | #1（含 Alpha-5） |
| Core.Tle.TleFormatterTests | 10 | 0 | 0.2 | #1、#21 |
| Core.Tle.TleCatalogTests | 16 | 0 | 0.1 | #2 |
| Core.Propagation.Sgp4ReferenceTests | 26 | 0 | 0.2 | #3、#4、#5 |
| Core.Propagation.Sgp4PropagatorTests | 5 | 0 | 0.2 | #5、#20 |
| Core.Time.JulianDateTests | 29 | 0 | 0.0 | #6、#21 |
| Core.Time.GmstTests | 3 | 0 | 0.0 | #6 |
| Core.Coordinates.CoordinateReferenceTests | 1（132 組資料） | 0 | 0.0 | #7 |
| Core.Coordinates.CoordinateTests | 25 | 0 | 0.1 | #7、#20、#21 |
| Core.Sun.SunAndShadowTests | 5 | 0 | 0.1 | #8 |
| Core.Sun.ShortShadowTests | 5 | 0 | 7.6 | #8、#14（擦邊短陰影） |
| Core.Passes.PassFinderPropertyTests | 5 | 0 | 10.9 | #9、#10、#11、#12、#21 |
| Core.Passes.PassFinderTests | 10 | 0 | 0.1 | #5、#11 |
| Core.Passes.PassPredictorTests | 7 | 0 | 1.1 | #16、#21 |
| Core.Doppler.DopplerTests | 6 | 0 | 0.1 | #13 |
| Core.Visibility.VisibilityTests | 7 | 0 | 4.1 | #14 |
| Core.Layout.SkyPlotGeometryTests | 12 | 0 | 0.1 | #15 |
| Core.Layout.TimelineGeometryTests | 17 | 0 | 0.1 | #15 |
| Core.Export.PassCsvExporterTests | 10 | 0 | 0.0 | #19 |
| Core.Mathematics.UnitConversionPropertyTests | 1 | 0 | 0.0 | #21 |
| Core.Mathematics.Vector3dTests | 1 | 0 | 0.0 | — |
| Core.HandCalculationTests | 51 | 0 | 0.1 | 交付物（手算範例） |
| Data.StorageTests | 15 | 0 | 0.8 | #18 |
| Presenters.PredictionPresenterTests | 5 | 0 | 0.4 | #16 |
| Presenters.PassTablePresenterTests | 11 | 0 | 0.0 | 畫面需求 |
| Presenters.SettingsPresenterTests | 8 | 0 | 0.0 | 畫面需求 |
| Presenters.CatalogPresenterTests | 5 | 0 | 0.2 | #1、#2 |
| Presenters.DiagnosticsPresenterTests | 1 | 0 | 0.0 | 畫面需求 |
| Presenters.LiveTrackingPresenterTests | 6 | 0 | 0.2 | #17 |
| Presenters.TimelinePresenterTests | 3 | 0 | 0.2 | #15 |
| Presenters.MainPresenterTests | 10 | 0 | 0.6 | #18、#19、聯動 |
| Samples.SampleDataTests | 2 | 0 | 0.1 | 交付物（預期過境表） |
| Performance.PredictionPerformanceTests | 1 | 0 | 0.4 | #20 |
| Performance.LiveTrackingPerformanceTests | 1 | 0 | 0.0 | #17 |
| Performance.PerformanceReportTests | 2 | 0 | 14.3 | #20、README 數據 |
| **合計** | **384** | **0** | | |

Core 與 Presenter 的案例（不含 Data、架構、效能、範例資料）共 359 個，符合 #22「至少 200 個」。

## 3. 驗收標準逐項檢查

判定：✅ 符合。「證據」欄為主要測試；數值皆取自本次 Release 執行。

| # | 標準（摘要） | 判定方式 | 證據與實測 | 判定 |
|---|---|---|---|:-:|
| 1 | TLE 解析：隱含小數點偏心率、BSTAR／二階項尾數與指數、負號、57 分界、校驗碼（連字號計 1）、長度與行號錯誤、三行與兩行；錯誤組別有行號與原因，其餘照常匯入 | 單元測試 | `TleParserTests` 58 案例：Alpha-5 編號（含跳過 I／O 與不合法字元）、各欄位值、`Checksum_counts_minus_sign_as_one`、`Two_digit_epoch_year_pivots_at_57`（57/99/00/56）、16 種欄位錯誤皆斷言「行號 + 類型」、`Missing_line_two_and_orphans_are_reported_and_others_imported`、UTF-8 含／不含 BOM、CRLF/LF | ✅ |
| 2 | 同編號保留較新 epoch、歷史最多 5 組、深空不支援、陳舊警告 | 單元測試 | `TleCatalogTests` 16 案例：匯入順序無關、上限 5、較舊者捨棄、釘選與自動解除、週期恰為 225 分為深空、14 天邊界（14.0 不陳舊／14.0001 陳舊）；另有 500 次隨機性質測試 | ✅ |
| 3 | SGP4 與已發表案例 ≥ 20 組（近圓、高偏心率、低／高傾角、極低近地點、逆行），位置 ≤ 1e-6 km、速度 ≤ 1e-9 km/s | 單元測試 | 23 組（SGP4-VER 9 組 + CelesTrak 真實 14 組）、739 個時間點全部在容差內；`Published_cases_cover_required_orbit_classes` 斷言涵蓋 e < 0.001、e > 0.1、i < 30°、i > 90°、近地點 < 220 km、衰減；另與 Vallado `tcppver.out` 交叉比對 | ✅ |
| 4 | 離線參考資料 ≥ 300 組隨機近地軌道 × ≥ 5 時間點 | 單元測試 | `Random_reference_orbits_match`：320 組 × 6 點 = 1920 筆，含 107 筆錯誤碼案例，全部一致 | ✅ |
| 5 | 會衰減的軌道：衰減前結果正確、衰減時回傳錯誤碼不擲例外；預測在衰減時停止並產生診斷 | 單元測試 | VER-28872 衰減前各點與參考相符、60 分鐘回傳 `Decayed`；`PassFinderTests.Decay_stops_prediction_and_produces_diagnostic` 斷言診斷時刻前 1.5 s 可傳播、後 1.5 s 不可；範例資料中 `SKYPASS DECAY` 產生診斷 | ✅ |
| 6 | UTC↔儒略日（2000 閏日、2100 非閏年、跨年跨月）、GMST ≤ 1e-9 rad、epoch 時間差換算 | 單元測試 | `JulianDateTests` 11 組已知日期正反向 + 135 組參考 + 2000 次隨機往返；`GmstTests` 135 組對 `decimal` 60 位參考值全部 ≤ 1e-9 rad；`Minutes_since_epoch` | ✅ |
| 7 | TEME→地固（位置與速度）、大地→地固（高緯度、南半球、±180°、負高度）、方位角、仰角、距離、距離變化率；≥ 100 組，角度 ≤ 1e-6°、距離 ≤ 1e-6 km、變化率 ≤ 1e-9 km/s | 單元測試 | `CoordinateReferenceTests` 132 組（含北緯 89.5°、南緯 89.9°、雪梨、經度 ±179.9999°、−0.43 km 死海）全部在容差內；另有往返與保長性質測試 | ✅ |
| 8 | 太陽向量、圓柱陰影、觀測點太陽仰角與參考一致；陰影進出時刻 ≤ 0.1 秒 | 單元測試 | `SunAndShadowTests`：120 組太陽向量（≤ 1e-12）、太陽仰角（≤ 1e-6°）、陰影判定；8 顆衛星 24 小時共 171 個進出事件，全部 ≤ 0.1 s；`ShortShadowTests` 構造 0.97–8.39 秒的擦邊短陰影（短於 10 秒取樣步長），對 0.05 秒暴力參考全部找到且 ≤ 0.1 s | ✅ |
| 9 | 過境與暴力參考解逐案一致：≥ 300 組隨機；AOS/LOS ≤ 0.1 s、最大仰角 ≤ 0.01°、時刻 ≤ 1 s | 性質測試 | `Property_matches_brute_force_reference`：320 組隨機（衛星、觀測點、1–8 小時範圍、0–30° 遮罩），比對 289 個過境全部在容差內；邊界過境（差 < 0.05°）依假設事項容忍 | ✅ |
| 10 | 最大仰角只比遮罩高 0.05°–0.5° 的案例 ≥ 50 組，全部找到 | 單元測試 | `Grazing_passes_just_above_mask_are_all_found`：60 組，以參考解找出過境後把遮罩設為 E − δ，全部找到且最大仰角差 ≤ 0.01° | ✅ |
| 11 | 過境橫跨起點或終點、AOS 剛好在邊界、範圍小於一個過境；標示正確；[t0,t1]+[t1,t2] 合併與一次預測相同 | 性質測試 | `PassFinderTests` 6 個邊界案例；`Property_window_split_invariance` 200 組隨機拆分點，合併後 AOS/LOS 差 ≤ 0.01 s、最大仰角差 ≤ 1e-6° | ✅ |
| 12 | AOS < 最大時刻 < LOS；AOS/LOS 仰角 = 遮罩（≤ 0.01°）；方位角 ∈ [0, 360)；持續時間；同一衛星不重疊且排序 | 性質測試 | `Property_pass_invariants` 300 組（範圍最長 2 天） | ✅ |
| 13 | 都卜勒：接近時頻率升高、ṙ = 0 時頻移為零、與參考 ≤ 1 Hz（145.8 MHz） | 單元測試 | `DopplerTests`：5087 個參考點全部 ≤ 1 Hz；`FindZeroShift` 處頻移 < 0.01 Hz、|ṙ| < 1e-6 km/s | ✅ |
| 14 | 日照與陰影區段、太陽仰角門檻、可目視起訖、整段陰影／整段白天、陰影邊界在 AOS/LOS 前後 | 單元測試 | `VisibilityTests` 7 案例：以定義逐秒核對 > 50 個真實過境的區段，邊界前後 0.01 s 條件翻轉；四種情境各一案例；門檻 −6° 與「至少 1 秒」 | ✅ |
| 15 | 北上東左、天頂在圓心、仰角到半徑線性、遮罩圈、軌跡取樣密度、時間軸區段不重疊與時間到座標映射、DPI 整數像素無累積誤差 | 單元測試 | `SkyPlotGeometryTests` 12、`TimelineGeometryTests` 17：含投影往返與 DPI 無縫性質測試、`Dpi_rounding_has_no_cumulative_error`（驗證累加取整會產生誤差而本實作不會） | ✅ |
| 16 | 1 與 N 執行緒逐位元相同；取消不留半套結果；進度單調遞增 | 單元測試 | `Results_are_bitwise_identical_for_1_and_N_threads`、`Property_result_is_independent_of_thread_count`（12 組隨機）、`Cancellation_throws_and_returns_no_partial_result`、`Progress_is_monotonic_and_completes`；Presenter 層 `Cancel_keeps_previous_result_and_never_publishes_partial_one` | ✅ |
| 17 | 即時追蹤每秒數值與參考一致；單次更新 ≤ 1 ms（效能測試）；衛星改變、時間回溯、軌道資料更新後狀態正確 | 單元＋效能 | 960 秒逐秒參考（整秒 UTC）全部在 #7／#13 容差內；5000 次更新中位數 1.2 µs、P99.9 3.0 µs、最大 47.6 µs（斷言 P99.9 ≤ 1 ms）；`Changing_satellite…`、`Time_going_backwards…`、`Updated_elements…` | ✅ |
| 18 | 目錄與歷史往返無損、設定檔損毀用預設值並提示、資料庫版本過新或損毀顯示明確訊息且不崩潰 | 單元測試 | `StorageTests` 15 案例：含釘選的 14 顆 × 5 組往返、4 種損毀設定檔、逐欄修正、`user_version = 99`、非 SQLite 檔案；`MainPresenterTests.Read_only_database_and_bad_settings_do_not_crash_startup` | ✅ |
| 19 | CSV 欄位、時區轉換、UTF-8 with BOM、跨日與跨月 | 單元測試 | `PassCsvExporterTests` 10 案例：10/31 23:55 UTC 在 +8／−5／0 的轉換、跨年 +5:30、四捨五入到秒、RFC 4180 跳脫、BOM 位元組 | ✅ |
| 20 | 100 顆 × 7 天 × 10° 在 8 核心 ≤ 2 秒；SGP4 熱路徑零配置（配置計數斷言） | 效能測試 | 8 執行緒 **354 ms**；SGP4 與觀測計算 400 萬次呼叫 **0 bytes**（`GC.GetAllocatedBytesForCurrentThread`） | ✅ |
| 21 | ≥ 8 組性質測試，涵蓋座標往返、視窗拆分、與參考解一致、執行緒數無關、單位換算、時間單調性、TLE 往返、遮罩單調縮小 | 性質測試 | 16 組（見下表），8 個指定主題全部涵蓋 | ✅ |
| 22 | Core 與 Presenter 至少 200 個案例，`dotnet test` 全數通過 | 執行測試 | 384 個全數通過（Core＋Presenter 359 個） | ✅ |
| 23 | README 含建置步驟、操作截圖、架構、SGP4／座標／過境搜尋演算法（含避免漏掉掠過過境）、效能數據、參考資料產生方式、設計取捨、已知限制 | 檢視 README | 逐節核對 [README](../README.md) 皆具備；截圖 5 張 | ✅ |
| 24 | GitHub Actions 在 Windows runner 建置（含 App）並測試、綠燈、README 徽章、上傳 win-x64 發佈檔 | 檢視 Actions | [執行 #39551531671](https://github.com/hn83320589/SkyPass/actions/runs/39551531671)（commit `8f3997f`）：建置（含 App）、測試、發佈全部成功；artifact `test-results`、`SkyPass-win-x64` 已上傳；README 已有徽章 | ✅ |

### 性質測試清單（#21）

| # | 性質 | 次數 | 種子 | 驗收主題 |
|---|---|---:|---:|---|
| 1 | UTC↔JD 往返與單調性 | 2000 | 6101 | **時間單調性** |
| 2 | TLE 往返（格式化→解析→格式化） | 3000 | 7102 | **TLE 往返** |
| 3 | 目錄：歷史排序、上限與目前組 | 500 | 7203 | — |
| 4 | 大地↔地固往返 | 5000 | 8301 | **座標往返** |
| 5 | TEME→ECEF 保持長度與 z | 2000 | 8302 | 座標 |
| 6 | 仰角變化率 ≈ 數值微分 | 500 | 8303 | — |
| 7 | 過境集合與暴力參考解一致 | 320 | 9401 | **與參考解一致** |
| 8 | 掠過過境不漏 | 60 | 9402 | — |
| 9 | 視窗拆分不變性 | 200 | 9403 | **視窗拆分不變性** |
| 10 | 過境屬性 | 300 | 9404 | — |
| 11 | 遮罩提高時過境集合單調縮小 | 200 | 9405 | **遮罩單調縮小** |
| 12 | 結果與執行緒數無關 | 12 | 16001 | **執行緒數無關** |
| 13 | 單位換算 | 5000 | 21001 | **單位換算** |
| 14 | 天空圖投影 | 2000 | 15001 | — |
| 15 | 時間軸映射往返 | 2000 | 15101 | — |
| 16 | DPI 四捨五入後相鄰區段無縫 | 2000 | 15102 | — |

## 4. 規格限制檢查

| 限制 | 檢查方式 | 結果 |
|---|---|:-:|
| C#、.NET 9、Silk.NET + ImGui.NET；不使用 WPF／WinForms／WinUI／MAUI／Avalonia／Uno | `Directory.Build.props` 為 `net9.0`；`LayeringTests` 以反射檢查 Core 與 Presenters 不引用這些組件；App 只引用 Silk.NET | ✅ |
| 專案切分 Core／Data／Presenters／App；Core 純 net9.0、不引用 UI／OpenGL／Windows 組件 | `LayeringTests`（Core 不引用任何其他 SkyPass 組件、Sqlite、Silk.NET、ImGui） | ✅ |
| Microsoft.Data.Sqlite 版本與 .NET 9 對齊 | `Directory.Packages.props`：9.0.21 | ✅ |
| 不使用現成 SGP4／衛星／天文座標套件 | 正式程式只引用 Silk.NET、ImGui.NET、Microsoft.Data.Sqlite；python-sgp4 只在 `tools/refgen` 離線產生參考資料 | ✅ |
| 所有「現在時間」透過 TimeProvider | `LayeringTests.Source_never_reads_the_system_clock_directly` 掃描 `src/` 原始碼 | ✅（本輪修正，見 §7） |
| 數值一律 double；公開 API 角度以度為單位，單位在命名上看得出來 | `CoordinateTests.Public_core_api_exposes_no_radian_parameters_or_members` 以反射檢查 Core 的公開成員 | ✅（本輪修正，見 §7） |
| 熱路徑（SGP4、座標轉換、仰角）零配置、執行緒安全 | 零配置斷言（§6）；`Concurrent_propagation_gives_identical_results` 8 執行緒 2 萬次與循序結果相同 | ✅ |
| 不使用付費授權套件 | Silk.NET（MIT）、ImGui.NET（MIT）、Microsoft.Data.Sqlite（MIT）、SQLitePCLRaw（Apache-2.0）、xUnit（Apache-2.0）、coverlet（MIT） | ✅ |
| 不呼叫外部網路服務 | 正式程式沒有任何 HTTP 或網路 API；資料只來自使用者匯入的檔案 | ✅ |
| 測試：xUnit、≥ 8 組性質測試（固定種子、失敗印出種子）、獨立暴力參考解、離線參考資料檔與 README 說明 | §1、§3 | ✅ |
| 資料位置 `%LocalAppData%\SkyPass\`；觀測點最多 5 個；時區預設 UTC+8；Core 內部一律 UTC | `StorageTests.Default_paths_are_under_local_app_data`、`SettingsPresenterTests.At_most_five_sites`、`AppSettings.DefaultUtcOffset` | ✅ |

## 5. 交付物檢查

| 交付物 | 位置 | 結果 |
|---|---|:-:|
| 原始碼（Solution 含 Core、Data、Presenters、App、Tests） | `SkyPass.sln`、`src/`、`tests/` | ✅ |
| README | `README.md` | ✅ |
| ≥ 30 組近地 TLE（含構造的極低近地點與會衰減者）＋預期過境表 | `samples/skypass-samples.tle`（30 近地 + 1 GEO）、`samples/expected-passes.csv`（125 筆）；`SampleDataTests` 逐筆比對 | ✅ |
| 時間、GMST、座標、都卜勒、陰影各 ≥ 10 組逐步手算範例 | `docs/hand-calculations.md`（各 10 組）；`HandCalculationTests` 核對 50 組最終值 | ✅ |
| 離線產生參考資料的腳本與說明 | `tools/refgen/*.py`、`tools/refgen/README.md`（Python 3.11.9、sgp4 2.27） | ✅ |
| 暴力過境參考解（測試專案內）及其獨立性說明 | `tests/SkyPass.Tests/Reference/BruteForcePassFinder.cs`（類別註解）、README「測試」節 | ✅ |
| CI（setup-dotnet 9.0.x、上傳 win-x64 發佈檔） | `.github/workflows/ci.yml` | ✅ |

## 6. 效能量測

Release 建置，量測時效能測試不與其他測試平行執行。

| 項目 | 實測 | 要求 |
|---|---|---|
| 100 顆 × 7 天 × 10°（含可目視），8 執行緒 | **394 ms**（2955 個過境） | ≤ 2 s |
| 同上，1／2／4／24 執行緒 | 2896／1556／784／142 ms | 見下方說明 |
| SGP4 單次呼叫 | 689 ns，0 bytes | 零配置 |
| 傳播 + GMST + TEME→地固 + 站心觀測 | 1015 ns，0 bytes | 零配置 |
| 即時追蹤單次更新（5000 次） | 中位數 1.2 µs、P99 1.7 µs、P99.9 8.3 µs、最大 56.5 µs | ≤ 1 ms |

數值會因背景負載在 ±15% 內起伏；README 與本報告的數值取自不同次執行，屬同一量級。

**前提**：單執行緒約 2.6–2.9 秒，超過 2 秒；2 秒門檻以 8 核心為準。預報時間大致與「單核效能 × 核心數」成反比，
單核心電腦，或單核效能明顯低於本機的 2–4 核心電腦，100 顆 × 7 天可能超過 2 秒（README 效能一節已註明）。

## 7. 本輪檢查發現並修正的問題

逐條對照需求與技術限制時發現以下 5 點，均已修正並加上防止回歸的測試：

| # | 問題 | 修正 | 防回歸 |
|---|---|---|---|
| 1 | `SqliteTleCatalogStore` 以 `DateTime.UtcNow` 記錄匯入時刻，違反「所有現在時間透過 TimeProvider」 | 建構子注入 `TimeProvider`（預設 `TimeProvider.System`），App 傳入同一個時鐘 | 原始碼掃描測試 |
| 2 | `PredictionPresenter` 以 `Stopwatch` 量測耗時 | 改用 `TimeProvider.GetTimestamp()`／`GetElapsedTime()` | 同上 |
| 3 | 公開 API 出現弧度：`Gmst.ComputeRad`、`Frames.TemeToEcef(…, gmstRad)`、`Frames.RotateTemeToEcef`、`Observation.GmstRad`、`Frames.EarthRotationRadPerSec` | 弧度版本改為 `internal`（測試仍以 1e-9 rad 驗證）；新增 `Gmst.ComputeDeg` 與 `Frames.TemeToEcef(teme, JulianDate)`；移除 `Observation.GmstRad` | 反射檢查測試 |
| 4 | 需求「過境表可依衛星、**時間**、最大仰角篩選」，原本缺少時間篩選 | `PassFilter` 新增 `From`／`To`（保留與範圍重疊的過境）；介面提供「全部／接下來 6、12、24、48 小時」 | `Filtering_by_time_range_keeps_overlapping_passes` |
| 5 | 即時追蹤效能測試與其他測試平行執行時，曾出現 1.2 ms 的離群值（CPU 競爭） | 效能測試移入 `DisableParallelization` 的集合，量測 5000 次並改以更嚴格的 P99.9 ≤ 1 ms 斷言 | 隔離後最大值 47.6 µs |

另外重新產生截圖時發現過境表欄位被擠壓（表格第一次繪製時尚無資料，自動欄寬被鎖在標題寬度），已改為明確指定欄寬。

### 7.1 外部審查意見與處理

| 審查發現 | 處理 | 驗證 |
|---|---|---|
| 陰影以 10 秒取樣，短於約 10 秒的擦邊地影（高 β 角）可能漏判 | **已修正**：比照過境搜尋，同時以解析式計算陰影邊界函數的變化率；函數不變號但變化率變號時以黃金分割求區間極值，極小值 < 0 就在兩側各求一次根。可目視計算改用同一套陰影搜尋 | `ShortShadowTests`：構造 0.97、2.17、4.84、8.39 秒的擦邊陰影，10 秒步長全部找到（≤ 0.1 s），可目視區段在短陰影處正確切開；原 171 個參考事件仍通過 |
| 不支援 Alpha-5 衛星編號（≥ 100000） | **已修正**：解析與格式化支援 Alpha-5（A–Z 不含 I、O，`E8493` = 148493，上限 `Z9999` = 339999）；編號欄寫錯時仍回報欄位錯誤與行號 | `TleParserTests` 新增 19 案例（對照表以 python-sgp4 的 `alpha5` 模組核對）；TLE 往返性質測試涵蓋 Alpha-5 範圍 |
| README 效能數字的前提不明 | **已補充**：README 效能一節說明單執行緒約 2.6 秒、2 秒門檻以 8 核心為準，單核心或單核效能較低的 2–4 核心電腦可能超過 2 秒 | 檢視 README |
| 過境搜尋依賴「30 秒內不會同時出現仰角極大與極小」 | 維持現狀，README 已說明非任意軌道的數學保證；審查者的高偏心率案例（遠地點約 9,000 km）未出現問題 | — |
| 「起點前已開始」的過境，最大仰角取時間範圍內的值 | 維持現狀，符合題目定義，README 已說明 | — |

審查者自行預報的 315 個可目視過境未發現不一致。
## 8. CI

`.github/workflows/ci.yml`（`windows-latest`、`actions/setup-dotnet@v4` 9.0.x）：

1. `dotnet restore` → `dotnet build -c Release`（含 App）
2. `dotnet test -c Release`，上傳 TRX 為 artifact `test-results`
3. `dotnet publish src/SkyPass.App -c Release -r win-x64 --self-contained`，上傳為 artifact `SkyPass-win-x64`

本機已以相同指令驗證：建置 0 警告、測試 360/360、發佈檔約 82 MB，包含 `glfw3.dll`、`cimgui.dll`、`e_sqlite3.dll`。

**GitHub Actions 執行結果**（首次推送，commit `8f3997f`）：

| 項目 | 結果 |
|---|---|
| 執行 | [#39551531671](https://github.com/hn83320589/SkyPass/actions/runs/39551531671)，`windows-latest`，2026-10-21 05:47–05:50 UTC（約 3 分鐘） |
| 步驟 | Restore、Build（含 App）、Test、Upload test results、Publish win-x64、Upload win-x64 publish：**全部 success** |
| Artifact | `test-results`（TRX，約 81 KB）、`SkyPass-win-x64`（壓縮後約 39 MB） |
| 結論 | **綠燈** |

之後每次推送（含本次審查修正）的結果以 [Actions 頁面](https://github.com/hn83320589/SkyPass/actions) 與 README 的 CI 徽章為準。

## 9. 未涵蓋範圍與風險

| 項目 | 說明 |
|---|---|
| App 畫面無自動化斷言 | App 只負責繪製與事件轉送，邏輯都在 Presenter（已測試）。畫面以 `--screenshot` 實際渲染並人工檢視；沒有像素級或 UI 自動化測試 |
| CI 上的效能門檻 | 2 秒門檻以 8 核心為準；CI runner 核心數較少時依比例放寬（例如 4 核心為 4 秒）並記錄實測值 |
| 共用 runner 的時間抖動 | 即時追蹤以 P99.9 斷言，耗時約為門檻的 1/300；共用 runner 極端負載下仍可能出現離群值 |
| 偶發的零配置失敗 | 開發期間曾有一次零配置測試失敗、之後無法重現。已改為量測 3 輪取最小值：排除執行環境的一次性配置，但每次呼叫都配置的退化仍會被測出 |
| 參考資料的獨立性 | 座標、太陽、陰影參考值是依同一份公式以 Python 重寫，可抓出實作錯誤，但無法抓出「公式本身」的錯誤；SGP4 與 GMST 的參考則分別來自 Vallado C++ 與高精度 `decimal` |
| 演算法假設 | 過境搜尋假設 30 秒內不會同時出現仰角極大與極小值、陰影搜尋假設 10 秒內不會同時出現邊界函數的極大與極小值（近地軌道兩者相隔約半圈，以暴力參考解驗證，非數學證明）；陰影極值偵測把太陽方向視為定值，深度小於約 0.2 公尺的極淺擦邊陰影可能仍會漏判 |

完整的已知限制見 README「已知限制」。

## 10. 重現方式

```powershell
dotnet build SkyPass.sln -c Release
dotnet test SkyPass.sln -c Release --no-build --logger "trx;LogFileName=results.trx"
# 只看效能數據
dotnet test SkyPass.sln -c Release --no-build --filter Category=Performance --logger "console;verbosity=detailed"
# 重現單一失敗的性質測試案例：測試訊息會列出 caseSeed
#   Property.Replay(caseSeed: <數字>, rng => { ... })
```
