using System;
using System.IO;
using System.Threading;

namespace Caxa3dcudeaddin
{
    /// <summary>
    /// 调试日志：%TEMP%\Caxa3dcudeaddin_trace.log。
    /// 静态构造时自动清空旧日志（每次 CAXA 启动从新会话开始记录），
    /// 同时把日志行转发给 UI 接收器（由 MainForm 设置）。
    /// </summary>
    internal static class Diagnostics
    {
        public static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "Caxa3dcudeaddin_trace.log");

        /// <summary>UI 日志接收器（由 MainForm 设置；线程安全由接收方保证）。</summary>
        public static Action<string> UiLogSink;

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

            Action<string> sink = UiLogSink;
            if (sink != null)
            {
                try { sink(line); } catch { }
            }
        }
    }
}
