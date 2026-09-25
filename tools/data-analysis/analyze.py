#!/usr/bin/env python3
"""BMW N20 OBD log analyzer.

Loads the two CSV fragments, concatenates by time, detects acquisition gaps,
classifies driving conditions, and prints statistics for the diagnostic report.
stdlib only.
"""

from __future__ import annotations

import csv
import math
import os
import statistics
from collections import defaultdict
from datetime import datetime
from typing import Any

DIR = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(DIR, "raw-data")
FILES = [
    os.path.join(RAW, "obdlog_20260922_231849.csv"),
    os.path.join(RAW, "obdlog_20260922_232546.csv"),
]

NUMERIC_FIELDS = [
    "RPM",
    "IdleTargetRPM",
    "VehicleSpeed",
    "EngineLoad",
    "EngineLoadRel",
    "ThrottlePosition",
    "ThrottleTarget",
    "ECT",
    "MAF",
    "BoostActual",
    "AmbientPressure",
    "LambdaActual",
    "LambdaTarget",
    "LambdaIntegrator",
    "LambdaAdaption",
    "InjQtyCyl1",
    "InjQtyCyl2",
    "InjQtyCyl3",
    "InjQtyCyl4",
    "MisfireCyl1",
    "MisfireCyl2",
    "MisfireCyl3",
    "MisfireCyl4",
    "VanosIntakePWM",
    "VanosExhaustPWM",
]

GAP_THRESHOLD_S = 3.0
ENGINE_ON_RPM = 400.0
COLD_WINDOW_S = 300.0


def parse_time(s: str) -> datetime:
    return datetime.strptime(s.strip(), "%Y-%m-%d %H:%M:%S.%f")


def to_float(v: str | None) -> float | None:
    if v is None:
        return None
    v = v.strip()
    if not v:
        return None
    try:
        return float(v)
    except ValueError:
        return None


def load_csv(path: str) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    with open(path, encoding="utf-8") as f:
        header = None
        for line in f:
            if line.startswith("#"):
                continue
            if header is None:
                header = [c.strip() for c in line.strip().split(",")]
                continue
            if line.startswith("[ms]"):
                continue
            parts = next(csv.reader([line]))
            rec: dict[str, Any] = {}
            for i, name in enumerate(header):
                rec[name] = parts[i] if i < len(parts) else ""
            rec["_source"] = os.path.basename(path)
            rec["_t"] = parse_time(rec["Time"])
            ok = True
            for name in NUMERIC_FIELDS:
                val = to_float(rec.get(name, ""))
                rec[name] = val
                if val is None and name in ("RPM", "ECT"):
                    ok = False
            rec["_valid"] = ok
            rows.append(rec)
    return rows


def mean(xs: list[float]) -> float | None:
    return statistics.mean(xs) if xs else None


def stdev(xs: list[float]) -> float | None:
    return statistics.pstdev(xs) if len(xs) >= 2 else (0.0 if xs else None)


def median(xs: list[float]) -> float | None:
    return statistics.median(xs) if xs else None


def pctl(xs: list[float], q: float) -> float | None:
    if not xs:
        return None
    ys = sorted(xs)
    i = min(len(ys) - 1, max(0, int(round(q * (len(ys) - 1)))))
    return ys[i]


def fmt(v: float | None, digits: int = 3) -> str:
    if v is None:
        return "n/a"
    return f"{v:.{digits}f}"


def pct_adapt(v: float | None) -> str:
    if v is None:
        return "n/a"
    return f"{(v - 1.0) * 100:+.2f}%"


def summarize(rows: list[dict[str, Any]], fields: list[str]) -> dict[str, dict[str, float | None]]:
    out: dict[str, dict[str, float | None]] = {}
    for name in fields:
        xs = [r[name] for r in rows if r.get(name) is not None]
        out[name] = {
            "n": len(xs),
            "min": min(xs) if xs else None,
            "p25": pctl(xs, 0.25),
            "median": median(xs),
            "mean": mean(xs),
            "p75": pctl(xs, 0.75),
            "max": max(xs) if xs else None,
            "stdev": stdev(xs),
        }
    return out


