// CRX 桥接封装 —— 机械复刻参考工程 caxa3dcodenameextraction.CrxBridge 的契约（PostCommand 返回 seq、
// LogCount / EventSince / LogQuiet / CurrentLogOffset / IsCrxLoaded / ReadLogSince），
// 但底层走本工程的 ViewBridge（EXEC_NATIVE / CLOSEDOC / CLOSERAW / PING … 协议），而非参考的请求文件格式。
//
// 关键不变量：PostCommand 返回的 seq 必须是 CRX 在日志里实际回的 seq。
// 本工程 CRX 收到请求后会 log "REQ.ack | seq=N action=..."，而 ViewBridge.Request 内部用同一套 _seq 计数器、
// 回包也按 seq 认领，因此 rep.Seq 就是 CRX 实际 seq ⇒ 直接返回，SheetMetalOps 等“等 REQ.ack | seq=”的判定即可命中。
//
// LogCount / EventSince / LogQuiet 逐字照搬参考实现，仅 LogPath 指向 ViewBridge.LogPath（两者本就是同一文件）。
using System;
using System.IO;
using System.Text;

namespace caxa3dBatchMaster
{
    internal static class CrxBridge
    {
        public static readonly string RequestPath = ViewBridge.RequestPath;
        public static readonly string LogPath = ViewBridge.LogPath;
        public static readonly string MarkerPath = ViewBridge.MarkerPath;

        public static bool IsCrxLoaded() => ViewBridge.IsCrxLoaded();
        public static long CurrentLogOffset() => ViewBridge.CurrentLogOffset();

        public static string ReadLogSince(long offset, out long newOffset)
        {
            return ViewBridge.ReadLogSince(offset, out newOffset);
        }

        /// <summary>投递命令（"CMD:VERB[|ARG]" 格式，兼容参考调用方）。返回 CRX 实际 seq（供“REQ.ack | seq=”判定）。</summary>
        public static int PostCommand(string command)
        {
            if (string.IsNullOrEmpty(command)) return 0;
            // 本工程 CRX 的 needsToken 命令要带会话令牌（参考 CrxBridge 协议没有令牌闸）。
            // 首次投递前惰性 PING 握手取 token，否则 TITLEBLOCK_3DSET / CLOSEDOC 会被 CRX 拒成 ERR。
            if (string.IsNullOrEmpty(ViewBridge.Token)) ViewBridge.EnsureToken(15000, null);
            string verb = command, arg = "";
            if (command.StartsWith("CMD:", StringComparison.OrdinalIgnoreCase))
                verb = command.Substring(4);
            int idx = verb.IndexOf('|');
            if (idx >= 0) { arg = verb.Substring(idx + 1); verb = verb.Substring(0, idx); }
            bool needConfirm = string.Equals(verb, "CLOSEDOC", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(verb, "CLOSERAW", StringComparison.OrdinalIgnoreCase);
            BridgeReply rep = ViewBridge.Request(verb, arg, needConfirm, 30000, null);
            return rep.Seq;
        }

        /// <summary>CRX 日志已静默 quietMs 毫秒以上（打开图纸后的突发事件已停下来）。</summary>
        public static bool LogQuiet(int quietMs)
        {
            try
            {
                if (!File.Exists(LogPath)) return true;
                return (DateTime.Now - File.GetLastWriteTime(LogPath)).TotalMilliseconds >= quietMs;
            }
            catch { return true; }
        }

        /// <summary>自 since 起（含 1s 容差）是否出现过某标记——用于判定「本次打开」的
        /// CX_open_Impl 已结束，不受上一次运行残留日志影响。</summary>
        public static bool EventSince(string marker, DateTime since)
        {
            try
            {
                if (!File.Exists(LogPath)) return false;
                string text;
                using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();
                TimeSpan startT = since.TimeOfDay;
                int i = 0;
                while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
                {
                    int lineStart = text.LastIndexOf('\n', i) + 1;
                    TimeSpan ts;
                    if (i - lineStart >= 8 &&
                        TimeSpan.TryParseExact(text.Substring(lineStart, 8), @"hh\:mm\:ss",
                            System.Globalization.CultureInfo.InvariantCulture, out ts)
                        && ts >= startT - TimeSpan.FromSeconds(1))
                        return true;
                    i += marker.Length;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>CRX 日志中某标记出现次数（用于等待 close-done 等）。</summary>
        public static int LogCount(string marker)
        {
            try
            {
                if (!File.Exists(LogPath)) return 0;
                string text;
                using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                    text = sr.ReadToEnd();
                int n = 0, i = 0;
                while ((i = text.IndexOf(marker, i, StringComparison.Ordinal)) >= 0)
                { n++; i += marker.Length; }
                return n;
            }
            catch { return 0; }
        }
    }
}
