using System;
using System.Runtime.InteropServices;

namespace ObdLogger.Ediabas
{
    /// <summary>
    /// EDIABAS 7.3.0 原生 API（C:\EDIABAS\Bin\api32.dll，仅 32 位）P/Invoke 声明。
    ///
    /// 签名依据（本机实测 + 官方/社区权威来源交叉验证）：
    /// 1. api32.dll 反汇编：所有函数以 stdcall 返回（ret N 与导出别名 ___apiXxx@N 的 N 完全一致）；
    /// 2. 宝马官方 apiNET32.dll（Ediabas.API）内部 __api32* 包装的参数列表；
    /// 3. ediabaslib（uholeschak）Api32/Api.h 与 ApiDll.cpp（FAR PASCAL = stdcall）。
    ///
    /// 与 6.x 经典文档的差异：7.x 为多实例 API，除 apiCheckVersion/apiXSysSetConfig 外，
    /// 所有函数首参均为 apiInit 输出的 APIHANDLE（UInt32）。
    /// 字符串均为 ANSI；APIBOOL 为 4 字节枚举（APIFALSE=0 / APITRUE=1）。
    /// </summary>
    internal static class NativeMethods
    {
        // ---- Api.h 缓冲区常量 ----
        internal const int ApiMaxName = 64;    // APIMAXNAME：job/ecu/result 名（含 '\0'）
        internal const int ApiMaxText = 1024;  // APIMAXTEXT：文本结果（含 '\0'）
        internal const int ApiMaxBinary = 1024; // APIMAXBINARY：二进制结果

        private const string ApiDll = "api32.dll";

        // ---- Windows：指定 DLL 搜索目录（确保 api32.dll 及其依赖从 C:\EDIABAS\Bin 解析） ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetDllDirectory(string lpPathName);

        // ---- 生命周期 ----

        [DllImport(ApiDll, EntryPoint = "__apiInit", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApiInit(out uint handle);

        [DllImport(ApiDll, EntryPoint = "__apiEnd", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        internal static extern void ApiEnd(uint handle);

        [DllImport(ApiDll, EntryPoint = "__apiBreak", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        internal static extern void ApiBreak(uint handle);

        // ---- Job 执行（只读识别类 Job 专用；apiJob 本身无返回值，错误经 apiErrorCode 反馈） ----

        [DllImport(ApiDll, EntryPoint = "__apiJob", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern void ApiJob(uint handle, string ecu, string job, string para, string result);

        [DllImport(ApiDll, EntryPoint = "__apiJobInfo", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern int ApiJobInfo(uint handle, byte[] infoText);

        // ---- 错误信息 ----

        [DllImport(ApiDll, EntryPoint = "__apiErrorCode", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        internal static extern int ApiErrorCode(uint handle);

        [DllImport(ApiDll, EntryPoint = "__apiErrorText", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        internal static extern void ApiErrorText(uint handle, byte[] buf, int bufsize);

        // ---- 结果集枚举 ----

        [DllImport(ApiDll, EntryPoint = "__apiResultSets", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApiResultSets(uint handle, out ushort sets);

        [DllImport(ApiDll, EntryPoint = "__apiResultNumber", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApiResultNumber(uint handle, out ushort count, ushort rset);

        [DllImport(ApiDll, EntryPoint = "__apiResultName", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApiResultName(uint handle, byte[] buf, ushort index, ushort rset);

        [DllImport(ApiDll, EntryPoint = "__apiResultFormat", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApiResultFormat(uint handle, out int format, string result, ushort rset);

        [DllImport(ApiDll, EntryPoint = "__apiResultText", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApiResultText(uint handle, byte[] buf, string result, ushort rset, string format);

        [DllImport(ApiDll, EntryPoint = "__apiResultBinary", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApiResultBinary(uint handle, byte[] buf, ref ushort buflen, string result, ushort rset);

        // ---- 状态/版本 ----

        [DllImport(ApiDll, EntryPoint = "__apiState", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
        internal static extern int ApiState(uint handle);

        [DllImport(ApiDll, EntryPoint = "__apiCheckVersion", ExactSpelling = true, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ApiCheckVersion(int versionCompatibility, byte[] versionInfo);
    }
}
