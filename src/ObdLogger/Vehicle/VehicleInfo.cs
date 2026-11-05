using System.Collections.Generic;
using ObdLogger.Ediabas;

namespace ObdLogger.Vehicle
{
    /// <summary>一个被提取的目标字段：显示名 + 值 + 来源（Job 名 + 字段名，便于核对）。</summary>
    internal sealed class ExtractedField
    {
        public string Label;
        public string Value;
        public string SourceJob;
        public string SourceField;
        public bool Found { get { return !string.IsNullOrEmpty(Value); } }

        public static ExtractedField Missing(string label)
        {
            return new ExtractedField { Label = label };
        }
    }

    /// <summary>
    /// 需求文档 3.3 节“车辆基础信息”：MVP 必须输出 + 可选输出 + 全部原始 Job 结果。
    /// </summary>
    internal sealed class VehicleInfo
    {
        // ---- 必须输出（MVP） ----
        public ExtractedField Vin = ExtractedField.Missing("VIN 车架号");
        public ExtractedField EcuName = ExtractedField.Missing("ECU 名称/诊断标识");
        public ExtractedField HwVersion = ExtractedField.Missing("ECU 硬件号/版本");
        public ExtractedField SwVersion = ExtractedField.Missing("ECU 软件号/版本");

        // ---- 可选输出 ----
        public ExtractedField Ignition = ExtractedField.Missing("点火开关 (KL.15)");
        public ExtractedField Voltage = ExtractedField.Missing("蓄电池电压");

        /// <summary>目标 ECU（SGBD 名，如 mevd1724）。</summary>
        public string SgbdName;

        /// <summary>已成功执行的全部 Job 的原始结果（按执行顺序；识别 Job 在前，VIN 补齐 Job 在后）。</summary>
        public readonly List<JobResult> Raws = new List<JobResult>();

        /// <summary>已执行 Job 名（" + " 连接，用于标题展示）。</summary>
        public string JobNames
        {
            get
            {
                var names = new List<string>();
                foreach (JobResult r in Raws)
                {
                    if (!names.Contains(r.JobName)) names.Add(r.JobName);
                }
                return string.Join(" + ", names);
            }
        }

        public int TotalFieldCount
        {
            get
            {
                int n = 0;
                foreach (JobResult r in Raws) n += r.TotalFieldCount;
                return n;
            }
        }

        public bool AllMvpFound
        {
            get { return Vin.Found && EcuName.Found && HwVersion.Found && SwVersion.Found; }
        }
    }
}
