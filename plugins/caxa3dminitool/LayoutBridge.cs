using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace caxa3dminitool
{
    /// <summary>
    /// C# ↔ CRX 文件桥（集合体子插件版的通道）。
    ///
    /// 来源：独立 Add-in `C:\code\ICAPI\caxa3dminitool\LayoutBridge.cs` 移植而来。
    ///
    /// ★ 与独立版**必须是两套文件**：本机同时装着独立版（`MiniToolBridge.crx`，临时文件
    ///   `caxa3dminitool_*.txt`）与集合体版（`caxa3dminitool.crx`，临时文件
    ///   `caxa3dminitool_sub_*.txt`）。前缀一旦撞车，两个桥会互相取走对方的请求 →
    ///   表现为「删了不该删的/等 30 秒超时」，所以临时文件名、marker、日志全部独立。
    ///
    /// 协议（%TEMP%）：caxa3dminitool_sub_request.txt / caxa3dminitool_sub_response.txt
    ///   请求: seq=N / action=命令名 / confirm=yes|no / token=会话令牌 / arg=参数
    ///   响应: seq=N / status=OK|ERR|SCAN|PONG / papers=a|b / model=model / deleted=N / token=.. / message=..
    ///
    /// 硬约束（每条都对应一次踩过的坑）：
    ///   · 响应文件**整文件读**，绝不按字节偏移增量读（坑 76）；
    ///   · 请求用 .tmp 写好再 Move 原子替换，避免 CRX 半读；
    ///   · seq 单调递增，CRX 只处理 seq > 已处理 的请求，过期即丢弃，天然幂等；
    ///   · ★ 等待走**主线程消息泵**（`MiniToolsForm.WaitUntil`：DoEvents + 50 ms），
    ///     不能裸 `Thread.Sleep`：集合体的长流程就在 CAXA 的 UI 线程上跑，
    ///     堵住消息泵 → CRX 的 WM_TIMER 取件饿死 → 必然 30 秒超时（坑 24）；
    ///   · 破坏性命令要带 confirm=yes **且**回填本次加载的会话令牌（令牌每次加载都变）。
    /// </summary>
    internal static class LayoutBridge
    {
        private static readonly string ReqPath =
            Path.Combine(Path.GetTempPath(), "caxa3dminitool_sub_request.txt");
        private static readonly string RespPath =
            Path.Combine(Path.GetTempPath(), "caxa3dminitool_sub_response.txt");

        /// <summary>CRX 在 On_kInitAppMsg 写、On_kUnloadAppMsg 删的就绪探针。</summary>
        public static readonly string MarkerPath =
            Path.Combine(Path.GetTempPath(), "caxa3dminitool_sub_crx.marker");

        /// <summary>CRX 日志（复盘首选）。</summary>
        public static readonly string CrxLogPath =
            Path.Combine(Path.GetTempPath(), "caxa3dminitool_sub_crx.log");

        public const string LoadHint =
            "图纸命令桥未加载：集合体版 CRX（caxa3dminitool.crx）随图纸模块（CAXADraft）初始化自动加载，" +
            "请先在 CAXA 里打开任意一张图纸（进入图纸环境）后重试；" +
            "若仍不行，查 %TEMP%\\caxa3dminitool_sub_crx.log 是否含 MOD: On_kInitAppMsg 行" +
            "（并确认 deploy.ps1 的 [3b/5] 已把 crx 落到 CAXADraft\\Bin 且 CxAutorun.cx 有登记行）。";

        /// <summary>
        /// 桥是否就绪：marker 存在且写入时间 >= 本进程启动时间。
        /// 只看"文件在"会把上次会话异常退出遗留的过期 marker 当成就绪（caxa3ddiffrename 口径）。
        /// </summary>
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

        //-----------------------------------------------------------------
        // 「模型」布局名判定 —— CRX 侧 IsModelPaperName 的 C# 镜像（双保险）
        //
        // 实机取证：中文版 CAXA 3D 的 PaperTable 里「模型」的**内部名是英文小写 model**。
        // 早期只按中文「模型」比较 ⇒ 过滤失效 ⇒ 模型空间被当普通图幅删掉 ⇒ 图纸环境窗口消失。
        //-----------------------------------------------------------------
        public static bool IsModelLayoutName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return true;      // 空名一律当模型，宁可不删
            string s = raw.Trim().TrimStart('*').Trim();
            if (s.Length == 0) return true;
            string low = s.ToLowerInvariant();
            if (low == "model") return true;                 // ★ 实机取证
            if (low == "model_space") return true;
            if (low.StartsWith("model_space")) return true;
            if (low == "模型") return true;
            if (low.StartsWith("模型空间")) return true;
            return false;
        }

        private static int _seq;

        /// <summary>本次 CRX 加载生成的会话令牌（握手后缓存；CRX 重载后需重新握手）。</summary>
        private static string _token;

        public static void ResetSession() { _token = null; }

        /// <summary>握手：确认桥是本次加载的实例，并拿到会话令牌。</summary>
        public static BridgeResponse Handshake()
        {
            var r = Exec("PING", false, null, false);
            if (r != null && r.Status == "PONG" && !string.IsNullOrEmpty(r.Token))
                _token = r.Token;
            return r;
        }

        public static BridgeResponse RequestScan() { return Exec("SCAN_LAYOUTS", false, null, false); }

        public static BridgeResponse RequestDelete()
        {
            return Exec("DELETE_LAYOUTS_EXCEPT_MODEL", true, null, true);
        }

        /// <summary>注入一条 CRX 认可（白名单前缀）的宿主命令。异步投递，返回 OK 只代表"已投出"。</summary>
        public static BridgeResponse RequestExecNative(string command)
        {
            return Exec("EXEC_NATIVE", true, command, true);
        }

        private static BridgeResponse Exec(string action, bool confirm, string arg, bool requireSession)
        {
            if (!IsCrxLoaded())
            {
                _token = null;
                return new BridgeResponse { Status = "NOCRX", Message = LoadHint };
            }

            // 令牌过期（CRX 重载过）就重新握手；缓存的令牌只对本次加载有效。
            if (_token == null)
            {
                var pong = SendOnce("PING", false, null);
                if (pong != null && pong.Status == "PONG") _token = pong.Token;
            }
            if (requireSession && string.IsNullOrEmpty(_token))
            {
                return new BridgeResponse
                {
                    Status = "ERR",
                    Message = "与 CRX 握手失败，未取得本次会话令牌，已拒绝发送破坏性命令。" +
                              "请确认图纸环境已打开、%TEMP%\\caxa3dminitool_crx.log 有本次 On_kInitAppMsg。"
                };
            }

            var r = SendOnce(action, confirm, arg);

            // 令牌失效（两次点击之间 CRX 被重新加载过）→ 自动重握手 + 重发一次
            if (r != null && r.Status == "ERR" &&
                (r.Message ?? "").IndexOf("令牌", StringComparison.Ordinal) >= 0)
            {
                _token = null;
                var pong = SendOnce("PING", false, null);
                if (pong != null && pong.Status == "PONG" && !string.IsNullOrEmpty(pong.Token))
                {
                    _token = pong.Token;
                    r = SendOnce(action, confirm, arg);
                }
            }
            return r;
        }

        private static BridgeResponse SendOnce(string action, bool confirm, string arg)
        {
            int seq = Interlocked.Increment(ref _seq);
            WriteRequest(seq, action, confirm, arg);
            return WaitResponse(seq, 30000);
        }

        private static void WriteRequest(int seq, string action, bool confirm, string arg)
        {
            string tmp = ReqPath + ".tmp";
            var lines = new List<string>
            {
                "seq=" + seq.ToString("D10"),
                "action=" + action,
                "confirm=" + (confirm ? "yes" : "no"),
                "token=" + (_token ?? "")
            };
            if (!string.IsNullOrEmpty(arg)) lines.Add("arg=" + arg);
            File.WriteAllText(tmp, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            if (File.Exists(ReqPath)) File.Delete(ReqPath);
            File.Move(tmp, ReqPath);
        }

        private static BridgeResponse WaitResponse(int seq, int timeoutMs)
        {
            string want = seq.ToString("D10");
            BridgeResponse hit = null;

            // 主线程消息泵等待（不是裸 Thread.Sleep）：CRX 的取件定时器是宿主 UI 线程的
            // WM_TIMER，主线程不泵消息它就永远读不到请求。WaitUntil 每 50 ms DoEvents 一次，
            // 顺带 Touch 心跳并能响应「停止」。
            MiniToolsForm.WaitUntil("CRX 回包", timeoutMs, delegate
            {
                try
                {
                    if (!File.Exists(RespPath)) return false;
                    var map = Parse(File.ReadAllText(RespPath, new UTF8Encoding(false)));
                    if (!map.TryGetValue("seq", out string s) || s != want) return false;
                    hit = ToResponse(map);
                    try { File.Delete(RespPath); } catch { }
                    return true;
                }
                catch { return false; }   // 文件被 CRX 半写，下一轮再试
            }, 50);

            if (hit != null) return hit;
            return new BridgeResponse
            {
                Status = "TIMEOUT",
                Message = "CRX 未在 " + (timeoutMs / 1000) + " 秒内回应（请确认已进入图纸环境、有前台图纸、" +
                          "caxa3dminitool.crx 已加载；详见 %TEMP%\\caxa3dminitool_sub_crx.log）"
            };
        }

        private static Dictionary<string, string> Parse(string text)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = raw.IndexOf('=');
                if (eq < 0) continue;
                map[raw.Substring(0, eq).Trim()] = raw.Substring(eq + 1).Trim();
            }
            return map;
        }

        private static BridgeResponse ToResponse(Dictionary<string, string> map)
        {
            var r = new BridgeResponse
            {
                Status = map.TryGetValue("status", out string s) ? s : "",
                Model = map.TryGetValue("model", out string m) ? m : "",
                Message = map.TryGetValue("message", out string msg) ? msg : "",
                Token = map.TryGetValue("token", out string tk) ? tk : ""
            };
            if (map.TryGetValue("deleted", out string d) && int.TryParse(d, out int dn)) r.Deleted = dn;
            if (map.TryGetValue("papers", out string p) && !string.IsNullOrEmpty(p))
                r.Papers.AddRange(p.Split('|'));
            return r;
        }
    }

    internal sealed class BridgeResponse
    {
        public string Status = "";          // OK / ERR / SCAN / PONG / TIMEOUT / NOCRX
        public List<string> Papers = new List<string>();
        public string Model = "";
        public int Deleted;
        public string Message = "";
        public string Token = "";           // CRX 本次加载的会话令牌（PING 回包携带）
    }
}
