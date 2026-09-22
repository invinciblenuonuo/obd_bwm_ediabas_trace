using System;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using ObdLogger.Ediabas;
using ObdLogger.Logging;
using ObdLogger.Vehicle;

namespace ObdLogger
{
    /// <summary>
    /// OBD Logger 一期入口：验证“程序 → EDIABAS → K+DCAN → 车辆 DME”链路，
    /// 读取并打印车辆基础信息（需求文档第 3 节）。
    ///
    /// 退出码：0=成功；1=意外错误；2=EDIABAS 环境/初始化失败；3=通信/端口失败；4=无可用的识别类 Job。
    /// </summary>
    internal static class Program
    {
        private const string DefaultEcuName = "mevd1724";
        private const string DefaultLogEcuName = "MEVD174K"; // 二期 log 默认（TC-5：本车实测可用 SGBD）
        private const string EdiabasBinDir = @"C:\EDIABAS\Bin";
        private const string DefaultPortName = "COM6";

        private static volatile bool _cancelRequested;

        // 进程启动时的工作目录：EDIABAS 需将 CurrentDirectory 切到 C:\EDIABAS\Bin，
        // 但用户传入的相对 --config/--out 应按启动目录解析（否则会被错误地相对 Bin 目录）。
        private static string _launchDir;

        private static int Main(string[] args)
        {
            try { _launchDir = Directory.GetCurrentDirectory(); }
            catch { _launchDir = AppDomain.CurrentDomain.BaseDirectory; }
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 旧控制台不支持时忽略 */ }

            // 二期子命令分派：`log` → 实时采样落盘（FR-2-1）；无子命令 → 一期车辆基础信息（行为不变，AC-2-7 回归）。
            if (args.Length > 0 && string.Equals(args[0], "log", StringComparison.OrdinalIgnoreCase))
            {
                return RunLogging(args, 1);
            }
            return RunInfo(args);
        }

