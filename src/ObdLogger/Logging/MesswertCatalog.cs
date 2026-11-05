using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace ObdLogger.Logging
{
    /// <summary>
    /// MESSWERTETAB 中的一条测量值定义（来源：doc/材料/MEVD174K.b1v 全量导出，FR-2-9）。
    /// 关键约定：向 STATUS_MESSWERTBLOCK_LESEN 传入的动态参数名为 <see cref="Arg"/>，
    /// Job 返回的结果字段名为 <see cref="ResultName"/>（b1v 中 tabget "RESULTNAME" → ergs），
    /// 二者通常不同（如 INMOT → STAT_MOTORDREHZAHL_WERT），故必须经本目录解析。
    /// </summary>
    internal sealed class MesswertDef
    {
        public string Arg;         // 传给 Job 的动态参数名（MESSWERTETAB.ARG）
        public string Id;          // 数据标识（0x....）
        public string ResultName;  // Job 返回的结果字段名（MESSWERTETAB.RESULTNAME，STAT_*）
        public string Info;        // 德语描述（MESSWERTETAB.INFO）
        public string Unit;        // 单位（MESSWERTETAB.EINHEIT，可直接写入 CSV 单位行）
        public string Label;       // A2L 名（MESSWERTETAB.LABEL）
        public string DataType;    // 数据类型（MESSWERTETAB.DATENTYP）
    }

    /// <summary>
    /// 测量值目录：从嵌入资源 messwertetab.csv 加载 ARG →（RESULTNAME / EINHEIT / INFO）映射，
    /// 作为运行期解析采样项“结果字段名 / 单位 / 描述”的权威数据源。
    /// CSV 由 scripts/Export-Messwertetab.ps1 从 MEVD174K.b1v 导出（FR-2-9），随程序嵌入，
    /// 保证单 exe 零安装（NFR-2-3）且不依赖运行期外部文件。
    /// 线程安全（构造后只读）。
    /// </summary>
    internal sealed class MesswertCatalog
    {
        private const string ResourceSuffix = "messwertetab.csv";

        private readonly Dictionary<string, MesswertDef> _byArg =
            new Dictionary<string, MesswertDef>(StringComparer.OrdinalIgnoreCase);

        /// <summary>已加载的条目数（0 表示资源缺失/解析失败，调用方应降级并告警）。</summary>
        public int Count { get { return _byArg.Count; } }

        /// <summary>从嵌入资源加载目录；失败时返回空目录（不抛出，交由调用方降级）。</summary>
        public static MesswertCatalog LoadEmbedded(AppLog log)
        {
            var catalog = new MesswertCatalog();
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                string resName = null;
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (name.EndsWith(ResourceSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        resName = name;
                        break;
                    }
                }
                if (resName == null)
                {
                    log.Warn("未找到嵌入资源 {0}，测量值目录为空（采样项结果名将退化为按 ARG 猜测）", ResourceSuffix);
                    return catalog;
                }

                using (Stream stream = asm.GetManifestResourceStream(resName))
                using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                {
                    catalog.Parse(reader, log);
                }
                log.Info("测量值目录已加载：{0} 条（资源 {1}）", catalog.Count, resName);
            }
            catch (Exception ex)
            {
                log.Warn("加载测量值目录失败（降级为按 ARG 猜测结果名）：{0}", ex.Message);
            }
            return catalog;
        }

        private void Parse(StreamReader reader, AppLog log)
        {
            string line;
            bool isHeader = true;
            var col = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int badRows = 0;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                List<string> fields = ParseCsvLine(line);
                if (isHeader)
                {
                    for (int i = 0; i < fields.Count; i++) col[fields[i]] = i;
                    isHeader = false;
                    // 必需列校验
                    if (!col.ContainsKey("ARG") || !col.ContainsKey("RESULTNAME"))
                    {
                        log.Warn("messwertetab.csv 表头缺少 ARG/RESULTNAME 列，目录不可用");
                        return;
                    }
                    continue;
                }

                string arg = Get(fields, col, "ARG");
                if (string.IsNullOrEmpty(arg)) { badRows++; continue; }
                var def = new MesswertDef
                {
                    Arg = arg,
                    Id = Get(fields, col, "ID"),
                    ResultName = Get(fields, col, "RESULTNAME"),
                    Info = Get(fields, col, "INFO"),
                    Unit = NormalizeUnit(Get(fields, col, "EINHEIT")),
                    Label = Get(fields, col, "LABEL"),
                    DataType = Get(fields, col, "DATENTYP"),
                };
                if (string.IsNullOrEmpty(def.ResultName)) def.ResultName = arg;
                // ARG 唯一；若重复保留首条
                if (!_byArg.ContainsKey(arg)) _byArg[arg] = def;
            }
            if (badRows > 0) log.Warn("messwertetab.csv 有 {0} 行缺 ARG 被跳过", badRows);
        }

        /// <summary>单位归一：b1v 中 "-" 表示无单位；统一为空串以便 CSV 单位行留 "[-]"。</summary>
        private static string NormalizeUnit(string unit)
        {
            if (string.IsNullOrWhiteSpace(unit)) return string.Empty;
            return unit.Trim();
        }

        private static string Get(List<string> fields, Dictionary<string, int> col, string name)
        {
            int idx;
            if (!col.TryGetValue(name, out idx) || idx >= fields.Count) return string.Empty;
            return fields[idx] ?? string.Empty;
        }

        public bool TryGet(string arg, out MesswertDef def)
        {
            if (string.IsNullOrEmpty(arg)) { def = null; return false; }
            return _byArg.TryGetValue(arg.Trim(), out def);
        }

        /// <summary>
        /// 解析一行 CSV（字段可含引号；引号内 "" 表示一个字面引号；字段内逗号安全）。
        /// 与 Export-Messwertetab.ps1 的“始终加引号”写法对应。
        /// </summary>
        internal static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            if (line == null) return result;
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else { inQuotes = false; }
                    }
                    else sb.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') { result.Add(sb.ToString()); sb.Length = 0; }
                    else sb.Append(c);
                }
            }
            result.Add(sb.ToString());
            return result;
        }
    }
}
