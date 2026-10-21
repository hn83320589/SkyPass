"""產生範例資料與「預期過境表」。

範例 TLE：CelesTrak 2026-10-21 快照 14 顆 + 自行構造 16 顆合法近地軌道（共 30 顆近地）+ 1 顆 GEO（示範「不支援」）。
預期過境表：以 python-sgp4（Vallado C++）傳播、gen_coords.py 的獨立公式計算仰角，
從視窗起點「逐秒」取樣，二分法求 AOS/LOS（1e-4 秒），黃金分割求最大仰角——與 C# 的過境搜尋演算法無關。

固定條件：觀測點 台北（25.0330°N, 121.5654°E, 10 m），2026-10-21 00:00:00 至 2026-10-22 00:00:00 UTC，遮罩 10°。

用法：python -I gen_samples.py <inputs 目錄> <samples 目錄>
"""
import csv
import datetime as dt
import math
import sys
from pathlib import Path

from sgp4.api import WGS72, Satrec, jday

sys.path.insert(0, str(Path(__file__).resolve().parent))
from gen_coords import geodetic_to_ecef, gmst, load_tles, look_angles, teme_to_ecef  # noqa: E402
from gen_sgp4 import checksum, fmt_exp, fmt_ndot  # noqa: E402

SITE = (25.0330, 121.5654, 0.010)
START = dt.datetime(2026, 10, 21, tzinfo=dt.timezone.utc)
DURATION_S = 86400
MASK = 10.0

# 名稱, 傾角, 升交點赤經, 偏心率, 近地點引數, 平近點角, 平均運動(圈/日), BSTAR, 說明
CONSTRUCTED = [
    ("SKYPASS SSO-1", 97.8, 10.0, 0.0012, 90.0, 0.0, 14.90, 1.0e-4, "太陽同步"),
    ("SKYPASS SSO-2", 98.2, 100.0, 0.0008, 45.0, 120.0, 14.30, 5.0e-5, "太陽同步"),
    ("SKYPASS SSO-3", 99.0, 200.0, 0.0015, 300.0, 240.0, 14.10, 3.0e-5, "太陽同步"),
    ("SKYPASS MID-1", 45.0, 30.0, 0.0010, 0.0, 0.0, 15.20, 2.0e-4, "中傾角"),
    ("SKYPASS MID-2", 52.0, 150.0, 0.0005, 180.0, 90.0, 15.50, 3.0e-4, "中傾角"),
    ("SKYPASS LOWINC", 20.0, 60.0, 0.0010, 0.0, 180.0, 14.80, 1.0e-4, "低傾角"),
    ("SKYPASS EQUATORIAL", 5.0, 0.0, 0.0005, 0.0, 0.0, 14.50, 5.0e-5, "近赤道（台北看不到）"),
    ("SKYPASS RETRO-1", 120.0, 80.0, 0.0020, 30.0, 300.0, 14.60, 1.0e-4, "逆行"),
    ("SKYPASS RETRO-2", 140.0, 250.0, 0.0010, 200.0, 30.0, 13.90, 8.0e-5, "逆行"),
    ("SKYPASS POLAR", 90.0, 300.0, 0.0010, 0.0, 0.0, 14.00, 6.0e-5, "極軌道"),
    ("SKYPASS ECC-1", 63.4, 40.0, 0.1500, 270.0, 0.0, 10.50, 1.0e-4, "高偏心率"),
    ("SKYPASS ECC-2", 30.0, 120.0, 0.3000, 90.0, 180.0, 7.00, 5.0e-5, "高偏心率、週期 206 分（接近深空門檻）"),
    ("SKYPASS LOWPERIGEE", 51.6, 10.0, 0.0040, 0.0, 0.0, 16.20, 2.0e-4, "近地點約 190 km（isimp 簡化）"),
    ("SKYPASS DECAY", 51.6, 200.0, 0.0010, 0.0, 0.0, 16.30, 1.0e-2, "高阻力，約 9 小時後衰減"),
    ("SKYPASS CUBE-1", 97.4, 330.0, 0.0009, 100.0, 50.0, 15.10, 1.5e-4, "立方衛星"),
    ("SKYPASS CUBE-2", 82.5, 170.0, 0.0060, 10.0, 350.0, 13.70, 2.0e-5, "立方衛星"),
    ("SKYPASS GEO", 0.05, 0.0, 0.0002, 0.0, 0.0, 1.0027, 0.0, "地球同步（深空，不支援）"),
]


def constructed_tle(i, row):
    name, inc, raan, ecc, argp, ma, n, bstar, _ = row
    norad = 90001 + i
    ndot = 0.002 if bstar >= 1e-3 else 0.00001
    l1 = (f"1 {norad:05d}U 26001{chr(ord('A') + i)}   26294.00000000 {fmt_ndot(ndot)} {fmt_exp(0.0)} "
          f"{fmt_exp(bstar)} 0  999")
    l2 = f"2 {norad:05d} {inc:8.4f} {raan:8.4f} {round(ecc * 1e7):07d} {argp:8.4f} {ma:8.4f} {n:11.8f}   10"
    assert len(l1) == 68 and len(l2) == 68, (l1, l2)
    return name, l1 + str(checksum(l1)), l2 + str(checksum(l2))


