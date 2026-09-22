using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using ObdLogger.Ediabas;

namespace ObdLogger.Logging
{
    /// <summary>采样统计（FR-2-11）：线程安全（Interlocked），供采集线程写、显示线程读。</summary>
    internal sealed class SamplerStats
    {
        private long _ok, _gap;
        public DateTime StartTimeUtc;

        public void IncOk() { Interlocked.Increment(ref _ok); }
        public void IncGap() { Interlocked.Increment(ref _gap); }
        public long Ok { get { return Interlocked.Read(ref _ok); } }
        public long Gap { get { return Interlocked.Read(ref _gap); } }
        public double ElapsedSeconds { get { return (DateTime.UtcNow - StartTimeUtc).TotalSeconds; } }
        public double RateHz
        {
            get { double e = ElapsedSeconds; return e > 0.5 ? Ok / e : 0.0; }
        }
    }

    /// <summary>采样启动阶段的致命失败（连接/参数/SGBD），由 Program 映射为退出码与提示。</summary>
    internal sealed class SamplerStartException : Exception
    {
        public enum FailureKind { NoConnection, BadArguments, EcuRejected, Environment }
        public FailureKind Kind { get; private set; }
        public string Detail { get; private set; }

        public SamplerStartException(FailureKind kind, string message, string detail)
            : base(message)
        {
            Kind = kind;
            Detail = detail ?? string.Empty;
        }
    }

    /// <summary>采样运行参数（由 Program 从 CLI 组装）。</summary>
    internal sealed class SamplerSettings
    {
        public string Ecu = "MEVD174K";
        public double RateHz = 1.0;             // 目标采样率（clamp 到 [0.1,5]，§5）
        public string ParaSep = ";";            // 动态参数分隔符（R-2-1；启动时自动协商兜底 ,）
        public bool AllowModeJa = true;         // false → 保守仅 NEIN（§3.4，不写 ECU RAM）
        public bool HandleQuitKey = true;       // 监听 q 键停止
        public int MaxBackoffMs = 5000;         // 缺口重试退避上限
    }

    /// <summary>
    /// 采样循环（FR-2-2/6/7/8）：<b>唯一持有并调用 <see cref="EdiabasClient"/> 的线程</b>（§5 线程模型）。
    /// 采集在主线程串行调用 STATUS_MESSWERTBLOCK_LESEN；显示经 <see cref="LiveDisplay"/> 快照盒解耦，
    /// CSV 经有界 <see cref="BlockingCollection{T}"/> 交给独立消费线程落盘——采集不被显示/IO 阻塞（NFR-2-1）。
    /// 通信中断 → 标记缺口 + 指数退避重试，进程不崩溃不挂死（FR-2-8）；Ctrl+C/q → 置停止标志 + apiBreak。
    /// </summary>
    internal sealed class Sampler : IDisposable
    {
        /// <summary>核心采样 Job（b1v 权威定义；只读边界见二期需求 §3.4）。</summary>
        public const string JobName = "STATUS_MESSWERTBLOCK_LESEN";

        private const int CsvQueueCapacity = 2000;   // 有界队列：背压保护，防内存无增长（NFR-2-1）
        private const int CsvAddTimeoutMs = 5000;    // 入队超时：CSV 线程卡死时丢弃并告警，绝不挂死采集

        // MODE 值取自 b1v MESSWERTEMODE 表（ein/aus/ja/nein/yes→1/0）；大小写敏感性未知，故启动时按序自动协商。
        private static readonly string[][] ModePairs =
        {
            new[] { "JA", "NEIN" },
            new[] { "ja", "nein" },
            new[] { "ein", "aus" },
        };

        private readonly EdiabasClient _client;
        private readonly SampleConfig _config;
        private readonly CsvWriter _csv;
        private readonly LiveDisplay _display;
        private readonly AppLog _log;
        private readonly SamplerSettings _settings;