def print_summary(title: str, rows: list[dict[str, Any]], fields: list[str]) -> None:
    print(f"\n=== {title}  n={len(rows)} ===")
    if not rows:
        print("  (empty)")
        return
    stats = summarize(rows, fields)
    header = f"{'field':22s} {'n':>5s} {'min':>10s} {'p25':>10s} {'med':>10s} {'mean':>10s} {'p75':>10s} {'max':>10s} {'stdev':>10s}"
    print(header)
    for name, s in stats.items():
        print(
            f"{name:22s} {s['n']:5.0f} {fmt(s['min']):>10s} {fmt(s['p25']):>10s} "
            f"{fmt(s['median']):>10s} {fmt(s['mean']):>10s} {fmt(s['p75']):>10s} "
            f"{fmt(s['max']):>10s} {fmt(s['stdev']):>10s}"
        )


def pearson(xs: list[float], ys: list[float]) -> float | None:
    if len(xs) < 3 or len(xs) != len(ys):
        return None
    mx, my = mean(xs), mean(ys)
    if mx is None or my is None:
        return None
    num = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    dx = math.sqrt(sum((x - mx) ** 2 for x in xs))
    dy = math.sqrt(sum((y - my) ** 2 for y in ys))
    if dx == 0 or dy == 0:
        return None
    return num / (dx * dy)


def bin_mean(
    rows: list[dict[str, Any]],
    x_field: str,
    y_field: str,
    edges: list[float],
) -> list[tuple[str, int, float | None, float | None]]:
    buckets: list[list[tuple[float, float]]] = [[] for _ in range(len(edges) - 1)]
    for r in rows:
        x, y = r.get(x_field), r.get(y_field)
        if x is None or y is None:
            continue
        for i in range(len(edges) - 1):
            if edges[i] <= x < edges[i + 1]:
                buckets[i].append((x, y))
                break
    out = []
    for i, bucket in enumerate(buckets):
        label = f"[{edges[i]:g},{edges[i+1]:g})"
        n = len(bucket)
        mx = mean([p[0] for p in bucket])
        my = mean([p[1] for p in bucket])
        out.append((label, n, mx, my))
    return out


def classify(rows: list[dict[str, Any]]) -> None:
    engine_on = [r for r in rows if (r.get("RPM") or 0) >= ENGINE_ON_RPM]
    t0 = engine_on[0]["_t"] if engine_on else rows[0]["_t"]
    for r in rows:
        r["_t0s"] = (r["_t"] - t0).total_seconds()
        r["_gap"] = False
        rpm = r.get("RPM") or 0
        spd = r.get("VehicleSpeed") or 0
        ect = r.get("ECT")
        load = r.get("EngineLoad") or 0
        inj = r.get("InjQtyCyl1") or 0
        lam = r.get("LambdaActual") or 0
        r["_engine_on"] = rpm >= ENGINE_ON_RPM
        r["_fuel_cut"] = r["_engine_on"] and inj <= 0.2 and lam > 1.15
        r["_autostop"] = (not r["_engine_on"]) and spd > 0.5
        r["_cond"] = "other"

        if not r["_engine_on"]:
            r["_cond"] = "off"
            continue
        if r["_fuel_cut"]:
            r["_cond"] = "fuel_cut"
            continue

        cold = r["_t0s"] <= COLD_WINDOW_S
        idle_like = spd < 1.0 and 500 <= rpm <= 1600
        cruise = 60.0 <= spd <= 100.0 and 8 <= load <= 40
        accel = load >= 28 and (r.get("ThrottlePosition") or 0) >= 12

        if cold and idle_like:
            r["_cond"] = "A_cold_idle"
        elif cold and accel:
            r["_cond"] = "A_cold_accel"
        elif cold:
            r["_cond"] = "A_cold_other"
        elif idle_like and ect is not None and ect >= 75:
            r["_cond"] = "B_hot_idle"
        elif cruise:
            r["_cond"] = "C_cruise"
        elif accel:
            r["_cond"] = "D_accel"
        elif spd >= 5:
            r["_cond"] = "urban"
        else:
            r["_cond"] = "other"


