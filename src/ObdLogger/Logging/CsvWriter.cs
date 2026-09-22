using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ObdLogger.Logging
{
    /// <summary>CSV 头部元信息（写入文件头两行 # 注释，§6）。</summary>
    internal sealed class CsvHeaderInfo
    {
        public string Vehicle = "E84 N20";
        public string Sgbd = "MEVD174K";
        public string Vin = string.Empty;   // 一期两阶段读取；失败留空
        public DateTime StartTime;
        public double RateHz;
        public string ConfigSource = "内置默认";
    }

    /// <summary>
    /// CSV 落盘（FR-2-5 / NFR-2-4）：
    /// - 头两行 # 注释（车辆/SGBD/VIN/开始 + 配置元信息），第三行显示名表头，第四行单位行；
    /// - 每行一次采样：Time + 各采样项一列；缺失写空字段；逗号分隔；小数点用点（InvariantCulture）；UTF-8 无 BOM；
    /// - 逐行 flush 防丢；单文件 ≥ 1 GB 或跨天自动滚动新文件（滚动不丢样本）。
    /// 仅由 CSV 消费线程访问（Open 在主线程先行完成，经线程启动/Join 形成内存屏障），故内部无需加锁。
    /// </summary>
    internal sealed class CsvWriter : IDisposable
    {
        private const long MaxFileBytes = 1024L * 1024L * 1024L; // 1 GB 滚动阈值（NFR-2-4）

        private readonly SampleConfig _config;
        private readonly CsvHeaderInfo _meta;
        private readonly AppLog _log;
        private readonly string _baseDir;
        private readonly string _explicitFileName; // --out 指定了具体文件名时用于滚动派生；否则为 null

        private StreamWriter _writer;
        private string _currentPath;
        private DateTime _currentFileDate;
        private bool _disposed;

        public string CurrentPath { get { return _currentPath; } }

        /// <summary>已写入的数据行数（跨滚动累计，用于统计）。</summary>
        public long RowsWritten { get; private set; }

        /// <summary>滚动次数（用于统计）。</summary>
        public int RollCount { get; private set; }

        public CsvWriter(string outputPath, SampleConfig config, CsvHeaderInfo meta, AppLog log)
        {
            _config = config;
            _meta = meta;
            _log = log;

            string dir;
            string explicitName;
            ResolveOutput(outputPath, meta.StartTime, out dir, out explicitName);
            _baseDir = dir;
            _explicitFileName = explicitName;
        }

        /// <summary>解析输出目标：null→ exe 目录\log\obdlog_&lt;ts&gt;.csv；目录→其内生成；文件→原名（滚动派生）。</summary>
        private static void ResolveOutput(string outputPath, DateTime start, out string dir, out string explicitName)
        {
            string defaultName = "obdlog_" + start.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv";
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
                explicitName = null;
                return;
            }
            // 已存在的目录，或以分隔符结尾 → 视为目录
            bool looksLikeDir = outputPath.EndsWith("\\", StringComparison.Ordinal) ||
                                outputPath.EndsWith("/", StringComparison.Ordinal);
            if (!looksLikeDir)
            {
                try { if (Directory.Exists(outputPath)) looksLikeDir = true; }
                catch { /* 忽略，按文件处理 */ }
            }
            if (looksLikeDir)
            {
                dir = outputPath;
                explicitName = null;
            }
            else
            {
                string full = Path.GetFullPath(outputPath);
                dir = Path.GetDirectoryName(full) ?? AppDomain.CurrentDomain.BaseDirectory;
                explicitName = Path.GetFileName(full);
            }
        }

        /// <summary>创建首个 CSV 文件并写入头部（失败抛出，由上层给出明确提示并退出）。</summary>
        public void Open()
        {
            string path = NextPath(true);
            OpenFile(path);
        }

        private string NextPath(bool first)
        {
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string name;
            if (first && !string.IsNullOrEmpty(_explicitFileName))
            {
                name = _explicitFileName;
            }
            else if (!string.IsNullOrEmpty(_explicitFileName))
            {
                string stem = Path.GetFileNameWithoutExtension(_explicitFileName);
                string ext = Path.GetExtension(_explicitFileName);
                if (string.IsNullOrEmpty(ext)) ext = ".csv";
                name = stem + "_" + ts + ext;
            }
            else
            {
                name = "obdlog_" + ts + ".csv";
            }
            return Path.Combine(_baseDir, name);
        }

        private void OpenFile(string path)
        {
            Directory.CreateDirectory(_baseDir);
            var encoding = new UTF8Encoding(false); // 无 BOM（§6，Excel 兼容）
            _writer = new StreamWriter(path, false, encoding) { AutoFlush = false };
            _currentPath = path;
            _currentFileDate = DateTime.Now.Date;

            WriteHeaders();
            _writer.Flush();
            _log.Info("CSV 已创建: {0}", path);
        }

        private void WriteHeaders()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            // 注释行 1：车辆/SGBD/VIN/开始
            _writer.WriteLine("# OBD Logger 二期  车辆: {0}  SGBD: {1}  VIN: {2}  开始: {3}",
                _meta.Vehicle, _meta.Sgbd,
                string.IsNullOrEmpty(_meta.Vin) ? "(未读取到)" : _meta.Vin,
                _meta.StartTime.ToString("yyyy-MM-dd HH:mm:ss", inv));
            // 注释行 2：采样配置元信息
            _writer.WriteLine("# 采样项: {0}  目标速率: {1}Hz  配置: {2}  时间戳: 本地时间(毫秒)  分隔符: ,  小数点: .  编码: UTF-8",
                _config.Count, _meta.RateHz.ToString("0.##", inv), _meta.ConfigSource);

            // 表头行：Time + 显示名
            var header = new StringBuilder();
            header.Append("Time");
            foreach (SampleItem item in _config.Items)
            {
                header.Append(',').Append(Quote(item.DisplayName));
            }
            _writer.WriteLine(header.ToString());

            // 单位行：[ms] + [单位]
            var units = new StringBuilder();
            units.Append("[ms]");
            foreach (SampleItem item in _config.Items)
            {
                string u = string.IsNullOrEmpty(item.Unit) ? "-" : item.Unit;
                units.Append(',').Append(Quote("[" + u + "]"));
            }
            _writer.WriteLine(units.ToString());
        }

        /// <summary>写入一帧采样（必要时先滚动），逐行 flush 防丢（FR-2-5）。</summary>
        public void Write(SampleSnapshot s)
        {
            if (_writer == null) throw new InvalidOperationException("CsvWriter 未打开");

            // 跨天滚动（NFR-2-4）
            if (s.Timestamp.Date != _currentFileDate)
            {
                Roll();
            }

            string line = BuildLine(s);

            // 超大小滚动：若写入本行将超过阈值且当前文件非空，则先滚动
            if (_writer.BaseStream.Position > 0 &&
                _writer.BaseStream.Position + line.Length > MaxFileBytes)
            {
                Roll();
            }

            _writer.WriteLine(line);
            _writer.Flush(); // 逐行 flush 防丢
            RowsWritten++;
        }

        private string BuildLine(SampleSnapshot s)
        {
            var sb = new StringBuilder(64 + s.Values.Length * 10);
            sb.Append(s.FormatTimestamp());
            for (int i = 0; i < _config.Items.Count; i++)
            {
                sb.Append(',');
                string v = i < s.Values.Length ? s.Values[i] : string.Empty;
                sb.Append(Quote(v));
            }
            return sb.ToString();
        }

        private void Roll()
        {
            string old = _currentPath;
            try
            {
                _writer.Flush();
                _writer.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warn("滚动时关闭旧 CSV 异常（忽略）：{0}", ex.Message);
            }
            string path = NextPath(false);
            // 避免同名覆盖（极端：同一秒内多次滚动）
            int guard = 1;
            while (File.Exists(path) && guard < 1000)
            {
                path = NextPath(false) ;
                path = Path.Combine(_baseDir,
                    Path.GetFileNameWithoutExtension(path) + "_" + guard + Path.GetExtension(path));
                guard++;
            }
            OpenFile(path);
            RollCount++;
            _log.Info("CSV 滚动：{0} → {1}（累计 {2} 行）", old, path, RowsWritten);
        }

        /// <summary>CSV 字段转义：仅当含逗号/引号/换行时加引号（数字与常规列名保持裸值，Excel 友好）。</summary>
        private static string Quote(string field)
        {
            if (string.IsNullOrEmpty(field)) return string.Empty;
            bool need = field.IndexOf(',') >= 0 || field.IndexOf('"') >= 0 ||
                        field.IndexOf('\n') >= 0 || field.IndexOf('\r') >= 0;
            if (!need) return field;
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        public void Close()
        {
            if (_writer != null)
            {
                try
                {
                    _writer.Flush();
                    _writer.Dispose();
                    _log.Info("CSV 已关闭: {0}（共 {1} 行，滚动 {2} 次）", _currentPath, RowsWritten, RollCount);
                }
                catch (Exception ex)
                {
                    _log.Warn("关闭 CSV 异常（忽略）：{0}", ex.Message);
                }
                finally
                {
                    _writer = null;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Close();
        }
    }
}