        private readonly List<List<SampleItem>> _groups;
        private readonly bool _singleGroup;
        private readonly SamplerStats _stats;
        private readonly int _intervalMs;

        private bool[] _groupDefined;    // 该组是否已用 MODE=JA 定义过（单组 NEIN 复用前提）
        private bool[] _groupJaDisabled; // 该组 JA 被 ECU 拒绝（R-2-4）→ 后续只用 NEIN

        private BlockingCollection<SampleSnapshot> _csvQueue;
        private Thread _csvThread;
        private Thread _inputThread;

        private volatile bool _stop;
        private volatile bool _stopRequested;
        private volatile bool _csvFaulted;

        private string _paraSep;
        private string _modeDefine = "JA";
        private string _modeRead = "NEIN";

        private int _consecutiveFailures;
        private readonly Dictionary<string, int> _throttle = new Dictionary<string, int>(StringComparer.Ordinal);

        public SamplerStats Stats { get { return _stats; } }
        public bool StopRequested { get { return _stopRequested; } }
        public double EffectiveRateHz { get { return 1000.0 / _intervalMs; } }
        public string NegotiatedParaSep { get { return _paraSep; } }
        public string NegotiatedModeDefine { get { return _modeDefine; } }
        public string NegotiatedModeRead { get { return _modeRead; } }

        public Sampler(EdiabasClient client, SampleConfig config, CsvWriter csv, LiveDisplay display,
            AppLog log, SamplerSettings settings, SamplerStats stats)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _csv = csv ?? throw new ArgumentNullException(nameof(csv));
            _display = display ?? throw new ArgumentNullException(nameof(display));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _settings = settings ?? new SamplerSettings();
            _stats = stats ?? new SamplerStats();

            if (_config.Count == 0) throw new SampleConfigException("采样清单为空，无法启动 log 模式");

            _groups = _config.BuildGroups();
            _singleGroup = _groups.Count == 1;
            _groupDefined = new bool[_groups.Count];
            _groupJaDisabled = new bool[_groups.Count];

            double rate = _settings.RateHz;
            if (rate < 0.1) rate = 0.1;
            if (rate > 5.0) rate = 5.0;   // §5 上限 5 Hz
            if (Math.Abs(rate - _settings.RateHz) > 1e-9)
            {
                _log.Warn("目标速率 {0}Hz 超出 [0.1,5] 范围，已 clamp 为 {1}Hz", _settings.RateHz, rate);
            }
            _intervalMs = Math.Max(1, (int)Math.Round(1000.0 / rate));
            _paraSep = string.IsNullOrEmpty(_settings.ParaSep) ? ";" : _settings.ParaSep;
        }

        /// <summary>请求停止（Ctrl+C / q 键，可跨线程调用）：置标志 + apiBreak 中断在途 Job。</summary>
        public void RequestStop()
        {
            _stopRequested = true;
            _stop = true;
            try { _client.RequestBreak(); } catch { /* 忽略 */ }
        }

        /// <summary>
        /// 主流程（主线程）：先协商参数并确认连通（失败致命抛出，不留空 CSV），
        /// 再打开 CSV、启动消费/显示/按键线程，进入稳态采集循环，最后收尾。
        /// </summary>
        public void Run()
        {
            _stats.StartTimeUtc = DateTime.UtcNow;

            Prime();                 // 致命错误在此抛出（Program 映射退出码）
            _csv.Open();             // 连通已确认，写头部
            _display.SetCsvPath(_csv.CurrentPath);

            _csvQueue = new BlockingCollection<SampleSnapshot>(CsvQueueCapacity);
            _csvThread = new Thread(CsvConsumeLoop) { IsBackground = true, Name = "CsvWriter" };
            _csvThread.Start();

            _display.Start();

            if (_settings.HandleQuitKey && !IsInputRedirected())
            {
                _inputThread = new Thread(InputLoop) { IsBackground = true, Name = "KeyInput" };
                _inputThread.Start();
            }

            _log.Info("采样启动：{0} 项 / {1} 组，目标 {2} Hz，分隔符='{3}'，MODE 定义/读取='{4}'/'{5}'，CSV={6}",
                _config.Count, _groups.Count, EffectiveRateHz.ToString("0.##", CultureInfo.InvariantCulture),
                _paraSep, _settings.AllowModeJa ? _modeDefine : "(不用JA)", _modeRead, _csv.CurrentPath);

            try
            {
                AcquisitionLoop();
            }
            finally
            {
                Shutdown();
            }
        }

