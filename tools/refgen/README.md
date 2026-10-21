# 參考資料產生工具（refgen）

測試使用的參考資料都由本目錄的腳本離線產生，產生後提交到 `tests/SkyPass.Tests/TestData/`。
正式程式與 CI 都不需要 Python。

## 環境

| 項目 | 版本 |
|---|---|
| Python | 3.11.9（Windows x64） |
| sgp4 | 2.27（`requirements.txt` 固定版本；需為 C++ 加速版，`sgp4.api.accelerated == True`） |

```powershell
cd tools/refgen
python -m venv .venv
.\.venv\Scripts\python -m pip install -r requirements.txt
```

## 腳本

| 腳本 | 輸出 | 說明 |
|---|---|---|
| `gen_time.py` | `time_gmst.csv` | 儒略日（以 `datetime.toordinal()` 換算）與 GMST（IAU-82 公式，以 `decimal` 60 位有效數字計算）。只用標準函式庫 |
| `gen_sgp4.py` | `sgp4_published.csv`、`sgp4_random.csv` | 以 python-sgp4 的 `Satrec.twoline2rv` + `sgp4_tsince` 產生；python-sgp4 的加速版直接呼叫 Vallado 官方 C++ 程式碼 |

```powershell
.\.venv\Scripts\python -I gen_time.py ..\..\tests\SkyPass.Tests\TestData
.\.venv\Scripts\python -I gen_sgp4.py inputs ..\..\tests\SkyPass.Tests\TestData
```

## 輸入資料（`inputs/`）

| 檔案 | 來源 |
|---|---|
| `SGP4-VER.TLE`、`tcppver.out` | Vallado 等人（2006）發表的 SGP4 驗證案例與 C++ 輸出，取自 python-sgp4 2.27 原始碼套件。其中只有 9 組近地軌道 |
| `celestrak-snapshot-20261021.tle` | 2026-10-21 從 CelesTrak 下載的 14 顆真實近地衛星 TLE（ISS、天和、Meteor-M、風雲 3D、AO-7 等），用來補足至少 20 組近地案例，並涵蓋低傾角、逆行與低近地點 |

## 參考資料內容

- **sgp4_published.csv**：23 組近地案例。SGP4-VER 依檔案中指定的起訖與步長取時間點；CelesTrak 快照取 −1440 到 +2880 分鐘、每 120 分鐘一點。錯誤時刻記錄錯誤碼，狀態為 `nan`。
- **sgp4_random.csv**：320 組隨機近地軌道（種子 20261021），每組 6 個時間點（0 與 5 個 −2880 到 +10080 分鐘的隨機點）。約 8% 為低近地點、高阻力、會在範圍內衰減的軌道。TLE 文字由腳本自行格式化，與 C# 的格式化器互相獨立。
- **tcppver.out**：測試另外直接以其列印精度（位置 1e-8 km、速度 1e-9 km/s）交叉比對。

所有數值以 Python `repr` 輸出（可完整還原 double）。
