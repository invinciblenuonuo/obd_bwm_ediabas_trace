# OBD Logger（一期）

Windows 命令行车辆数据记录器。一期目标：验证“程序 → EDIABAS → K+DCAN → 车辆 DME”链路可用，并从 DME 读取车辆基础信息打印到命令行。**一期只做只读诊断，不做数据记录、不做写入。**

```text
ObdLogger.exe → api32.dll (32 位) → EDIABAS 7.3.0 → mevd1724.prg → K+DCAN (COM6) → E84 N20 DME
```

需求与背景见 [doc/需求文档.md](doc/需求文档.md)、[doc/项目背景.md](doc/项目背景.md)。

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

```text
ObdLogger.exe [选项]

  --ecu <名称>     SGBD/PRG 名称（默认 mevd1724；实车 DME 不匹配时更换，风险项 R-1）
  --job <名称>     强制使用指定只读 Job（跳过候选探测；Job 名可用 Tool32 查看）
  --verbose, -v    控制台输出 API 调用明细（日志文件始终记录）
  --help, -h       显示帮助
```

退出码：`0`=成功；`1`=意外错误；`2`=EDIABAS 环境/初始化失败；`3`=通信/端口失败；`4`=无可用的识别类 Job。

日志：每次运行在 exe 目录 `logs\ObdLogger_<时间戳>.log` 记录全部 API 调用、Job 名、参数与结果（FR-8）；目录不可写时退到 `%LOCALAPPDATA%\ObdLogger\logs`。

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
| BMW 零件号 (ID_BMW_NR) | **8631730**（DME 身份的权威标识，可在 realoem.com 查证对应型号） |
| 硬件号 (ID_HW_NR) | 01 |
| 供应商 (ID_LIEF_TEXT) | Bosch |
| 数据日期 (ID_DATUM) | 05.07.2014 |
| 软件号 FSV (ID_SW_NR_FSV) | 255.255.255（出厂原始软件，未被编程过） |

- 被拒绝的 SGBD：mevd1724（默认 4.4MB E 系完整版）、mevd172y、mevd1725、mevd172、mevd172y_alt、mevd172_alt——其 `ident` 请求的子功能不被本 DME 软件支持（PRG 与 DME 软件版本不完全匹配，即风险项 R-1 的实际表现）。
- 0x80 识别块**不含 VIN**；后续可用 `--ecu MEVD174K --job fin_lesen`（或 vin_lesen / fa_lesen / zcs_lesen，均为只读）尝试获取 VIN，尚未实车验证。
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

## 安全红线（TC-6）

一期仅调用只读识别类 Job（`ident` 等）。代码中没有任何写入、复位、编程、刷写类调用；`EdiabasClient` 不暴露此类入口。故障码清除等写操作不在本期范围。

## 代码结构（NFR-3：封装层与业务输出分离，二期可复用）

```text
src/ObdLogger/
├── Program.cs                  入口：参数、预检、报告输出、退出码
├── Ediabas/                    EDIABAS API 封装层（二期直接复用）
│   ├── NativeMethods.cs        api32.dll P/Invoke（stdcall + APIHANDLE 首参，7.x 多实例 API）
│   ├── EdiabasClient.cs        Init/Job(异步等待)/结果枚举/End 的安全封装（IDisposable）
│   ├── EdiabasException.cs     异常体系 + 错误码区间分类（IFH/BIP/SYS/API/NET/RUN）
│   ├── ApiResultFormat.cs      结果格式枚举（Api.h）
│   └── ResultField.cs          结果字段/结果集/Job 结果模型
├── Vehicle/                    业务层
│   ├── VehicleInfoReader.cs    识别 Job 候选探测 + MVP 字段模式提取
│   └── VehicleInfo.cs          “车辆基础信息”模型（需求 3.3）
└── Logging/AppLog.cs           应用层日志（文件 + --verbose 控制台）
```

## 关键技术事实（开发期实测/验证）

- `api32.dll` 7.3.0 为 **stdcall** 调用约定，导出 `__apiXxx` / `___apiXxx@N` 双别名；与 6.x 文档不同，**7.x 每个函数首参为 `apiInit` 输出的 APIHANDLE (UInt32)**
- **`apiJob` 为异步**：须轮询 `apiState()`（0=BUSY）直至结束后再查 `apiErrorCode()` 与读取结果；进度经 `apiJobInfo()` 百分比反馈（与宝马官方 `apiNET32.dll`/TestCsNET32 流程一致）
- 结果集索引 0 基（代码含 1 基自适应兜底）；`APIRESULTFORMAT` 为 0 基枚举；文本缓冲区 `APIMAXTEXT=1024`

## 验收状态

| AC | 场景 | 状态 |
| -- | ---- | ---- |
| AC-1 | 接车+点火 ON：初始化成功、建立通信、退出码 0 | **已验证**（2026-09-21，`--ecu MEVD174K`，退出码 0；默认 mevd1724 被 ECU 拒绝，见“实车联调结论”） |
| AC-2 | 打印全部 MVP 字段；VIN 与铭牌一致 | 部分验证：硬件号/软件号/BMW 零件号已读取；**VIN 待验证**（0x80 块不含 VIN，需试 fin_lesen 等 Job） |
| AC-3 | 确认 E84 N20 实际 DME 型号 | 初步：Bosch MEVD17 家族，BMW 零件号 **8631730**（待 realoem 比对确认具体变体） |
| AC-4 | 未接车/点火 OFF：明确提示、正常退出 | **已验证**（IFH-0003/IFH-0009，退出码 3） |
| AC-5 | COM6 被占用：明确提示、正常退出 | **已验证**（预检拦截，退出码 3） |
| AC-6 | 全程仅只读 Job | **已验证**（代码评审：无写入/复位/编程调用；联调仅使用 ident 等只读 Job） |

## 后续阶段（展望，非本期承诺）

- 二期：实时运行数据（转速、水温、油温、车速、节气门等）定时采样与 CSV 落盘
- 三期：故障码读取、历史记录管理、简单图表
- 长期：Job 配置化、数据导出分析
