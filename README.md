# BimmerLogger

> **基于 EDIABAS / K+DCAN 的宝马全链路专有数据采集与发动机动力学诊断套件**  
> *BMW EDIABAS-Native Telemetry Logger & DME Powertrain Health Diagnostics Toolkit*

---

## 📖 项目简介

**BimmerLogger** 是专为宝马（BMW）E/F 系列车型打造的**全链路专有数据记录与动力学健康诊断工具**。

本项目绕过了传统通用 OBD-II 标准协议采样率低（通常仅 1~2 个参数/秒）、PID 覆盖有限的弊端，直接通过 **EDIABAS 7.3 原生 API (`api32.dll`)** 与 **K+DCAN** 物理链路，与宝马发动机控制电脑（DME，如 MEVD17 家族）建立专有诊断通信。

项目不仅包含一个**工业级高韧性、全程只读、毫秒级采样的 Windows 采集客户端**（C# / .NET），还配套了**纯标准库打造的动力系统健康分析与工况拆解引擎**（Python），覆盖从数据采集、黑匣子落盘到实车故障排查（真空泄漏、PCV 故障、燃油修正偏离等）的完整工程闭环。

---

## ✨ 核心特性

- 🛡️ **绝对安全红线（Strict Read-Only）**：
  - 核心通信层严格受限于只读类诊断 Job（如 `ident`、`AIF_LESEN`、`STATUS_MESSWERTBLOCK_LESEN`）。
  - 底层封死任何写 Flash、写 EEPROM、编码、复位或清码调用，确保对实车电控单元零风险。
- ⚡ **突破通用 OBD-II 瓶颈（KWP2000 $2C 动态测量块）**：
  - 利用 DME 的 DynamicallyDefinedLocalIdentifier 机制，**单次请求并发打包读取多达 42 项底层测量参数**。
  - 稳定实现 1~2 Hz 的全参数同步高频快照，彻底告别通用 OBD 的轮询时差。
- 🔄 **覆盖全工况生命周期（Ignition-to-Run Seamless Logging）**：
  - 支持从车辆上电（KL.15 ON）、发动机未打火、启动瞬态（RPM 0 → 怠速跳变）、连续运转到熄火的完整过程。
  - 内置通信中断缺口标记与指数退避重试机制，面对中途掉电、熄屏或线缆抖动，进程不崩溃、不挂死。
- 📦 **零依赖单 Exe 独立分发**：
  - x86 原生编译，内嵌从 BEST/2 反编译提取的完整 483 条 `MESSWERTETAB` 参数映射表。
  - 运行时自动协商参数分隔符与 MODE 协议，外部配置灵活可扩展。
- 📊 **端到端动力学诊断引擎**：
  - 配套 Python 分析套件，自动识别冷启动暖机、热车怠速、巡航、加减速与自动启停等复杂工况。
  - 基于 Lambda 长期修正（Adaption）与负荷/MAF 的相关性，形成诊断证据链（如精准锁定 PCV 阀/气门室盖真空微泄漏）。

---

## 🏗️ 架构拓扑

```text
┌────────────────────────────────────────────────────────┐
│                   BimmerLogger 套件                    │
└────────────────────────────────────────────────────────┘
                           │
      ┌────────────────────┴────────────────────┐
      ▼                                         ▼
【 数据采集端 (ObdLogger) 】              【 诊断分析端 (tools) 】
• C# (.NET 4.8 / x86)                    • Python 3 (仅依赖标准库)
• 控制台就地平滑刷新 (无闪烁)            • 驾驶工况自动切分 (冷启动/怠速/加减速)
• 毫秒级时间戳 CSV 实时落盘/分卷         • 燃油修正闭环/进气泄漏诊断证据链
• 参数自动协商 & 异常退避重试            • 综合动力健康报告生成
      │                                         ▲
      ▼                                         │
┌───────────────────────────────┐               │
│ api32.dll (EDIABAS 7.3.0 x86) │               │
└──────────────┬────────────────┘               │
               ▼                                │
┌───────────────────────────────┐               │
│ SGBD / PRG (如 MEVD174K.prg)  │               │
└──────────────┬────────────────┘               │
               ▼                                │
┌───────────────────────────────┐               │
│  FTDI K+DCAN 线缆 (COM6 独占) │               │
└──────────────┬────────────────┘               │
               ▼                                │
┌───────────────────────────────┐               │
│ 车辆 DME (如 BMW N20 8631730) │ ──(CSV导出)───┘
└───────────────────────────────┘
```

---

## 🚀 快速上手

### 1. 运行前提

