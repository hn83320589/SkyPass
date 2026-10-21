"""產生「逐步手算範例」文件（docs/hand-calculations.md）與其最終值（hand_examples.csv，供 C# 測試核對）。

每一步都依「假設事項」的公式以 Python 逐項計算並列出中間值。

用法：python -I gen_hand_examples.py <docs 目錄> <TestData 目錄>
"""
import csv
import math
import sys
from pathlib import Path

OMEGA = 7.2921158553e-5
A84 = 6378.137
F84 = 1.0 / 298.257223563
C = 299792.458


def f(x, digits=10):
    return f"{x:.{digits}f}"


class Doc:
    def __init__(self):
        self.lines = []
        self.rows = []

    def h(self, text):
        self.lines += ["", text, ""]

    def step(self, text):
        self.lines.append(f"- {text}")

    def result(self, section, ex_id, inputs: dict, outputs: dict):
        self.rows.append([section, ex_id,
                          ";".join(f"{k}={v!r}" for k, v in inputs.items()),
                          ";".join(f"{k}={v!r}" for k, v in outputs.items())])


# ---------------- 1. UTC → 儒略日 ----------------
TIME_CASES = [
    (2000, 1, 1, 12, 0, 0), (2000, 2, 29, 0, 0, 0), (2000, 3, 1, 6, 30, 0), (2100, 2, 28, 0, 0, 0),
    (2100, 3, 1, 0, 0, 0), (2024, 12, 31, 23, 59, 59), (2025, 1, 1, 0, 0, 0), (2026, 10, 21, 10, 50, 0),
    (1957, 10, 4, 19, 28, 34), (2056, 12, 31, 18, 0, 0),
]