def mark_gaps(rows: list[dict[str, Any]]) -> list[dict[str, Any]]:
    gaps = []
    for i in range(1, len(rows)):
        dt = (rows[i]["_t"] - rows[i - 1]["_t"]).total_seconds()
        rows[i]["_dt"] = dt
        if dt >= GAP_THRESHOLD_S:
            rows[i]["_gap"] = True
            gaps.append(
                {
                    "from": rows[i - 1]["Time"],
                    "to": rows[i]["Time"],
                    "dt": dt,
                    "empty_prev": not rows[i - 1]["_valid"],
                }
            )
        else:
            rows[i]["_gap"] = False
    if rows:
        rows[0]["_dt"] = None
        rows[0]["_gap"] = False
    return gaps


def rate_safe(rows: list[dict[str, Any]], field: str) -> list[float]:
    """Finite differences that skip acquisition gaps."""
    rates = []
    for i in range(1, len(rows)):
        if rows[i].get("_gap"):
            continue
        dt = rows[i].get("_dt")
        a, b = rows[i - 1].get(field), rows[i].get(field)
        if dt and dt > 0 and a is not None and b is not None:
            rates.append((b - a) / dt)
    return rates


def misfire_trend(rows: list[dict[str, Any]]) -> dict[str, Any]:
    out = {}
    for cyl in range(1, 5):
        field = f"MisfireCyl{cyl}"
        series = [(r["_t0s"], r[field]) for r in rows if r.get(field) is not None and r["_engine_on"]]
        if not series:
            out[field] = {"max": 0, "delta": 0, "first_nonzero_s": None}
            continue
        vals = [v for _, v in series]
        mx = max(vals)
        first_nz = next((t for t, v in series if v and v > 0), None)
        # BMW counters may reset across sessions; compute max observed and last-nonzero time
        out[field] = {
            "max": mx,
            "delta": mx - vals[0],
            "first_nonzero_s": first_nz,
            "last": vals[-1],
        }
    return out


def inj_spread(rows: list[dict[str, Any]]) -> dict[str, float | None]:
    spreads = []
    means = []
    cyl_means = {i: [] for i in range(1, 5)}
    for r in rows:
        qs = [r.get(f"InjQtyCyl{i}") for i in range(1, 5)]
        if any(q is None for q in qs):
            continue
        if all(q == 0 for q in qs):
            continue
        m = mean(qs)  # type: ignore[arg-type]
        if m is None or m < 0.5:
            continue
        spreads.append((max(qs) - min(qs)) / m * 100)  # type: ignore[arg-type]
        means.append(m)
        for i, q in enumerate(qs, 1):
            cyl_means[i].append(q)
    return {
        "n": len(spreads),
        "mean_spread_pct": mean(spreads),
        "max_spread_pct": max(spreads) if spreads else None,
        "mean_inj": mean(means),
        **{f"cyl{i}_mean": mean(cyl_means[i]) for i in range(1, 5)},
    }


