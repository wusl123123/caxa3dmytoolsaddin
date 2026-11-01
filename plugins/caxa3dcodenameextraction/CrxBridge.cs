using System;
using System.IO;
using System.Text;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 与 CRX 桥接模块（caxa3dcodenameextraction.crx）的通信封装。
    ///
    /// C# → CRX：把命令写进请求文件 %TEMP%\caxa3dcodenameextraction_request.txt，
    ///            CRX 内的 100ms 定时器发现后读取、删除，并把命令投递到当前图纸文档执行。
    /// CRX → C#：CRX 把 commandWillStart/commandEnded 等事件追加到
    ///            %TEMP%\caxa3dcodenameextraction_crx.log，本类按偏移量增量读取，
    ///            用于等待 CX_DFT_ViewClose / close-done 等结果。
    /// </summary>
    internal static class CrxBridge
    {
        public static readonly string RequestPath =
            Path.Combine(Path.GetTempPath(), "caxa3dcodenameextraction_request.txt");
        public static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "caxa3dcodenameextraction_crx.log");
        public static readonly string MarkerPath =
            Path.Combine(Path.GetTempPath(), "caxa3dcodenameextraction_crx.marker");

        /// <summary>CRX 模块是否已加载：探针存在且写入时间晚于本进程启动时间。
        /// 上次会话异常退出遗留的过期探针不算（否则 CRX 尚未加载也误判已加载）。</summary>
        public static bool IsCrxLoaded()
        {
            try
            {
                var fi = new FileInfo(MarkerPath);
                if (!fi.Exists) return false;
                DateTime procStart = System.Diagnostics.Process.GetCurrentProcess().StartTime;
                return fi.LastWriteTime >= procStart;
            }
            catch { return false; }
        }

        /// <summary>当前 CRX 日志长度（作为之后增量读取的起始偏移）。</summary>
        public static long CurrentLogOffset()
        {
            try { return File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0; }
            catch { return 0; }
        }

        /// <summary>
        /// 投递一条图纸环境命令（如 CX_DFT_ViewUpdate）。
        /// 命令文本不含换行，CRX 端执行时自动补 "\n"。
        /// 若上一条请求还没被取走则覆盖（调用方保证串行，不会出现这种情况）。
        /// </summary>
        /// <summary>投递刷新请求。
        /// closeAfter=true → REFRESHCLOSE（批处理：更新+保存+关闭）
        /// closeAfter=false → REFRESH（手工：更新+保存，保留文档）</summary>
        public static void PostRefresh(bool closeAfter)
        {
            PostCommand(closeAfter ? "REFRESHCLOSE" : "REFRESH");
        }

        private static int _seq;
        private static readonly object _seqLock = new object();

        /// <summary>投递命令，返回本条请求的序号。
        /// CRX 侧会回一条 <c>REQ.ack | seq=N cmd=...</c>，调用方必须先等到回执，
        /// 再开始等命令结果——否则两条命令抢同一个请求文件会互相覆盖
        /// （16:13 实例：CMD:CLOSEDOC 被紧随其后的 CMD:CLOSE 覆盖，CRX 根本没看到）。</summary>
        public static int PostCommand(string command)
        {
            int seq;
            lock (_seqLock) { seq = ++_seq; }
            string payload = command + "#seq=" + seq;
            // UTF-8 无 BOM：CRX 端按 UTF-8 解码为宽字符
            File.WriteAllBytes(RequestPath, Encoding.UTF8.GetBytes(payload));
            Diagnostics.Log("CrxBridge: 已投递 (seq=" + seq + ") " + command);
            return seq;
        }

        /// <summary>
        /// 从 offset 之后读取新增日志内容（共享读，不阻塞 CRX 追加写）。
        /// 返回新增文本；通过 out 参数返回新偏移。
        /// </summary>
        public static string ReadLogSince(long offset, out long newOffset)
        {
            newOffset = offset;
            try
            {
                if (!File.Exists(LogPath)) return string.Empty;
                using (FileStream fs = new FileStream(LogPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < offset) offset = 0;   // 日志被重建（CRX 重载清空）
                    fs.Seek(offset, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - offset];
                    int read = fs.Read(buf, 0, buf.Length);
                    newOffset = offset + read;
                    // 容错解码：末尾可能读到半个 UTF-8 字符，ReplaceFallback 不会抛
                    return Encoding.UTF8.GetString(buf, 0, read);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("CrxBridge.ReadLogSince: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>全量扫日志取某命令最后一次执行结果。用于 CRX 刚就绪、
        /// 无有效偏移的场景（如会话首次打开图纸触发模块加载）。</summary>
        public static string FindLastCommandResult(string command)
        {
            try { return FindCommandResult(File.ReadAllText(LogPath), command); }
            catch { return null; }
        }

        public static string FindCommandResult(string logChunk, string command)
        {
            if (string.IsNullOrEmpty(logChunk)) return null;
            string[] lines = logChunk.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                if (line.IndexOf(command, StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("EXEC", StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (line.Contains("EDITOR.commandEnded") &&
                    line.IndexOf(command, StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Ended";
                if (line.Contains("EDITOR.commandCancelled") &&
                    line.IndexOf(command, StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Cancelled";
                if (line.Contains("EDITOR.commandFailed") &&
                    line.IndexOf(command, StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Failed";
                // CRX 投递本身失败（无活动文档等），不可能再等到 commandEnded
                if (line.Contains("EXEC.fail"))
                    return "ExecFail";
            }
            return null;
        }
        /// <summary>CRX 日志已静默 quietMs 毫秒以上（打开图纸后的突发事件已停下来）。
        /// 注意：不能用“CX_DFT_default 是否已结束”判断空闲——它是图纸环境的空闲态命令。</summary>
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
                using (var fs = new FileStream(LogPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
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
                using (var fs = new FileStream(LogPath, FileMode.Open,
                           FileAccess.Read, FileShare.ReadWrite))
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