def time_section(d: Doc):
    d.h("## 1. UTC → 儒略日（格里曆，Fliegel–Van Flandern 整數演算法）")
    d.lines.append("JDN = ⌊1461(Y+4800+a)/4⌋ + ⌊367(M−2−12a)/12⌋ − ⌊3⌊(Y+4900+a)/100⌋/4⌋ + D − 32075，其中 a = ⌊(M−14)/12⌋（向零取整）；"
                   "當日 0h 的儒略日 = JDN − 0.5，日內比例 = (時·3600 + 分·60 + 秒) / 86400。")
    for i, (y, mo, day, hh, mm, ss) in enumerate(TIME_CASES, 1):
        a = int((mo - 14) / 12)
        t1 = (1461 * (y + 4800 + a)) // 4
        t2 = (367 * (mo - 2 - 12 * a)) // 12
        t3 = (3 * ((y + 4900 + a) // 100)) // 4
        jdn = t1 + t2 - t3 + day - 32075
        frac = (hh * 3600 + mm * 60 + ss) / 86400
        d.h(f"### T{i}：{y:04d}-{mo:02d}-{day:02d} {hh:02d}:{mm:02d}:{ss:02d} UTC")
        d.step(f"a = ⌊({mo} − 14) / 12⌋ = {a}")
        d.step(f"⌊1461·({y} + 4800 + {a}) / 4⌋ = {t1}")
        d.step(f"⌊367·({mo} − 2 − 12·{a}) / 12⌋ = {t2}")
        d.step(f"⌊3·⌊({y} + 4900 + {a}) / 100⌋ / 4⌋ = {t3}")
        d.step(f"JDN = {t1} + {t2} − {t3} + {day} − 32075 = {jdn}；當日 0h 儒略日 = {jdn - 0.5}")
        d.step(f"日內比例 = ({hh}·3600 + {mm}·60 + {ss}) / 86400 = {f(frac, 12)}")
        d.step(f"**JD = {jdn - 0.5} + {f(frac, 12)} = {f(jdn - 0.5 + frac, 8)}**")
        d.result("time", f"T{i}", {"utc": f"{y:04d}-{mo:02d}-{day:02d}T{hh:02d}:{mm:02d}:{ss:02d}Z"},
                 {"jd_day": jdn - 0.5, "jd_frac": frac})


# ---------------- 2. GMST ----------------
def gmst_parts(day, frac):
    t = ((day - 2451545.0) + frac) / 36525.0
    c3 = -6.2e-6 * t ** 3
    c2 = 0.093104 * t ** 2
    c1 = (876600.0 * 3600 + 8640184.812866) * t
    sec = c3 + c2 + c1 + 67310.54841
    raw = sec * math.pi / 180.0 / 240.0
    g = math.fmod(raw, 2 * math.pi)
    if g < 0:
        g += 2 * math.pi
    return t, c3, c2, c1, sec, raw, g


def gmst_section(d: Doc):
    d.h("## 2. GMST（IAU-82）")
    d.lines.append("T = (JD − 2451545.0) / 36525；θ秒 = −6.2e-6·T³ + 0.093104·T² + (876600·3600 + 8640184.812866)·T + 67310.54841；"
                   "GMST = (θ秒 · π / 180 / 240) mod 2π。儒略日以「當日 0h + 日內比例」兩段相加，避免精度損失。")
    for i, (y, mo, day, hh, mm, ss) in enumerate(TIME_CASES, 1):
        a = int((mo - 14) / 12)
        jdn = (1461 * (y + 4800 + a)) // 4 + (367 * (mo - 2 - 12 * a)) // 12 - (3 * ((y + 4900 + a) // 100)) // 4 + day - 32075
        jd_day, frac = jdn - 0.5, (hh * 3600 + mm * 60 + ss) / 86400
        t, c3, c2, c1, sec, raw, g = gmst_parts(jd_day, frac)
        d.h(f"### G{i}：JD = {jd_day} + {f(frac, 12)}（{y:04d}-{mo:02d}-{day:02d} {hh:02d}:{mm:02d}:{ss:02d} UTC）")
        d.step(f"T = (({jd_day} − 2451545.0) + {f(frac, 12)}) / 36525 = {t:.15e}")
        d.step(f"−6.2e-6·T³ = {c3:.6e}；0.093104·T² = {c2:.6e}；(3164400000 + 8640184.812866)·T = {f(c1, 6)}")
        d.step(f"θ秒 = {f(sec, 6)} s")
        d.step(f"θ秒·π/180/240 = {f(raw, 9)} rad")
        d.step(f"**GMST = {f(g, 12)} rad = {f(math.degrees(g), 9)}°**")
        d.result("gmst", f"G{i}", {"jd_day": jd_day, "jd_frac": frac}, {"gmst_rad": g})


# ---------------- 3. 座標 ----------------
COORD_CASES = [
    # 觀測點 (lat, lon, h_km)、衛星 TEME r (km)、v (km/s)、GMST (rad)
    ((25.0330, 121.5654, 0.010), (-4400.0, 3200.0, 3700.0), (-1.5, -6.0, 3.5), 1.0),
    ((25.0330, 121.5654, 0.010), (-2000.0, 5500.0, 3300.0), (-6.5, -2.5, 1.0), 2.5),
    ((0.0, 0.0, 0.0), (7000.0, 0.0, 0.0), (0.0, 7.5, 0.0), 0.0),
    ((0.0, 0.0, 0.0), (6878.0, 300.0, 400.0), (0.0, 1.0, 7.5), 0.0),
    ((89.0, 45.0, 0.0), (500.0, 500.0, 7000.0), (5.0, -5.0, 0.0), 0.3),
    ((-33.8688, 151.2093, 0.058), (-4600.0, 2700.0, -4300.0), (3.0, 5.5, -1.5), 0.2),
    ((-77.85, 166.67, 0.024), (-1300.0, 600.0, -6900.0), (7.0, 2.0, -1.0), 0.1),
    ((0.0, 179.9999, 0.0), (-6900.0, 100.0, 600.0), (-0.5, -7.0, 2.5), 0.0),
    ((0.0, -179.9999, 0.0), (-6900.0, -100.0, -600.0), (0.5, 7.0, -2.5), 0.0),
    ((31.5, 35.5, -0.430), (4800.0, 3400.0, 3900.0), (-5.0, 5.0, 2.0), 0.0),
]


def coord_section(d: Doc):
    d.h("## 3. 座標轉換：TEME → 地固、大地 → 地固、站心方位角／仰角／距離／距離變化率")
    d.lines.append("x' = cosθ·x + sinθ·y，y' = −sinθ·x + cosθ·y，z' = z；v_ecef = R(θ)·v_teme + (ω·y', −ω·x', 0)，ω = 7.2921158553e-5 rad/s。"
                   "大地 → 地固：N = a / √(1 − e²sin²φ)，x = (N+h)cosφcosλ，y = (N+h)cosφsinλ，z = (N(1−e²)+h)sinφ（WGS-84）。"
                   "站心：ρ = r_sat − r_obs；S = sinφcosλ·ρx + sinφsinλ·ρy − cosφ·ρz，E = −sinλ·ρx + cosλ·ρy，"
                   "Z = cosφcosλ·ρx + cosφsinλ·ρy + sinφ·ρz；仰角 = asin(Z/|ρ|)，方位角 = atan2(E, −S)，距離變化率 = ρ·v / |ρ|。")
    e2 = F84 * (2 - F84)
    for i, ((lat, lon, h), r, v, th) in enumerate(COORD_CASES, 1):
        c, s = math.cos(th), math.sin(th)
        xp, yp = c * r[0] + s * r[1], -s * r[0] + c * r[1]
        vxp, vyp = c * v[0] + s * v[1], -s * v[0] + c * v[1]
        re = (xp, yp, r[2])
        ve = (vxp + OMEGA * yp, vyp - OMEGA * xp, v[2])
        phi, lam = math.radians(lat), math.radians(lon)
        n = A84 / math.sqrt(1 - e2 * math.sin(phi) ** 2)
        obs = ((n + h) * math.cos(phi) * math.cos(lam), (n + h) * math.cos(phi) * math.sin(lam), (n * (1 - e2) + h) * math.sin(phi))
        rho = [re[k] - obs[k] for k in range(3)]
        sp, cp, sl, cl = math.sin(phi), math.cos(phi), math.sin(lam), math.cos(lam)
        S = sp * cl * rho[0] + sp * sl * rho[1] - cp * rho[2]
        E = -sl * rho[0] + cl * rho[1]
        Z = cp * cl * rho[0] + cp * sl * rho[1] + sp * rho[2]
        rng = math.sqrt(sum(x * x for x in rho))
        el = math.degrees(math.asin(Z / rng))
        az = math.degrees(math.atan2(E, -S)) % 360.0
        rate = sum(rho[k] * ve[k] for k in range(3)) / rng
        d.h(f"### C{i}：觀測點 ({lat}°, {lon}°, {h} km)，θ = {th} rad")
        d.step(f"r_TEME = {r} km，v_TEME = {v} km/s；cosθ = {f(c)}，sinθ = {f(s)}")
        d.step(f"r_ECEF = ({f(re[0], 6)}, {f(re[1], 6)}, {f(re[2], 6)}) km")
        d.step(f"R(θ)·v = ({f(vxp, 9)}, {f(vyp, 9)}, {v[2]})；加上 (ω·y', −ω·x', 0) → v_ECEF = ({f(ve[0], 9)}, {f(ve[1], 9)}, {f(ve[2], 9)}) km/s")
        d.step(f"N = {f(n, 6)} km；r_obs = ({f(obs[0], 6)}, {f(obs[1], 6)}, {f(obs[2], 6)}) km")
        d.step(f"ρ = ({f(rho[0], 6)}, {f(rho[1], 6)}, {f(rho[2], 6)}) km，|ρ| = {f(rng, 6)} km")
        d.step(f"S = {f(S, 6)}，E = {f(E, 6)}，Z = {f(Z, 6)}")
        d.step(f"**仰角 = asin({f(Z, 4)} / {f(rng, 4)}) = {f(el, 8)}°；方位角 = atan2({f(E, 4)}, {f(-S, 4)}) = {f(az, 8)}°**")
        d.step(f"**距離變化率 = ρ·v / |ρ| = {f(rate, 10)} km/s**")
        d.result("coord", f"C{i}", {"lat": lat, "lon": lon, "h": h, "rx": r[0], "ry": r[1], "rz": r[2],
                                    "vx": v[0], "vy": v[1], "vz": v[2], "gmst": th},
                 {"ex": re[0], "ey": re[1], "ez": re[2], "evx": ve[0], "evy": ve[1], "evz": ve[2],
                  "ox": obs[0], "oy": obs[1], "oz": obs[2], "az": az, "el": el, "range": rng, "rate": rate})


# ---------------- 4. 都卜勒 ----------------
DOPPLER_CASES = [(145.8e6, r) for r in (-7.5, -5.0, -3.0, -1.0, -0.25, 0.0, 0.25, 1.0, 3.0, 7.5)]


def doppler_section(d: Doc):
    d.h("## 4. 都卜勒")
    d.lines.append("接收頻率 = f0 × (1 − ṙ / c)，c = 299792.458 km/s；ṙ < 0 表示接近，頻率升高。")
    for i, (f0, rr) in enumerate(DOPPLER_CASES, 1):
        ratio = rr / C
        rx = f0 * (1 - ratio)
        d.h(f"### D{i}：f0 = {f0 / 1e6:.3f} MHz，ṙ = {rr} km/s")
        d.step(f"ṙ / c = {rr} / {C} = {ratio:.12e}")
        d.step(f"1 − ṙ/c = {f(1 - ratio, 12)}")
        d.step(f"**接收頻率 = {f(rx, 4)} Hz，頻移 = {f(rx - f0, 4)} Hz**")
        d.result("doppler", f"D{i}", {"f0": f0, "rate": rr}, {"rx": rx})


# ---------------- 5. 太陽向量與圓柱陰影 ----------------
SHADOW_CASES = [
    # (JD 當日 0h, 日內比例, r_TEME km)
    (2460969.5, 0.0, (7000.0, 0.0, 0.0)),
    (2460969.5, 0.0, (-7000.0, 0.0, 0.0)),
    (2460969.5, 0.25, (-5000.0, 3000.0, 1500.0)),
    (2460969.5, 0.5, (4000.0, -5000.0, 2500.0)),
    (2460755.5, 0.375, (-6800.0, -500.0, 300.0)),
    (2460755.5, 0.375, (-6800.0, 0.0, 6000.0)),
    (2460847.5, 0.5, (2000.0, -6700.0, -2600.0)),
    (2460847.5, 0.5, (-1000.0, 6800.0, 2800.0)),
    (2461031.5, 0.125, (-200.0, 6500.0, 2900.0)),
    (2461031.5, 0.125, (300.0, -6600.0, -2700.0)),
]


def sun_parts(day, frac):
    n = (day - 2451545.0) + frac
    L = (280.460 + 0.9856474 * n) % 360.0
    g = (357.528 + 0.9856003 * n) % 360.0
    lam = (L + 1.915 * math.sin(math.radians(g)) + 0.020 * math.sin(math.radians(2 * g))) % 360.0
    eps = 23.439 - 0.0000004 * n
    s = (math.cos(math.radians(lam)), math.cos(math.radians(eps)) * math.sin(math.radians(lam)),
         math.sin(math.radians(eps)) * math.sin(math.radians(lam)))
    return n, L, g, lam, eps, s


def shadow_section(d: Doc):
    d.h("## 5. 太陽向量與圓柱陰影判定")
    d.lines.append("n = JD − 2451545.0；L = 280.460° + 0.9856474°·n；g = 357.528° + 0.9856003°·n；λ = L + 1.915°·sin g + 0.020°·sin 2g；"
                   "ε = 23.439° − 0.0000004°·n；ŝ = (cos λ, cos ε·sin λ, sin ε·sin λ)。若 r·ŝ < 0 且 |r − (r·ŝ)ŝ| < 6378.137 km，衛星在陰影內。")
    for i, (day, frac, r) in enumerate(SHADOW_CASES, 1):
        n, L, g, lam, eps, s = sun_parts(day, frac)
        dot = sum(r[k] * s[k] for k in range(3))
        perp = [r[k] - dot * s[k] for k in range(3)]
        dist = math.sqrt(sum(x * x for x in perp))
        shadow = dot < 0 and dist < 6378.137
        d.h(f"### S{i}：JD = {day} + {frac}，r = {r} km")
        d.step(f"n = {f(n, 6)}；L = {f(L, 6)}°；g = {f(g, 6)}°；λ = {f(lam, 6)}°；ε = {f(eps, 8)}°")
        d.step(f"ŝ = ({f(s[0])}, {f(s[1])}, {f(s[2])})")
        d.step(f"r·ŝ = {f(dot, 6)} km")
        d.step(f"r − (r·ŝ)ŝ = ({f(perp[0], 4)}, {f(perp[1], 4)}, {f(perp[2], 4)})，長度 = {f(dist, 6)} km")
        verdict = "在陰影內" if shadow else ("受日照（r·ŝ ≥ 0，位於向陽側）" if dot >= 0 else "受日照（在陰影圓柱之外）")
        d.step(f"**{verdict}**")
        d.result("shadow", f"S{i}", {"jd_day": day, "jd_frac": frac, "rx": r[0], "ry": r[1], "rz": r[2]},
                 {"sx": s[0], "sy": s[1], "sz": s[2], "in_shadow": int(shadow)})


def main(docs: Path, testdata: Path):
    d = Doc()
    d.lines += ["# 逐步手算範例", "",
                "由 `tools/refgen/gen_hand_examples.py` 依「假設事項」的公式逐步計算產生；",
                "各範例的最終值同時寫入 `tests/SkyPass.Tests/TestData/hand_examples.csv`，由 `HandCalculationTests` 核對程式結果。"]
    time_section(d)
    gmst_section(d)
    coord_section(d)
    doppler_section(d)
    shadow_section(d)
    docs.mkdir(parents=True, exist_ok=True)
    (docs / "hand-calculations.md").write_text("\n".join(d.lines) + "\n", encoding="utf-8")
    with open(testdata / "hand_examples.csv", "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh, delimiter="|", lineterminator="\n")
        w.writerow(["section", "id", "inputs", "outputs"])
        w.writerows(d.rows)
    print(f"{len(d.rows)} examples")


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
