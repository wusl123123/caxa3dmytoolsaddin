// 图纸环境 CRX 桥的 C# 客户端（11-06 用户指令 4「刷新工程图」，通道选定原文：「自带 CRX 桥（与参考同构）」）。
// 同构基准 = caxa3dmytoolsaddin\plugins\caxa3dCXDFTViewUpdate\CrxBridge.cs + caxa3dminitool\Bridge（桥接型样板），
// 但**全部标识换成本项目前缀**（CRX 名 caxa3dBatchMasterView、临时文件前缀 caxa3dbatchmaster_view_、窗口类名 …BridgeWnd），
// 因为 %TEMP% 里 caxa3d_viewupdate_* / caxa3dminitool_* / ExbTracker 系列已被别的插件占用，同名互覆盖＝串台（坑 185 同族）。
//
// 为什么必须有 CRX：远程 COM 调图纸文档的 Save/SaveAs/Close 是**假成功**（hr=0 但只写出 xxx.exb.ics，图纸没存、窗口没关，实机已证），
// 保存+关闭必须由 CRX 在图纸进程内投原生命令（CX_save → 等 mtime → WM_CLOSE）。
//
// 协议（与 CRX 侧同一份原文，两边不许各自演化）：
//   请求 caxa3dbatchmaster_view_request.txt ：seq=<int> / action=<命令名> / confirm=yes|no / token=<会话令牌> / arg=<参数>
//   回包 caxa3dbatchmaster_view_response.txt：seq / action / status=OK|ERR / token / message（CRX 只写 OK/ERR）
//   marker caxa3dbatchmaster_view_crx.marker；日志 caxa3dbatchmaster_view_crx.log
//   四个判词不许互顶：OK＝回包 status=OK；ERR＝业务失败（CRX 动了手但没成）；
//   NOCRX＝marker 不在或比本进程启动旧（压根没投递）；TIMEOUT＝投了请求、窗口期内没有对应 seq 的回包（通道无响应）。
//
// 冷启动铁律：CRX 没加载时**不许投递任何会动手的命令**（CLOSEDOC/CLOSERAW/SAVE/EXEC_NATIVE），只有 PING 例外；
//   要动手先走 WarmUpDrawingEnv（唯一入口＝OpenFile2 打开专用空图纸「caxa3d图纸环境预热.exb」→ 等 marker ≤30 s → CLOSERAW 立即不保存关掉）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    /// <summary>一次桥内往来的结论。</summary>
    [ComVisible(false)]
    internal sealed class BridgeReply
    {
        public string Status = "NOCRX";   // OK / ERR / NOCRX / TIMEOUT（四个词互不顶替）
        public int Seq;
        public string Message = "";
        public bool Ok { get { return string.Equals(Status, "OK", StringComparison.OrdinalIgnoreCase); } }
        public string Describe() { return Status + "（seq=" + Seq + "）" + Message; }
    }

    [ComVisible(false)]
    internal static class ViewBridge
    {
        private const string Prefix = "caxa3dbatchmaster_view_";

        public static readonly string RequestPath = Path.Combine(Path.GetTempPath(), Prefix + "request.txt");
        public static readonly string ResponsePath = Path.Combine(Path.GetTempPath(), Prefix + "response.txt");
        public static readonly string MarkerPath = Path.Combine(Path.GetTempPath(), Prefix + "crx.marker");
        public static readonly string LogPath = Path.Combine(Path.GetTempPath(), Prefix + "crx.log");
        /// <summary>运维安全开关（CRX 侧移植自样板）：这个文件在场＝桥停止取件、请求留盘不消费。
        /// 它不是协议的一部分，但它会让「已投递」永远等不到回包，所以判 TIMEOUT 时必须先排除它，
        /// 否则会把运维动作误诊成桥故障。</summary>
        public static readonly string KillPath = Path.Combine(Path.GetTempPath(), Prefix + "crx.kill");

        public static bool KillSwitchPresent()
        {
            try { return File.Exists(KillPath); } catch { return false; }
        }

        /// <summary>本进程启动时刻：marker 必须不早于它才算「本次会话真的加载了」。</summary>
        private static readonly DateTime ProcStart = ProcessStartOf();

        private static readonly object Gate = new object();
        private static int _seq;
        private static string _token = "";
        /// <summary>取到令牌那一刻的时间戳：marker 比它还新 ⇒ CRX 换过实例，旧令牌作废必须重 PING。</summary>
        private static DateTime _tokenAt = DateTime.MinValue;

        private static DateTime ProcessStartOf()
        {
            try { return Process.GetCurrentProcess().StartTime; }
            catch { return DateTime.MinValue; }
        }

        public static string Token { get { return _token; } }

        /// <summary>marker 存在且写入时间晚于本进程启动 ⇒ 桥在本次会话里真的活着（过期遗留探针不算，防误判）。</summary>
        public static bool IsCrxLoaded()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return false;
                return new FileInfo(MarkerPath).LastWriteTime >= ProcStart;
            }
            catch { return false; }
        }

        public static string MarkerState()
        {
            try
            {
                if (!File.Exists(MarkerPath)) return "marker 不存在＝CRX 没加载（或被卸载/被清 %TEMP%）";
                DateTime w = new FileInfo(MarkerPath).LastWriteTime;
                return "marker " + w.ToString("yyyy-MM-dd HH:mm:ss") +
                       (w >= ProcStart ? "（本次会话，有效）" : "（早于本进程启动 " + ProcStart.ToString("HH:mm:ss") + "＝上次会话遗留，按没加载处理）");
            }
            catch (Exception ex) { return "marker 读不到（第三态）：" + ex.Message; }
        }

        /// <summary>CRX 日志当前长度，作为之后增量读判据行的起点。</summary>
        public static long CurrentLogOffset()
        {
            try { return File.Exists(LogPath) ? new FileInfo(LogPath).Length : 0; }
            catch { return 0; }
        }

        /// <summary>从 offset 之后增量读日志（共享读，不阻塞 CRX 追加写）；日志被重建则从头读。</summary>
        public static string ReadLogSince(long offset, out long newOffset)
        {
            newOffset = offset;
            try
            {
                if (!File.Exists(LogPath)) return "";
                using (FileStream fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < offset) offset = 0;
                    fs.Seek(offset, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - offset];
                    int read = fs.Read(buf, 0, buf.Length);
                    newOffset = offset + read;
                    return Encoding.UTF8.GetString(buf, 0, read);   // 末尾半个字符也不会抛（GetString 容错）
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Trace("ViewBridge.ReadLogSince: " + ex.Message);
                return "";
            }
        }

        /// <summary>日志里出现过某个锚点（close-done / commandEnded | CX_DFT_ViewClose / modified=0 等）。</summary>
        public static bool LogHas(long fromOffset, string anchor, int timeoutMs, Action<string> log)
        {
            long off = fromOffset;
            string text = "";
            try
            {
                CaxaAutomation.WaitUntil(delegate()
                {
                    long n;
                    text += ReadLogSince(off, out n);
                    off = n;
                    return text.IndexOf(anchor, StringComparison.OrdinalIgnoreCase) >= 0;
                }, timeoutMs, "等 CRX 日志锚点「" + anchor + "」");
                return true;
            }
            catch (OperationStoppedException) { throw; }
            catch (TimeoutException)
            {
                if (log != null) log("vbridge: 日志里没出现「" + anchor + "」（≤" + timeoutMs + "ms）＝第三态，不能当成没生效");
                return false;
            }
        }

        /// <summary>取某锚点最后一次出现的那一行原文（判据要能引用原文，不能只说「有」）。</summary>
        public static string LastLogLine(long fromOffset, string anchor)
        {
            long n;
            string text = ReadLogSince(fromOffset, out n);
            if (string.IsNullOrEmpty(text)) return "";
            string hit = "";
            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (line.IndexOf(anchor, StringComparison.OrdinalIgnoreCase) >= 0) hit = line;
            return hit;
        }

        // ============================================================ 投递与等回包

        /// <summary>投递一条命令并等它自己的回包（按 seq 认领）。marker 不在＝NOCRX，压根不写请求文件。</summary>
        public static BridgeReply Request(string action, string arg, bool needConfirm, int timeoutMs, Action<string> log)
        {
            var rep = new BridgeReply { Message = "" };
            if (log == null) log = delegate { };

            if (!IsCrxLoaded())
            {
                rep.Status = "NOCRX";
                rep.Message = MarkerState();
                log("vbridge: " + action + " 未投递（NOCRX）—— " + rep.Message);
                return rep;
            }
            if (KillSwitchPresent())
            {
                // 运维开关在场＝桥主动停止取件，投了也只会被留盘空等一个超时 ⇒ 按「压根没投递」处理，别把运维动作误诊成桥故障
                rep.Status = "NOCRX";
                rep.Message = "运维安全开关在场（" + KillPath + "）＝桥停止取件；删掉该文件并重启 CAXA 后再试";
                log("vbridge: " + action + " 未投递（NOCRX）—— " + rep.Message);
                return rep;
            }

            int seq;

            // 破坏性命令必须带有效令牌。令牌由 CRX 每次加载自己生成，只在 WarmUp 里 PING 一次的话，
            // 图纸环境早已加载（本次 09:14:43 自动加载）时压根没有 WarmUp ⇒ _token 为空 ⇒
            // 实机 09:24 的「会话令牌不匹配 got=」把 CX_DFT_ChangeLinks / CLOSEDOC 全部拒掉。
            // 这里在写请求文件前补一次 PING（PING 自身 needConfirm=false，不会递归）。
            // 顺序要求：令牌握手必须先于本条的 seq 分配，免得 PING 拿到本条的号（请求文件只留最后写入的一条）。
            if (needConfirm && !TokenIsFresh())
            {
                if (!EnsureToken(timeoutMs, log))
                {
                    rep.Status = "ERR";
                    rep.Message = "没有可用的会话令牌（PING 握手失败）＝不发破坏性命令";
                    log("vbridge: " + action + " 未投递 —— " + rep.Message);
                    return rep;
                }
            }

            lock (Gate) { seq = ++_seq; }
            rep.Seq = seq;

            // 先清旧回包：留下的任何回包都只可能是本条的（回包完整性由 CRX 侧保证：消费即回、只回一条）
            try { if (File.Exists(ResponsePath)) File.Delete(ResponsePath); }
            catch (Exception ex) { log("vbridge: 旧回包没清掉（继续，按 seq 认领）：" + ex.Message); }

            var sb = new StringBuilder();
            sb.Append("seq=").Append(seq).Append("\r\n");
            sb.Append("action=").Append(action).Append("\r\n");
            sb.Append("confirm=").Append(needConfirm ? "yes" : "no").Append("\r\n");
            sb.Append("token=").Append(_token ?? "").Append("\r\n");
            sb.Append("arg=").Append(arg ?? "").Append("\r\n");
            try { File.WriteAllBytes(RequestPath, Encoding.UTF8.GetBytes(sb.ToString())); }
            catch (Exception ex)
            {
                rep.Status = "TIMEOUT";
                rep.Message = "请求文件写不进去：" + ex.Message;
                log("vbridge: " + rep.Message);
                return rep;
            }
            log("vbridge: 已投递 (seq=" + seq + ") " + action + (string.IsNullOrEmpty(arg) ? "" : " arg=" + arg));

            string body = "";
            try
            {
                CaxaAutomation.WaitUntil(delegate()
                {
                    if (!File.Exists(ResponsePath)) return false;
                    try { body = File.ReadAllText(ResponsePath, Encoding.UTF8); }
                    catch { return false; }        // CRX 正在写，读到半个文件算没到
                    int got = ParseInt(GetField(body, "seq"), -1);
                    if (got != seq) return false;  // 不是本条的回包＝旧残留，继续等（绝不认领错号）
                    return true;
                }, timeoutMs, "等 seq=" + seq + " 的回包");
            }
            catch (OperationStoppedException) { TryPurgeResponse(); throw; }
            catch (TimeoutException)
            {
                rep.Status = "TIMEOUT";
                rep.Message = "投递后 " + timeoutMs + "ms 内没有 seq=" + seq + " 的回包＝通道无响应（不代表命令没生效，去 CRX 日志核对：" + LogPath + "）";
                log("vbridge: " + rep.Message);
                return rep;
            }

            TryPurgeResponse();     // 认领成功即消费掉，避免下一条读到上一条
            rep.Status = GetField(body, "status");
            rep.Message = GetField(body, "message");
            if (string.IsNullOrEmpty(rep.Status)) rep.Status = "ERR";
            if (string.Equals(rep.Status, "ERR", StringComparison.OrdinalIgnoreCase))
                log("vbridge: ERR (seq=" + seq + ") " + rep.Message + "｜CRX 动了手但业务失败，不重试");
            else
                log("vbridge: " + rep.Status + " (seq=" + seq + ") " + rep.Message);
            return rep;
        }

        /// <summary>PING 一次并把会话令牌存下来（破坏性命令要带 token；令牌由 CRX 每次加载自己生成）。</summary>
        public static bool EnsureToken(int timeoutMs, Action<string> log)
        {
            BridgeReply r = Request("PING", "", false, timeoutMs, log);
            if (!r.Ok) { if (log != null) log("vbridge: PING 失败＝桥不可用（" + r.Describe() + "），本轮不投任何会动手的命令"); return false; }
            string tok = ExtractToken(r.Message);
            if (string.IsNullOrEmpty(tok))
            {
                if (log != null) log("vbridge: PING 回了 OK 但 message 里没有 token=（协议不符）＝不能发破坏性命令。原文：" + r.Message);
                return false;
            }
            _token = tok;
            _tokenAt = DateTime.Now;
            if (log != null) log("vbridge: 会话令牌＝" + tok);
            return true;
        }

        /// <summary>令牌可用：非空且 CRX marker 没在取令牌之后被重写（重写＝CRX 换过实例，旧令牌必然被拒）。</summary>
        private static bool TokenIsFresh()
        {
            if (string.IsNullOrEmpty(_token)) return false;
            try
            {
                if (!File.Exists(MarkerPath)) return false;
                return new FileInfo(MarkerPath).LastWriteTime <= _tokenAt;
            }
            catch { return false; }
        }

        private static string ExtractToken(string message)
        {
            if (string.IsNullOrEmpty(message)) return "";
            int i = message.IndexOf("token=", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return "";
            string rest = message.Substring(i + 6);
            int sp = rest.IndexOfAny(new[] { ' ', ';', ',', '\r', '\n' });
            return (sp < 0 ? rest : rest.Substring(0, sp)).Trim();
        }

        private static void TryPurgeResponse()
        {
            try { if (File.Exists(ResponsePath)) File.Delete(ResponsePath); } catch { }
        }

        private static string GetField(string body, string key)
        {
            if (string.IsNullOrEmpty(body)) return "";
            string p = key + "=";
            foreach (string line in body.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return t.Substring(p.Length).Trim();
            }
            return "";
        }

        private static int ParseInt(string s, int def)
        {
            int v;
            return int.TryParse((s ?? "").Trim(), out v) ? v : def;
        }

        // ============================================================ 图纸环境预热（唯一入口）

        /// <summary>专用空图纸 .exb 的定位：输出目录 → 逐级向上 → 开发机基准目录（全仓唯一那份，不拷副本）。</summary>
        public static string LocateWarmupExb()
        {
            const string fileName = "caxa3d图纸环境预热.exb";
            var tried = new List<string>();
            try
            {
                string dir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                for (int up = 0; up < 4 && !string.IsNullOrEmpty(dir); up++)
                {
                    string p = Path.Combine(dir, fileName);
                    tried.Add(p);
                    if (File.Exists(p)) return p;
                    try { dir = Path.GetDirectoryName(dir) ?? ""; } catch { break; }
                }
            }
            catch (Exception ex) { Diagnostics.Trace("LocateWarmupExb EX: " + ex.Message); }
            string dev = Path.Combine(@"C:\code\ICAPI\caxa3dmytoolsaddin", fileName);
            tried.Add(dev);
            if (File.Exists(dev)) return dev;
            Diagnostics.Trace("预热图纸没找到，找过：" + string.Join(" | ", tried.ToArray()));
            return "";
        }

        /// <summary>
        /// 图纸环境预热（规则里的直线一条）：OpenFile2 打开专用空图纸 .exb → 等 marker 新鲜（≤waitMs）→ CLOSERAW 立即不保存关掉。
        /// 绝不用业务 exb 当载体（AV 触发面是内嵌 CRxDbView3D）；失败即判死，不重试、不换第二条入口。
        /// </summary>
        public static bool WarmUpDrawingEnv(IZBaseApp app, int waitMs, Action<string> log)
        {
            if (log == null) log = delegate { };
            if (app == null) { log("vbridge: 预热失败：没有 IZBaseApp。"); return false; }
            string exb = LocateWarmupExb();
            if (string.IsNullOrEmpty(exb))
            {
                log("vbridge: 预热失败：找不到「caxa3d图纸环境预热.exb」（找过输出目录并逐级向上＋开发机基准目录）＝不猜别的载体。");
                return false;
            }
            log("vbridge: 预热开始：OpenFile2 打开专用空图纸 " + exb);
            try
            {
                bool already;
                app.OpenFile2(exb, false, true, out already);   // 槽 30；假失败（异常/空指针但真打开）按已实测坑处理＝不据返回值判成败
                log("vbridge: OpenFile2 返回 alreadyOpen=" + already + "（假失败也继续，判据看 marker）");
            }
            catch (Exception ex)
            {
                log("vbridge: OpenFile2 抛异常（可能是假失败，图纸也许已打开）：" + ex.Message + " → 继续等 marker");
            }

            // 等图纸环境把 CRX 带起来：用 WaitUntil（泵消息＋认停止标志），不裸 Sleep 长等
            try
            {
                CaxaAutomation.WaitUntil(delegate() { return IsCrxLoaded(); }, waitMs, "等 CRX marker 新鲜＝图纸环境就绪");
            }
            catch (OperationStoppedException) { throw; }
            catch (TimeoutException)
            {
                log("vbridge: 预热失败：等 " + waitMs + "ms marker 仍不新鲜（" + MarkerState() + "）＝CRX 没随图纸模块加载，"
                    + "检查安装是否把 .crx 放进 CAXADraft\\Bin 且 CxAutorun.cx 里有那一行。判死，不再试别的入口。");
                return false;
            }
            log("vbridge: marker 新鲜 ⇒ 桥已活。现在 PING 拿令牌，再把预热图纸不保存关掉");
            if (!EnsureToken(10000, log)) return false;
            BridgeReply close = Request("CLOSERAW", Path.GetFileName(exb), true, 30000, log);
            if (!close.Ok) log("vbridge: 预热图纸没关成（" + close.Describe() + "）＝界面可能多出一张「caxa3d图纸环境预热」，请手工关掉；预热本身成功，不影响后续");
            return true;
        }
    }
}