def main() -> None:
    all_rows: list[dict[str, Any]] = []
    for path in FILES:
        all_rows.extend(load_csv(path))
    all_rows.sort(key=lambda r: r["_t"])

    n_invalid = sum(1 for r in all_rows if not r["_valid"])
    valid = [r for r in all_rows if r["_valid"]]
    gaps = mark_gaps(valid)
    classify(valid)

    t0 = valid[0]["_t"]
    t1 = valid[-1]["_t"]
    span = (t1 - t0).total_seconds()
    engine_on = [r for r in valid if r["_engine_on"]]
    start_on = next(r for r in valid if r["_engine_on"])
    t_start = start_on["_t"]

    print("=" * 80)
    print("BMW N20 OBD analysis  E84 / MEVD174K")
    print(f"files: {', '.join(os.path.basename(p) for p in FILES)}")
    print(f"rows total={len(all_rows)}  valid={len(valid)}  invalid/empty={n_invalid}")
    print(f"span: {valid[0]['Time']} -> {valid[-1]['Time']}  wall={span:.1f}s")
    print(f"engine-on start: {start_on['Time']}  ECT={start_on['ECT']}  RPM={start_on['RPM']}")
    print(f"engine-on samples: {len(engine_on)}")

    print("\n--- acquisition gaps (>= %.1fs) ---" % GAP_THRESHOLD_S)
    gap_total = 0.0
    for g in gaps:
        print(f"  {g['from']} -> {g['to']}  dt={g['dt']:.1f}s  empty_prev={g['empty_prev']}")
        gap_total += g["dt"]
    print(f"  gap count={len(gaps)}  gap_total={gap_total:.1f}s  recorded={span - gap_total:.1f}s")

    cond_count = defaultdict(int)
    for r in valid:
        cond_count[r["_cond"]] += 1
    print("\n--- condition counts ---")
    for k, v in sorted(cond_count.items(), key=lambda kv: -kv[1]):
        print(f"  {k:16s} {v:4d}")

    core = [
        "RPM",
        "IdleTargetRPM",
        "VehicleSpeed",
        "EngineLoad",
        "EngineLoadRel",
        "ThrottlePosition",
        "ECT",
        "MAF",
        "BoostActual",
        "AmbientPressure",
        "LambdaActual",
        "LambdaTarget",
        "LambdaIntegrator",
        "LambdaAdaption",
        "InjQtyCyl1",
        "InjQtyCyl2",
        "InjQtyCyl3",
        "InjQtyCyl4",
        "MisfireCyl1",
        "MisfireCyl2",
        "MisfireCyl3",
        "MisfireCyl4",
        "VanosIntakePWM",
        "VanosExhaustPWM",
    ]

    print_summary("ALL engine-on (excl fuel-cut)", [r for r in engine_on if not r["_fuel_cut"]], core)

    groups = {
        "A_cold first 5 min engine-on": [r for r in engine_on if r["_t0s"] <= COLD_WINDOW_S],
        "A_cold_idle": [r for r in valid if r["_cond"] == "A_cold_idle"],
        "B_hot_idle": [r for r in valid if r["_cond"] == "B_hot_idle"],
        "C_cruise 60-100": [r for r in valid if r["_cond"] == "C_cruise"],
        "D_accel/high load": [r for r in valid if r["_cond"] == "D_accel"],
        "urban moving": [r for r in valid if r["_cond"] == "urban"],
        "fuel_cut": [r for r in valid if r["_cond"] == "fuel_cut"],
        "warm engine-on ECT>=75 excl fuel-cut": [
            r for r in engine_on if (r.get("ECT") or 0) >= 75 and not r["_fuel_cut"]
        ],
    }
    for title, rs in groups.items():
        print_summary(title, rs, core)

    # ECT curve
    print("\n--- ECT timeline (engine-on, 30s buckets) ---")
    ect_buckets: dict[int, list[float]] = defaultdict(list)
    for r in engine_on:
        if r.get("ECT") is None:
            continue
        ect_buckets[int(r["_t0s"] // 30)].append(r["ECT"])
    for k in sorted(ect_buckets):
        xs = ect_buckets[k]
        print(f"  t={k*30:4d}-{(k+1)*30:4d}s  n={len(xs):3d}  ECT mean={mean(xs):6.2f}  min={min(xs):6.2f}  max={max(xs):6.2f}")

    # LambdaAdaption vs load / MAF
    warm = groups["warm engine-on ECT>=75 excl fuel-cut"]
    closed = [
        r
        for r in warm
        if r.get("LambdaActual") is not None
        and 0.7 <= r["LambdaActual"] <= 1.3
        and r.get("LambdaAdaption") is not None
    ]
    print("\n--- LambdaAdaption vs EngineLoad (warm closed-loop) ---")
    load_edges = [0, 8, 12, 16, 20, 25, 30, 35, 45, 80]
    for label, n, mx, my in bin_mean(closed, "EngineLoad", "LambdaAdaption", load_edges):
        print(f"  load {label:16s} n={n:4d}  meanLoad={fmt(mx,2)}  meanAdapt={fmt(my,4)} ({pct_adapt(my)})")

    print("\n--- LambdaAdaption vs MAF (warm closed-loop) ---")
    maf_edges = [0, 12, 16, 20, 30, 40, 55, 70, 100]
    for label, n, mx, my in bin_mean(closed, "MAF", "LambdaAdaption", maf_edges):
        print(f"  MAF  {label:16s} n={n:4d}  meanMAF={fmt(mx,1)}  meanAdapt={fmt(my,4)} ({pct_adapt(my)})")

    xs = [r["EngineLoad"] for r in closed]
    ys = [r["LambdaAdaption"] for r in closed]
    print(f"  corr(Load, Adapt) = {fmt(pearson(xs, ys), 3)}")
    xs = [r["MAF"] for r in closed]
    print(f"  corr(MAF, Adapt)  = {fmt(pearson(xs, ys), 3)}")

    # Adaption vs inj
    print("\n--- LambdaAdaption vs InjQty (warm closed-loop) ---")
    inj_edges = [0, 8, 12, 16, 20, 25, 30, 40, 60]
    for label, n, mx, my in bin_mean(closed, "InjQtyCyl1", "LambdaAdaption", inj_edges):
        print(f"  inj  {label:16s} n={n:4d}  meanInj={fmt(mx,2)}  meanAdapt={fmt(my,4)} ({pct_adapt(my)})")

    # Low vs high load adaption contrast (type classification)
    low = [r["LambdaAdaption"] for r in closed if r["EngineLoad"] < 15]
    mid = [r["LambdaAdaption"] for r in closed if 15 <= r["EngineLoad"] < 28]
    high = [r["LambdaAdaption"] for r in closed if r["EngineLoad"] >= 28]
    print("\n--- Adaption pattern ---")
    print(f"  low load <15%   n={len(low):3d}  mean={fmt(mean(low),4)} ({pct_adapt(mean(low))})  med={fmt(median(low),4)}")
    print(f"  mid 15-28%      n={len(mid):3d}  mean={fmt(mean(mid),4)} ({pct_adapt(mean(mid))})  med={fmt(median(mid),4)}")
    print(f"  high >=28%      n={len(high):3d}  mean={fmt(mean(high),4)} ({pct_adapt(mean(high))})  med={fmt(median(high),4)}")
    print("  type hint: type1 if low>>high; type2 if all similar positive; type3 if high>>low")

    # Integrator at idle
    hot_idle = groups["B_hot_idle"]
    print("\n--- hot idle closed-loop extras ---")
    if hot_idle:
        integ = [r["LambdaIntegrator"] for r in hot_idle if r.get("LambdaIntegrator") is not None]
        adapt = [r["LambdaAdaption"] for r in hot_idle if r.get("LambdaAdaption") is not None]
        rpm = [r["RPM"] for r in hot_idle]
        tgt = [r["IdleTargetRPM"] for r in hot_idle if r.get("IdleTargetRPM")]
        rpm_err = [r["RPM"] - (r["IdleTargetRPM"] or 0) for r in hot_idle]
        print(f"  Integrator mean={fmt(mean(integ),4)} ({pct_adapt(mean(integ))})  min={fmt(min(integ) if integ else None,4)} max={fmt(max(integ) if integ else None,4)}")
        print(f"  Adaption   mean={fmt(mean(adapt),4)} ({pct_adapt(mean(adapt))})")
        print(f"  RPM mean={fmt(mean(rpm),1)}  target={fmt(mean(tgt),1)}  err={fmt(mean(rpm_err),1)}  stdev={fmt(stdev(rpm),2)}")
        print(f"  MAF mean={fmt(mean([r['MAF'] for r in hot_idle]),2)} kg/h")
        print(f"  Load mean={fmt(mean([r['EngineLoad'] for r in hot_idle]),2)} %")
        print(f"  InjQty mean={fmt(mean([r['InjQtyCyl1'] for r in hot_idle]),2)} mg/Hub")
        # estimated idle L/h: qty(mg) * rpm/2 * 4cyl / 1e6 * 60 / 0.74
        inj_m = mean([r["InjQtyCyl1"] for r in hot_idle]) or 0
        rpm_m = mean(rpm) or 0
        g_per_h = inj_m * (rpm_m / 2.0) * 4 * 60 / 1000.0
        l_per_h = g_per_h / 740.0
        print(f"  estimated idle fuel ~ {g_per_h:.0f} g/h  ~ {l_per_h:.2f} L/h")

    # MAF vs EngineLoad
    print("\n--- MAF vs EngineLoad (warm, fueling) ---")
    for label, n, mx, my in bin_mean(closed, "EngineLoad", "MAF", load_edges):
        print(f"  load {label:16s} n={n:4d}  meanLoad={fmt(mx,2)}  meanMAF={fmt(my,1)}")
    print(f"  corr(Load, MAF) = {fmt(pearson([r['EngineLoad'] for r in closed], [r['MAF'] for r in closed]), 3)}")

    # Boost
    print("\n--- Boost vs EngineLoad (warm) ---")
    for label, n, mx, my in bin_mean(warm, "EngineLoad", "BoostActual", load_edges):
        amb = mean([r["AmbientPressure"] for r in warm if r.get("AmbientPressure")])
        gauge = (my - amb) if my is not None and amb is not None else None
        print(f"  load {label:16s} n={n:4d}  Boost={fmt(my,1)} hPa  gauge~{fmt(gauge,1)} hPa")
    print(f"  Boost max={fmt(max(r['BoostActual'] for r in warm if r.get('BoostActual')),1)}  Ambient mean={fmt(mean([r['AmbientPressure'] for r in warm if r.get('AmbientPressure')]),1)}")
    print(f"  corr(Load, Boost) = {fmt(pearson([r['EngineLoad'] for r in warm if r.get('BoostActual')], [r['BoostActual'] for r in warm if r.get('BoostActual')]), 3)}")
    print(f"  corr(MAF, Boost)  = {fmt(pearson([r['MAF'] for r in warm if r.get('BoostActual') and r.get('MAF')], [r['BoostActual'] for r in warm if r.get('BoostActual') and r.get('MAF')]), 3)}")

    # Misfire
    print("\n--- misfire ---")
    mt = misfire_trend(engine_on)
    for k, v in mt.items():
        print(f"  {k}: max={v['max']}  delta={v['delta']}  first_nonzero_s={v['first_nonzero_s']}  last={v['last']}")
    cold = groups["A_cold first 5 min engine-on"]
    print("  cold-window per-cyl max:")
    for i in range(1, 5):
        xs = [r[f"MisfireCyl{i}"] or 0 for r in cold]
        print(f"    cyl{i} max={max(xs) if xs else 0}")

    # Injection spread
    print("\n--- injection quantity spread ---")
    for title, rs in [
        ("cold idle", groups["A_cold_idle"]),
        ("hot idle", groups["B_hot_idle"]),
        ("urban", groups["urban moving"]),
        ("accel", groups["D_accel/high load"]),
        ("warm closed", closed),
    ]:
        s = inj_spread(rs)
        print(
            f"  {title:12s} n={s['n']:4.0f}  meanInj={fmt(s['mean_inj'],2)}  "
            f"spread% mean={fmt(s['mean_spread_pct'],2)} max={fmt(s['max_spread_pct'],2)}  "
            f"c1={fmt(s['cyl1_mean'],2)} c2={fmt(s['cyl2_mean'],2)} c3={fmt(s['cyl3_mean'],2)} c4={fmt(s['cyl4_mean'],2)}"
        )

    # VANOS
    print("\n--- VANOS vs load (warm, fueling) ---")
    for label, n, mx, my in bin_mean(closed, "EngineLoad", "VanosIntakePWM", load_edges):
        print(f"  IN  load {label:16s} n={n:4d}  PWM={fmt(my,1)}")
    for label, n, mx, my in bin_mean(closed, "EngineLoad", "VanosExhaustPWM", load_edges):
        print(f"  EX  load {label:16s} n={n:4d}  PWM={fmt(my,1)}")
    print(f"  corr(Load, IN PWM) = {fmt(pearson([r['EngineLoad'] for r in closed], [r['VanosIntakePWM'] for r in closed]), 3)}")
    print(f"  corr(Load, EX PWM) = {fmt(pearson([r['EngineLoad'] for r in closed], [r['VanosExhaustPWM'] for r in closed]), 3)}")

    # Cold start specifics
    print("\n--- cold start sequence ---")
    first_rpm = [r for r in valid if (r.get("RPM") or 0) > 50][:1]
    print(f"  first combustion row: {first_rpm[0]['Time'] if first_rpm else 'n/a'}")
    # RPM decay after start while speed=0
    first_60 = [r for r in engine_on if r["_t0s"] <= 60]
    print(f"  first 60s RPM mean={fmt(mean([r['RPM'] for r in first_60]),1)} min={fmt(min(r['RPM'] for r in first_60) if first_60 else None,1)} max={fmt(max(r['RPM'] for r in first_60) if first_60 else None,1)}")
    print(f"  first 60s IdleTarget mean={fmt(mean([r['IdleTargetRPM'] for r in first_60 if r.get('IdleTargetRPM')]),1)}")
    print(f"  first 60s Adapt mean={fmt(mean([r['LambdaAdaption'] for r in first_60 if r.get('LambdaAdaption')]),4)} ({pct_adapt(mean([r['LambdaAdaption'] for r in first_60 if r.get('LambdaAdaption')]))})")
    print(f"  first 60s Integrator mean={fmt(mean([r['LambdaIntegrator'] for r in first_60 if r.get('LambdaIntegrator')]),4)}")
    # lambda closed loop: when actual leaves default and integrator leaves 1
    closed_start = next(
        (r for r in engine_on if r.get("LambdaIntegrator") is not None and abs(r["LambdaIntegrator"] - 1.0) > 0.01),
        None,
    )
    if closed_start:
        print(f"  integrator leaves 1.00 at t={closed_start['_t0s']:.1f}s  {closed_start['Time']}  integ={closed_start['LambdaIntegrator']}")

    # speed / load envelope
    print("\n--- driving envelope ---")
    print(f"  speed max={fmt(max(r['VehicleSpeed'] or 0 for r in valid),1)} km/h")
    print(f"  load max={fmt(max(r['EngineLoad'] or 0 for r in engine_on),1)} %")
    print(f"  throttle max={fmt(max(r['ThrottlePosition'] or 0 for r in engine_on),1)} %")
    print(f"  MAF max={fmt(max(r['MAF'] or 0 for r in engine_on),1)} kg/h")
    print(f"  samples speed>=60: {sum(1 for r in valid if (r.get('VehicleSpeed') or 0) >= 60)}")
    print(f"  samples load>=40: {sum(1 for r in engine_on if (r.get('EngineLoad') or 0) >= 40)}")

    # Adaption time series coarse
    print("\n--- Adaption timeline (engine-on, 30s buckets, excl fuel-cut) ---")
    ad_buckets: dict[int, list[float]] = defaultdict(list)
    for r in engine_on:
        if r["_fuel_cut"] or r.get("LambdaAdaption") is None:
            continue
        ad_buckets[int(r["_t0s"] // 30)].append(r["LambdaAdaption"])
    for k in sorted(ad_buckets):
        xs = ad_buckets[k]
        print(f"  t={k*30:4d}-{(k+1)*30:4d}s  n={len(xs):3d}  adapt mean={mean(xs):.4f} ({pct_adapt(mean(xs))})  min={min(xs):.4f} max={max(xs):.4f}")

    print("\nDONE")


if __name__ == "__main__":
    main()
