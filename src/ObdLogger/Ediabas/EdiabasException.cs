using System;

namespace ObdLogger.Ediabas
{
    /// <summary>错误类别（按 Api.h 错误码区间划分，用于生成场景化提示）。</summary>
    internal enum EdiabasErrorCategory
    {
        None = 0,
        Interface,  // IFH 10-59 / 200-249：接口/通信（线缆、端口占用、ECU 无响应）
        Bip,        // BIP 60-89：BEST/2 解释器（SGBD 文件或 Job 不存在等）
        Sys,        // SYS 90-119：EDIABAS 系统错误
        Api,        // API 120-149：API 调用方式错误
        Net,        // NET 150-199：网络接口错误
        Run,        // RUN 250-349：Job 运行时错误（ECU 拒绝/负响应等）
        Unknown,
    }

    /// <summary>EDIABAS 环境/初始化类错误（无可用的错误码上下文）。</summary>
    internal sealed class EdiabasException : Exception
    {
        public EdiabasException(string message) : base(message) { }
        public EdiabasException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Job 等待超时（防挂死兜底，FR-7）。</summary>
    internal sealed class EdiabasTimeoutException : Exception
    {
        public EdiabasTimeoutException(string job, int seconds)
            : base(string.Format("Job {0} 执行超过 {1} 秒仍未结束，已中止（ECU 无响应或线缆/时序异常）", job, seconds))
        {
        }
    }

    /// <summary>Job 执行失败：携带 EDIABAS 错误码、错误文本与 JobInfo。</summary>
    internal sealed class EdiabasJobException : Exception
    {
        public string Ecu { get; private set; }
        public string Job { get; private set; }
        public int ErrorCode { get; private set; }
        public string ErrorText { get; private set; }
        public string JobInfo { get; private set; }
        public EdiabasErrorCategory Category { get; private set; }

        public EdiabasJobException(string ecu, string job, int errorCode, string errorText, string jobInfo)
            : base(string.Format("Job {0} 失败：错误码 {1} ({2})", job, errorCode, errorText))
        {
            Ecu = ecu;
            Job = job;
            ErrorCode = errorCode;
            ErrorText = errorText;
            JobInfo = jobInfo;
            Category = Classify(errorCode);
        }

        private EdiabasJobException(string ecu, string job, string ecuJobStatus)
            : base(string.Format("Job {0} 被 ECU 拒绝：{1}", job, ecuJobStatus))
        {
            Ecu = ecu;
            Job = job;
            ErrorCode = 0;
            ErrorText = ecuJobStatus;
            JobInfo = string.Empty;
            Category = EdiabasErrorCategory.Run;
        }

        /// <summary>ECU 级拒绝（API 层成功但 JOB_STATUS 为 ERROR_...）。ErrorCode 恒为 0。</summary>
        public static EdiabasJobException ForEcuRejection(string ecu, string job, string jobStatus)
        {
            return new EdiabasJobException(ecu, job, "JOB_STATUS=" + jobStatus);
        }

        /// <summary>是否为 ECU 级拒绝（区别于 API 错误码失败）。</summary>
        public bool IsEcuRejection { get { return ErrorCode == 0 && Category == EdiabasErrorCategory.Run; } }

        internal static EdiabasErrorCategory Classify(int code)
        {
            if (code == 0) return EdiabasErrorCategory.None;
            if ((code >= 10 && code <= 59) || (code >= 200 && code <= 249)) return EdiabasErrorCategory.Interface;
            if (code >= 60 && code <= 89) return EdiabasErrorCategory.Bip;
            if (code >= 90 && code <= 119) return EdiabasErrorCategory.Sys;
            if (code >= 120 && code <= 149) return EdiabasErrorCategory.Api;
            if (code >= 150 && code <= 199) return EdiabasErrorCategory.Net;
            if (code >= 250 && code <= 349) return EdiabasErrorCategory.Run;
            return EdiabasErrorCategory.Unknown;
        }
    }
}
