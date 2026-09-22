# OBD Logger

Windows 命令行车辆数据记录器（EDIABAS / K+DCAN）。**全程只读诊断，无任何写入/复位/编程操作。**

- **一期（已完成并实车验证）**：打通“程序 → EDIABAS → K+DCAN → 车辆 DME”链路，读取并打印车辆基础信息（ident + 两阶段 VIN 自动补齐）。
- **二期（本次实现）**：`log` 子命令对发动机实时数据定时采样、控制台就地刷新显示、落盘 CSV；覆盖上电未打火（KL.15 ON）到打火运转全过程，通信中断自动标记缺口并退避重试，进程不崩溃不挂死。

```text
ObdLogger.exe → api32.dll (32 位) → EDIABAS 7.3.0 → MEVD174K.prg → K+DCAN (COM6) → E84 N20 DME
```

需求与背景见 [doc/一期需求文档.md](doc/一期需求文档.md)、[doc/二期需求文档.md](doc/二期需求文档.md)、[doc/项目背景.md](doc/项目背景.md)。

## 环境前提（硬性）

- Windows，`C:\EDIABAS\` 已安装（EDIABAS 7.3.0，`EDIABAS.INI` 中 `Interface = STD:OBD`，`obd.ini` 中 `Port=Com6`）
- `C:\EDIABAS\ECU\mevd1724.prg` 存在
- .NET Framework 4.8 运行时（系统已装）
- FTDI K+DCAN 线（COM6）；**运行时请勿同时打开 INPA / Tool32**（COM6 独占）

## 构建

需要 .NET SDK 8+（构建机已装）：

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1           # Release（默认）
powershell -ExecutionPolicy Bypass -File build.ps1 -Configuration Debug
```

输出：`src\ObdLogger\bin\<Configuration>\net48\ObdLogger.exe`（构建脚本自动校验产物为 32 位 x86）。
也可用 Visual Studio 2022 打开 `ObdLogger.sln` 构建。产物为 x86 / .NET Framework 4.8 控制台程序，零安装依赖，拷贝 exe 即可运行。

## 使用

接好 K+DCAN 线、打开点火开关 (KL.15)，然后：

### 一期：车辆基础信息

```text
ObdLogger.exe --ecu MEVD174K     # 本车（E84 N20，DME 8631730）推荐用法：一条命令输出全部 MVP
ObdLogger.exe [选项]

  --ecu <名称>     SGBD/PRG 名称（默认 mevd1724；本车实测应使用 MEVD174K）
  --job <名称>     强制使用指定只读 Job（跳过两阶段候选探测）
  --para <参数>    传给 Job 的参数（识别类 Job 通常留空）
  --verbose, -v    控制台输出 API 调用明细（日志文件始终记录）
  --help, -h       显示帮助
```

### 二期：实时采样落盘（log 子命令）

```text
ObdLogger.exe log                          # 默认：MEVD174K、1Hz、内置 20 项清单，落盘 exe\log\obdlog_<时间戳>.csv
ObdLogger.exe log [log 选项]

  --ecu <名称>     SGBD/PRG 名称（log 默认 MEVD174K）
  --out <路径>     CSV 输出（文件或目录；默认 exe目录\log\obdlog_<时间戳>.csv）
  --rate <Hz>      目标采样率（默认 1，范围 0.1~5；实际以 ECU 响应为准，达不到按实际记录）
  --config <文件>  采样清单（每行 ARG,显示名[,单位]；默认用内置 §3.3 已核实清单）
  --para-sep <c>   动态参数分隔符（默认 ';'；启动时自动协商 ';' 与 ','，风险项 R-2-1）
  --no-ja          保守模式：仅 MODE=NEIN 读 ECU 已存定义，不写 ECU RAM（§3.4）
  --no-hotkey      不监听 q 键（无人值守/后台运行；Ctrl+C 仍可停止）
  --verbose, -v    控制台输出 API 明细（log 默认静默、仅文件）
```

运行中：控制台就地刷新显示各采样项（不滚动）；按 `q` 或 `Ctrl+C` 停止，保证 CSV 完整关闭与 `apiEnd()` 释放。示例扩展清单（含全 4 缸点火/爆震、燃油轨压、喷油量、Valvetronic、VANOS 位置等）见 [config/sample-extended.txt](config/sample-extended.txt)。

退出码：`0`=成功/正常停止；`1`=意外错误；`2`=EDIABAS 环境/初始化失败；`3`=通信/端口失败（含未接车）；`4`=无可用 Job / 测量块参数被拒。

