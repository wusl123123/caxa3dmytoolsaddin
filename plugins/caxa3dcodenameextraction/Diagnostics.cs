using System;
using System.IO;
using System.Threading;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 调试日志：%TEMP%\caxa3dcodenameextraction_trace.log。
    /// 静态构造时自动清空旧日志（每次 CAXA 启动从新会话开始记录），
    /// 同时把日志行转发给 UI 接收器（由 MainForm 设置）。
    /// 文件日志带线程号；UI 日志去掉线程号，只保留时间与消息。
    /// </summary>
    internal static class Diagnostics
    {
        public static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3dcodenameextraction_trace.log");

        /// <summary>UI 日志接收器（由 MainForm 设置；线程安全由接收方保证）。</summary>
        public static Action<string> UiLogSink;

        static Diagnostics()
        {
            try { File.Delete(LogPath); } catch { }
        }

        /// <summary>常规日志：写入文件（带线程号）并转发到 UI（不带线程号）。</summary>
        public static void Log(string msg)
        {
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

        /// <summary>
        /// 细粒度诊断日志：只写文件，不进入 UI 日志区（避免刷屏）。
        /// </summary>
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
