using ObdLogger.Ediabas;

namespace ObdLogger.Vehicle
{
    /// <summary>一个被提取的目标字段：显示名 + 值 + 来源字段名（便于核对）。</summary>
    internal sealed class ExtractedField
    {
        public string Label;
        public string Value;
        public string SourceField;
        public bool Found { get { return !string.IsNullOrEmpty(Value); } }

        public static ExtractedField Missing(string label)
        {
            return new ExtractedField { Label = label, Value = null, SourceField = null };
        }
    }

    /// <summary>
    /// 需求文档 3.3 节“车辆基础信息”：MVP 必须输出 + 可选输出 + 原始 Job 结果。
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

        /// <summary>实际成功执行的识别类 Job 名。</summary>
        public string JobName;

        /// <summary>该 Job 的完整原始结果（全部字段，供 3.3 节之外的核对）。</summary>
        public JobResult Raw;

        public bool AllMvpFound
        {
            get { return Vin.Found && EcuName.Found && HwVersion.Found && SwVersion.Found; }
        }
    }
}