日志：每次运行在 exe 目录 `logs\ObdLogger_<时间戳>.log` 记录全部 API 调用、Job 名、参数与结果（FR-8/FR-2-10）；目录不可写时退到 `%LOCALAPPDATA%\ObdLogger\logs`。

## 输出

- **MVP（必须）**：VIN 车架号、ECU 名称/诊断标识、ECU 硬件号/版本、ECU 软件号/版本
- **可选**：点火开关 (KL.15)、蓄电池电压
- **原始结果**：识别 Job 返回的全部结果集与字段（字段名 + 格式 + 值），供人工核对

MVP 字段从原始结果中按名称模式提取；若某字段未命中，会标注“未读取到”并完整打印原始结果，便于对照 PRG 实际字段命名。

## 识别类 Job 探测结论（2026-09 本机实测）

`mevd1724.prg` 为压缩二进制，无法静态枚举 Job；利用“SYS-0008 = Job 不存在”在无车状态安全探测：

| Job 名 | 结果 |
| ------ | ---- |
| `ident` | **存在**（E 系标准识别 Job，默认首选） |
| `identification`、`identifikation`、`aif_lesen`、`c_c_lesen`、`ident_lesen`、`ecu_identification`、`info_lesen`、`status_lesen`、`lieferanten_ident`、`sgbd_ident`、`fin_lesen`、`vin_lesen`、`zcs_lesen`、`fa_lesen` 等 30+ 个常见名 | 不存在 |

## 实车联调结论（2026-09-21，点火 ON）

**本车 DME 拒绝 mevd1724.prg 的 `ident`（JOB_STATUS=ERROR_ECU_SUB_FUNCTION_NOT_SUPPORTED），
但接受以下 SGBD 的 `ident`（JOB_STATUS=OKAY）并返回真实数据：**

```text
ObdLogger.exe --ecu MEVD174K       # 或 01mevd1724 / MEVD176K / MEVD17KW / 01mevd1726 / 01MEVD1727 / 01MEVD1729
```

DME 自报（KWP2000 服务 0x1A 识别块 0x80，地址 0x12）：

| 字段 | 值 |
| ---- | -- |
| **VIN 车架号 (AIF_FG_NR_LANG)** | **LBVUG7100EMD09858**（LBV=华晨宝马，E=2014 款；尾 7 位 MD09858 与 C_FG_LESEN 结果互证） |
| BMW 零件号 (ID_BMW_NR) | **8631730**（DME 身份的权威标识，可在 realoem.com 查证对应型号） |
| 硬件号 (ID_HW_NR) | 01 |
| 供应商 (ID_LIEF_TEXT) | Bosch |
| 数据日期 (ID_DATUM) | 05.07.2014 |
| 软件号 FSV (ID_SW_NR_FSV) | 255.255.255（出厂原始软件，未被编程过） |
| AIF 软件号 (AIF_SW_NR) / 组装号 (AIF_ZB_NR) | 8632977 / 8632976（编程日期 2014-07-19，编程次数 1） |

- 被拒绝的 SGBD：mevd1724（默认 4.4MB E 系完整版）、mevd172y、mevd1725、mevd172、mevd172y_alt、mevd172_alt——其 `ident` 请求的子功能不被本 DME 软件支持（PRG 与 DME 软件版本不完全匹配，即风险项 R-1 的实际表现）。
- **VIN 由第 2 阶段 Job 自动补齐**：ident 结果（0x80 块）不含 VIN；程序在 VIN 未命中时自动按序尝试 `AIF_LESEN`（$23 读 AIF，17 位 VIN）→ `C_FG_LESEN`（$1A/$90，7 位）等只读 Job。Job 定义依据反编译文件 [doc/材料/MEVD174K.b1v](doc/材料/MEVD174K.b1v)（MEVD174K.prg 完整 BEST/2 源码，权威的 Job/参数/结果目录）。
- **系统结果字段 -1 的含义**：UBATTCURRENT/IGNITIONCURRENT 等显示 -1 = “未定义”（0=关闭，1=开启）。原因是 EDIABAS.INI 中 `UbattHandling = 0` / `IgnitionHandling = 0`（电压/点火监测未启用）。如需实际值可将两项设为 1（程序不修改配置；廉价 FTDI 线可能未接状态线，设置后仍可能为 -1）。

## 排障指南

