# 範例資料

| 檔案 | 內容 |
|---|---|
| `skypass-samples.tle` | 31 組 TLE（三行格式）：30 顆近地衛星 + 1 顆 GEO（示範「不支援」） |
| `expected-passes.csv` | 預期過境表（UTF-8 with BOM）：逐筆 AOS、最大仰角、LOS 與邊界標示 |

## TLE 組成

- **CelesTrak 快照（14 顆）**：2026-10-21 下載的真實近地衛星（ISS、天和、Meteor-M、風雲 3D、AO-7、FO-29、RS-44 等）。
- **自行構造（16 顆，NORAD 90001–90016）**：epoch 2026-10-21 00:00 UTC 的合法軌道，涵蓋
  太陽同步、中傾角、低傾角、近赤道（台北看不到）、逆行（120°、140°）、極軌道、
  高偏心率（e = 0.15、0.30，後者週期約 206 分鐘，接近深空門檻）、
  極低近地點（約 190 km，觸發 isimp 簡化），以及高阻力、約 9 小時後衰減的 `SKYPASS DECAY`。
- **SKYPASS GEO（90017）**：地球同步軌道，週期 ≥ 225 分鐘，標示為不支援。

## 預期過境表的條件

- 觀測點：台北（25.0330°N, 121.5654°E, 10 m）
- 時間範圍：2026-10-21 00:00:00 至 2026-10-22 00:00:00 UTC
- 遮罩：10°

由 `tools/refgen/gen_samples.py` 以 python-sgp4（Vallado C++）傳播，從起點**逐秒**取樣仰角，
二分法求 AOS/LOS、黃金分割求最大仰角產生，與程式的過境搜尋演算法無關。
`SampleDataTests` 以程式預報同一條件並逐筆比對（AOS/LOS ≤ 0.1 秒、最大仰角 ≤ 0.01°）。
`SKYPASS DECAY` 約在 08:54 UTC 傳播失敗，預報會在該時刻停止並產生診斷。

## 試用

```powershell
SkyPass.App.exe --import samples\skypass-samples.tle --now 2026-10-21T10:50:00Z
```
