"""產生時間與 GMST 參考資料（僅用 Python 標準函式庫）。

儒略日：以 datetime.toordinal()（proleptic 格里曆）換算，與 C# 的格里曆演算法互相獨立。
GMST：依 IAU-82 公式以 decimal（60 位有效數字）計算，避免 double 的捨入誤差。

用法：python -I gen_time.py <輸出目錄>
"""
import csv
import datetime as dt
import random
import sys
from decimal import Decimal, getcontext
from pathlib import Path

getcontext().prec = 60
PI = Decimal("3.14159265358979323846264338327950288419716939937510582097494459")
TWO_PI = 2 * PI


def julian_parts(t: dt.datetime):
    """回傳 (day, fraction)，day 為當日 0h 的儒略日（x.5），fraction 為日內比例。"""
    day = Decimal(t.toordinal()) + Decimal("1721424.5")
    secs = Decimal(t.hour * 3600 + t.minute * 60 + t.second) + Decimal(t.microsecond) / Decimal(10**6)
    return day, secs / Decimal(86400)


def gmst_rad(day: Decimal, frac: Decimal) -> Decimal:
    t = ((day - Decimal("2451545.0")) + frac) / Decimal(36525)
    theta_sec = (Decimal("-6.2e-6") * t**3 + Decimal("0.093104") * t**2
                 + (Decimal(876600 * 3600) + Decimal("8640184.812866")) * t + Decimal("67310.54841"))
    rad = theta_sec * PI / Decimal(180) / Decimal(240)
    rad = rad % TWO_PI  # Decimal 的 % 與被除數同號
    if rad < 0:
        rad += TWO_PI
    return rad


def fixed_cases():
    utc = dt.timezone.utc
    return [
        dt.datetime(2000, 1, 1, 12, 0, 0, tzinfo=utc),
        dt.datetime(2000, 2, 28, 23, 59, 59, tzinfo=utc),
        dt.datetime(2000, 2, 29, 0, 0, 0, tzinfo=utc),
        dt.datetime(2000, 2, 29, 18, 30, 0, tzinfo=utc),
        dt.datetime(2000, 3, 1, 0, 0, 0, tzinfo=utc),
        dt.datetime(2100, 2, 28, 12, 0, 0, tzinfo=utc),
        dt.datetime(2100, 3, 1, 0, 0, 0, tzinfo=utc),
        dt.datetime(1999, 12, 31, 23, 59, 59, 999000, tzinfo=utc),
        dt.datetime(2024, 12, 31, 23, 0, 0, tzinfo=utc),
        dt.datetime(2025, 1, 1, 0, 0, 0, tzinfo=utc),
        dt.datetime(2025, 1, 31, 23, 59, 30, tzinfo=utc),
        dt.datetime(2025, 2, 1, 0, 0, 30, tzinfo=utc),
        dt.datetime(1957, 10, 4, 19, 28, 34, tzinfo=utc),
        dt.datetime(1970, 1, 1, 0, 0, 0, tzinfo=utc),
        dt.datetime(2056, 12, 31, 23, 59, 59, tzinfo=utc),
    ]


def main(out_dir: Path):
    rng = random.Random(20261021)
    cases = fixed_cases()
    lo = dt.datetime(1957, 1, 1, tzinfo=dt.timezone.utc).timestamp()
    hi = dt.datetime(2099, 12, 31, tzinfo=dt.timezone.utc).timestamp()
    for _ in range(120):
        ts = rng.uniform(lo, hi)
        t = (dt.datetime(1970, 1, 1, tzinfo=dt.timezone.utc) + dt.timedelta(seconds=int(ts))).replace(microsecond=rng.randrange(0, 1000) * 1000)
        cases.append(t)

    out_dir.mkdir(parents=True, exist_ok=True)
    with open(out_dir / "time_gmst.csv", "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["utc_iso", "jd_day", "jd_fraction", "gmst_rad"])
        for t in cases:
            day, frac = julian_parts(t)
            w.writerow([t.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z",
                        f"{day:.1f}", f"{frac:.25f}", f"{gmst_rad(day, frac):.25f}"])
    print(f"wrote {len(cases)} cases")


if __name__ == "__main__":
    main(Path(sys.argv[1]))
