using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ObdLogger.Logging
{
    /// <summary>
    /// 应用层日志（FR-8）：记录每次 API 调用、Job 名称、关键参数与结果。
    /// 始终写文件（exe 目录下 logs\，失败则退到 %LOCALAPPDATA%），
    /// 详细级别（API 调用明细）在 --verbose 时同时输出到控制台。
    /// </summary>
    internal sealed class AppLog : IDisposable
    {
        private readonly object _sync = new object();
        private readonly StreamWriter _writer;
        private readonly bool _verbose;
        private readonly string _filePath;
        private volatile bool _consoleEnabled = true;

        /// <summary>
        /// 控制台输出总开关（默认 true，保持一期行为不变）。
        /// 二期 log 模式在就地刷新显示期间置 false，使 Warn/Error 仅落文件，
        /// 避免与 <see cref="LiveDisplay"/> 抢占控制台而破坏“不滚动”布局（FR-2-3）。
        /// </summary>
        public bool ConsoleEnabled
        {
            get { return _consoleEnabled; }
            set { _consoleEnabled = value; }
        }

        private AppLog(StreamWriter writer, bool verbose, string filePath)
        {
            _writer = writer;
            _verbose = verbose;
            _filePath = filePath;
        }

        public string FilePath { get { return _filePath; } }

        public static AppLog Create(bool verbose)
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string fileName = "ObdLogger_" + stamp + ".log";
            string path = TryCreate(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"), fileName);
            if (path == null)
            {
                string fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ObdLogger", "logs");
                path = TryCreate(fallback, fileName);
            }
            if (path == null)
            {
                // 文件日志不可用时不阻塞主流程，仅输出控制台
                return new AppLog(null, verbose, null);
            }
            var writer = new StreamWriter(path, true, Encoding.UTF8) { AutoFlush = true };
            return new AppLog(writer, verbose, path);
        }

        private static string TryCreate(string dir, string fileName)
        {
            try
            {
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, fileName);
                using (File.Open(path, FileMode.Append, FileAccess.Write, FileShare.Read)) { }
                return path;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>API 调用明细（FR-8）：写文件；--verbose 时同时上屏。</summary>
        public void Api(string format, params object[] args)
        {
            Write("API", format, args, console: _verbose);
        }

        public void Info(string format, params object[] args)
        {
            Write("INFO", format, args, console: _verbose);
        }

        public void Warn(string format, params object[] args)
        {
            Write("WARN", format, args, console: true);
        }

        public void Error(string format, params object[] args)
        {
            Write("ERROR", format, args, console: true);
        }

        private void Write(string level, string format, object[] args, bool console)
        {
            string msg;
            try
            {
                msg = args == null || args.Length == 0
                    ? format
                    : string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
                msg = format;
            }
            string line = string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1,-5}] {2}", DateTime.Now, level, msg);
            lock (_sync)
            {
                if (_writer != null)
                {
                    try { _writer.WriteLine(line); } catch { /* 日志失败不影响主流程 */ }
                }
                if (console && _consoleEnabled)
                {
                    try { Console.Error.WriteLine(line); } catch { }
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_writer != null)
                {
                    try { _writer.Dispose(); } catch { }
                }
            }
        }
    }
}