- **操作系统**：Windows 10 / 11（已配置 .NET Framework 4.8）
- **诊断软件底座**：已安装 `C:\EDIABAS\`（EDIABAS 7.3.0，`EDIABAS.INI` 中 `Interface = STD:OBD`，`obd.ini` 中 `Port=Com6`）
- **物理硬件**：FTDI 芯片的 K+DCAN 诊断线（默认占用 `COM6` 端口）
- **注意事项**：运行前请关闭 INPA、Tool32 等串口占用程序（COM6 独占）。

### 2. 构建采集程序

需要 .NET SDK 8+（或使用 Visual Studio 2022 打开 `ObdLogger.sln`）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1           # Release（默认）
powershell -ExecutionPolicy Bypass -File build.ps1 -Configuration Debug
```

产物输出于：`src\ObdLogger\bin\Release\net48\ObdLogger.exe`（32 位 x86 控制台程序，零额外安装依赖，拷贝 exe 即可运行）。

### 3. 常用命令

接好 K+DCAN 线、打开点火开关 (KL.15)：

#### ① 识别车辆硬件与软件版本（链路与身份自检）

```powershell
ObdLogger.exe --ecu MEVD174K     # 推荐用法：一条命令输出全部基础信息（两阶段自动补齐 VIN）
```

#### ② 实时采样发动机数据流并落盘 CSV

```powershell
# 默认：MEVD174K、1Hz、内置 20 项核心清单，落盘 exe\log\obdlog_<时间戳>.csv
ObdLogger.exe log

# 自定义采样率（例如 2Hz）并指定扩展清单与输出目录
ObdLogger.exe log --rate 2 --config config\sample-extended.txt --out D:\logs\

# 无人值守定时采样（例如录制 600 秒后自动退出）
ObdLogger.exe log --duration 600 --no-hotkey
```

运行中：控制台就地刷新显示各采样项；按 `q` 或 `Ctrl+C` 停止，保证 CSV 完整关闭与 `apiEnd()` 释放。

#### ③ 运行动力学数据分析引擎

```powershell
cd tools/data-analysis
python analyze.py
```
自动合并多段采集碎片、剔除启停与断油噪点、按工况（冷启动/热怠速/加速/断油）切分并生成动力系统健康分析证据链。

---

## 📋 典型采集参数支持

| 类别 | 监测参数 (示例) | 单位 | 来源 Job / 映射 |
| :--- | :--- | :--- | :--- |
| **基础转速与车速** | 实际发动机转速 (RPM)、怠速目标转速、物理车速 | 1/min, km/h | `INMOT`, `SNLLD`, `IVKMH` |
| **进气与负荷** | 节气门开度/目标、MAF 进气质量流量、相对负荷率 | %, kg/h | `IWDKL`, `ILMKG`, `SLAST`, `ILREL` |
| **压力与增压** | 节前绝对进气压力 (MAP/Boost)、环境大气压 | hPa | `IPLAD`, `IPUMG` |
| **空燃比与修正** | Lambda 实际值/目标值、短期修正积分值、长期乘法自适应 | -, % | `ILAG1`, `SINT1`, `ILIN1`, `ILAM1` |
| **点火与燃烧** | 1~4 缸点火提前角、爆震识别状态、逐缸爆震基准电压 | °KW, V | `IZZY1`, `IZWZ1-4`, `ISKLO`, `IUKZ1-4` |
| **燃油高低压系统** | 高压共轨实际轨压 / 目标轨压、低压供油压力、逐缸喷油量 | MPa, kPa, mg/Hub | `STAT_0x58EF_WERT`, `STAT_0x4522_WERT` 等 |
| **配气与气门控制** | 进/排气 VANOS PWM 占空比、凸轮轴位置角、Valvetronic 升程 | %, mm, °KW | `IAVEP`, `IAVAP`, `STAT_0x4602_WERT` 等 |

---

## 🔒 安全说明（Safety Policy）

1. **全程只读机制**：程序仅调用识别类 Job 与测量块读取 `STATUS_MESSWERTBLOCK_LESEN`，代码中没有任何写入 Flash/EEPROM、复位、编程或清除故障码的入口。
2. **易失性内存使用**：在多参数块采样中，`MODE=JA` 所使用的 KWP2000 `$2C` 动态定义仅写入 ECU **RAM**，断电即失、无持久化。若需完全遵循保守模式，可传入 `--no-ja` 参数仅读取 ECU 已存定义。

---

## 🗺️ 后续演进

- [x] **Phase 1**：EDIABAS 通信链路打通与两阶段 VIN / DME 基础信息探测
- [x] **Phase 2**：高频动态测量块（`STATUS_MESSWERTBLOCK_LESEN`）实时采样、CSV 落盘与 Python 故障诊断工具集
- [ ] **Phase 3**：只读故障码（DTC / `FS_LESEN`）读取、冻结帧提取与历史快照比对
- [ ] **Phase 4**：轻量级 Web/GUI 实时曲线监控仪表盘
