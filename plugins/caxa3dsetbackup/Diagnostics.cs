// 诊断日志模板：%TEMP%\<项目名>_trace.log
// 规则：加载时清空；高频路径 10s 节流；关键判据必须写文件（步骤类只进 UI）；线程安全。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace caxa3dsetbackup
{
    internal static class Diagnostics
    {
        private static readonly object _lock = new object();
        private static readonly string _logPath =
            Path.Combine(Path.GetTempPath(), "caxa3dsetbackup_trace.log");

        private static readonly Dictionary<string, DateTime> _throttle =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>加载时清空日志。InitSelf 最开始调用一次。</summary>
        public static void Reset()
        {
            try
            {
                lock (_lock)
                {
                    if (File.Exists(_logPath)) File.Delete(_logPath);
                    File.AppendAllText(_logPath, "== caxa3dsetbackup trace " +
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==\r\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }

        /// <summary>写文件日志（关键判据用）。throttleMs > 0 时同 key 节流。</summary>
        public static void Log(string message, int throttleMs = 0)
        {
            try
            {
                lock (_lock)
                {
                    if (throttleMs > 0)
                    {
                        string key = message.Length > 60 ? message.Substring(0, 60) : message;
                        DateTime last;
                        if (_throttle.TryGetValue(key, out last) &&
                            (DateTime.Now - last).TotalMilliseconds < throttleMs) return;
                        _throttle[key] = DateTime.Now;
                    }

                    File.AppendAllText(_logPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + "\r\n",
                        new UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static string LogPath { get { return _logPath; } }
    }
}