| 现象 | 排查 |
| ---- | ---- |
| 提示 COM6 无法打开/被占用 | 关闭 INPA / Tool32 / 其他串口工具后重试 |
| 提示未检测到 COM6 | 检查 USB 线、FTDI 驱动（设备管理器中应有 USB Serial Port COM6） |
| IFH-0003 / IFH-0009 等接口类错误 | 确认已接车、点火 ON；检查线缆与电压；先用 INPA/Tool32 验证线缆通信再回到本程序 |
| SYS-0008 JOB NOT FOUND | Job 名不在 PRG 中：用 Tool32 查实际 Job 名后 `--job` 指定 |
| ECU 拒绝（JOB_STATUS=ERROR_ECU_SUB_FUNCTION_NOT_SUPPORTED） | DME 通信正常但 SGBD 的识别服务不匹配：`--ecu MEVD174K`（本车实测可用）；或 `--para` 传 Job 参数 |
| ECU 识别信息不符（R-1） | 实车 DME 可能不是 MEVD1724：`--ecu <名称>` 更换匹配 SGBD |

深度排障（FR-9，程序不修改配置）：将 `C:\EDIABAS\Bin\EDIABAS.INI` 中 `ApiTrace` / `IfhTrace` 临时设为 `1`，重跑后查看 `C:\EDIABAS\TRACE\` 跟踪文件，排障后改回 `0`。

## 安全红线（TC-4/TC-6）

全程仅调用只读 Job：一期为识别类（`ident` / `AIF_LESEN` / `C_FG_LESEN`），二期为测量块读取 `STATUS_MESSWERTBLOCK_LESEN`。代码中没有任何写入/复位/编程/刷写类调用，`EdiabasClient` 不暴露此类入口。

二期 `MODE=JA` 的 `$2C`（DynamicallyDefinedLocalIdentifier）仅将“动态标识符定义”写入 ECU **RAM**（断电即失、无持久化、非编码/编程），属诊断标准流程（INPA 模拟量视图同款），认定为只读边界内（§3.4）；若评审有异议可加 `--no-ja` 仅用 `MODE=NEIN` 读 ECU 已存定义。

## 代码结构（NFR-3 封装层/业务分离；二期在 Logging 下扩展采样）

```text
src/ObdLogger/
├── Program.cs                  入口：子命令分派（info/log）、参数、预检、报告/汇总、退出码
├── Ediabas/                    EDIABAS API 封装层（一二期共用，原样复用）
│   ├── NativeMethods.cs        api32.dll P/Invoke（stdcall + APIHANDLE 首参，7.x 多实例 API）
│   ├── EdiabasClient.cs        Init/Job(异步等待)/结果枚举/End 的安全封装（IDisposable）
│   ├── EdiabasException.cs     异常体系 + 错误码区间分类（IFH/BIP/SYS/API/NET/RUN）
│   ├── ApiResultFormat.cs      结果格式枚举（Api.h）
│   └── ResultField.cs          结果字段/结果集/Job 结果模型
├── Vehicle/                    一期业务层（log 模式复用其两阶段读取 VIN）
│   ├── VehicleInfoReader.cs    识别 Job 候选探测 + 两阶段 VIN 补齐 + MVP 字段提取
│   └── VehicleInfo.cs          “车辆基础信息”模型（需求 3.3）
└── Logging/                    应用日志 + 二期采样
    ├── AppLog.cs               应用层日志（文件 + --verbose 控制台；log 模式可静默控制台）
    ├── Sampler.cs              采样循环（唯一持有 EdiabasClient 的线程；MODE 协商/分组/退避重试/统计）
    ├── SampleConfig.cs         采样清单（内置默认 + 外部文件）+ 采样项模型
    ├── MesswertCatalog.cs       嵌入 MESSWERTETAB → ARG→RESULTNAME/单位 解析
    ├── SampleSnapshot.cs       单帧快照 + 值归一（德语逗号小数→不变文化）
    ├── CsvWriter.cs            CSV 创建/头部/逐行 flush/按大小·跨天滚动/关闭
    ├── LiveDisplay.cs          控制台就地刷新（不滚动；无控制台自动禁用）
    └── messwertetab.csv        FR-2-9 导出并嵌入的测量值目录（483 条）

