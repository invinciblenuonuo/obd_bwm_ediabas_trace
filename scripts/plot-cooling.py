#!/usr/bin/env python3
"""Plot an ObdLogger cooling CSV; write PNG, SVG, JSON and a Markdown report.

Requires matplotlib: python -m pip install matplotlib
Usage: python scripts/plot-cooling.py logs/cooling/obdlog_20261101_113645.csv
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import statistics
from collections import Counter
from datetime import datetime
from pathlib import Path


FIELDS = {
    "WaterPumpTargetAckRPM": "水泵目标转速确认值（目录单位 rpm；缩放待核对）",
    "WaterPumpActualRPM": "水泵实际转速（目录单位 1/min；缩放待核对）",
    "ECT": "水温（°C）",
    "CoolantTargetTemperature": "冷却液目标温度（°C）",
    "ThermostatHeaterPWM": "节温器加热 PWM（目录单位未定义）",
    "WaterPumpVoltage": "水泵供电电压（V）",
    "WaterPumpCurrent": "水泵电流（目录单位 A；待核对）",
    "WaterPumpElectronicsTemperature": "水泵电子模块温度（°C）",
    "WaterPumpStatus": "水泵原始状态字",
}
# MEVD174K.b1v: STATUS_MESSWERTE_EWAP / BasSvrAppl_stComPmp.
STATUS_BITS = {
    7: "解堵激活", 6: "供电电压异常", 5: "干运转", 4: "过流",
    3: "过温", 2: "锁止/解堵（Sperrung-Deblockierung）",
    1: "无转速监控", 0: "目标值锁止",
}


def number(value: str | None) -> float:
    try:
        result = float(value or "")
        return result if math.isfinite(result) else math.nan
    except ValueError:
        return math.nan


def load_csv(path: Path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        reader = csv.DictReader(line for line in stream if not line.lstrip().startswith("#"))
        missing = {"Time", *FIELDS} - set(reader.fieldnames or [])
        if missing:
            raise ValueError(f"Missing columns: {', '.join(sorted(missing))}")
        rows, units = [], {}
        for row in reader:
            # ObdLogger writes a units row immediately after the column names.
            if (row.get("Time") or "").startswith("["):
                units = {key: row.get(key, "") for key in FIELDS}
                continue
            if not any(row.values()):
                continue
            when = datetime.fromisoformat(row["Time"])
            rows.append((when, {key: number(row.get(key)) for key in FIELDS}))
    if len(rows) < 2:
        raise ValueError("At least two timestamped rows are required")
    if any(b[0] <= a[0] for a, b in zip(rows, rows[1:])):
        raise ValueError("Timestamps must be strictly increasing")
    return rows, units


def summarize(rows, units):
    intervals = [(b[0] - a[0]).total_seconds() for a, b in zip(rows, rows[1:])]
    median = statistics.median(intervals)
    threshold = max(1.0, 5 * median)
    gaps = [dict(start=a[0].isoformat(sep=" "), end=b[0].isoformat(sep=" "), seconds=dt)
            for a, b, dt in zip(rows, rows[1:], intervals) if dt > threshold]
    columns = {}
    for key in FIELDS:
        values = [r[key] for _, r in rows if math.isfinite(r[key])]
        columns[key] = dict(valid=len(values), missing=len(rows) - len(values),
                            minimum=min(values) if values else None,
                            maximum=max(values) if values else None,
                            first=values[0] if values else None, last=values[-1] if values else None,
                            unique=len(set(values)), unit=units.get(key, ""))
    valid_temperature = [(t, r["ECT"]) for t, r in rows if math.isfinite(r["ECT"])]
    peak = max(valid_temperature, key=lambda x: x[1]) if valid_temperature else None
    states = Counter(int(r["WaterPumpStatus"]) for _, r in rows
                     if math.isfinite(r["WaterPumpStatus"]))
    transitions, previous = [], None
    for when, row in rows:
        value = row["WaterPumpStatus"]
        if math.isfinite(value):
            state = int(value)
            if state != previous:
                transitions.append(dict(time=when.isoformat(sep=" "), value=state))
            previous = state
    duration = (rows[-1][0] - rows[0][0]).total_seconds()
    pairs = [r for _, r in rows if math.isfinite(r["WaterPumpTargetAckRPM"])
             and math.isfinite(r["WaterPumpActualRPM"]) and r["WaterPumpTargetAckRPM"] > 0]
    return dict(start=rows[0][0].isoformat(sep=" "), end=rows[-1][0].isoformat(sep=" "),
                duration_seconds=duration, rows=len(rows),
                rows_with_all_fields=sum(all(math.isfinite(v) for v in r.values()) for _, r in rows),
                wall_clock_hz=(len(rows)-1)/duration, median_interval_hz=1/median,
                gap_threshold_seconds=threshold, gaps=gaps, columns=columns,
                temperature_peak=dict(time=peak[0].isoformat(sep=" "), value=peak[1]) if peak else None,
                status_counts=dict(states), status_transitions=transitions,
                positive_target_samples=len(pairs),
                zero_actual_with_positive_target=sum(r["WaterPumpActualRPM"] == 0 for r in pairs))


def plot(rows, summary, output: Path):
    try:
        import matplotlib
        matplotlib.use("Agg")
        import matplotlib.dates as mdates
        import matplotlib.pyplot as plt
    except ImportError as error:
        raise SystemExit("Install plotting dependency: python -m pip install matplotlib") from error
    plt.rcParams.update({"font.sans-serif": ["Microsoft YaHei", "SimHei", "DejaVu Sans"],
                         "axes.unicode_minus": False, "font.size": 10})
    fig, axes = plt.subplots(7, 1, figsize=(15, 18), sharex=True, layout="constrained")
    times, data = [], {key: [] for key in FIELDS}
    previous = None
    for when, row in rows:
        # Insert NaNs so long acquisition gaps are never bridged by a line.
        if previous and (when-previous).total_seconds() > summary["gap_threshold_seconds"]:
            times.append(previous + (when-previous)/2)
            for key in FIELDS:
                data[key].append(math.nan)
        times.append(when)
        for key in FIELDS:
            data[key].append(row[key])
        previous = when

    def line(ax, key, color, label=None, **kwargs):
        ax.plot(times, data[key], color=color, label=label or FIELDS[key], linewidth=1.25, **kwargs)

    line(axes[0], "ECT", "#c0392b")
    line(axes[0], "CoolantTargetTemperature", "#2471a3", linestyle="--")
    axes[0].set_ylabel("温度 / °C")
    peak = summary["temperature_peak"]
    if peak:
        when = datetime.fromisoformat(peak["time"])
        axes[0].annotate(f"峰值 {peak['value']:g}°C", xy=(when, peak["value"]),
                         xytext=(-100, 18), textcoords="offset points",
                         arrowprops={"arrowstyle": "->"}, fontsize=10)
    line(axes[1], "WaterPumpTargetAckRPM", "#8e44ad", "目标确认值（左轴；未校准）")
    actual_axis = axes[1].twinx()
    line(actual_axis, "WaterPumpActualRPM", "#148f77", "实际值（右轴；未校准）")
    axes[1].set_ylabel("目标确认值 / 目录 rpm", color="#8e44ad")
    actual_axis.set_ylabel("实际值 / 目录 1/min", color="#148f77")
    axes[1].set_title("水泵两列使用不同纵轴；不计算转速跟随误差")
    for ax, key, color, ylabel in [
        (axes[2], "ThermostatHeaterPWM", "#d68910", "加热控制 / 目录未定义单位"),
        (axes[3], "WaterPumpVoltage", "#2471a3", "电压 / V"),
        (axes[4], "WaterPumpCurrent", "#148f77", "电流 / 目录 A"),
        (axes[5], "WaterPumpElectronicsTemperature", "#a04000", "模块温度 / °C"),
        (axes[6], "WaterPumpStatus", "#566573", "原始状态字"),
    ]:
        line(ax, key, color, **({"drawstyle": "steps-post"} if key == "WaterPumpStatus" else {}))
        ax.set_ylabel(ylabel)
    for ax in axes:
        ax.grid(alpha=0.25)
        handles, labels = ax.get_legend_handles_labels()
        if ax is axes[1]:
            h, lab = actual_axis.get_legend_handles_labels()
            handles, labels = handles + h, labels + lab
        ax.legend(handles, labels, loc="upper left", fontsize=9)
        for gap in summary["gaps"]:
            ax.axvspan(datetime.fromisoformat(gap["start"]), datetime.fromisoformat(gap["end"]),
                       color="#999999", alpha=0.18)
    axes[-1].xaxis.set_major_locator(mdates.AutoDateLocator(minticks=6, maxticks=12))
    axes[-1].xaxis.set_major_formatter(mdates.DateFormatter("%H:%M:%S"))
    axes[-1].set_xlabel("日志本地时间；灰色区域为采样间隔，未插值")
    fig.suptitle(f"冷却系统采集曲线 — {output.name}\n"
                 f"{summary['rows_with_all_fields']} 条完整数据 | "
                 f"全时段 {summary['wall_clock_hz']:.2f} Hz | "
                 f"中位间隔对应 {summary['median_interval_hz']:.2f} Hz", fontsize=15)
    fig.savefig(output.with_suffix(".png"), dpi=160)
    fig.savefig(output.with_suffix(".svg"))
    plt.close(fig)


def report(summary, source: Path, output: Path):
    s = summary
    lines = [f"# 冷却系统数据分析：{source.stem}", "", f"输入文件：`{source.resolve()}`", "",
             f"用户提供的工况：{s.get('context', '未提供')}。", "",
             f"时间：{s['start']} ～ {s['end']}；跨度 {s['duration_seconds']:.3f} 秒。",
             f"共 {s['rows']} 行时间记录，{s['rows_with_all_fields']} 行完整数据；"
             f"全时段频率 {s['wall_clock_hz']:.3f} Hz，中位间隔对应 {s['median_interval_hz']:.3f} Hz。", "",
             "## 数据质量", ""]
    for gap in s["gaps"]:
        lines.append(f"- 较长采样间隔：{gap['start']} ～ {gap['end']}，{gap['seconds']:.3f} 秒；曲线断开显示。")
    if not s["gaps"]:
        lines.append("未发现超过自适应阈值的较长采样间隔。")
    lines += ["", "## 各参数统计", "", "| 参数 | 首值 | 末值 | 最小 | 最大 | 缺失行 |",
              "|---|---:|---:|---:|---:|---:|"]
    for key, title in FIELDS.items():
        col = s["columns"][key]
        values = [f"{col[k]:g}" if col[k] is not None else "缺失" for k in ("first", "last", "minimum", "maximum")]
        lines.append(f"| {title} | {' | '.join(values)} | {col['missing']} |")
    lines += ["", "## 从本次记录可确认的现象", ""]
    if s["temperature_peak"]:
        peak = s["temperature_peak"]
        lines.append(f"- 水温峰值为 {peak['value']:g}°C，首次出现于 {peak['time']}。")
    lines.append(f"- 目标确认值大于零的 {s['positive_target_samples']} 个样本中，"
                 f"实际转速列为零的有 {s['zero_actual_with_positive_target']} 个；这不是经校准的机械转速跟随判定。")
    for state, count in s["status_counts"].items():
        decoded = "；".join(text for bit, text in STATUS_BITS.items() if state & (1 << bit)) or "无置位"
        lines.append(f"- 状态字 {state}（0x{state:02X}）：{count} 个样本；按项目 MEVD174K 定义：{decoded}。")
    lines += ["", "状态变化（中间缺失行不代表状态复位）：", ""]
    lines += [f"- {item['time']} → {item['value']}" for item in s["status_transitions"]]
    lines += ["", "## 判读边界", "",
              "- 图中所有数值沿用 CSV，未对水泵转速额外乘系数。目标/实际两列的单位及缩放须与同车 ISTA 或专用读取作业对照，不能直接据此计算转速差、认定堵转或更换水泵。",
              "- 节温器 PWM 是加热控制，不是机械阀门开度；控制值变化不能证明阀门已动作。目录未定义其单位，图中不强行标为百分比。",
              "- 项目测量目录将水泵电流标为 A，而专用作业描述存在不同单位，须核实。电流恒零、模块温度恒定时，应同时考虑数据更新、映射/缩放、通信和部件状态。",
              "- 状态位译自 doc/材料/MEVD174K.b1v 的 STATUS_MESSWERTE_EWAP；4=bit2，锁止/解堵相关。它不等于干运转（bit5）或过温（bit3），也不能单独证明机械卡死。",
              "- 日志不包含发动机转速、车速、风扇控制、散热器进出口温度、冷却液液位和故障码；不能确认运行工况、循环流量或单独定位水泵/节温器故障。",
              "- 目标温度为零时原样保留；不能解释为真实的 0°C 温控目标。", ""]
    output.with_suffix(".md").write_text("\n".join(lines), encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv", type=Path, help="ObdLogger cooling CSV")
    parser.add_argument("--out-dir", type=Path, help="Default: <CSV directory>/analysis")
    parser.add_argument("--context", default="未提供", help="Known operating conditions for the report")
    args = parser.parse_args()
    rows, units = load_csv(args.csv)
    summary = summarize(rows, units)
    summary["context"] = args.context
    directory = args.out_dir or args.csv.parent / "analysis"
    directory.mkdir(parents=True, exist_ok=True)
    output = directory / (args.csv.stem + "_cooling")
    plot(rows, summary, output)
    output.with_suffix(".json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    report(summary, args.csv, output)
    for suffix in (".png", ".svg", ".json", ".md"):
        print(output.with_suffix(suffix).resolve())


if __name__ == "__main__":
    main()
