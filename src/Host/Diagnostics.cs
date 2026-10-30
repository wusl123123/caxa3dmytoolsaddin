using System;
using System.IO;
using System.Threading;

namespace caxa3dmytoolsaddin
{
    /// <summary>
    /// 调试日志：%TEMP%\caxa3dmytoolsaddin_trace.log。
    /// 静态构造时清空旧日志（每次 CAXA 启动从新会话开始记录）。
    /// </summary>
    internal static class Diagnostics
    {
        public static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3dmytoolsaddin_trace.log");

        static Diagnostics()
        {
            try { File.Delete(LogPath); } catch { }
        }

        public static void Log(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + " [tid " +
                Thread.CurrentThread.ManagedThreadId +
                (Thread.CurrentThread.IsBackground ? "/bg" : "/ui") + "] " + msg;

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