def brute_force_passes(sat):
    lat, lon, h = SITE
    obs = geodetic_to_ecef(lat, lon, h)
    jd0, fr0 = jday(START.year, START.month, START.day, 0, 0, 0)

    def el_at(s):
        fr = fr0 + s / 86400.0
        tsince = (jd0 - sat.jdsatepoch) * 1440.0 + (fr - sat.jdsatepochF) * 1440.0
        err, r, v = sat.sgp4_tsince(tsince)
        if err:
            return None, None
        carry = math.floor(fr)
        re, ve = teme_to_ecef(r, v, gmst(jd0 + carry, fr - carry))
        az, el, _, _ = look_angles(re, ve, lat, lon, obs)
        return el - MASK, az

    def bisect(lo, hi):
        flo = el_at(lo)[0]
        while hi - lo > 1e-4:
            mid = 0.5 * (lo + hi)
            if (el_at(mid)[0] < 0) == (flo < 0):
                lo = mid
            else:
                hi = mid
        return 0.5 * (lo + hi)

    def golden(a, b):
        g = (math.sqrt(5) - 1) / 2
        c, d = b - g * (b - a), a + g * (b - a)
        fc, fd = el_at(c)[0], el_at(d)[0]
        while b - a > 1e-4:
            if fc > fd:
                b, d, fd = d, c, fc
                c = b - g * (b - a)
                fc = el_at(c)[0]
            else:
                a, c, fc = c, d, fd
                d = a + g * (b - a)
                fd = el_at(d)[0]
        return 0.5 * (a + b)

    values = []
    decayed_at = None
    for s in range(DURATION_S + 1):
        f, _ = el_at(float(s))
        if f is None:
            decayed_at = s
            break
        values.append(f)

    passes = []
    i = 0
    n = len(values)
    while i < n:
        if values[i] < 0:
            i += 1
            continue
        started_before = i == 0
        aos = 0.0 if started_before else bisect(i - 1.0, float(i))
        j = i
        while j + 1 < n and values[j + 1] >= 0:
            j += 1
        ends_after = j == n - 1 and decayed_at is None
        decay_cut = j == n - 1 and decayed_at is not None
        los = float(DURATION_S) if ends_after else (float(j) if decay_cut else bisect(float(j), j + 1.0))
        best = max(range(i, j + 1), key=lambda k: values[k])
        tmax = golden(max(aos, best - 1.0), min(los, best + 1.0))
        if el_at(tmax)[0] < values[best]:
            tmax = float(best)
        passes.append((aos, tmax, el_at(tmax)[0] + MASK, los, el_at(aos)[1], el_at(los)[1], started_before, ends_after))
        i = j + 1
    return passes, decayed_at


def iso(seconds):
    return (START + dt.timedelta(seconds=seconds)).strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def main(inputs: Path, samples: Path):
    sats = list(load_tles(inputs))
    notes = {}
    for i, row in enumerate(CONSTRUCTED):
        name, l1, l2 = constructed_tle(i, row)
        sats.append((name, l1, l2))
        notes[l1[2:7]] = row[8]

    samples.mkdir(parents=True, exist_ok=True)
    with open(samples / "skypass-samples.tle", "w", newline="\n", encoding="utf-8") as f:
        for name, l1, l2 in sats:
            f.write(f"{name}\n{l1}\n{l2}\n")

    rows = []
    for name, l1, l2 in sats:
        sat = Satrec.twoline2rv(l1, l2, WGS72)
        if sat.method != "n":
            print(f"{name}: 深空軌道，略過")
            continue
        passes, decayed_at = brute_force_passes(sat)
        for aos, tmax, max_el, los, aos_az, los_az, sb, ea in passes:
            flags = ";".join(x for x, on in (("起點前已開始", sb), ("終點後才結束", ea)) if on)
            rows.append([name, l1[2:7].strip(), iso(aos), f"{aos_az:.1f}", iso(tmax), f"{max_el:.3f}", iso(los), f"{los_az:.1f}", flags])
        if decayed_at is not None:
            print(f"{name}: 於 {iso(decayed_at)} 前後傳播失敗（衰減）")

    rows.sort(key=lambda r: (r[2], int(r[1])))
    with open(samples / "expected-passes.csv", "w", newline="", encoding="utf-8-sig") as f:
        w = csv.writer(f, lineterminator="\r\n")
        w.writerow(["衛星", "NORAD", "AOS (UTC)", "AOS 方位(°)", "最大仰角時刻 (UTC)", "最大仰角(°)", "LOS (UTC)", "LOS 方位(°)", "標示"])
        w.writerows(rows)
    print(f"{len(sats)} 組 TLE，預期過境 {len(rows)} 筆")


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
