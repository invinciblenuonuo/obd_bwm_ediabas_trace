# 分任务采集

| 启动脚本 | 配置文件 | 内容 |
|---|---|---|
| `Start-Cooling.ps1` | `config/sample-cooling.txt` | 9 项：水泵目标转速确认值、实际转速、水温、冷却液目标温度、节温器加热 PWM、水泵电压、电流、电子模块温度、状态字 |
| `Start-Fuel.ps1` | `config/sample-fuel.txt` | 10 项：转速、水温、燃油高压目标/实际、燃油低压、Lambda 实际/目标、积分修正、适应值、空气流量 |
| `Start-Intake.ps1` | `config/sample-intake.txt` | 10 项：转速、水温、进气温度、空气流量、增压实际压力、环境压力、节气门实际/目标位置、发动机负荷、相对负荷 |

在项目根目录启动（也可以通过脚本的绝对路径从其他目录启动）：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Start-Cooling.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Start-Fuel.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Start-Intake.ps1
```

默认每秒采样一次，持续采集直到按 Q 或 Ctrl+C 退出。数据分别保存到项目根目录下的 `logs/cooling/`、`logs/fuel/`、`logs/intake/`。一次只启动一个任务，避免争用诊断连接。采样器会探测 ECU 每组容量并拆组；实际更新频率由车辆响应决定。

通用参数示例：2 Hz、采集 120 秒、自定义输出目录和程序位置：

```powershell
.\scripts\Start-Fuel.ps1 -RateHz 2 -DurationSec 120 -OutPath D:\logs\fuel -ExePath D:\tools\ObdLogger.exe
```

- `-RateHz`：目标频率，范围 0.1～5 Hz，默认 1。
- `-DurationSec`：采集秒数，默认 0 表示持续采集。
- `-OutPath`：输出目录；相对路径相对于启动时的工作目录。
- `-ExePath`：采集程序路径，默认 `src/ObdLogger/bin/Release/net48/ObdLogger.exe`。
- `-Ecu`：ECU/PRG 名称，默认 `MEVD174K`，须与实际车辆匹配。
- `-DryRun`：只显示任务、配置和命令参数，不创建日志目录、不启动程序、不连接车辆。

```powershell
.\scripts\Start-Cooling.ps1 -DryRun | Format-List
```

需要先构建采集程序（`build.ps1`），或用 `-ExePath` 指定已有兼容程序。启动脚本不会自动构建或修改 EDIABAS 设置。当前程序使用 `C:\EDIABAS\Bin`，端口从其中的 `obd.ini` 读取，缺省 COM6；连接前应完成相应环境配置。

参数是否支持、单位与缩放须通过实车确认。水泵目标列是目标转速确认值；节温器 PWM 是加热控制，不是阀门开闭反馈；水泵状态字按原始数值输出。配置文件省略单位覆盖，沿用程序内嵌参数目录的单位。

## 冷却系统曲线

```powershell
python -m pip install -r .\scripts\requirements-plot.txt
python .\scripts\plot-cooling.py .\logs\cooling\obdlog_20261101_113645.csv --context 行驶中
```

默认在 CSV 同级的 `analysis` 目录生成 PNG、SVG、统计 JSON 和 Markdown 报告；使用 `--out-dir` 可指定其他目录。脚本支持日志注释行、单位行和缺失数值，较长采样间隔会断开并标灰，不进行插值。水泵两条曲线使用不同纵轴并保留原始缩放，避免把未经验证的读数当作已校准机械转速。

## 文本编码

C# 源码、项目文件及含中文的 PowerShell 脚本使用 UTF-8 BOM；CSV 输出也使用 UTF-8 BOM，便于 Excel 自动识别中文。Python 读取 CSV 时使用 `encoding="utf-8-sig"`，可兼容带 BOM 和不带 BOM 的 UTF-8 文件。旧版程序已写出的替换字符无法通过切换显示编码恢复；本机历史冷却日志的注释修复副本存放在 `logs/cooling/repaired`，测量数据保持原样。
