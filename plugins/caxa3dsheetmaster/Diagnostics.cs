using System;
using System.IO;
using System.Threading;

namespace caxa3dsheetmaster
{
    /// <summary>
    /// 调试日志：%TEMP%\caxa3dsheetmaster_trace.log。
    /// 静态构造时清空旧日志（每次 CAXA 启动从新会话记录），并转发到 UI 日志区。
    /// </summary>
    internal static class Diagnostics
    {
        public static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3dsheetmaster_trace.log");

        /// <summary>UI 日志接收器（由 MainForm 设置）。</summary>
        public static Action<string> UiLogSink;

        /// <summary>轮询期间抑制文件日志，避免 trace.log 被每秒一次的探测刷爆。</summary>
        public static bool Suppress;

        static Diagnostics()
        {
            try { File.Delete(LogPath); } catch { }
        }

        public static void Log(string msg)
        {
            if (Suppress) return;
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            string fileLine = time + " [tid " +
                Thread.CurrentThread.ManagedThreadId +
                (Thread.CurrentThread.IsBackground ? "/bg" : "/ui") + "] " + msg;

            Append(fileLine);

            Action<string> sink = UiLogSink;
            if (sink != null)
            {
                try { sink(time + " " + msg); } catch { }
            }
        }

        /// <summary>细粒度诊断：只写文件，不进 UI（避免刷屏）。</summary>
        public static void Trace(string msg)
        {
            if (Suppress) return;
            Append(DateTime.Now.ToString("HH:mm:ss.fff") + " [tid " +
                Thread.CurrentThread.ManagedThreadId + "] " + msg);
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
