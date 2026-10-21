"""產生 SGP4 參考資料（使用 python-sgp4，版本見 requirements.txt）。

python-sgp4 的 `Satrec` 在有編譯擴充時直接呼叫 Vallado 的官方 C++ 程式碼（SGP4.cpp），
本腳本要求 `sgp4.api.accelerated == True`，確保參考值來自 C++ 參考實作。
傳播一律使用 `sgp4_tsince(分鐘)`，避免儒略日換算造成的差異。

輸出（全部以 repr 輸出 17 位有效數字）：
  sgp4_published.csv  已發表的驗證案例：SGP4-VER.TLE 的近地衛星（依其指定的時間點）
                      + CelesTrak 真實 TLE 快照（-1440 到 +2880 分鐘，每 120 分鐘）
  sgp4_random.csv     隨機近地軌道（固定種子），每組 6 個時間點

用法：python -I gen_sgp4.py <inputs 目錄> <輸出目錄>
"""
import csv
import math
import random
import sys
from pathlib import Path

import sgp4
from sgp4.api import WGS72, Satrec, accelerated

assert accelerated, "需要 python-sgp4 的 C++ 加速版本（Vallado 官方程式碼）"

HEADER = ["case", "source", "norad", "line1", "line2", "tsince_min", "error",
          "rx_km", "ry_km", "rz_km", "vx_km_s", "vy_km_s", "vz_km_s"]


def propagate_rows(case, source, line1, line2, times):
    sat = Satrec.twoline2rv(line1, line2, WGS72)
    assert sat.method == "n", f"{case} 不是近地軌道"
    rows = []
    for t in times:
        err, r, v = sat.sgp4_tsince(t)
        values = [repr(x) for x in (*r, *v)] if err == 0 else ["nan"] * 6
        rows.append([case, source, line1[2:7], line1, line2, repr(t), err, *values])
    return rows


def frange(start, stop, step):
    n = int(math.floor((stop - start) / step + 1e-9))
    return [start + i * step for i in range(n + 1)]


def published_rows(inputs: Path):
    rows = []
    lines = [l.rstrip("\n") for l in open(inputs / "SGP4-VER.TLE") if l.startswith(("1 ", "2 "))]
    for l1, l2 in zip(lines[0::2], lines[1::2]):
        sat = Satrec.twoline2rv(l1[:69], l2[:69], WGS72)
        if sat.method != "n":
            continue
        start, stop, step = (float(x) for x in l2[69:].split())
        times = frange(start, stop, step)
        if times[-1] != stop:
            times.append(stop)
        rows += propagate_rows(f"VER-{l1[2:7]}", "SGP4-VER.TLE", l1[:69], l2[:69], times)

    snap = [l.rstrip() for l in open(inputs / "celestrak-snapshot-20261021.tle", encoding="utf-8") if l.strip()]
    for i in range(0, len(snap), 3):
        name, l1, l2 = snap[i], snap[i + 1], snap[i + 2]
        rows += propagate_rows(f"CT-{l1[2:7]}", f"CelesTrak 2026-10-21 {name}", l1, l2, frange(-1440.0, 2880.0, 120.0))
    return rows


# ---------- 隨機 TLE（獨立於 C# 的格式化實作） ----------

def checksum(line68):
    return sum(int(c) if c.isdigit() else (1 if c == "-" else 0) for c in line68) % 10


def fmt_exp(value):
    if value == 0.0:
        return " 00000-0"
    e = math.floor(math.log10(abs(value))) + 1
    m = round(abs(value) / 10.0 ** e * 1e5)
    if m >= 100000:
        e += 1
        m = round(abs(value) / 10.0 ** e * 1e5)
    assert -9 <= e <= 9
    return ("-" if value < 0 else " ") + f"{m:05d}" + ("-" if e < 0 else "+") + str(abs(e))


def fmt_ndot(value):
    text = f"{abs(value):.8f}"
    assert text.startswith("0.")
    return ("-" if value < 0 else " ") + text[1:]


def make_tle(rng: random.Random, norad: int):
    xke = 60.0 / math.sqrt(6378.135 ** 3 / 398600.8)
    n = rng.uniform(6.6, 16.6)                                   # 圈/日（週期 87–218 分鐘）
    a_er = (xke / (n * 2 * math.pi / 1440.0)) ** (2.0 / 3.0)
    decaying = rng.random() < 0.08
    hp = rng.uniform(120.0, 260.0) if decaying else rng.uniform(150.0, 1500.0)
    ecc = 1.0 - (6378.135 + hp) / (a_er * 6378.135)
    if ecc < 0.0:
        ecc = rng.uniform(0.0, 0.02)
    ecc = min(ecc, 0.75)
    bstar = (1 if rng.random() < 0.85 else -1) * 10 ** rng.uniform(-6, -3)
    if decaying:
        bstar = 10 ** rng.uniform(-3, -1.5)
    ndot = rng.choice([1, -1]) * 10 ** rng.uniform(-8, -3)
    nddot = 0.0 if rng.random() < 0.7 else rng.choice([1, -1]) * 10 ** rng.uniform(-9, -5)
    year = rng.randint(1990, 2040)
    days = 366 if (year % 4 == 0 and (year % 100 != 0 or year % 400 == 0)) else 365
    epoch_day = rng.uniform(1.0, days + 1.0 - 1e-6)

    l1 = (f"1 {norad:05d}U 26001A   {year % 100:02d}{epoch_day:012.8f} {fmt_ndot(ndot)} "
          f"{fmt_exp(nddot)} {fmt_exp(bstar)} 0 {rng.randint(0, 9999):4d}")
    l2 = (f"2 {norad:05d} {rng.uniform(0, 180):8.4f} {rng.uniform(0, 359.9999):8.4f} {round(ecc * 1e7):07d} "
          f"{rng.uniform(0, 359.9999):8.4f} {rng.uniform(0, 359.9999):8.4f} {n:11.8f}{rng.randint(0, 99999):5d}")
    assert len(l1) == 68 and len(l2) == 68, (l1, l2)
    return l1 + str(checksum(l1)), l2 + str(checksum(l2))


def random_rows(count: int, seed: int):
    rng = random.Random(seed)
    rows = []
    case = 0
    while case < count:
        l1, l2 = make_tle(rng, 90000 + case)
        sat = Satrec.twoline2rv(l1, l2, WGS72)
        if sat.method != "n" or sat.error != 0:
            continue
        times = [0.0] + sorted(rng.uniform(-2880.0, 10080.0) for _ in range(5))
        rows += propagate_rows(f"RND-{case:04d}", f"random seed={seed}", l1, l2, times)
        case += 1
    return rows


def write(path: Path, rows):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f, lineterminator="\n")
        w.writerow(HEADER)
        w.writerows(rows)


def main(inputs: Path, out: Path):
    out.mkdir(parents=True, exist_ok=True)
    pub = published_rows(inputs)
    write(out / "sgp4_published.csv", pub)
    rnd = random_rows(320, seed=20261021)
    write(out / "sgp4_random.csv", rnd)
    print(f"sgp4 {sgp4.__version__}: published {len({r[0] for r in pub})} cases / {len(pub)} rows; "
          f"random {len({r[0] for r in rnd})} cases / {len(rnd)} rows; "
          f"error rows {sum(1 for r in pub + rnd if r[6] != 0)}")


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
