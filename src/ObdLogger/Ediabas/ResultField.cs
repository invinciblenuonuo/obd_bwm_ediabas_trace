using System;
using System.Collections.Generic;

namespace ObdLogger.Ediabas
{
    /// <summary>单个结果字段：字段名 + 格式 + 已转换为文本的值。</summary>
    internal sealed class ResultField
    {
        public string Name;
        public ApiResultFormat Format;
        public string Value;
    }

    /// <summary>一个结果集（rset）内的全部字段。</summary>
    internal sealed class ResultSet
    {
        public ushort Index;
        public readonly List<ResultField> Fields = new List<ResultField>();
    }

    /// <summary>一次 Job 调用的完整结果（可能包含多个结果集）。</summary>
    internal sealed class JobResult
    {
        public string JobName;
        public readonly List<ResultSet> Sets = new List<ResultSet>();

        /// <summary>
        /// ECU 级 Job 状态（系统结果字段 JOB_STATUS）。
        /// 注意：apiErrorCode==0 仅表示 API 层成功；ECU 拒绝服务（如 SUB_FUNCTION_NOT_SUPPORTED）
        /// 时错误只会体现在 JOB_STATUS 字段中，必须单独检查。
        /// </summary>
        public string FindJobStatus()
        {
            string fallback = null;
            foreach (ResultField field in AllFields())
            {
                if (string.Equals(field.Name, "JOB_STATUS", StringComparison.OrdinalIgnoreCase))
                {
                    return field.Value;
                }
                if (string.Equals(field.Name, "JOBSTATUS", StringComparison.OrdinalIgnoreCase))
                {
                    fallback = field.Value;
                }
            }
            return fallback;
        }

        /// <summary>JOB_STATUS 是否为错误（EDIABAS 约定：错误以 "ERROR" 前缀表示；空为正常）。</summary>
        public static bool IsErrorStatus(string status)
        {
            return !string.IsNullOrWhiteSpace(status) &&
                   status.TrimStart().StartsWith("ERROR", StringComparison.OrdinalIgnoreCase);
        }

        public IEnumerable<ResultField> AllFields()
        {
            foreach (ResultSet set in Sets)
            {
                foreach (ResultField field in set.Fields)
                {
                    yield return field;
                }
            }
        }

        public int TotalFieldCount
        {
            get
            {
                int n = 0;
                foreach (ResultSet set in Sets) n += set.Fields.Count;
                return n;
            }
        }
    }
}
