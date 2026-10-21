"""產生座標轉換、太陽位置與陰影的參考資料。

衛星 TEME 狀態由 python-sgp4（Vallado C++）產生；其餘全部依「假設事項」的公式在此以 Python 獨立實作，
不共用 C# 程式碼。陰影進出時刻以 1 秒取樣後二分法求到 1e-7 秒。

輸出：
  coords.csv         TEME→ECEF、大地→ECEF、方位角／仰角／距離／距離變化率（≥ 100 組）
  sun.csv            太陽單位向量、觀測點太陽仰角、圓柱陰影判定
  shadow_events.csv  衛星進入／離開陰影的時刻（tsince 分鐘）

用法：python -I gen_coords.py <inputs 目錄> <輸出目錄>
"""
import csv
import math
import random
import sys
from pathlib import Path

from sgp4.api import WGS72, Satrec, accelerated

assert accelerated

OMEGA_EARTH = 7.2921158553e-5   # rad/s
WGS84_A = 6378.137
WGS84_F = 1.0 / 298.257223563
RE_SHADOW = 6378.137


def gmst(jd_day, jd_frac):
    t = ((jd_day - 2451545.0) + jd_frac) / 36525.0
    sec = -6.2e-6 * t ** 3 + 0.093104 * t ** 2 + (876600.0 * 3600 + 8640184.812866) * t + 67310.54841
    g = math.fmod(sec * math.pi / 180.0 / 240.0, 2 * math.pi)
    return g + 2 * math.pi if g < 0 else g


def teme_to_ecef(r, v, theta):
    c, s = math.cos(theta), math.sin(theta)
    x, y, z = r
    rx, ry = c * x + s * y, -s * x + c * y
    vx, vy = c * v[0] + s * v[1], -s * v[0] + c * v[1]
    return (rx, ry, z), (vx + OMEGA_EARTH * ry, vy - OMEGA_EARTH * rx, v[2])


def geodetic_to_ecef(lat_deg, lon_deg, h_km):
    lat, lon = math.radians(lat_deg), math.radians(lon_deg)
    e2 = WGS84_F * (2 - WGS84_F)
    n = WGS84_A / math.sqrt(1 - e2 * math.sin(lat) ** 2)
    return ((n + h_km) * math.cos(lat) * math.cos(lon),
            (n + h_km) * math.cos(lat) * math.sin(lon),
            (n * (1 - e2) + h_km) * math.sin(lat))


def look_angles(r_ecef, v_ecef, lat_deg, lon_deg, obs):
    lat, lon = math.radians(lat_deg), math.radians(lon_deg)
    rho = [r_ecef[i] - obs[i] for i in range(3)]
    sl, cl, so, co = math.sin(lat), math.cos(lat), math.sin(lon), math.cos(lon)
    s = sl * co * rho[0] + sl * so * rho[1] - cl * rho[2]
    e = -so * rho[0] + co * rho[1]
    z = cl * co * rho[0] + cl * so * rho[1] + sl * rho[2]
    rng = math.sqrt(sum(x * x for x in rho))
    el = math.degrees(math.asin(z / rng))
    az = math.degrees(math.atan2(e, -s)) % 360.0
    rate = sum(rho[i] * v_ecef[i] for i in range(3)) / rng
    return az, el, rng, rate


def norm360(deg):
    return deg % 360.0


def sun_unit(jd_day, jd_frac):
    n = (jd_day - 2451545.0) + jd_frac
    L = norm360(280.460 + 0.9856474 * n)
    g = math.radians(norm360(357.528 + 0.9856003 * n))
    lam = math.radians(norm360(L + 1.915 * math.sin(g) + 0.020 * math.sin(2 * g)))
    eps = math.radians(23.439 - 0.0000004 * n)
    return (math.cos(lam), math.cos(eps) * math.sin(lam), math.sin(eps) * math.sin(lam))


def sun_elevation(jd_day, jd_frac, lat_deg, lon_deg):
    s = sun_unit(jd_day, jd_frac)
    th = gmst(jd_day, jd_frac)
    c, si = math.cos(th), math.sin(th)
    se = (c * s[0] + si * s[1], -si * s[0] + c * s[1], s[2])
    lat, lon = math.radians(lat_deg), math.radians(lon_deg)
    up = (math.cos(lat) * math.cos(lon), math.cos(lat) * math.sin(lon), math.sin(lat))
    return math.degrees(math.asin(sum(se[i] * up[i] for i in range(3))))


def shadow_margin(r, s):
    """連續函數：負值代表在陰影內。"""
    d = sum(r[i] * s[i] for i in range(3))
    if d >= 0:
        return math.sqrt(sum(x * x for x in r)) - RE_SHADOW
    perp = [r[i] - d * s[i] for i in range(3)]
    return math.sqrt(sum(x * x for x in perp)) - RE_SHADOW


def load_tles(inputs: Path):
    snap = [l.rstrip() for l in open(inputs / "celestrak-snapshot-20261021.tle", encoding="utf-8") if l.strip()]
    return [(snap[i], snap[i + 1], snap[i + 2]) for i in range(0, len(snap), 3)]


def split_jd(sat, tsince):
    """與 C# 相同：epoch 兩段式 + tsince，正規化成 (當日 0h, 日內比例)。"""
    day = sat.jdsatepoch
    frac = sat.jdsatepochF + tsince / 1440.0
    carry = math.floor(frac)
    return day + carry, frac - carry


