// 诊断日志模板：%TEMP%\<项目名>_trace.log
// 规则：加载时清空；高频路径 10s 节流；关键判据必须写文件（步骤类只进 UI）；线程安全。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace caxa3dButton
{
    internal static class Diagnostics
    {
        private static readonly object _lock = new object();
        private static readonly string _logPath =
            Path.Combine(Path.GetTempPath(), "caxa3dButton_trace.log");

        private static readonly Dictionary<string, DateTime> _throttle =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        /// <summary>界面日志区回调（MainForm 构造时挂上，FormClosed 清空）。</summary>
        public static Action<string> UiLogSink;

        /// <summary>
        /// 轮询热路径开关：长等待（MiniFlow.WaitUntil 的 WaitQuietly）期间置 true，
        /// 逐次调用的细粒度 Trace 不落盘，只由 LogPoll 记「与上一次不同」的快照行。
        /// 实机教训（caxa3ddiffrename）：100 ms 轮询的 3 分钟等待能刷出 60 万字节日志，把关键行埋掉。
        /// </summary>
        public static volatile bool PollQuiet;

        /// <summary>
        /// 设计树遍历的**逐节点** Trace 开关（消息里带 "(walk)" 的那些）。默认关。
        /// </summary>
        public static volatile bool VerboseWalk = false;

        private static readonly string[] HotPrefixes =
        {
            "ActiveDoc ", "get_SelectionMgr", "GetSelectedElementsZArray", "GetSelectedElements(",
            "IZArray.Count", "IZArray.Get", "IZElement.get_Type", "IZElement.get_Name",
            "IZDoc.get_Name", "GetExternallyLinkedInfo", "SelectionsAvailable"
        };

        private static string _lastPollSnapshot;

        /// <summary>轮询快照行：内容没变就不写，变了才写一条。</summary>
        public static void LogPoll(string snapshot)
        {
            if (string.Equals(snapshot, _lastPollSnapshot, StringComparison.Ordinal)) return;
            _lastPollSnapshot = snapshot;
            Trace(snapshot);
        }

        /// <summary>
        /// 取证块（控件树 / MSAA 条目清单）整段写进正式日志，只有一条通道：
        /// ① 单行 Trace 会被 PollQuiet 吃掉，而这些块正是「点不动」时唯一能定位的东西；
        /// ② 几百行不转发 UI，否则日志区会被自己刷死。
        /// </summary>
        public static void Dump(string tag, string block)
        {
            string head = "取证[" + tag + "] ↓↓↓ " + DateTime.Now.ToString("HH:mm:ss.fff");
            Append(head);
            Append(block);
            Append("取证[" + tag + "] ↑↑↑ 结束");
        }

        /// <summary>细粒度诊断：只写文件，不进 UI（避免刷屏）。轮询期与逐节点走查按开关抑制。</summary>
        public static void Trace(string msg)
        {
            if (PollQuiet && IsHot(msg)) return;
            if (!VerboseWalk && msg.IndexOf("(walk)", StringComparison.Ordinal) >= 0) return;
            Append(DateTime.Now.ToString("HH:mm:ss.fff") + " [tid " +
                System.Threading.Thread.CurrentThread.ManagedThreadId +
                (System.Threading.Thread.CurrentThread.IsBackground ? "/bg" : "/ui") + "] " + msg);
        }

        private static bool IsHot(string msg)
        {
            for (int i = 0; i < HotPrefixes.Length; i++)
                if (msg.StartsWith(HotPrefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        private static void Append(string line)
        {
            try
            {
                lock (_lock)
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }

        /// <summary>加载时清空日志。InitSelf 最开始调用一次。</summary>
        public static void Reset()
        {
            try
            {
                lock (_lock)
                {
                    if (File.Exists(_logPath)) File.Delete(_logPath);
                    File.AppendAllText(_logPath, "== caxa3dButton trace " +
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ==\r\n", new UTF8Encoding(false));
                }
            }
            catch { }
        }

        /// <summary>写文件日志（关键判据用）。throttleMs > 0 时同 key 节流；同时转发界面日志区。</summary>
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

            // 同时转发界面日志区（不持锁，避免 UI 线程回调里再进 lock）
            Action<string> sink = UiLogSink;
            if (sink != null)
            {
                try { sink(message); } catch { }
            }
        }

        public static string LogPath { get { return _logPath; } }
    }
}