        // ---------- 启动协商：确定 para 分隔符与 MODE 拼写，并确认连通（R-2-1 / R-2-4） ----------

        private void Prime()
        {
            List<SampleItem> group0 = _groups[0];
            List<string> separators = DistinctList(new[] { _settings.ParaSep, ";", "," });
            string lastStatus = null;

            foreach (string sep in separators)
            {
                foreach (string[] pair in ModePairs)
                {
                    if (_stop) throw new OperationCanceledException("启动阶段被中断");

                    // 保守模式（不允许 JA）：用读取拼写直接试读 ECU 已存定义
                    string defineMode = _settings.AllowModeJa ? pair[0] : pair[1];
                    string para = BuildPara(defineMode, group0, sep);
                    _log.Api("Prime 尝试：para=\"{0}\"", para);

                    JobResult res;
                    try
                    {
                        res = _client.RunJob(_settings.Ecu, JobName, para);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (EdiabasTimeoutException tex)
                    {
                        throw new SamplerStartException(SamplerStartException.FailureKind.NoConnection,
                            "首次测量块读取超时，ECU 无响应", tex.Message);
                    }
                    catch (EdiabasJobException jex)
                    {
                        if (jex.Category == EdiabasErrorCategory.Interface)
                        {
                            throw new SamplerStartException(SamplerStartException.FailureKind.NoConnection,
                                string.Format("无法与 DME 建立通信（接口错误 {0}: {1}）", jex.ErrorCode, jex.ErrorText),
                                jex.ErrorText);
                        }
                        if (jex.Category == EdiabasErrorCategory.Bip || jex.Category == EdiabasErrorCategory.Sys)
                        {
                            // Job/SGBD 层错误（如 JOB NOT FOUND）：换拼写无意义，直接致命
                            throw new SamplerStartException(SamplerStartException.FailureKind.EcuRejected,
                                string.Format("SGBD/Job 层错误（{0}: {1}）——确认 --ecu 指定的 PRG 含 {2}",
                                    jex.ErrorCode, jex.ErrorText, JobName),
                                jex.ErrorText);
                        }
                        lastStatus = jex.ErrorText;
                        continue;
                    }
                    catch (EdiabasException eex)
                    {
                        throw new SamplerStartException(SamplerStartException.FailureKind.Environment,
                            "EDIABAS 环境错误：" + eex.Message, eex.Message);
                    }

                    string status = res.FindJobStatus();
                    if (!JobResult.IsErrorStatus(status))
                    {
                        _paraSep = sep;
                        if (_settings.AllowModeJa)
                        {
                            _modeDefine = pair[0];
                            _modeRead = pair[1];
                            _groupDefined[0] = _singleGroup; // 单组：本组已定义，后续 NEIN 复用
                        }
                        else
                        {
                            _modeDefine = null;
                            _modeRead = defineMode;
                        }
                        _log.Info("Prime 成功：分隔符='{0}'，MODE='{1}'，JOB_STATUS={2}",
                            sep, defineMode, string.IsNullOrEmpty(status) ? "OKAY" : status);
                        return;
                    }

                    lastStatus = status;
                    _log.Api("Prime 被拒（sep='{0}', MODE='{1}'）：JOB_STATUS={2}", sep, defineMode, status);
                }
            }

            throw new SamplerStartException(
                SamplerStartException.FailureKind.BadArguments,
                "测量块首次定义/读取被拒（已尝试分隔符 ';' 与 ',' 及多种 MODE 拼写）",
                string.IsNullOrEmpty(lastStatus) ? "(无 JOB_STATUS)" : ("最后 JOB_STATUS=" + lastStatus));
        }

        // ---------- 稳态采集循环 ----------

        private void AcquisitionLoop()
        {
            DateTime nextTick = DateTime.UtcNow;
            while (!_stop)
            {
                DateTime nowUtc = DateTime.UtcNow;
                if (nextTick > nowUtc)
                {
                    if (SleepInterruptible((int)(nextTick - nowUtc).TotalMilliseconds)) break;
                }
                // 落后不追赶（不堆积、不突发，§5）：以下一周期为基准重排
                nextTick = DateTime.UtcNow + TimeSpan.FromMilliseconds(_intervalMs);

                DateTime ts = DateTime.Now; // 采样发起时刻（本地，含毫秒，§5）
                SampleSnapshot snap;
                try
                {
                    snap = AcquireOnce(ts);
                }
                catch (OperationCanceledException)
                {
                    break; // apiBreak（Ctrl+C/q）
                }

                if (snap.IsGap)
                {
                    _stats.IncGap();
                    _consecutiveFailures++;
                    _display.SetStatus(BuildGapStatus(snap));
                    int backoff = ComputeBackoff(_consecutiveFailures);
                    nextTick = DateTime.UtcNow + TimeSpan.FromMilliseconds(backoff);
                }
                else
                {
                    if (_consecutiveFailures > 0)
                    {
                        _log.Info("通信恢复（此前连续 {0} 次缺口）", _consecutiveFailures);
                        ClearThrottle("comm");
                    }
                    _consecutiveFailures = 0;
                    _stats.IncOk();
                    _display.SetStatus(null);
                }

                _display.Publish(snap);
                EnqueueCsv(snap);
            }
        }

        /// <summary>读取一个周期的全部组；通信类失败 → 整帧缺口（不致命）。</summary>
        private SampleSnapshot AcquireOnce(DateTime ts)
        {
            var snap = new SampleSnapshot(ts, _config.Count);
            bool anyOk = false;
            bool commFail = false;
            string note = null;

            for (int gi = 0; gi < _groups.Count; gi++)
            {
                if (_stop) break;
                List<SampleItem> group = _groups[gi];
                bool useJa = ChooseUseJa(gi);
                string mode = useJa ? (_modeDefine ?? _modeRead) : _modeRead;
                string para = BuildPara(mode, group, _paraSep);

                try
                {
                    JobResult res = _client.RunJob(_settings.Ecu, JobName, para);
                    string status = res.FindJobStatus();

                    if (JobResult.IsErrorStatus(status))
                    {
                        // R-2-4：MODE=JA 被拒（如发动机运转中）→ 回退 NEIN 试一次，并禁用该组后续 JA
                        if (useJa && _settings.AllowModeJa)
                        {
                            _log.Api("组{0} MODE=JA 被拒（{1}），回退 NEIN 重试", gi, status);
                            string para2 = BuildPara(_modeRead, group, _paraSep);
                            JobResult res2 = _client.RunJob(_settings.Ecu, JobName, para2);
                            string status2 = res2.FindJobStatus();
                            if (!JobResult.IsErrorStatus(status2))
                            {
                                ExtractValues(res2, group, snap);
                                anyOk = true;
                                _groupJaDisabled[gi] = true;
                                _groupDefined[gi] = false;
                                LogThrottled("ja" + gi, string.Format(
                                    "组{0} 拒绝 MODE=JA，已降级为 NEIN 读取（值可能对应 ECU 已存定义）", gi));
                                continue;
                            }
                            status = status2;
                        }
                        else if (_singleGroup)
                        {
                            // 用 NEIN 读失败 → 定义可能已丢失（如点火循环后），下个周期强制 JA 重定义
                            _groupDefined[gi] = false;
                        }
                        note = status;
                        LogThrottled("grp" + gi, string.Format("组{0} JOB_STATUS={1}", gi, status));
                        continue;
                    }

                    ExtractValues(res, group, snap);
                    anyOk = true;
                    if (useJa) _groupDefined[gi] = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (EdiabasTimeoutException tex)
                {
                    commFail = true; note = "超时无响应";
                    LogThrottled("comm", "通信超时：" + tex.Message);
                    break;
                }
                catch (EdiabasJobException jex)
                {
                    if (jex.Category == EdiabasErrorCategory.Interface)
                    {
                        commFail = true; note = "接口/通信中断";
                        LogThrottled("comm", string.Format("接口错误 {0}：{1}", jex.ErrorCode, jex.ErrorText));
                    }
                    else
                    {
                        note = jex.ErrorText;
                        LogThrottled("job", string.Format("Job 错误 {0}：{1}", jex.ErrorCode, jex.ErrorText));
                    }
                    break;
                }
                catch (EdiabasException eex)
                {
                    commFail = true; note = "EDIABAS 错误";
                    LogThrottled("comm", "EDIABAS：" + eex.Message);
                    break;
                }
                catch (Exception ex)
                {
                    // 未预期的内部错误也不得拖垮无人值守长时间采集（FR-2-8 / NFR-2-1）
                    commFail = true; note = "内部错误";
                    LogThrottled("internal", "采集内部错误：" + ex.Message);
                    break;
                }
            }

            snap.IsGap = commFail || !anyOk;
            snap.Note = note;
            return snap;
        }

        private bool ChooseUseJa(int gi)
        {
            if (!_settings.AllowModeJa) return false;
            if (_groupJaDisabled[gi]) return false;
            if (!_singleGroup) return true;   // 多组：$2C 单块语义 → 每组每周期重新定义
            return !_groupDefined[gi];        // 单组：定义一次后 NEIN 复用（更快）
        }

        private void ExtractValues(JobResult res, List<SampleItem> group, SampleSnapshot snap)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (ResultField f in res.AllFields())
            {
                if (!string.IsNullOrEmpty(f.Name) && !map.ContainsKey(f.Name)) map[f.Name] = f.Value;
            }
            foreach (SampleItem item in group)
            {
                string v;
                if (!string.IsNullOrEmpty(item.ResultName) && map.TryGetValue(item.ResultName, out v))
                {
                    snap.Values[item.Index] = SampleSnapshot.Normalize(v);
                }
            }
        }

        private static string BuildPara(string mode, List<SampleItem> group, string sep)
        {
            var sb = new StringBuilder(mode ?? string.Empty);
            foreach (SampleItem item in group)
            {
                sb.Append(sep).Append(item.Arg);
            }
            return sb.ToString();
        }

        // ---------- CSV 消费线程 ----------

        private void CsvConsumeLoop()
        {
            try
            {
                foreach (SampleSnapshot snap in _csvQueue.GetConsumingEnumerable())
                {
                    _csv.Write(snap);
                }
            }
            catch (Exception ex)
            {
                _csvFaulted = true;
                _log.Error("CSV 写入线程异常（停止落盘，采集与显示继续）：{0}", ex);
            }
            finally
            {
                _csv.Close();
            }
        }

        private void EnqueueCsv(SampleSnapshot snap)
        {
            if (_csvFaulted || _csvQueue == null) return;
            try
            {
                if (!_csvQueue.TryAdd(snap, CsvAddTimeoutMs))
                {
                    if (_csvThread == null || !_csvThread.IsAlive)
                    {
                        _csvFaulted = true;
                        _log.Error("CSV 写入线程已停止（磁盘满/权限？），后续样本仅实时显示不落盘");
                        _display.SetStatus("[!] CSV 写入失败，仅实时显示");
                    }
                    else
                    {
                        // 极端 IO 卡顿：丢弃 1 帧以保护采集不挂死（NFR-2-1 不挂死优先）
                        _log.Warn("CSV 队列积压超时（>{0}s），丢弃 1 帧以保护采集线程", CsvAddTimeoutMs / 1000);
                    }
                }
            }
            catch (InvalidOperationException) { /* 队列已 CompleteAdding */ }
            catch (Exception ex) { _log.Warn("入队 CSV 异常：{0}", ex.Message); }
        }

        // ---------- 按键输入线程（q 停止） ----------

        private void InputLoop()
        {
            while (!_stop)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo k = Console.ReadKey(true);
                        if (k.Key == ConsoleKey.Q || char.ToLowerInvariant(k.KeyChar) == 'q')
                        {
                            _log.Info("检测到 q 键，请求停止采样");
                            RequestStop();
                            break;
                        }
                    }
                }
                catch { break; } // 无控制台输入
                if (SleepInterruptible(80)) break;
            }
        }

        private static bool IsInputRedirected()
        {
            try { return Console.IsInputRedirected; }
            catch { return true; }
        }

        // ---------- 收尾 ----------

        private void Shutdown()
        {
            _stop = true;
            if (_csvQueue != null)
            {
                try { _csvQueue.CompleteAdding(); } catch { }
            }
            if (_csvThread != null)
            {
                try { _csvThread.Join(15000); } catch { }  // 排空剩余样本并关闭 CSV
                _csvThread = null;
            }
            _display.Stop();
            if (_inputThread != null)
            {
                try { _inputThread.Join(1000); } catch { }
                _inputThread = null;
            }
        }

        public void Dispose()
        {
            Shutdown();
        }

        // ---------- 工具 ----------

        /// <summary>可中断休眠：返回 true 表示因停止标志提前退出。</summary>
        private bool SleepInterruptible(int ms)
        {
            if (ms <= 0) return _stop;
            const int Slice = 100;
            int slept = 0;
            while (slept < ms)
            {
                if (_stop) return true;
                int step = Math.Min(Slice, ms - slept);
                Thread.Sleep(step);
                slept += step;
            }
            return _stop;
        }

        private int ComputeBackoff(int failures)
        {
            int f = Math.Min(Math.Max(failures, 0), 5);
            long ms = (long)_intervalMs * (1L << f); // interval × 2^failures
            if (ms > _settings.MaxBackoffMs) ms = _settings.MaxBackoffMs;
            if (ms < _intervalMs) ms = _intervalMs;
            return (int)ms;
        }

        private string BuildGapStatus(SampleSnapshot snap)
        {
            string note = string.IsNullOrEmpty(snap.Note) ? "无数据" : snap.Note;
            return string.Format("数据缺口：{0}（连续 {1} 次，退避重试中…）", note, _consecutiveFailures);
        }

        private void LogThrottled(string key, string message)
        {
            int n;
            _throttle.TryGetValue(key, out n);
            _throttle[key] = n + 1;
            if (n == 0) _log.Warn("{0}", message);
            else if (n % 100 == 0) _log.Warn("{0}（累计 {1} 次）", message, n + 1);
        }

        private void ClearThrottle(string key)
        {
            int n;
            if (_throttle.TryGetValue(key, out n) && n > 1)
            {
                _log.Info("'{0}' 累计 {1} 次后已恢复", key, n);
            }
            _throttle.Remove(key);
        }

        private static List<string> DistinctList(string[] values)
        {
            var list = new List<string>();
            foreach (string v in values)
            {
                if (string.IsNullOrEmpty(v)) continue;
                bool dup = false;
                foreach (string e in list) if (e == v) { dup = true; break; }
                if (!dup) list.Add(v);
            }
            return list;
        }
    }
}
