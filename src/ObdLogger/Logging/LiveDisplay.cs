using System;
using System.Text;
using System.Threading;

namespace ObdLogger.Logging
{
    /// <summary>
    /// 控制台就地刷新显示（FR-2-3）：固定行数块内用 SetCursorPosition 覆盖更新，<b>不滚动刷日志</b>。
    /// 采集不被显示阻塞：采集线程只 <see cref="Publish"/> 一帧引用（加锁极短），显示线程按自身节拍渲染。
    /// 无控制台/输出被重定向（无人值守/后台运行）时自动禁用，仅不影响 CSV 与文件日志。
    /// </summary>
    internal sealed class LiveDisplay
    {
        private const int ReservedLines = 6;      // 标题 + 状态 + 分隔 + 溢出提示 + 页脚 + 余量（确保不滚动）
        private const int RefreshMs = 120;        // ≈8Hz 刷新，肉眼流畅且 CPU 友好

        private readonly SampleConfig _config;
        private readonly SamplerStats _stats;
        private readonly AppLog _log;
        private readonly bool _enabled;

        private readonly object _sync = new object();
        private SampleSnapshot _latest;
        private string _status;
        private string _csvPath;

        private Thread _thread;
        private volatile bool _stop;
        private volatile bool _started;
        private int _top;
        private int _lastLineCount;

        public LiveDisplay(SampleConfig config, SamplerStats stats, AppLog log)
        {
            _config = config;
            _stats = stats;
            _log = log;
            _enabled = DetectConsole(log);
        }

        public bool Enabled { get { return _enabled; } }

        public void SetCsvPath(string path) { lock (_sync) { _csvPath = path; } }

        /// <summary>设置状态行文本（如缺口/重试提示）；null 清除。</summary>
        public void SetStatus(string message) { lock (_sync) { _status = message; } }

        /// <summary>发布最新一帧供显示（采集线程调用；仅替换引用，不阻塞）。</summary>
        public void Publish(SampleSnapshot snapshot)
        {
            if (!_enabled) return;
            lock (_sync) { _latest = snapshot; }
        }

        private static bool DetectConsole(AppLog log)
        {
            try
            {
                if (Console.IsOutputRedirected)
                {
                    log.Info("标准输出被重定向，禁用控制台就地显示（CSV/文件日志不受影响）");
                    return false;
                }
                int h = Console.WindowHeight;
                int w = Console.WindowWidth;
                if (h <= 0 || w <= 0) return false;
                return true;
            }
            catch (Exception ex)
            {
                log.Info("无可用控制台，禁用就地显示：{0}", ex.Message);
                return false;
            }
        }

        public void Start()
        {
            if (!_enabled) return;
            try
            {
                Console.Clear();
                _top = 0;
                _lastLineCount = 0;
                Console.CursorVisible = false;
            }
            catch (Exception ex)
            {
                _log.Warn("初始化控制台显示失败（转为静默）：{0}", ex.Message);
            }
            _stop = false;
            _started = true;
            _thread = new Thread(RenderLoop) { IsBackground = true, Name = "LiveDisplay" };
            _thread.Start();
        }

        private void RenderLoop()
        {
            while (!_stop)
            {
                try
                {
                    Render();
                }
                catch (Exception)
                {
                    // 控制台异常（尺寸变化/关闭）不应拖垮采集：静默重试
                }
                Thread.Sleep(RefreshMs);
            }
            // 收尾：再渲染一帧最终状态
            try { Render(); } catch { }
        }

        private void Render()
        {
            SampleSnapshot snap;
            string status, csvPath;
            lock (_sync)
            {
                snap = _latest;
                status = _status;
                csvPath = _csvPath;
            }

            int width = SafeWindowWidth();
            int height = SafeWindowHeight();
            int maxItems = Math.Max(1, height - ReservedLines);

            var lines = new System.Collections.Generic.List<string>();
            lines.Add(Trunc("══ OBD Logger 二期 · 实时采样 ══════════════════════", width));
            lines.Add(Trunc(BuildStatusLine(status), width));
            lines.Add(Trunc(new string('-', Math.Max(8, width - 1)), width));

            int shown = 0;
            if (snap != null)
            {
                foreach (SampleItem item in _config.Items)
                {
                    if (shown >= maxItems) break;
                    string val = item.Index < snap.Values.Length ? snap.Values[item.Index] : string.Empty;
                    if (string.IsNullOrEmpty(val)) val = snap.IsGap ? "--缺口--" : "----";
                    lines.Add(Trunc(string.Format("  {0,-20} {1,12}  {2}",
                        item.DisplayName, val, item.Unit), width));
                    shown++;
                }
            }
            else
            {
                lines.Add(Trunc("  等待首帧采样…", width));
                shown = 1;
            }

            int overflow = _config.Count - shown;
            string hint = overflow > 0
                ? string.Format("  另有 {0} 项见 CSV", overflow)
                : string.Empty;
            if (hint.Length > 0) lines.Add(Trunc(hint, width));

            string foot = string.Format("  CSV: {0}   |   按 q 或 Ctrl+C 停止",
                string.IsNullOrEmpty(csvPath) ? "(准备中)" : csvPath);
            lines.Add(Trunc(foot, width));

            WriteBlock(lines);
        }

        private string BuildStatusLine(string statusOverride)
        {
            if (!string.IsNullOrEmpty(statusOverride))
            {
                return "  [!] " + statusOverride;
            }
            double elapsed = _stats.ElapsedSeconds;
            string elapsedText = FormatDuration(elapsed);
            return string.Format("  速率 {0,0.00} Hz   样本 {1}   缺口 {2}   已运行 {3}",
                _stats.RateHz, _stats.Ok, _stats.Gap, elapsedText);
        }

        private static string FormatDuration(double totalSeconds)
        {
            if (totalSeconds < 0) totalSeconds = 0;
            var ts = TimeSpan.FromSeconds(totalSeconds);
            return string.Format("{0:00}:{1:00}:{2:00}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
        }

        private void WriteBlock(System.Collections.Generic.List<string> lines)
        {
            int width = SafeWindowWidth();
            for (int i = 0; i < lines.Count; i++)
            {
                Console.SetCursorPosition(0, _top + i);
                Console.Write(lines[i]);
                int pad = (width - 1) - lines[i].Length;
                if (pad > 0) Console.Write(new string(' ', pad));
            }
            // 清除本次比上次少写的残留行
            for (int i = lines.Count; i < _lastLineCount; i++)
            {
                Console.SetCursorPosition(0, _top + i);
                Console.Write(new string(' ', Math.Max(0, width - 1)));
            }
            _lastLineCount = lines.Count;
            Console.SetCursorPosition(0, _top + lines.Count);
        }

        private static int SafeWindowWidth()
        {
            try { int w = Console.WindowWidth; return w > 20 ? w : 80; }
            catch { return 80; }
        }

        private static int SafeWindowHeight()
        {
            try { int h = Console.WindowHeight; return h > 6 ? h : 24; }
            catch { return 24; }
        }

        private static string Trunc(string s, int width)
        {
            if (s == null) return string.Empty;
            int max = Math.Max(8, width - 1);
            return s.Length <= max ? s : s.Substring(0, max);
        }

        public void Stop()
        {
            _stop = true;
            if (_thread != null)
            {
                try { _thread.Join(1000); } catch { }
                _thread = null;
            }
            if (_enabled && _started)
            {
                try
                {
                    Console.CursorVisible = true;
                    Console.SetCursorPosition(0, _top + Math.Max(_lastLineCount, 0));
                    Console.WriteLine();
                }
                catch { }
                _started = false;
            }
        }
    }
}
