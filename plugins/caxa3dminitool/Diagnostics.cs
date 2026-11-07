using System;
using System.IO;
using System.Threading;

namespace caxa3dminitool
{
    /// <summary>
    /// 调试日志：%TEMP%\caxa3dminitool_trace.log。
    /// 静态构造时清空旧日志（每次 CAXA 启动从新会话记录），并转发到 UI 日志区。
    /// </summary>
    internal static class Diagnostics
    {
        public static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3dminitool_trace.log");

        /// <summary>UI 日志接收器（由主窗体设置）。</summary>
        public static Action<string> UiLogSink;

        /// <summary>
        /// 轮询热路径开关：长等待（WaitUntil）期间置 true，逐次调用的细粒度 Trace 不落盘，
        /// 只由 LogPoll 记「与上一次不同」的快照行。
        /// 实机教训（caxa3ddiffrename）：100 ms 轮询的 3 分钟等待能刷出 60 万字节日志，把关键行埋掉。
        /// </summary>
        public static volatile bool PollQuiet;

        /// <summary>
        /// 设计树遍历的**逐节点** Trace 开关（消息里带 "(walk)" 的那些）。
        /// 默认关：一次替换流程轮询 500ms 一轮、每轮几千个节点，实测单次运行能刷出 4.6 MB / 6.5 万行。
        /// </summary>
        public static volatile bool VerboseWalk = false;

        private static readonly string[] HotPrefixes =
        {
            "ActiveDoc ", "get_SelectionMgr", "GetSelectedElementsZArray", "GetSelectedElements(",
            "IZArray.Count", "IZArray.Get", "IZElement.get_Type", "IZElement.get_Name",
            "IZDoc.get_Name", "GetExternallyLinkedInfo", "SelectionsAvailable"
        };

        private static string _lastPollSnapshot;

        static Diagnostics()
        {
            try { File.Delete(LogPath); } catch { }
        }

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
        /// ② 几百行不转发 UI，否则日志区会被自己刷死；
        /// ③ 不另开旁路文件——正式日志每次启动宿主就清空，旁路文件跨会话累积，两边时间轴对不上等于没有取证。
        /// </summary>
        public static void Dump(string tag, string block)
        {
            string head = "取证[" + tag + "] ↓↓↓ " + DateTime.Now.ToString("HH:mm:ss.fff");
            Append(head);
            Append(block);
            Append("取证[" + tag + "] ↑↑↑ 结束");
        }

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

        /// <summary>细粒度诊断：只写文件，不进 UI（避免刷屏）。轮询期与逐节点走查按开关抑制。</summary>
        public static void Trace(string msg)
        {
            if (PollQuiet && IsHot(msg)) return;
            if (!VerboseWalk && msg.IndexOf("(walk)", StringComparison.Ordinal) >= 0) return;
            Append(DateTime.Now.ToString("HH:mm:ss.fff") + " [tid " +
                Thread.CurrentThread.ManagedThreadId +
                (Thread.CurrentThread.IsBackground ? "/bg" : "/ui") + "] " + msg);
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
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }
    }
}
