using System;
using System.Globalization;

namespace ObdLogger.Logging
{
    /// <summary>
    /// 一次采样快照（采集线程生产，显示/CSV 线程消费）。
    /// 值以“文化不变、已归一小数点”的文本形式保存：数字直接可写入 CSV；
    /// 空串表示该项本周期缺失（写 CSV 时留空字段，§6 / FR-2-8）。
    /// </summary>
    internal sealed class SampleSnapshot
    {
        /// <summary>采样发起时刻（§5：以发起时刻计，含毫秒）。</summary>
        public DateTime Timestamp;

        /// <summary>每个采样项一个值（下标 = SampleItem.Index）；"" 表示缺失。</summary>
        public string[] Values;

        /// <summary>整帧是否为数据缺口（通信中断/全组失败）：CSV 写时间戳 + 空值以标记缺口。</summary>
        public bool IsGap;

        /// <summary>缺口/异常简述（用于控制台状态行与日志，不写入 CSV 数据列）。</summary>
        public string Note;

        public SampleSnapshot(DateTime timestamp, int itemCount)
        {
            Timestamp = timestamp;
            Values = new string[itemCount];
            for (int i = 0; i < itemCount; i++) Values[i] = string.Empty;
        }

        /// <summary>有效（非空）值个数，用于统计与显示。</summary>
        public int FilledCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Values.Length; i++)
                {
                    if (!string.IsNullOrEmpty(Values[i])) n++;
                }
                return n;
            }
        }

        public string FormatTimestamp()
        {
            return Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 值归一：Job 返回的物理值文本 → CSV 友好文本。
        /// - 空 → 空串（缺失）；
        /// - 可解析为数字（先按不变文化，再按德语逗号小数）→ 以不变文化重格式化（小数点用点，§6）；
        /// - 非数字文本（如枚举/状态字）→ 原样保留（去首尾空白）。
        /// </summary>
        public static string Normalize(string raw)
        {
            if (raw == null) return string.Empty;
            string s = raw.Trim();
            if (s.Length == 0) return string.Empty;

            double d;
            const NumberStyles st = NumberStyles.Float;
            CultureInfo inv = CultureInfo.InvariantCulture;
            if (double.TryParse(s, st, inv, out d))
            {
                return d.ToString("0.####", inv);
            }
            // 德语小数逗号（如 "87,5"）→ 点
            if (s.IndexOf(',') >= 0 && s.IndexOf('.') < 0)
            {
                string swapped = s.Replace(',', '.');
                if (double.TryParse(swapped, st, inv, out d))
                {
                    return d.ToString("0.####", inv);
                }
            }
            return s;
        }
    }
}
