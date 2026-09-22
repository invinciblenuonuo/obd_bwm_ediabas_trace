using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ObdLogger.Logging
{
    /// <summary>
    /// 单个采样项：ARG（发给 Job 的动态参数）→ RESULTNAME（读回结果字段）+ 显示名 + 单位。
    /// 结果名/单位/描述优先取自 <see cref="MesswertCatalog"/>（b1v 权威），显示名为 ASCII（Excel 兼容，§6）。
    /// </summary>
    internal sealed class SampleItem
    {
        public int Index;            // 在快照值数组 / CSV 列中的位置（0 基，位于 Time 列之后）
        public string Arg;           // 传给 STATUS_MESSWERTBLOCK_LESEN 的 MESSWERT 动态参数名
        public string ResultName;    // Job 返回结果字段名（用于回读匹配；未解析时为 null）
        public string DisplayName;   // CSV 列名 / 控制台显示名（ASCII）
        public string Unit;          // 单位（写入 CSV 单位行）
        public string Description;   // 德语描述（来自 b1v INFO，仅日志/文档用）
        public bool Resolved;        // ResultName 是否已由目录解析（否则回读时按 ARG 兜底匹配）
    }

    /// <summary>
    /// 采样清单配置（FR-2-4）：内置 §3.3“已核实”默认清单，或从外部 UTF-8 文本文件加载。
    /// 外部文件每行：<c>ARG,显示名[,单位]</c>（# 或 ; 开头为注释，空行忽略）；
    /// 结果名一律由嵌入的 MESSWERTETAB 目录按 ARG 解析，故外部配置无需了解 STAT_* 命名。
    /// </summary>
    internal sealed class SampleConfig
    {
        /// <summary>单次 Job 调用可携带的最大动态参数数（b1v：bis zu 42 Argumente）。</summary>
        public const int MaxArgsPerCall = 42;

        public readonly List<SampleItem> Items = new List<SampleItem>();

        /// <summary>清单来源描述（“内置默认”或文件路径），用于日志与 CSV 注释行。</summary>
        public string Source = "内置默认";

        public int Count { get { return Items.Count; } }

        /// <summary>
        /// 按 <see cref="MaxArgsPerCall"/> 将采样项分组（≤42 项一次调用；超出则分组轮询，FR-2-2）。
        /// 单组时可首循环 MODE=JA 定义、后续 NEIN 复用；多组因 $2C 单块语义须每组每循环重新 JA 定义。
        /// </summary>
        public List<List<SampleItem>> BuildGroups()
        {
            var groups = new List<List<SampleItem>>();
            if (Items.Count == 0) return groups;
            var current = new List<SampleItem>();
            foreach (SampleItem item in Items)
            {
                current.Add(item);
                if (current.Count >= MaxArgsPerCall)
                {
                    groups.Add(current);
                    current = new List<SampleItem>();
                }
            }
            if (current.Count > 0) groups.Add(current);
            return groups;
        }

        /// <summary>
        /// 内置默认清单：二期需求文档 §3.3“已核实存在”的核心项（ARG 名引自 MESSWERTETAB，
        /// 结果名/单位由目录解析）。控制在 20 项以内，单次调用即可（≤42），兼顾控制台紧凑显示。
        /// 全 4 缸点火/爆震、燃油轨压、喷油量、Valvetronic、VANOS 位置等见 config\sample-extended.txt。
        /// </summary>
        private static readonly string[][] DefaultList =
        {
            new[] { "INMOT", "RPM" },
            new[] { "SNLLD", "IdleTargetRPM" },
            new[] { "IVKMH", "VehicleSpeed" },
            new[] { "SLAST", "EngineLoad" },
            new[] { "ILREL", "EngineLoadRel" },
            new[] { "IWDKL", "ThrottlePosition" },
            new[] { "SWDKL", "ThrottleTarget" },
            new[] { "ITKUM", "ECT" },
            new[] { "ITANS", "IAT" },
            new[] { "ILMKG", "MAF" },
            new[] { "IPLAD", "BoostActual" },
            new[] { "IPUMG", "AmbientPressure" },
            new[] { "ILAG1", "LambdaActual" },
            new[] { "SINT1", "LambdaTarget" },
            new[] { "ILIN1", "LambdaIntegrator" },
            new[] { "ILAM1", "LambdaAdaption" },
            new[] { "IZZY1", "IgnitionTiming" },
            new[] { "ISKLO", "KnockStatus" },
            new[] { "IAVEP", "VanosIntakePWM" },
            new[] { "IAVAP", "VanosExhaustPWM" },
        };

        public static SampleConfig LoadDefault(MesswertCatalog catalog, AppLog log)
        {
            var cfg = new SampleConfig { Source = "内置默认" };
            foreach (string[] pair in DefaultList)
            {
                SampleItem item = BuildItem(cfg.Items.Count, pair[0], pair[1], null, catalog, log);
                cfg.Items.Add(item);
            }
            return cfg;
        }

        /// <summary>
        /// 从外部文件加载采样清单（FR-2-4）。文件不存在/为空/全部无效时抛出 <see cref="SampleConfigException"/>。
        /// </summary>
        public static SampleConfig LoadFile(string path, MesswertCatalog catalog, AppLog log)
        {
            if (!File.Exists(path))
            {
                throw new SampleConfigException("采样配置文件不存在: " + path);
            }
            var cfg = new SampleConfig { Source = path };
            int unresolved = 0;
            foreach (string raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string line = StripComment(raw);   // 去行内注释（# / ; 起，引号内除外）+ trim
                if (line.Length == 0) continue;

                List<string> parts = MesswertCatalog.ParseCsvLine(line);
                // 也允许未加引号的简单逗号分隔（ParseCsvLine 对无引号行同样按逗号切分）
                string arg = parts.Count > 0 ? parts[0].Trim() : string.Empty;
                if (arg.Length == 0) continue;
                string display = parts.Count > 1 ? parts[1].Trim() : string.Empty;
                string unit = parts.Count > 2 ? parts[2].Trim() : string.Empty;
                if (display.Length == 0) display = arg;

                SampleItem item = BuildItem(cfg.Items.Count, arg, display, unit, catalog, log);
                if (!item.Resolved) unresolved++;
                cfg.Items.Add(item);
            }

            if (cfg.Items.Count == 0)
            {
                throw new SampleConfigException("采样配置文件无有效条目: " + path);
            }
            if (unresolved > 0)
            {
                log.Warn("配置 {0} 中有 {1} 个 ARG 未在 MESSWERTETAB 命中，回读将按 ARG 名兜底匹配（可能被 Job 拒绝）",
                    path, unresolved);
            }
            return cfg;
        }

        /// <summary>
        /// 去除行内注释：从第一个未被双引号包裹的 '#' 或 ';' 处截断，并 trim。
        /// 整行注释/空行返回空串（调用方跳过）。
        /// </summary>
        private static string StripComment(string raw)
        {
            if (raw == null) return string.Empty;
            bool inQuotes = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (c == '"') inQuotes = !inQuotes;
                else if (!inQuotes && (c == '#' || c == ';')) return raw.Substring(0, i).Trim();
            }
            return raw.Trim();
        }

        /// <summary>构造一个采样项：显示名/单位可由配置覆盖，结果名与缺省单位取自目录。</summary>
        private static SampleItem BuildItem(int index, string arg, string displayName, string unitOverride,
            MesswertCatalog catalog, AppLog log)
        {
            var item = new SampleItem
            {
                Index = index,
                Arg = arg,
                DisplayName = SanitizeColumn(displayName.Length > 0 ? displayName : arg),
            };

            MesswertDef def;
            if (catalog != null && catalog.TryGet(arg, out def))
            {
                item.ResultName = def.ResultName;
                item.Description = def.Info;
                item.Unit = string.IsNullOrWhiteSpace(unitOverride) ? def.Unit : unitOverride;
                item.Resolved = true;
            }
            else
            {
                // 目录未命中：结果名退化为 ARG 本身（部分条目 ARG==RESULTNAME，如 STAT_0xXXXX_WERT）
                item.ResultName = arg;
                item.Unit = unitOverride ?? string.Empty;
                item.Resolved = false;
                log.Warn("采样项 ARG={0} 未在 MESSWERTETAB 命中，结果名按 ARG 兜底（{1}）", arg, arg);
            }
            if (string.IsNullOrEmpty(item.Unit)) item.Unit = "-";
            return item;
        }

        /// <summary>列名净化：去除会破坏 CSV/表头的字符（逗号、引号、换行、方括号）。</summary>
        private static string SanitizeColumn(string name)
        {
            if (string.IsNullOrEmpty(name)) return "col";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (c == ',' || c == '"' || c == '\r' || c == '\n' || c == '[' || c == ']') sb.Append('_');
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }

    /// <summary>采样配置加载失败（文件缺失/无有效条目）。</summary>
    internal sealed class SampleConfigException : Exception
    {
        public SampleConfigException(string message) : base(message) { }
    }
}
