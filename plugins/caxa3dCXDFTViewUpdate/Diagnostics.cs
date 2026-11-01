using System;
using System.IO;
using System.Threading;

namespace caxa3dCXDFTViewUpdate
{
    /// <summary>
    /// 调试日志：%TEMP%\caxa3dCXDFTViewUpdate_trace.log。
    /// 静态构造时自动清空旧日志（每次 CAXA 启动从新会话开始记录）。
    /// </summary>
    internal static class Diagnostics
    {
        public static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3dCXDFTViewUpdate_trace.log");

        static Diagnostics()
        {
            try { File.Delete(LogPath); } catch { }
        }

        /// <summary>常规日志：写入文件（带线程号），批处理清单另行展示给用户。</summary>
        public static void Log(string msg)
        {
            Append(DateTime.Now.ToString("HH:mm:ss.fff") + " [tid " +
                Thread.CurrentThread.ManagedThreadId +
                (Thread.CurrentThread.IsBackground ? "/bg" : "/ui") + "] " + msg);
        }

        /// <summary>细粒度诊断日志：只写文件，不进入 UI（避免刷屏）。</summary>
        public static void Trace(string msg)
        {
            Append(DateTime.Now.ToString("HH:mm:ss.fff") + " [tid " +
                Thread.CurrentThread.ManagedThreadId +
                (Thread.CurrentThread.IsBackground ? "/bg" : "/ui") + "] " + msg);
        }

        private static void Append(string line)
        {
            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}
