using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace caxa3didcskill
{
    /// <summary>唯一日志出口：文件 + 界面事件，线程安全。</summary>
    internal static class Diagnostics
    {
        private static readonly object Sync = new object();
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3didcskill_trace.log");

        private const long MaxBytes = 2 * 1024 * 1024;

        /// <summary>后台线程写日志时由界面订阅，内部用 BeginInvoke 切回 UI 线程。</summary>
        public static event Action<string> LineAppended;

        public static void Initialize()
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                {
                    string old = LogPath + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(LogPath, old);
                }
                File.WriteAllText(LogPath, string.Empty);
            }
            catch { }

            Log("启动 v" + typeof(Diagnostics).Assembly.GetName().Version +
                "，pid=" + Process.GetCurrentProcess().Id +
                "，日志=" + LogPath);
        }

        public static void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") +
                " [tid " + Thread.CurrentThread.ManagedThreadId + "] " + message;
            lock (Sync)
            {
                try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
            }

            Action<string> handler = LineAppended;
            if (handler != null)
            {
                try { handler(line); } catch { }
            }
        }

        public static void Exception(string context, Exception ex)
        {
            Log(context + ": " + (ex == null ? "(null)" : ex.ToString()));
        }
    }
}
