using System;
using System.IO;
using System.Threading;

namespace caxa3dexbtest
{
    /// <summary>
    /// 调试日志：%TEMP%\caxa3dexbtest_trace.log。
    /// 由原独立 EXE 的 Program.Log 迁移而来，保持同一份日志格式。
    /// </summary>
    internal static class Diagnostics
    {
        public static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3dexbtest_trace.log");

        /// <summary>UI 日志接收器（由 MainForm 设置；线程安全由接收方保证）。</summary>
        public static Action<string> UiLogSink;

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

            Action<string> sink = UiLogSink;
            if (sink != null)
            {
                try { sink(line); } catch { }
            }
        }
    }
}
