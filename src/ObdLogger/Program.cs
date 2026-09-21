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
        private const string EdiabasBinDir = @"C:\EDIABAS\Bin";
        private const string DefaultPortName = "COM6";

        private static volatile bool _cancelRequested;

        private static int Main(string[] args)
        {
            try { Console.OutputEncoding = Encoding.UTF8; } catch { /* 旧控制台不支持时忽略 */ }

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

                        if (info.Raw.TotalFieldCount == 0)
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
            Console.WriteLine("ECU (SGBD): {0}    识别 Job: {1}", info.SgbdName, info.JobName);
            PrintMvp(info.Vin);
            PrintMvp(info.EcuName);
            PrintMvp(info.HwVersion);
            PrintMvp(info.SwVersion);

            Console.WriteLine("---------------- 可选信息 ------------------");
            PrintOptional(info.Ignition, null);
            PrintOptional(info.Voltage, "V");

            Console.WriteLine("---------------- 原始结果 ------------------");
            foreach (ResultSet set in info.Raw.Sets)
            {
                Console.WriteLine("  [结果集 {0}]（{1} 个字段）", set.Index, set.Fields.Count);
                int width = set.Fields.Count == 0 ? 0 : set.Fields.Max(f => f.Name.Length);
                foreach (ResultField field in set.Fields)
                {
                    Console.WriteLine("    {0}  ({1})  =  {2}",
                        field.Name.PadRight(width), EdiabasClient.FormatName(field.Format), field.Value);
                }
            }
            Console.WriteLine("============================================");
        }

        private static void PrintMvp(ExtractedField field)
        {
            if (field.Found)
            {
                Console.WriteLine("  {0,-18}: {1}    (来源字段: {2})", field.Label, field.Value, field.SourceField);
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
            Console.WriteLine("  {0,-18}: {1}{2}    (来源字段: {3})", field.Label, field.Value, unit, field.SourceField);
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

        private static void PrintUsage()
        {
            Console.WriteLine("用法: ObdLogger.exe [选项]");
            Console.WriteLine();
            Console.WriteLine("选项:");
            Console.WriteLine("  --ecu <名称>     SGBD/PRG 名称（默认 " + DefaultEcuName + "；DME 不匹配时更换，风险项 R-1）");
            Console.WriteLine("  --job <名称>     强制使用指定只读 Job（跳过候选探测；Job 名可用 Tool32 查看）");
            Console.WriteLine("  --para <参数>    传给 Job 的参数（识别类 Job 通常留空；参数定义可用 Tool32 查看）");
            Console.WriteLine("  --verbose, -v    控制台输出 API 调用明细（日志文件始终记录）");
            Console.WriteLine("  --help, -h       显示本帮助");
            Console.WriteLine();
            Console.WriteLine("退出码: 0=成功  1=意外错误  2=EDIABAS 环境/初始化失败  3=通信/端口失败  4=无可用的识别类 Job");
            Console.WriteLine("注意: 一期仅执行只读识别类 Job；请勿与 INPA/Tool32 同时运行（COM6 独占）。");
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
