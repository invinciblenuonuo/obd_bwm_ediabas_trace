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
