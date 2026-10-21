"""產生都卜勒參考資料：真實衛星對固定觀測點、仰角 > −5° 期間每 10 秒的距離變化率與接收頻率。

距離變化率沿用 gen_coords.py 的獨立公式；接收頻率 = f0 × (1 − ṙ / c)，f0 = 145.800 MHz。

用法：python -I gen_doppler.py <inputs 目錄> <輸出目錄>
"""
import csv
import sys
from pathlib import Path

from sgp4.api import WGS72, Satrec

sys.path.insert(0, str(Path(__file__).resolve().parent))  # 以 -I 執行時仍可匯入同目錄的 gen_coords
from gen_coords import geodetic_to_ecef, gmst, load_tles, look_angles, split_jd, teme_to_ecef

C_KM_S = 299792.458
F0_HZ = 145.8e6
SITES = [(25.0330, 121.5654, 0.010), (-33.8688, 151.2093, 0.058), (64.8378, -147.7164, 0.136)]


def main(inputs: Path, out: Path):
    rows = []
    for k, (name, l1, l2) in enumerate(load_tles(inputs)[:6]):
        sat = Satrec.twoline2rv(l1, l2, WGS72)
        lat, lon, h = SITES[k % len(SITES)]
        obs = geodetic_to_ecef(lat, lon, h)
        t = 0.0
        while t <= 1440.0:
            err, r, v = sat.sgp4_tsince(t)
            day, frac = split_jd(sat, t)
            re, ve = teme_to_ecef(r, v, gmst(day, frac))
            _, el, _, rate = look_angles(re, ve, lat, lon, obs)
            if el > -5.0:
                rows.append([f"D-{l1[2:7]}", l1, l2, repr(lat), repr(lon), repr(h), repr(t), repr(el),
                             repr(rate), repr(F0_HZ * (1.0 - rate / C_KM_S))])
            t += 10.0 / 60.0
    with open(out / "doppler.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(["case", "line1", "line2", "lat_deg", "lon_deg", "h_km", "tsince_min", "el_deg",
                    "range_rate_km_s", "f_rx_hz"])
        w.writerows(rows)
    print(f"doppler rows {len(rows)}")


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