OBSERVERS = [
    (25.0330, 121.5654, 0.010),     # 台北
    (89.5, 45.0, 0.0),              # 高緯度（近北極）
    (-89.9, -120.0, 2.835),         # 南極點附近
    (-33.8688, 151.2093, 0.058),    # 南半球
    (-77.85, 166.67, 0.024),        # 南半球高緯度
    (64.8378, -147.7164, 0.136),    # 阿拉斯加
    (0.0, 179.9999, 0.0),           # 經度 +180 附近
    (0.0, -179.9999, 0.0),          # 經度 −180 附近
    (51.5, -0.0001, -0.05),         # 本初子午線、負高度
    (31.5, 35.5, -0.430),           # 死海（負高度）
    (-1.2921, 36.8219, 1.795),      # 赤道附近高海拔
]


def coords_rows(tles, rng):
    rows = []
    for i in range(132):
        name, l1, l2 = tles[i % len(tles)]
        sat = Satrec.twoline2rv(l1, l2, WGS72)
        tsince = rng.uniform(-1440.0, 2880.0)
        err, r, v = sat.sgp4_tsince(tsince)
        assert err == 0
        lat, lon, h = OBSERVERS[i % len(OBSERVERS)] if i < 2 * len(OBSERVERS) else (
            rng.uniform(-90, 90), rng.uniform(-180, 180), rng.uniform(-0.5, 5.0))
        day, frac = split_jd(sat, tsince)
        th = gmst(day, frac)
        re, ve = teme_to_ecef(r, v, th)
        obs = geodetic_to_ecef(lat, lon, h)
        az, el, rg, rate = look_angles(re, ve, lat, lon, obs)
        rows.append([f"C{i:03d}", repr(day), repr(frac), repr(lat), repr(lon), repr(h),
                     *map(repr, r), *map(repr, v), repr(th), *map(repr, re), *map(repr, ve),
                     *map(repr, obs), repr(az), repr(el), repr(rg), repr(rate)])
    return rows


def sun_rows(tles, rng):
    rows = []
    for i in range(120):
        name, l1, l2 = tles[i % len(tles)]
        sat = Satrec.twoline2rv(l1, l2, WGS72)
        tsince = rng.uniform(-1440.0, 2880.0)
        err, r, v = sat.sgp4_tsince(tsince)
        day, frac = split_jd(sat, tsince)
        # 一部分時間點換到 2000–2050 年間，涵蓋不同季節
        if i % 3 == 0:
            day = math.floor(rng.uniform(2451544.5, 2469807.5) - 0.5) + 0.5
            frac = rng.random()
        lat, lon = rng.uniform(-90, 90), rng.uniform(-180, 180)
        s = sun_unit(day, frac)
        rows.append([f"S{i:03d}", repr(day), repr(frac), repr(lat), repr(lon), *map(repr, s),
                     repr(sun_elevation(day, frac, lat, lon)), *map(repr, r),
                     1 if shadow_margin(r, s) < 0 else 0])
    return rows


def shadow_event_rows(tles):
    rows = []
    for name, l1, l2 in tles[:8]:
        sat = Satrec.twoline2rv(l1, l2, WGS72)

        def margin(t):
            err, r, _ = sat.sgp4_tsince(t)
            assert err == 0
            day, frac = split_jd(sat, t)
            return shadow_margin(r, sun_unit(day, frac))

        start, end = 0.0, 1440.0
        t, m = start, margin(start)
        step = 1.0 / 60.0
        while t < end - 1e-12:
            t2 = min(t + step, end)
            m2 = margin(t2)
            if (m < 0) != (m2 < 0):
                lo, hi, mlo = t, t2, m
                while (hi - lo) * 60.0 > 1e-7:
                    mid = 0.5 * (lo + hi)
                    mm = margin(mid)
                    if (mm < 0) == (mlo < 0):
                        lo, mlo = mid, mm
                    else:
                        hi = mid
                kind = "enter" if m2 < 0 else "exit"
                rows.append([f"E-{l1[2:7]}", name, l1, l2, repr(start), repr(end), kind, repr(0.5 * (lo + hi))])
            t, m = t2, m2
    return rows


def write(path, header, rows):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(header)
        w.writerows(rows)


def main(inputs: Path, out: Path):
    rng = random.Random(20261022)
    tles = load_tles(inputs)
    write(out / "coords.csv",
          ["case", "jd_day", "jd_frac", "lat_deg", "lon_deg", "h_km",
           "rx_teme", "ry_teme", "rz_teme", "vx_teme", "vy_teme", "vz_teme", "gmst_rad",
           "rx_ecef", "ry_ecef", "rz_ecef", "vx_ecef", "vy_ecef", "vz_ecef",
           "ox_ecef", "oy_ecef", "oz_ecef", "az_deg", "el_deg", "range_km", "range_rate_km_s"],
          coords_rows(tles, rng))
    write(out / "sun.csv",
          ["case", "jd_day", "jd_frac", "lat_deg", "lon_deg", "sx", "sy", "sz", "sun_el_deg",
           "rx_teme", "ry_teme", "rz_teme", "in_shadow"],
          sun_rows(tles, rng))
    ev = shadow_event_rows(tles)
    write(out / "shadow_events.csv",
          ["case", "name", "line1", "line2", "start_tsince_min", "end_tsince_min", "event", "tsince_min"], ev)
    print(f"coords 132, sun 120, shadow events {len(ev)}")


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
