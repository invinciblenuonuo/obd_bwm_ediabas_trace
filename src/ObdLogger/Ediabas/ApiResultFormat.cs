namespace ObdLogger.Ediabas
{
    /// <summary>
    /// 结果字段的数据格式（Api.h 中 APIRESULTFORMAT 枚举，C 枚举从 0 开始）。
    /// </summary>
    internal enum ApiResultFormat
    {
        Unknown = -1,
        Char = 0,
        Byte = 1,
        Integer = 2,
        Word = 3,
        Long = 4,
        DWord = 5,
        Text = 6,
        Binary = 7,
        Real = 8,
        LongLong = 9,
        QWord = 10,
    }
}