仓库根：scripts/Export-Messwertetab.ps1（从 b1v 导出 messwertetab.csv）、config/sample-extended.txt（扩展采样清单示例）
```

## 关键技术事实（开发期实测/验证）

- `api32.dll` 7.3.0 为 **stdcall** 调用约定，导出 `__apiXxx` / `___apiXxx@N` 双别名；与 6.x 文档不同，**7.x 每个函数首参为 `apiInit` 输出的 APIHANDLE (UInt32)**
- **`apiJob` 为异步**：须轮询 `apiState()`（0=BUSY）直至结束后再查 `apiErrorCode()` 与读取结果；进度经 `apiJobInfo()` 百分比反馈（与宝马官方 `apiNET32.dll`/TestCsNET32 流程一致）
- 结果集索引 0 基（代码含 1 基自适应兜底）；`APIRESULTFORMAT` 为 0 基枚举；文本缓冲区 `APIMAXTEXT=1024`
- **二期 `STATUS_MESSWERTBLOCK_LESEN`**：para = `MODE<分隔符>ARG1<分隔符>ARG2…`（b1v：`pars #1`=MODE、`pars #2..#43`=测量值，≤**42** 项）；结果字段名 = MESSWERTETAB.RESULTNAME（`tabseek "ARG"` → `tabget "RESULTNAME"` → `ergs`），故 `INMOT` 的结果是 `STAT_MOTORDREHZAHL_WERT`，必须经目录映射；Job 返回的已是换算后物理值
- **MESSWERTEMODE 表值为 `ein/aus/ja/nein/yes`（→1/0）**；大小写敏感性无法离线确认，故启动时自动协商分隔符（`;`/`,`）与 MODE 拼写（R-2-1/R-2-4），首帧失败即致命退出并给出对策
- MESSWERTETAB 全表 **483 条**（b1v 行 4900–5385），由 `scripts/Export-Messwertetab.ps1` 导出并嵌入 exe；内置默认 20 项、扩展示例 38 项均经离线自测全部解析出 RESULTNAME/单位

## 验收状态

| AC | 场景 | 状态 |
| -- | ---- | ---- |
| AC-1 | 接车+点火 ON：初始化成功、建立通信、退出码 0 | **已验证**（2026-09-21，`--ecu MEVD174K`，退出码 0；默认 mevd1724 被 ECU 拒绝，见“实车联调结论”） |
| AC-2 | 打印全部 MVP 字段；VIN 与铭牌一致 | **已验证**：VIN `LBVUG7100EMD09858`（AIF_LESEN）+ 零件号/硬件号/软件号全部输出（铭牌比对请人工确认） |
| AC-3 | 确认 E84 N20 实际 DME 型号 | **已读取**：Bosch MEVD17 家族，BMW 零件号 **8631730**（realoem 反查确认具体变体即可归档） |
| AC-4 | 未接车/点火 OFF：明确提示、正常退出 | **已验证**（IFH-0003/IFH-0009，退出码 3） |
| AC-5 | COM6 被占用：明确提示、正常退出 | **已验证**（预检拦截，退出码 3） |
| AC-6 | 全程仅只读 Job | **已验证**（代码评审 + 联调仅使用 ident / C_FG_LESEN / AIF_LESEN 只读 Job；写/复位/编程类 Job 见 b1v 红线清单，从未调用） |

### 二期验收状态（AC-2-x）

| AC | 场景 | 状态 |
| -- | ---- | ---- |
| AC-2-1 | 点火 ON、发动机 OFF：log 启动、实时显示、CSV 持续写入、Ctrl+C 干净退出 | 代码就绪，**待实车**（本机 COM6 未接） |
| AC-2-2 | 打火瞬间 RPM 0→怠速、无断档 | 代码就绪，**待实车** |
| AC-2-3 | 怠速 10 分钟、与 INPA 模拟量视图对照 | **待实车** |
| AC-2-4 | 中途点火 OFF→ON 自动恢复 | 代码就绪（缺口标记+退避重试），**待实车** |
| AC-2-5 | 未接车/点火 OFF 启动 log：明确提示、正常退出 | **已离线验证**（COM6 缺失→“未检测到 COM6”→退出码 3） |
| AC-2-6 | 全程仅只读 Job | **代码评审通过**（仅 STATUS_MESSWERTBLOCK_LESEN + ident/AIF_LESEN/C_FG_LESEN；MODE=JA 的 $2C 仅写 ECU RAM，§3.4） |
| AC-2-7 | 一期命令回归 | **已离线验证**（info/--ecu/--help 行为与退出码不变；x86 构建通过） |

> 离线自测（无实车，反射驱动）已验证：嵌入 MESSWERTETAB（483 条）加载、内置默认 20 项与扩展示例 38 项全部解析出 RESULTNAME/单位、CSV 头部/单位行/数据行/缺口行格式（§6）、值归一（德语逗号小数→点）、CLI 解析与退出码、x86 构建。**实车相关项（采样值正确性、打火过程、通信恢复、R-2-1 para 格式实车定稿）待接车验证**；para 分隔符与 MODE 拼写已做启动自动协商以降低首接触风险。

## 后续阶段（展望，非本期承诺）

- 三期：故障码（DTC）读取（FS_LESEN 已在 b1v 定位为只读 Job）、历史记录管理、简单图表
- 长期：全 Job 配置化（二期采样配置化已落地）、数据导出分析
