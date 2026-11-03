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

        /// <summary>
        /// 轮询热路径开关：长等待（WaitUntil）期间置 true，逐次调用的细粒度 Trace 不落盘，
        /// 只由 LogPoll 记「与上一次不同」的快照行。
        /// 实测一次 100 ms 轮询的 3 分钟等待能刷出 60 万字节日志，把关键行埋掉。
        /// </summary>
        public static volatile bool PollQuiet;

        /// <summary>
        /// 设计树遍历的**逐节点** Trace 开关（消息里带 "(walk)" 的那些）。
        /// 默认关：一次替换流程轮询 500ms 一轮、每轮几千个节点，实测单次运行刷出 4.6 MB / 6.5 万行，
        /// 关键行全被埋掉，回传日志也没法看（2026-11-01 17:38 那轮就是这样）。
        /// 汇总行「设计树定位 xxx：命中（遍历 N 节点）」不受影响，定位问题照样够用。
        /// 真要查遍历细节时把它置 true 再复现一轮。
        /// </summary>
        public static volatile bool VerboseWalk = false;

        private static readonly string[] HotPrefixes =
        {
            "ActiveDoc ", "get_SelectionMgr", "GetSelectedElementsZArray", "GetSelectedElements(",
            "IZArray.Count", "IZArray.Get", "IZElement.get_Type", "IZElement.get_Name",
            "IZDoc.get_Name",   // 19:03 实机：状态灯定时器 800 ms 一次，流程结束后刷了 55 行 hr=0x0
            "GetExternallyLinkedInfo", "SelectionsAvailable"
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
        /// 取证块（控件树 / MSAA 条目清单）整段写进正式日志。
        /// 三点考虑：① 单行 Trace 会被 PollQuiet 吃掉，而这些块正是"点不动"时唯一能定位的东西，
        /// 所以绕过过滤无条件落盘；② 几百行不转发 UI，否则日志区会被自己刷死；
        /// ③ 以前另开一份 `%TEMP%\caxa3dcodenameextraction_uiprobe.txt`，用户回传时经常只给 trace ⇒
        /// 2026-11-02 起并入本文件，取证与判据只有一份东西要发回来。
        /// </summary>
        public static void Dump(string tag, string block)
        {
            string head = "取证[" + tag + "] ↓↓↓ " + DateTime.Now.ToString("HH:mm:ss.fff");
            Append(head);
            Append(block);
            Append("取证[" + tag + "] ↑↑↑ 结束");
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
            if (PollQuiet && IsHot(msg)) return;
            // 逐节点遍历日志默认不落盘（单次运行曾刷到 4.6 MB / 6.5 万行，把关键行全埋了）
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
