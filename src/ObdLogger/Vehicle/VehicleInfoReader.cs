using System.Collections.Generic;
using System.Text.RegularExpressions;
using ObdLogger.Ediabas;
using ObdLogger.Logging;

namespace ObdLogger.Vehicle
{
    /// <summary>
    /// 业务层：选择识别类 Job、执行、从结果中提取“车辆基础信息”。
    /// 仅使用只读识别/状态类 Job（TC-6 安全红线）。
    /// </summary>
    internal sealed class VehicleInfoReader
    {
        /// <summary>
        /// 第 1 阶段：只读识别类 Job 候选清单（按可能性排序，首个执行成功者生效）。
        /// 已实测（2026-09，本机 mevd1724.prg，SYS-0008=Job 不存在）：
        ///   "ident" 存在于 mevd1724.prg（E 系经典识别 Job）；
        ///   identification / identifikation / aif_lesen / c_c_lesen / ident_lesen /
        ///   ecu_identification / info_lesen / status_lesen / lieferanten_ident / sgbd_ident 均不存在。
        /// 实车补充（2026-09-21）：本车 DME 拒绝 mevd1724.prg 的 ident 子功能，
        ///   但接受 01mevd1724 / MEVD174K / MEVD176K / MEVD17KW / 01mevd1726 / 01MEVD1727 / 01MEVD1729
        ///   的 ident（JOB_STATUS=OKAY，返回 ID_BMW_NR=8631730 等真实数据）——用 --ecu 切换。
        /// 其余候选保留供 --ecu 更换 SGBD 时兜底；也可用 --job 指定 PRG 内实际 Job 名（用 Tool32 查看）。
        /// </summary>
        private static readonly string[] IdentJobCandidates =
        {
            "ident",            // 已确认存在于 mevd1724.prg（E 系标准识别 Job）
            "identification",   // F 系及其他 SGBD 常见命名
            "aif_lesen",        // AIF（程序应用信息）读取
            "c_c_lesen",        // 编码数据读取（只读）
        };

        /// <summary>
        /// 第 2 阶段：VIN 补齐 Job 候选（全部只读“lesen”类；依据 doc/材料/MEVD174K.b1v
        /// 反编译的权威 Job 定义）：
        ///   AIF_LESEN ：$23 ReadMemoryByAddress 读 AIF（AIF_FG_NR_LANG，17 位完整 VIN，
        ///               无则回退 7 位），参数 AIF_NUMMER 留空默认 0=当前 AIF——已实车验证（2026-09-21）；
        ///   C_FG_LESEN：KWP2000 $1A/$90 读车架号（FG_NR，7 位）——已实车验证（2026-09-21）；
        ///   其余为其他 SGBD 命名兜底。
        /// 红线：绝不使用 *_SCHREIBEN / *_AUFTRAG / FS_LOESCHEN / *_RESET / PROGRAMMIEREN 等
        /// 写/复位/编程类 Job。
        /// </summary>
        private static readonly string[] VinJobCandidates =
        {
            "AIF_LESEN",
            "C_FG_LESEN",
            "fin_lesen",
            "vin_lesen",
            "fa_lesen",
            "zcs_lesen",
        };

        // ---- MVP/可选字段的名称匹配模式（不区分大小写，按优先级先后尝试） ----
        // 实车实测（2026-09-21）：本车 DME 的 0x80 识别块字段为 ID_BMW_NR（BMW 零件号）、
        // ID_HW_NR（硬件号）、ID_SW_NR_FSV 等；VIN 字段为 FG_NR（7 位）/ AIF_FG_NR_LANG（17 位，
        // 据 MEVD174K.b1v 反编译）；VARIANTE 为 SGBD 变体名（最后兜底）。
        private static readonly string[] VinPatterns = { @"\bVIN\b", @"FG.?NR.?LANG", @"FGNR", @"\bFIN\b", @"FIN$", @"^FG.?NR$", @"FZG" };
        private static readonly string[] EcuPatterns = { @"^ID_BMW_NR$", @"BMW.?NR", @"ECU.?NAME", @"BASIC.?ECU", @"ECU.?IDENT", @"BEDIAG", @"^VARIANTE$", @"DME" };
        private static readonly string[] HwPatterns = { @"HW.?(NR|NUM|REF|VER|TEIL|IDENT)", @"HARDWARE" };
        private static readonly string[] SwPatterns = { @"SW.?(NR|NUM|REF|VER|TEIL|IDENT)", @"SOFTWARE" };
        private static readonly string[] IgnitionPatterns = { @"KL.?15", @"IGNITION", @"Z[A?]NDUNG", @"KLEMME.?15" };
        private static readonly string[] VoltagePatterns = { @"UBATT", @"U.?BATT", @"SPANNUNG", @"VOLT" };

        private readonly AppLog _log;

        public VehicleInfoReader(AppLog log)
        {
            _log = log;
        }