        /// <summary>一期入口：读取并打印车辆基础信息（原 Main 主体，行为保持不变）。</summary>
        private static int RunInfo(string[] args)
        {
            Options options;
            if (!Options.TryParse(args, out options))
            {
                PrintUsage();
                return 1;
            }
            if (options.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            Console.WriteLine("OBD Logger 一期 - EDIABAS/K+DCAN 车辆基础信息读取");
            Console.WriteLine("链路: ObdLogger → api32.dll → EDIABAS 7.3.0 → " + options.EcuName + ".prg → K+DCAN → DME");
            Console.WriteLine("安全红线: 一期仅执行只读识别类 Job，无任何写入/复位/编程操作");
            Console.WriteLine();

            // ---------- FR-1 / TC-1：32 位进程自检 ----------
            if (Environment.Is64BitProcess)
            {
                Console.Error.WriteLine("[错误] 当前进程为 64 位，无法加载 32 位 api32.dll。请使用 x86 构建运行。");
                return 2;
            }

            // ---------- FR-2 / TC-3：EDIABAS 环境预检 ----------
            string apiDllPath = Path.Combine(EdiabasBinDir, "api32.dll");
            if (!File.Exists(apiDllPath))
            {
                Console.Error.WriteLine("[错误] 未找到 " + apiDllPath + "。请确认 EDIABAS 已安装在 C:\\EDIABAS。");
                return 2;
            }

            using (AppLog log = AppLog.Create(options.Verbose))
            {
                log.Info("启动；OS={0}；进程={1}位；ECU={2}；ForcedJob={3}",
                    Environment.OSVersion, Environment.Is64BitProcess ? 64 : 32,
                    options.EcuName, options.JobName ?? "(候选探测)");
                if (log.FilePath != null)
                {
                    Console.WriteLine("日志文件: " + log.FilePath);
                }

                // 让 api32.dll 及其依赖（ebas32.dll 等）从 C:\EDIABAS\Bin 解析
                if (!NativeMethods.SetDllDirectory(EdiabasBinDir))
                {
                    log.Warn("SetDllDirectory({0}) 失败，依赖 PATH 解析 api32.dll", EdiabasBinDir);
                }
                try { Environment.CurrentDirectory = EdiabasBinDir; }
                catch (Exception ex) { log.Warn("切换工作目录到 {0} 失败：{1}", EdiabasBinDir, ex.Message); }

                // ---------- AC-5：COM 端口预检（占用/未插入给出明确提示） ----------
                string comError = PrecheckComPort(log);
                if (comError != null)
                {
                    log.Error("COM 预检失败：{0}", comError);
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("[错误] " + comError);
                    return 3;
                }

                using (var client = new EdiabasClient(log))
                {
                    Console.CancelKeyPress += (s, e) =>
                    {
                        e.Cancel = true; // 走正常退出路径，保证 apiEnd 执行（FR-6）
                        _cancelRequested = true;
                        client.RequestBreak();
                    };

                    try
                    {
                        // FR-2：初始化
                        client.Init();
                        client.CheckVersion();

                        // FR-3/FR-4/FR-5：执行识别类 Job 并提取车辆基础信息
                        var reader = new VehicleInfoReader(log);
                        VehicleInfo info = reader.Read(client, options.EcuName, options.JobName, options.Para);

                        PrintReport(info);

                        if (info.TotalFieldCount == 0)
                        {
                            Console.WriteLine("[警告] Job 执行成功但未返回任何结果字段，请核对 ECU 与 Job 是否匹配。");
                        }
                        if (!info.AllMvpFound)
                        {
                            Console.WriteLine("[警告] MVP 字段未全部读取到（见上方“未读取到”项）。" +
                                "链路已验证，可对照原始结果确认字段命名，或用 --job 指定其他识别类 Job。");
                        }
                        Console.WriteLine();
                        Console.WriteLine("完成：与 DME 的诊断通信已建立并成功读取数据。");
                        return 0;
                    }
                    catch (OperationCanceledException ex)
                    {
                        log.Warn("用户中断：{0}", ex.Message);
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("[中断] 已被用户中断 (Ctrl+C)，资源已正常释放。");
                        return 3;
                    }
                    catch (EdiabasTimeoutException ex)
                    {
                        log.Error("超时：{0}", ex.Message);
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("[错误] " + ex.Message);
                        Console.Error.WriteLine("请检查：点火开关已打开 (KL.15)、K+DCAN 线连接可靠、线缆时序兼容性（必要时先用 INPA/Tool32 验证）。");
                        PrintTraceHint();
                        return 3;
                    }
                    catch (EdiabasJobException ex)
                    {
                        PrintJobError(ex, log);
                        return ex.Category == EdiabasErrorCategory.Interface ? 3 : 4;
                    }
                    catch (NoIdentJobException ex)
                    {
                        PrintNoIdentJob(ex, log);
                        return 4;
                    }
                    catch (EdiabasException ex)
                    {
                        log.Error("EDIABAS 错误：{0}", ex.Message);
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("[错误] " + ex.Message);
                        PrintEnvErrorHint();
                        PrintTraceHint();
                        return 2;
                    }
                    catch (Exception ex)
                    {
                        log.Error("意外错误：{0}", ex);
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("[错误] 发生意外错误（详细堆栈见日志文件）：" + ex.Message);
                        return 1;
                    }
                }
            }
        }

        // ---------- 输出 ----------

        /// <summary>FR-5：格式化打印字段名 + 值（单位在可推定时给出）。</summary>
        private static void PrintReport(VehicleInfo info)
        {
            Console.WriteLine();
            Console.WriteLine("================ 车辆基础信息 ================");
            Console.WriteLine("ECU (SGBD): {0}    Job: {1}", info.SgbdName, info.JobNames);
            PrintMvp(info.Vin);
            PrintMvp(info.EcuName);
            PrintMvp(info.HwVersion);
            PrintMvp(info.SwVersion);

            Console.WriteLine("---------------- 可选信息 ------------------");
            PrintOptional(info.Ignition, null);
            PrintOptional(info.Voltage, "V");

            Console.WriteLine("---------------- 原始结果 ------------------");
            foreach (JobResult raw in info.Raws)
            {
                Console.WriteLine("  [Job: {0}]", raw.JobName);
                foreach (ResultSet set in raw.Sets)
                {
                    Console.WriteLine("    [结果集 {0}]（{1} 个字段）", set.Index, set.Fields.Count);
                    int width = set.Fields.Count == 0 ? 0 : set.Fields.Max(f => f.Name.Length);
                    foreach (ResultField field in set.Fields)
                    {
                        Console.WriteLine("      {0}  ({1})  =  {2}",
                            field.Name.PadRight(width), EdiabasClient.FormatName(field.Format), field.Value);
                    }
                }
            }
            Console.WriteLine("============================================");
        }

        private static void PrintMvp(ExtractedField field)
        {
            if (field.Found)
            {
                Console.WriteLine("  {0,-18}: {1}    (来源: {2} / {3})", field.Label, field.Value, field.SourceJob, field.SourceField);
            }
            else
            {
                Console.WriteLine("  {0,-18}: 未读取到", field.Label);
            }
        }

        private static void PrintOptional(ExtractedField field, string unitHint)
        {
            if (!field.Found)
            {
                Console.WriteLine("  {0,-18}: 未读取到（可选）", field.Label);
                return;
            }
            string unit = string.Empty;
            if (unitHint != null)
            {
                double d;
                if (double.TryParse(field.Value.Replace(',', '.'),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out d))
                {
                    unit = " " + unitHint;
                }
            }
            Console.WriteLine("  {0,-18}: {1}{2}    (来源: {3} / {4})", field.Label, field.Value, unit, field.SourceJob, field.SourceField);
        }

        // ---------- 错误场景化提示（FR-7） ----------

        private static void PrintJobError(EdiabasJobException ex, AppLog log)
        {
            log.Error("Job 失败：{0}", ex.Message);
            Console.Error.WriteLine();
            if (_cancelRequested)
            {
                Console.Error.WriteLine("[中断] 已被用户中断 (Ctrl+C)，资源已正常释放。");
                return;
            }
            if (ex.IsEcuRejection)
            {
                Console.Error.WriteLine("[错误] 与 ECU 通信成功，但 ECU 拒绝了该诊断请求（{0}）。", ex.ErrorText);
                Console.Error.WriteLine("可能原因与对策：");
                Console.Error.WriteLine("  1) 实际 DME 与该 SGBD 不完全匹配（风险项 R-1）：--ecu 更换 SGBD 重试");
                Console.Error.WriteLine("     （实测本车 DME 接受：01mevd1724 / MEVD174K / MEVD176K / MEVD17KW / 01mevd1726 / 01MEVD1727 / 01MEVD1729）；");
                Console.Error.WriteLine("  2) 该 Job 需要参数：用 Tool32 查看 {0}.prg 中 {1} 的参数定义后 --para 传入；", ex.Ecu, ex.Job);
                Console.Error.WriteLine("  3) 若多个 SGBD 均报子功能不支持，可能 DME 软件版本过新/过旧，需匹配对应版本的 PRG。");
                return;
            }
            switch (ex.Category)
            {
                case EdiabasErrorCategory.Interface:
                    Console.Error.WriteLine("[错误] 无法与车辆 DME 建立通信（接口类错误 {0}: {1}）。", ex.ErrorCode, ex.ErrorText);
                    Console.Error.WriteLine("请依次检查：");
                    Console.Error.WriteLine("  1) K+DCAN 线已连接车辆 OBD 接口与电脑 USB，且车辆点火开关已打开 (KL.15)；");
                    Console.Error.WriteLine("  2) COM6 未被 INPA / Tool32 / 其他程序占用（端口为独占使用）；");
                    Console.Error.WriteLine("  3) 线缆为可用的 FTDI K+DCAN 线（必要时先用 INPA/Tool32 验证线缆与车辆通信）；");
                    Console.Error.WriteLine("  4) 蓄电池电压正常（电压过低会导致诊断中断，必要时接充电器）。");
                    PrintTraceHint();
                    break;
                case EdiabasErrorCategory.Bip:
                case EdiabasErrorCategory.Sys:
                    Console.Error.WriteLine("[错误] SGBD/Job 层错误（{0}: {1}）。", ex.ErrorCode, ex.ErrorText);
                    Console.Error.WriteLine("请确认 C:\\EDIABAS\\ECU\\{0}.prg 存在，且 Job 名称正确（可用 Tool32 查看 PRG 内 Job 列表）。", ex.Ecu);
                    break;
                default:
                    Console.Error.WriteLine("[错误] Job {0} 执行失败：错误码 {1} ({2})。JobInfo: {3}",
                        ex.Job, ex.ErrorCode, ex.ErrorText, ex.JobInfo);
                    PrintTraceHint();
                    break;
            }
        }

        private static void PrintNoIdentJob(NoIdentJobException ex, AppLog log)
        {
            log.Error("无可用识别 Job：{0}", ex.Message);
            Console.Error.WriteLine();
            Console.Error.WriteLine("[错误] 所有候选识别类 Job 均执行失败：");
            foreach (EdiabasJobException f in ex.Failures)
            {
                if (f.IsEcuRejection)
                {
                    Console.Error.WriteLine("  - {0}: ECU 拒绝 ({1})", f.Job, f.ErrorText);
                }
                else
                {
                    Console.Error.WriteLine("  - {0}: 错误码 {1} ({2})", f.Job, f.ErrorCode, f.ErrorText);
                }
            }
            Console.Error.WriteLine("排查建议：");
            Console.Error.WriteLine("  1) 若失败含 \"ECU 拒绝 (JOB_STATUS=ERROR_ECU_SUB_FUNCTION_NOT_SUPPORTED)\"：");
            Console.Error.WriteLine("     说明 DME 通信正常但该 SGBD 的识别服务不匹配 —— --ecu 换 SGBD（R-1），或 --para 传 Job 参数；");
            Console.Error.WriteLine("  2) 用 Tool32 (C:\\EDIABAS\\Bin\\tool32.exe) 加载 {0}.prg 查看实际 Job 列表与参数定义，", ex.EcuName);
            Console.Error.WriteLine("     然后用  ObdLogger.exe --job <Job名> [--para <参数>]  重试；");
            Console.Error.WriteLine("  3) 确认 C:\\EDIABAS\\ECU\\{0}.prg 文件存在。", ex.EcuName);
        }

        private static void PrintEnvErrorHint()
        {
            Console.Error.WriteLine("请确认：C:\\EDIABAS 目录完整、EDIABAS.INI 中 Interface = STD:OBD、PATH 包含 C:\\EDIABAS\\Bin。");
        }

        /// <summary>FR-9：提示可开启 EDIABAS 侧跟踪辅助定位（程序不修改配置）。</summary>
        private static void PrintTraceHint()
        {
            Console.Error.WriteLine("排障提示：可将 C:\\EDIABAS\\Bin\\EDIABAS.INI 中 ApiTrace / IfhTrace 临时设为 1，");
            Console.Error.WriteLine("          重跑后查看 C:\\EDIABAS\\TRACE\\ 下的跟踪文件（排障后改回 0）。");
        }

        // ---------- COM 端口预检 ----------

        /// <summary>返回 null 表示通过；否则返回人类可读的错误描述（AC-4/AC-5）。</summary>
        private static string PrecheckComPort(AppLog log)
        {
            string portName = ReadObdPortName(log);
            string[] ports;
            try
            {
                ports = SerialPort.GetPortNames();
            }
            catch (Exception ex)
            {
                log.Warn("枚举串口失败（跳过预检，交由 EDIABAS 判定）：{0}", ex.Message);
                return null;
            }

            bool exists = ports.Any(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase));
            if (!exists)
            {
                return "未检测到 " + portName + "（K+DCAN 线未插入电脑，或 FTDI 驱动未正常工作）。当前系统串口: " +
                       (ports.Length == 0 ? "（无）" : string.Join(", ", ports));
            }
            try
            {
                using (var sp = new SerialPort(portName))
                {
                    sp.Open();
                    sp.Close();
                }
                log.Info("COM 预检通过：{0} 可打开", portName);
                return null;
            }
            catch (Exception ex)
            {
                return portName + " 无法打开，可能被 INPA / Tool32 / 其他程序占用（端口独占）。请关闭占用程序后重试。(" + ex.Message + ")";
            }
        }

