using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using ObdLogger.Logging;

namespace ObdLogger.Ediabas
{
    /// <summary>
    /// EDIABAS API 封装层（对应需求文档 NFR-3：与业务输出分离，二期可复用）。
    /// 负责：初始化/释放、Job 执行、结果集枚举、错误信息提取。
    /// 仅暴露只读诊断能力；不提供任何写入/复位/编程入口（TC-6）。
    /// 非线程安全；一个进程一个实例。
    /// </summary>
    internal sealed class EdiabasClient : IDisposable
    {
        // apiState 返回值（Api.h）
        private const int ApiBusy = 0;
        private const int ApiReady = 1;
        private const int ApiBreakState = 2;
        private const int ApiErrorState = 3;

        private readonly AppLog _log;
        private uint _handle;
        private bool _initialized;
        private bool _disposed;

        public EdiabasClient(AppLog log)
        {
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public bool IsInitialized { get { return _initialized; } }

        /// <summary>FR-2：apiInit 初始化（使用现有 EDIABAS.INI / obd.ini 配置）。</summary>
        public void Init()
        {
            if (_initialized) return;
            _log.Api("apiInit()");
            bool ok = NativeMethods.ApiInit(out _handle);
            if (!ok || _handle == 0)
            {
                _log.Api("apiInit() => 失败 (ok={0}, handle=0x{1:X8})", ok, _handle);
                throw new EdiabasException(
                    "apiInit() 失败：EDIABAS 未能初始化。请检查 C:\\EDIABAS 目录、" +
                    "EDIABAS.INI（Interface = STD:OBD）以及 PATH 是否包含 C:\\EDIABAS\\Bin。");
            }
            _initialized = true;
            _log.Api("apiInit() => OK, handle=0x{0:X8}", _handle);
        }

        /// <summary>API 版本兼容性检查（仅记录日志，失败不中断）。</summary>
        public void CheckVersion()
        {
            try
            {
                byte[] buf = new byte[NativeMethods.ApiMaxText];
                // 兼容性版本号格式：主版本 << 8 | 次版本（本机 api32.dll 为 7.3.0）
                bool ok = NativeMethods.ApiCheckVersion(0x0703, buf);
                _log.Api("apiCheckVersion(7.3) => {0}, versionInfo=\"{1}\"", ok, AnsiToString(buf));
            }
            catch (Exception ex)
            {
                _log.Warn("apiCheckVersion 调用异常（忽略）：{0}", ex.Message);
            }
        }

        /// <summary>API 状态：0=BUSY 1=READY 2=BREAK 3=ERROR。</summary>
        public int State()
        {
            return NativeMethods.ApiState(_handle);
        }

        /// <summary>请求中断当前 Job（供 Ctrl+C 处理跨线程调用）。</summary>
        public void RequestBreak()
        {
            try
            {
                if (_initialized)
                {
                    _log.Api("apiBreak()");
                    NativeMethods.ApiBreak(_handle);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("apiBreak 调用异常（忽略）：{0}", ex.Message);
            }
        }

        /// <summary>
        /// FR-3/FR-4：执行只读 Job 并枚举全部结果。
        /// 注意：EDIABAS 7.x 的 apiJob 为异步启动，须轮询 apiState 直至非 BUSY 后再读取错误码与结果
        /// （流程参照宝马官方 apiNET32.dll / TestCsNET32；进度经 apiJobInfo 反馈）。
        /// Job 失败时抛出 <see cref="EdiabasJobException"/>（含错误码/文本/JobInfo）。
        /// </summary>
        /// <param name="ecu">SGBD 名（不含扩展名，如 mevd1724；EDIABAS 按 EcuPath 解析 .prg）</param>
        /// <param name="job">Job 名称</param>
        /// <param name="para">Job 参数（识别类 Job 通常为空）</param>
        public JobResult RunJob(string ecu, string job, string para)
        {
            EnsureInitialized();
            _log.Api("apiJob(ecu={0}, job={1}, para=\"{2}\")", ecu, job, para ?? string.Empty);
            NativeMethods.ApiJob(_handle, ecu, job, para ?? string.Empty, string.Empty);

            WaitJobCompletion(job);

            int errorCode = NativeMethods.ApiErrorCode(_handle);
            string jobInfo = ReadJobInfo(out int percent);
            if (errorCode != 0)
            {
                string errorText = ReadErrorText();
                _log.Api("apiJob({0}) => 错误码 {1} [{2}]；jobInfo=\"{3}\"；apiState={4}",
                    job, errorCode, errorText, jobInfo, State());
                throw new EdiabasJobException(ecu, job, errorCode, errorText, jobInfo);
            }

            _log.Api("apiJob({0}) => OK；jobInfo=\"{1}\" (progress={2}%)", job, jobInfo, percent);
            return ReadResults(job);
        }

        /// <summary>
        /// 等待异步 Job 结束：轮询 apiState（100ms），汇报进度变化；
        /// 带 60 秒兜底超时（FR-7 不挂死；成功场景通常在数秒内完成，满足 NFR-1 ≤30 秒）。
        /// </summary>
        private void WaitJobCompletion(string job)
        {
            const int PollMs = 100;
            const int MaxWaitMs = 60000;

            Thread.Sleep(50); // 避开启动竞态（apiJob 返回后状态未必立即进入 BUSY）
            var sw = Stopwatch.StartNew();
            int lastPercent = int.MinValue;
            while (true)
            {
                int state = NativeMethods.ApiState(_handle);
                if (state != ApiBusy)
                {
                    _log.Api("apiJob({0}) 异步执行结束，apiState={1} ({2})，耗时 {3:F1}s",
                        job, state, StateName(state), sw.Elapsed.TotalSeconds);
                    if (state == ApiBreakState)
                    {
                        throw new OperationCanceledException("Job " + job + " 被 apiBreak 中断");
                    }
                    return; // ApiReady / ApiErrorState：统一由调用方检查 errorCode
                }

                if (sw.ElapsedMilliseconds > MaxWaitMs)
                {
                    _log.Error("apiJob({0}) 等待超过 {1}s，调用 apiBreak 中止", job, MaxWaitMs / 1000);
                    RequestBreak();
                    throw new EdiabasTimeoutException(job, MaxWaitMs / 1000);
                }

                int percent;
                ReadJobInfo(out percent);
                if (percent != lastPercent)
                {
                    _log.Api("apiJob({0}) 执行中，进度 {1}%", job, percent);
                    lastPercent = percent;
                }
                Thread.Sleep(PollMs);
            }
        }

        private static string StateName(int state)
        {
            switch (state)
            {
                case ApiBusy: return "BUSY";
                case ApiReady: return "READY";
                case ApiBreakState: return "BREAK";
                case ApiErrorState: return "ERROR";
                default: return "UNKNOWN";
            }
        }

        /// <summary>FR-4：通过 apiResult...() 系列函数枚举最近一次 Job 的全部结果集与字段。</summary>
        private JobResult ReadResults(string jobName)
        {
            var result = new JobResult { JobName = jobName };
            if (!NativeMethods.ApiResultSets(_handle, out ushort sets))
            {
                _log.Api("apiResultSets => FALSE（该 Job 无结果集）");
                return result;
            }
            _log.Api("apiResultSets => {0} 个结果集", sets);
            if (sets == 0) return result;

            ushort setBase = DetectResultSetBase(sets);
            for (ushort i = 0; i < sets; i++)
            {
                ushort rset = (ushort)(setBase + i);
                var set = new ResultSet { Index = rset };
                if (!NativeMethods.ApiResultNumber(_handle, out ushort count, rset))
                {
                    _log.Api("apiResultNumber(rset={0}) => FALSE，跳过该结果集", rset);
                    result.Sets.Add(set);
                    continue;
                }
                _log.Api("结果集 {0}：{1} 个字段", rset, count);

                for (ushort f = 0; f < count; f++)
                {
                    byte[] nameBuf = new byte[NativeMethods.ApiMaxName];
                    if (!NativeMethods.ApiResultName(_handle, nameBuf, f, rset))
                    {
                        _log.Api("apiResultName(index={0}, rset={1}) => FALSE", f, rset);
                        continue;
                    }
                    string name = AnsiToString(nameBuf);

                    ApiResultFormat format = ApiResultFormat.Unknown;
                    if (NativeMethods.ApiResultFormat(_handle, out int fmt, name, rset))
                    {
                        format = Enum.IsDefined(typeof(ApiResultFormat), fmt)
                            ? (ApiResultFormat)fmt : ApiResultFormat.Unknown;
                    }

                    string value = ReadFieldText(name, rset, format);
                    set.Fields.Add(new ResultField { Name = name, Format = format, Value = value });
                    _log.Api("  [{0}] {1} ({2}) = {3}", rset, name, FormatName(format), value);
                }
                result.Sets.Add(set);
            }
            return result;
        }

        /// <summary>结果集索引基址自适应（0 基或 1 基；正常为 0 基，异常实现兜底探测）。</summary>
        private ushort DetectResultSetBase(ushort sets)
        {
            if (NativeMethods.ApiResultNumber(_handle, out _, 0))
            {
                return 0;
            }
            if (NativeMethods.ApiResultNumber(_handle, out _, 1))
            {
                _log.Warn("结果集索引为 1 基（非典型实现），已自动适配");
                return 1;
            }
            _log.Warn("apiResultNumber 对 rset=0/1 均返回 FALSE，按 0 基继续尝试");
            return 0;
        }

        /// <summary>读取单个字段的文本值；BINARY 格式在文本读取失败时回退为十六进制。</summary>
        private string ReadFieldText(string name, ushort rset, ApiResultFormat format)
        {
            byte[] buf = new byte[NativeMethods.ApiMaxText];
            if (NativeMethods.ApiResultText(_handle, buf, name, rset, null))
            {
                return AnsiToString(buf);
            }
            if (format == ApiResultFormat.Binary)
            {
                byte[] bin = new byte[NativeMethods.ApiMaxBinary];
                ushort len = (ushort)bin.Length;
                if (NativeMethods.ApiResultBinary(_handle, bin, ref len, name, rset) && len > 0)
                {
                    return ToHex(bin, len);
                }
            }
            return string.Empty;
        }

        public string ReadErrorText()
        {
            if (!_initialized) return string.Empty;
            byte[] buf = new byte[NativeMethods.ApiMaxText];
            NativeMethods.ApiErrorText(_handle, buf, buf.Length);
            return AnsiToString(buf);
        }

        public string ReadJobInfo(out int percent)
        {
            percent = 0;
            if (!_initialized) return string.Empty;
            byte[] buf = new byte[NativeMethods.ApiMaxText];
            percent = NativeMethods.ApiJobInfo(_handle, buf);
            return AnsiToString(buf);
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                throw new EdiabasException("EDIABAS 尚未初始化（请先调用 Init()）");
            }
        }

        internal static string FormatName(ApiResultFormat format)
        {
            switch (format)
            {
                case ApiResultFormat.Char: return "CHAR";
                case ApiResultFormat.Byte: return "BYTE";
                case ApiResultFormat.Integer: return "INTEGER";
                case ApiResultFormat.Word: return "WORD";
                case ApiResultFormat.Long: return "LONG";
                case ApiResultFormat.DWord: return "DWORD";
                case ApiResultFormat.Text: return "TEXT";
                case ApiResultFormat.Binary: return "BINARY";
                case ApiResultFormat.Real: return "REAL";
                case ApiResultFormat.LongLong: return "LONGLONG";
                case ApiResultFormat.QWord: return "QWORD";
                default: return "UNKNOWN";
            }
        }

        private static string AnsiToString(byte[] buf)
        {
            if (buf == null) return string.Empty;
            int len = Array.IndexOf(buf, (byte)0);
            if (len < 0) len = buf.Length;
            return Encoding.Default.GetString(buf, 0, len).Trim();
        }

        private static string ToHex(byte[] buf, int len)
        {
            int show = Math.Min(len, 64);
            var sb = new StringBuilder(show * 3 + 16);
            for (int i = 0; i < show; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(buf[i].ToString("X2"));
            }
            if (len > show) sb.Append(" ...");
            sb.Append(" (共 ").Append(len).Append(" 字节)");
            return sb.ToString();
        }

        /// <summary>FR-6：释放 EDIABAS 资源；异常路径同样保证执行（using/finally）。</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_initialized)
            {
                try
                {
                    _log.Api("apiEnd()");
                    NativeMethods.ApiEnd(_handle);
                }
                catch (Exception ex)
                {
                    _log.Warn("apiEnd 调用异常：{0}", ex.Message);
                }
                _initialized = false;
                _handle = 0;
            }
        }
    }
}