        /// <summary>
        /// 两阶段读取车辆基础信息：
        /// 第 1 阶段执行识别类 Job（ECU 标识/硬件/软件号）；
        /// 第 2 阶段在 VIN 未命中时自动按序尝试只读 VIN Job（C_FG_LESEN / AIF_LESEN 等）。
        /// </summary>
        /// <param name="forcedJob">--job 指定的 Job 名；非空时只执行该 Job（不做 VIN 补齐）。</param>
        /// <param name="para">--para 指定的 Job 参数（识别类 Job 通常为空）。</param>
        /// <exception cref="EdiabasJobException">接口类（通信）错误直接抛出</exception>
        /// <exception cref="NoIdentJobException">第 1 阶段所有候选 Job 均不可用</exception>
        public VehicleInfo Read(EdiabasClient client, string ecuName, string forcedJob, string para)
        {
            var info = new VehicleInfo { SgbdName = ecuName };

            // ---- 第 1 阶段：识别 Job ----
            IEnumerable<string> candidates = string.IsNullOrEmpty(forcedJob)
                ? IdentJobCandidates
                : new[] { forcedJob };
            var failures = new List<EdiabasJobException>();
            foreach (string job in candidates)
            {
                JobResult result = TryRunJob(client, ecuName, job, para, failures);
                if (result != null)
                {
                    info.Raws.Add(result);
                    break;
                }
            }
            if (info.Raws.Count == 0)
            {
                throw new NoIdentJobException(ecuName, failures);
            }
            Extract(info);

            // ---- 第 2 阶段：VIN 补齐（仅当未指定 --job 且 VIN 未命中；全部只读 Job） ----
            if (!info.Vin.Found && string.IsNullOrEmpty(forcedJob))
            {
                foreach (string job in VinJobCandidates)
                {
                    JobResult result = TryRunJob(client, ecuName, job, para, null);
                    if (result == null) continue;
                    info.Raws.Add(result);
                    Extract(info);
                    if (info.Vin.Found) break;
                }
                if (!info.Vin.Found)
                {
                    _log.Warn("VIN 补齐 Job 均未获得 VIN（可用 Tool32 查 PRG 后 --job 指定其他只读 Job）");
                }
            }

            return info;
        }

        /// <summary>
        /// 执行单个只读 Job：成功且未被 ECU 拒绝时返回结果，否则返回 null
        /// （接口类通信错误直接抛出，由 Program 给出场景化提示）。
        /// </summary>
        private JobResult TryRunJob(EdiabasClient client, string ecuName, string job, string para,
            IList<EdiabasJobException> failures)
        {
            try
            {
                JobResult result = client.RunJob(ecuName, job, para ?? string.Empty);

                // API 层成功还不够：ECU 拒绝服务时错误只体现在 JOB_STATUS 字段中
                string jobStatus = result.FindJobStatus();
                if (JobResult.IsErrorStatus(jobStatus))
                {
                    if (failures != null)
                    {
                        failures.Add(EdiabasJobException.ForEcuRejection(ecuName, job, jobStatus));
                    }
                    _log.Warn("Job {0} 被 ECU 拒绝（{1}），尝试下一个候选", job, jobStatus);
                    return null;
                }
                _log.Info("Job {0} 执行成功（JOB_STATUS: {1}）", job,
                    string.IsNullOrWhiteSpace(jobStatus) ? "正常" : jobStatus);
                return result;
            }
            catch (EdiabasJobException ex)
            {
                if (ex.Category == EdiabasErrorCategory.Interface)
                {
                    // 通信层失败：换 Job 无意义，直接上抛
                    throw;
                }
                if (failures != null) failures.Add(ex);
                _log.Warn("Job {0} 不可用（错误码 {1}，类别 {2}），尝试下一个候选", job, ex.ErrorCode, ex.Category);
                return null;
            }
        }

        /// <summary>
        /// 在全部 Job 结果中按模式提取 MVP/可选字段。
        /// 仅在未命中时填充：第 1 阶段（ident）的值优先于第 2 阶段补齐 Job 的同名字段。
        /// </summary>
        private void Extract(VehicleInfo info)
        {
            if (!info.Vin.Found) info.Vin = FindFirst(info, "VIN 车架号", VinPatterns);
            if (!info.EcuName.Found) info.EcuName = FindFirst(info, "ECU 名称/诊断标识", EcuPatterns);
            if (!info.HwVersion.Found) info.HwVersion = FindFirst(info, "ECU 硬件号/版本", HwPatterns);
            if (!info.SwVersion.Found) info.SwVersion = FindFirst(info, "ECU 软件号/版本", SwPatterns);
            if (!info.Ignition.Found) info.Ignition = FindFirst(info, "点火开关 (KL.15)", IgnitionPatterns);
            if (!info.Voltage.Found) info.Voltage = FindFirst(info, "蓄电池电压", VoltagePatterns);

            if (!info.AllMvpFound)
            {
                _log.Warn("MVP 字段未全部命中：VIN={0}, ECU={1}, HW={2}, SW={3}（详见原始结果与日志）",
                    info.Vin.Found, info.EcuName.Found, info.HwVersion.Found, info.SwVersion.Found);
            }
        }

        private static ExtractedField FindFirst(VehicleInfo info, string label, string[] patterns)
        {
            foreach (string pattern in patterns)
            {
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                foreach (JobResult raw in info.Raws)
                {
                    foreach (ResultField field in raw.AllFields())
                    {
                        if (regex.IsMatch(field.Name) && !string.IsNullOrWhiteSpace(field.Value))
                        {
                            return new ExtractedField
                            {
                                Label = label,
                                Value = field.Value.Trim(),
                                SourceJob = raw.JobName,
                                SourceField = field.Name,
                            };
                        }
                    }
                }
            }
            return ExtractedField.Missing(label);
        }
    }

    /// <summary>第 1 阶段所有候选识别 Job 均执行失败（含各候选的错误明细）。</summary>
    internal sealed class NoIdentJobException : System.Exception
    {
        public string EcuName { get; private set; }
        public IReadOnlyList<EdiabasJobException> Failures { get; private set; }

        public NoIdentJobException(string ecuName, IReadOnlyList<EdiabasJobException> failures)
            : base("未能在 " + ecuName + " 上执行任何候选识别类 Job")
        {
            EcuName = ecuName;
            Failures = failures;
        }
    }
}