        /// <summary>从 obd.ini [OBD] 段读取 Port（默认 COM6），保持与现有配置一致（FR-2/TC-3）。</summary>
        private static string ReadObdPortName(AppLog log)
        {
            try
            {
                string iniPath = Path.Combine(EdiabasBinDir, "obd.ini");
                if (!File.Exists(iniPath)) return DefaultPortName;
                bool inObdSection = false;
                foreach (string raw in File.ReadAllLines(iniPath))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[", StringComparison.Ordinal))
                    {
                        inObdSection = line.Equals("[OBD]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (inObdSection && line.StartsWith("Port", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                        {
                            string value = line.Substring(eq + 1).Trim();
                            if (value.Length > 0) return value.ToUpperInvariant();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.Warn("读取 obd.ini 失败，使用默认端口 {0}：{1}", DefaultPortName, ex.Message);
            }
            return DefaultPortName;
        }

        /// <summary>
        /// 将用户传入的相对路径按“启动目录”解析为绝对路径（空/已根化则原样返回）。
        /// 因 EDIABAS 需将 CurrentDirectory 切到 C:\EDIABAS\Bin，否则相对 --config/--out 会被错误解析。
        /// </summary>
        private static string ResolveUserPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            try
            {
                if (Path.IsPathRooted(path)) return path;
                string baseDir = string.IsNullOrEmpty(_launchDir) ? AppDomain.CurrentDomain.BaseDirectory : _launchDir;
                return Path.GetFullPath(Path.Combine(baseDir, path));
            }
            catch
            {
                return path; // 非法字符等：交由后续使用处报错
            }
        }

        // ================= 二期：log 模式（实时采样与 CSV 落盘） =================

        /// <summary>
        /// 二期入口（FR-2-1）：实时采样发动机数据、控制台就地显示、CSV 落盘。
        /// 复用一个期的环境预检与 EdiabasClient/VehicleInfoReader；采集在主线程串行调用只读测量块 Job。
        /// </summary>
        private static int RunLogging(string[] args, int start)
        {
            LogOptions lo;
            if (!LogOptions.TryParse(args, start, out lo))
            {
                PrintUsage();
                return 1;
            }
            if (lo.ShowHelp)
            {
                PrintUsage();
                return 0;
            }

            Console.WriteLine("OBD Logger 二期 - 实时数据定时采样与 CSV 落盘");
            Console.WriteLine("链路: ObdLogger → api32.dll → EDIABAS 7.3.0 → " + lo.EcuName + ".prg → K+DCAN → DME");
            Console.WriteLine("安全红线: 仅只读测量块 Job（STATUS_MESSWERTBLOCK_LESEN），无任何写入/复位/编程操作");
            Console.WriteLine();

            // ---------- TC-1：32 位进程自检 ----------
            if (Environment.Is64BitProcess)
            {
                Console.Error.WriteLine("[错误] 当前进程为 64 位，无法加载 32 位 api32.dll。请使用 x86 构建运行。");
                return 2;
            }
            // ---------- TC-2：EDIABAS 环境预检 ----------
            string apiDllPath = Path.Combine(EdiabasBinDir, "api32.dll");
            if (!File.Exists(apiDllPath))
            {
                Console.Error.WriteLine("[错误] 未找到 " + apiDllPath + "。请确认 EDIABAS 已安装在 C:\\EDIABAS。");
                return 2;
            }

            using (AppLog log = AppLog.Create(lo.Verbose))
            {
                log.Info("log 模式启动；OS={0}；进程={1}位；ECU={2}；rate={3}Hz；out={4}；config={5}；paraSep='{6}'；allowJA={7}",
                    Environment.OSVersion, Environment.Is64BitProcess ? 64 : 32, lo.EcuName, lo.RateHz,
                    lo.OutPath ?? "(默认 log\\)", lo.ConfigPath ?? "(内置默认)", lo.ParaSep, lo.AllowModeJa);
                if (log.FilePath != null)
                {
                    Console.WriteLine("日志文件: " + log.FilePath);
                }

                if (!NativeMethods.SetDllDirectory(EdiabasBinDir))
                {
                    log.Warn("SetDllDirectory({0}) 失败，依赖 PATH 解析 api32.dll", EdiabasBinDir);
                }
                try { Environment.CurrentDirectory = EdiabasBinDir; }
                catch (Exception ex) { log.Warn("切换工作目录到 {0} 失败：{1}", EdiabasBinDir, ex.Message); }

                // ---------- AC-2-5：COM 端口预检（未接车/占用 → 明确提示并正常退出） ----------
                string comError = PrecheckComPort(log);
                if (comError != null)
                {
                    log.Error("COM 预检失败：{0}", comError);
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("[错误] " + comError);
                    return 3;
                }

                // ---------- 加载测量值目录（嵌入 MESSWERTETAB，FR-2-9）+ 采样清单（FR-2-4） ----------
                MesswertCatalog catalog = MesswertCatalog.LoadEmbedded(log);
                SampleConfig config;
                string configPath = ResolveUserPath(lo.ConfigPath);
                string outPath = ResolveUserPath(lo.OutPath);
                try
                {
                    config = string.IsNullOrEmpty(configPath)
                        ? SampleConfig.LoadDefault(catalog, log)
                        : SampleConfig.LoadFile(configPath, catalog, log);
                }
                catch (SampleConfigException ex)
                {
                    log.Error("采样配置错误：{0}", ex.Message);
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("[错误] " + ex.Message);
                    return 1;
                }
                catch (Exception ex)
                {
                    log.Error("读取采样配置失败：{0}", ex);
                    Console.Error.WriteLine();
                    Console.Error.WriteLine("[错误] 读取采样配置失败：" + ex.Message);
                    return 1;
                }

                int groupCount = (int)Math.Ceiling(config.Count / (double)SampleConfig.MaxArgsPerCall);
                Console.WriteLine("采样清单: {0}（{1} 项，{2} 组，单次调用 ≤{3} 项）",
                    config.Source, config.Count, groupCount, SampleConfig.MaxArgsPerCall);

                var settings = new SamplerSettings
                {
                    Ecu = lo.EcuName,
                    RateHz = lo.RateHz,
                    ParaSep = lo.ParaSep,
                    AllowModeJa = lo.AllowModeJa,
                    HandleQuitKey = lo.HandleQuitKey,
                    MaxDurationSec = lo.DurationSec,
                };

                using (var client = new EdiabasClient(log))
                {
                    try
                    {
                        // FR-2：初始化
                        client.Init();
                        client.CheckVersion();

                        // 车辆基础信息（VIN 供 CSV 头；失败留空且不阻塞采样，§6 / FR-2-6）。
                        // 接口类错误/超时直接上抛，无车时快速退出（AC-2-5）。
                        string vin = string.Empty;
                        string ecuIdent = string.Empty;
                        try
                        {
                            var reader = new VehicleInfoReader(log);
                            VehicleInfo info = reader.Read(client, lo.EcuName, null, null);
                            if (info.Vin.Found) vin = info.Vin.Value;
                            if (info.EcuName.Found) ecuIdent = info.EcuName.Value;
                        }
                        catch (EdiabasTimeoutException) { throw; }
                        catch (OperationCanceledException) { throw; }
                        catch (EdiabasJobException jex)
                        {
                            if (jex.Category == EdiabasErrorCategory.Interface) throw; // 无车/通信失败：快速退出
                            log.Warn("识别 Job 失败（VIN 留空，继续采样）：{0}", jex.Message);
                        }
                        catch (Exception ex)
                        {
                            log.Warn("读取车辆基础信息失败（VIN 留空，继续采样）：{0}", ex.Message);
                        }
                        Console.WriteLine("车辆: VIN={0}  ECU标识={1}",
                            string.IsNullOrEmpty(vin) ? "(未读取到)" : vin,
                            string.IsNullOrEmpty(ecuIdent) ? "(未读取到)" : ecuIdent);

                        var meta = new CsvHeaderInfo
                        {
                            Vehicle = "E84 N20",
                            Sgbd = lo.EcuName,
                            Vin = vin,
                            StartTime = DateTime.Now,
                            RateHz = settings.RateHz,
                            ConfigSource = config.Source,
                        };

                        var stats = new SamplerStats();
                        var display = new LiveDisplay(config, stats, log);

                        using (var csv = new CsvWriter(outPath, config, meta, log))
                        using (var sampler = new Sampler(client, config, csv, display, log, settings, stats))
                        {
                            // FR-2-7：Ctrl+C → 置停止标志 + apiBreak（沿用一期机制，保证 CSV 完整关闭与 apiEnd）
                            Console.CancelKeyPress += (s, e) =>
                            {
                                e.Cancel = true;
                                sampler.RequestStop();
                            };

                            int code = 0;
                            log.ConsoleEnabled = false; // 显示接管控制台；日志仅落文件（FR-2-3）
                            try
                            {
                                sampler.Run();
                            }
                            catch (SamplerStartException sse)
                            {
                                code = ReportSamplerStart(sse, log);
                            }
                            catch (OperationCanceledException)
                            {
                                code = 0;
                            }
                            catch (EdiabasException ex)
                            {
                                log.Error("EDIABAS 错误：{0}", ex.Message);
                                code = 2;
                            }
                            catch (Exception ex)
                            {
                                log.Error("意外错误：{0}", ex);
                                code = 1;
                            }
                            finally
                            {
                                log.ConsoleEnabled = true;
                            }

                            if (code != 0)
                            {
                                return code;
                            }

                            PrintLogSummary(stats, csv, sampler, log);
                            return 0;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        log.Warn("用户中断（初始化阶段）");
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("[中断] 已被用户中断 (Ctrl+C)，资源已正常释放。");
                        return 3;
                    }
                    catch (EdiabasTimeoutException ex)
                    {
                        log.Error("超时：{0}", ex.Message);
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("[错误] " + ex.Message);
                        Console.Error.WriteLine("请检查：点火开关已打开 (KL.15)、K+DCAN 线连接可靠、线缆时序兼容性。");
                        PrintTraceHint();
                        return 3;
                    }
                    catch (EdiabasJobException ex)
                    {
                        PrintJobError(ex, log);
                        return ex.Category == EdiabasErrorCategory.Interface ? 3 : 4;
                    }
                    catch (EdiabasException ex)
                    {
                        log.Error("EDIABAS 错误：{0}", ex.Message);
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("[错误] " + ex.Message);
                        PrintEnvErrorHint();
                        PrintTraceHint();
                        return 2;
                    }
                    catch (Exception ex)
                    {
                        log.Error("意外错误：{0}", ex);
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("[错误] 发生意外错误（详细堆栈见日志文件）：" + ex.Message);
                        return 1;
                    }
                }
            }
        }

        /// <summary>采样启动阶段致命失败 → 场景化提示 + 退出码（复用一期错误提示风格）。</summary>
        private static int ReportSamplerStart(SamplerStartException sse, AppLog log)
        {
            log.Error("采样启动失败（{0}）：{1}；{2}", sse.Kind, sse.Message, sse.Detail);
            Console.Error.WriteLine();
            switch (sse.Kind)
            {
                case SamplerStartException.FailureKind.NoConnection:
                    Console.Error.WriteLine("[错误] 无法与车辆 DME 建立通信：" + sse.Message);
                    Console.Error.WriteLine("请依次检查：");
                    Console.Error.WriteLine("  1) K+DCAN 线已连接车辆 OBD 与电脑 USB，且点火开关已打开 (KL.15)；");
                    Console.Error.WriteLine("  2) COM6 未被 INPA / Tool32 / 其他程序占用（端口独占）；");
                    Console.Error.WriteLine("  3) 线缆为可用 FTDI K+DCAN（必要时先用 INPA/Tool32 验证）；");
                    Console.Error.WriteLine("  4) 蓄电池电压正常（过低会中断诊断，长记录建议接充电器，R-2-6）。");
                    PrintTraceHint();
                    return 3;
                case SamplerStartException.FailureKind.EcuRejected:
                    Console.Error.WriteLine("[错误] " + sse.Message);
                    Console.Error.WriteLine("对策：--ecu 更换 SGBD（本车实测可用：MEVD174K / 01mevd1724 / MEVD176K / MEVD17KW / 01mevd1726 / 01MEVD1727 / 01MEVD1729）。");
                    return 4;
                case SamplerStartException.FailureKind.BadArguments:
                    Console.Error.WriteLine("[错误] " + sse.Message);
                    Console.Error.WriteLine("  详情：" + sse.Detail);
                    Console.Error.WriteLine("可能原因与对策：");
                    Console.Error.WriteLine("  1) 动态参数分隔符不符（R-2-1）：程序已自动尝试 ';' 与 ','，若仍失败可用 --para-sep 指定；");
                    Console.Error.WriteLine("  2) 采样清单含无效 ARG：核对 doc\\材料\\messwertetab.csv（FR-2-9 导出）的 ARG 列；");
                    Console.Error.WriteLine("  3) 该 SGBD 不支持 STATUS_MESSWERTBLOCK_LESEN：--ecu 更换（见上）；");
                    Console.Error.WriteLine("  4) MODE=JA 的 $2C 动态定义被当前 ECU 状态拒绝（R-2-4）：加 --no-ja 用保守只读模式重试。");
                    return 4;
                case SamplerStartException.FailureKind.Environment:
                default:
                    Console.Error.WriteLine("[错误] " + sse.Message);
                    PrintEnvErrorHint();
                    PrintTraceHint();
                    return 2;
            }
        }

        /// <summary>FR-2-11：运行结束输出采样汇总。</summary>
        private static void PrintLogSummary(SamplerStats stats, CsvWriter csv, Sampler sampler, AppLog log)
        {
            double elapsed = stats.ElapsedSeconds;
            if (elapsed < 0) elapsed = 0;
            var ts = TimeSpan.FromSeconds(elapsed);
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            Console.WriteLine();
            Console.WriteLine("================ 采样汇总 (FR-2-11) ================");
            Console.WriteLine("运行时长 : {0:00}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            Console.WriteLine("有效样本 : {0}", stats.Ok);
            Console.WriteLine("数据缺口 : {0}", stats.Gap);
            Console.WriteLine("平均速率 : {0} Hz", stats.RateHz.ToString("0.00", inv));
            Console.WriteLine("CSV 文件 : {0}", string.IsNullOrEmpty(csv.CurrentPath) ? "(未创建)" : csv.CurrentPath);
            Console.WriteLine("           共 {0} 行，滚动 {1} 次", csv.RowsWritten, csv.RollCount);
            Console.WriteLine("协商参数 : 分隔符='{0}'  MODE 定义/读取='{1}'/'{2}'",
                sampler.NegotiatedParaSep,
                sampler.NegotiatedModeDefine ?? "(未用 JA)",
                sampler.NegotiatedModeRead);
            Console.WriteLine("测量块   : 单次定义最大 {0} 项 → {1} 组（ECU 对过大的 $2C 定义返回 requestOutOfRange，已自动收缩）",
                sampler.MaxBlockDiscovered, sampler.GroupCount);
            if (sampler.ExcludedArgs != null && sampler.ExcludedArgs.Length > 0)
            {
                Console.WriteLine("已跳过项 : {0}（被 ECU 拒绝，如 Resultname not valid；其 CSV 列留空）",
                    string.Join(", ", sampler.ExcludedArgs));
            }
            if (stats.Gap > 0)
            {
                Console.WriteLine("提示     : 存在数据缺口（CSV 中以空字段标记）；常见于点火 OFF/线缆松动，已自动退避重试并恢复（FR-2-8）。");
            }
            Console.WriteLine("排障     : 需电报级跟踪时，可将 C:\\EDIABAS\\Bin\\EDIABAS.INI 的 ApiTrace/IfhTrace 设为 1 后重跑（FR-2-12，程序不改配置）。");
            Console.WriteLine("====================================================");
            Console.WriteLine();
            Console.WriteLine("[完成] 采样已停止，CSV 已完整关闭，apiEnd 已释放（FR-2-7）。");

            log.Info("采样汇总：时长={0:F1}s，样本={1}，缺口={2}，速率={3:F2}Hz，CSV行数={4}，滚动={5}",
                elapsed, stats.Ok, stats.Gap, stats.RateHz, csv.RowsWritten, csv.RollCount);
        }

        /// <summary>二期 log 模式的命令行选项。</summary>
        private sealed class LogOptions
        {
            public string EcuName = DefaultLogEcuName;
            public string OutPath;
            public double RateHz = 1.0;
            public string ConfigPath;
            public string ParaSep = ";";
            public bool AllowModeJa = true;
            public bool HandleQuitKey = true;
            public double DurationSec = 0;
            public bool Verbose;
            public bool ShowHelp;

            public static bool TryParse(string[] args, int start, out LogOptions o)
            {
                o = new LogOptions();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                for (int i = start; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a)
                    {
                        case "--ecu":
                            if (++i >= args.Length) return false;
                            o.EcuName = args[i];
                            break;
                        case "--out":
                            if (++i >= args.Length) return false;
                            o.OutPath = args[i];
                            break;
                        case "--rate":
                            if (++i >= args.Length) return false;
                            double r;
                            if (!double.TryParse(args[i], System.Globalization.NumberStyles.Float, inv, out r) || r <= 0)
                            {
                                Console.Error.WriteLine("[错误] --rate 需为正数（Hz），如 1 / 2 / 0.5：" + args[i]);
                                return false;
                            }
                            o.RateHz = r;
                            break;
                        case "--config":
                            if (++i >= args.Length) return false;
                            o.ConfigPath = args[i];
                            break;
                        case "--para-sep":
                            if (++i >= args.Length) return false;
                            if (args[i].Length == 0)
                            {
                                Console.Error.WriteLine("[错误] --para-sep 不能为空");
                                return false;
                            }
                            o.ParaSep = args[i];
                            break;
                        case "--no-ja":
                            o.AllowModeJa = false;
                            break;
                        case "--no-hotkey":
                            o.HandleQuitKey = false;
                            break;
                        case "--duration":
                            if (++i >= args.Length) return false;
                            double dur;
                            if (!double.TryParse(args[i], System.Globalization.NumberStyles.Float, inv, out dur) || dur <= 0)
                            {
                                Console.Error.WriteLine("[错误] --duration 需为正数（秒）：" + args[i]);
                                return false;
                            }
                            o.DurationSec = dur;
                            break;
                        case "--verbose":
                        case "-v":
                            o.Verbose = true;
                            break;
                        case "--help":
                        case "-h":
                        case "/?":
                            o.ShowHelp = true;
                            break;
                        default:
                            Console.Error.WriteLine("[错误] log 模式未知参数: " + a);
                            return false;
                    }
                }
                return true;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine("用法:");
            Console.WriteLine("  ObdLogger.exe [选项]              一期：读取并打印车辆基础信息");
            Console.WriteLine("  ObdLogger.exe log [log 选项]      二期：实时采样发动机数据并落盘 CSV");
            Console.WriteLine();
            Console.WriteLine("一期选项:");
            Console.WriteLine("  --ecu <名称>     SGBD/PRG 名称（默认 " + DefaultEcuName + "；本车实测应使用 MEVD174K）");
            Console.WriteLine("  --job <名称>     强制使用指定只读 Job（跳过候选探测；Job 名可用 Tool32 查看）");
            Console.WriteLine("  --para <参数>    传给 Job 的参数（识别类 Job 通常留空）");
            Console.WriteLine("  --verbose, -v    控制台输出 API 调用明细（日志文件始终记录）");
            Console.WriteLine("  --help, -h       显示本帮助");
            Console.WriteLine();
            Console.WriteLine("二期 log 选项:");
            Console.WriteLine("  --ecu <名称>     SGBD/PRG 名称（log 默认 " + DefaultLogEcuName + "，本车实测可用）");
            Console.WriteLine("  --out <路径>     CSV 输出（文件或目录；默认 exe目录\\log\\obdlog_<时间戳>.csv）");
            Console.WriteLine("  --rate <Hz>      目标采样率（默认 1，范围 0.1~5；实际以 ECU 响应为准）");
            Console.WriteLine("  --config <文件>  采样清单配置（每行 ARG,显示名[,单位]；默认用内置 §3.3 已核实清单）");
            Console.WriteLine("  --para-sep <c>   动态参数分隔符（默认 ';'；启动时自动协商 ';' 与 ','，风险项 R-2-1）");
            Console.WriteLine("  --no-ja          保守模式：仅用 MODE=NEIN 读 ECU 已存定义，不写 ECU RAM（§3.4）");
            Console.WriteLine("  --no-hotkey      不监听 q 键（无人值守/后台运行；Ctrl+C 仍可停止）");
            Console.WriteLine("  --duration <秒>  运行指定秒数后优雅自停（无人值守定时录制；默认 0=直到手动停止）");
            Console.WriteLine("  --verbose, -v    控制台输出 API 明细（log 模式默认静默、仅文件，FR-2-3）");
            Console.WriteLine("  --help, -h       显示本帮助");
            Console.WriteLine();
            Console.WriteLine("退出码: 0=成功  1=意外错误  2=EDIABAS 环境/初始化失败  3=通信/端口失败  4=无可用 Job/参数被拒");
            Console.WriteLine("注意: 仅执行只读 Job；请勿与 INPA/Tool32 同时运行（COM6 独占）。");
            Console.WriteLine();
            Console.WriteLine("示例:");
            Console.WriteLine("  ObdLogger.exe log                                     默认 1Hz 采样内置清单，落盘 exe\\log\\");
            Console.WriteLine("  ObdLogger.exe log --rate 2 --out D:\\logs             2Hz，输出到 D:\\logs 目录");
            Console.WriteLine("  ObdLogger.exe log --config config\\sample-extended.txt 使用扩展采样清单");
        }

        private sealed class Options
        {
            public string EcuName = DefaultEcuName;
            public string JobName;
            public string Para;
            public bool Verbose;
            public bool ShowHelp;

            public static bool TryParse(string[] args, out Options options)
            {
                options = new Options();
                for (int i = 0; i < args.Length; i++)
                {
                    string a = args[i];
                    switch (a)
                    {
                        case "--ecu":
                            if (++i >= args.Length) return false;
                            options.EcuName = args[i];
                            break;
                        case "--job":
                            if (++i >= args.Length) return false;
                            options.JobName = args[i];
                            break;
                        case "--para":
                            if (++i >= args.Length) return false;
                            options.Para = args[i];
                            break;
                        case "--verbose":
                        case "-v":
                            options.Verbose = true;
                            break;
                        case "--help":
                        case "-h":
                        case "/?":
                            options.ShowHelp = true;
                            break;
                        default:
                            Console.Error.WriteLine("[错误] 未知参数: " + a);
                            return false;
                    }
                }
                return true;
            }
        }
    }
}
