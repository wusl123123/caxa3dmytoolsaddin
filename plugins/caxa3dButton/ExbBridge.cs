using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using interop.ICApiIronCAD;   // IZBaseApp / IZDoc / IZDrawingDoc / IZTDExportControl / eZExportDrawingType

namespace caxa3dButton
{
    /// <summary>
    /// C# ↔ CRX 文件桥。CRX **只做「C# ↔ 图纸环境」的联系通道**：
    /// 不自主执行任何命令、不建 UI、不注册命令；C# 只能
    ///   ① 激活 CRX 内**准备好的具名命令**（PING / SCAN_LAYOUTS /
    ///      DELETE_LAYOUTS_EXCEPT_MODEL / UPDATE_3DBOM / EXPORT_DWGDXF / CONVERT_DXXF /
    ///      CHANGE_LINKS / TITLEBLOCK_3DSET / CLOSEDOC）；
    ///   ② 经 EXEC_NATIVE **注入一条 CRX 认可（白名单前缀）的宿主命令**。
    ///
    /// 协议（%TEMP%）：
    ///   请求 request.txt : seq=N / action=<命令名> / confirm=yes|no / token=<会话令牌> / arg=<参数>
    ///   响应 response.txt: seq=N / status=OK|ERR|SCAN|PONG / papers=a|b|c / model=模型 /
    ///                      deleted=N / token=<会话令牌> / message=...
    ///
    /// 关键点（与规则/历史坑对齐）：
    ///   · **会话令牌**：CRX 每次加载都会重新生成，破坏性命令必须回填；这是"不自动执行命令"
    ///     的硬保证 —— 上会话残留的 confirm=yes 请求即使没被清掉，令牌也对不上。
    ///   · 响应文件**整文件读**，绝不按字节偏移增量读（坑 76）。
    ///   · 请求用 .tmp 写好再 Move 原子替换，避免 CRX 半读。
    ///   · seq 单调递增；CRX 只处理 seq > 已处理 的请求，过期即丢弃，天然幂等。
    ///   · 等待响应在**后台线程**上做（Thread.Sleep 不能堵 CAXA 主线程，否则 CRX 的 WM_TIMER
    ///     轮询无法触发、会死锁），结果通过 BeginInvoke 回 UI。
    /// </summary>
    internal static class ExbBridge
    {
        private static readonly string ReqPath =
            Path.Combine(Path.GetTempPath(), "caxa3dButton_request.txt");
        private static readonly string RespPath =
            Path.Combine(Path.GetTempPath(), "caxa3dButton_response.txt");

        /// <summary>CRX 在 On_kInitAppMsg 写、On_kUnloadAppMsg 删的就绪探针。</summary>
        public static readonly string MarkerPath =
            Path.Combine(Path.GetTempPath(), "caxa3dButton_crx.marker");

        /// <summary>
        /// 本版 CRX **随图纸模块自动加载**（登记 CxAutorun.cx，对齐产线 caxa3ddiffrename /
        /// caxa3dCXDFTViewUpdate）。判据照抄 caxa3ddiffrename：marker 存在且写入时间 >=
        /// 本进程启动时间，避免上次会话异常退出遗留的过期探针造成误判。
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

        public const string LoadHint =
            "图纸命令桥未加载：CRX 会随图纸模块（CAXADraft）初始化自动加载，" +
            "请在 CAXA 里打开任意一张图纸（进入图纸环境）后重试；" +
            "若仍不行，查 %TEMP%\\caxa3dButton_crx.log 是否含 MOD: On_kInitAppMsg 行。";

        //-----------------------------------------------------------------
        // 「模型」布局名判定 —— CRX 侧 IsModelPaperName 的 C# 镜像（双保险）
        //
        // 实机取证（2026-11-04）：中文版 CAXA 3D 2027 的 PaperTable 里，
        // 「模型」的**内部名是英文小写 `model`**，不是中文「模型」。
        // 早期只按中文比较 ⇒ 过滤失效 ⇒ model 被当普通图幅删除 ⇒ 图纸环境直接消失。
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

        /// <summary>
        /// 等待响应期间的消息泵（可插）：批量转换等 UI 线程调用方注入 Application.DoEvents，
        /// 防止 Thread.Sleep 堵住宿主消息泵；后台线程调用方保持 null（纯 Thread.Sleep）。
        /// 调用方负责用完置回 null。
        /// </summary>
        public static Action PumpDuringWait;

        /// <summary>本次 CRX 加载生成的会话令牌（握手后缓存；CRX 重载后需重新握手）。</summary>
        private static string _token;

        /// <summary>丢弃缓存的令牌（CRX 重载 / 卸载后调用）。</summary>
        public static void ResetSession() { _token = null; }

        public static BridgeResponse RequestScan()
        {
            return Exec("SCAN_LAYOUTS", false, null, false);
        }

        public static BridgeResponse RequestDelete()
        {
            return Exec("DELETE_LAYOUTS_EXCEPT_MODEL", true, null, true);
        }

        /// <summary>
        /// 注入一条「CRX 认可的命令」走宿主命令表（白名单前缀由 CRX 侧把关，
        /// 默认 CX_DFT_ / CX_）。注意 sendStringToExecute 是异步投递：
        /// 返回 OK 只代表"已投出"，不代表"已执行完"。
        /// </summary>
        public static BridgeResponse RequestExecNative(string command)
        {
            return Exec("EXEC_NATIVE", true, command, true);
        }

        /// <summary>
        /// 「更新3dbom」：让 CRX 在图纸上下文确认目标 exb 已开完（活动图纸 + 图幅表可打开），
        /// 然后对**那张图纸**推送更新 3D 明细的命令（`CX_DFT_Update3DBom`）。
        /// </summary>
        /// <param name="exbPath">
        /// 设计环境分支传同名 exb 的**全路径**（CRX 按它认图纸，认不到就报错、不瞎推）；
        /// 图纸环境分支传 null/空，表示"就用当前前台那张图纸"。
        /// </param>
        /// <remarks>
        /// 会改图纸数据，所以按破坏性命令对待：confirm=yes + 会话令牌（CRX 侧独立复核）。
        /// </remarks>
        public static BridgeResponse RequestUpdate3DBom(string exbPath)
        {
            return Exec("UPDATE_3DBOM", true, exbPath, true);
        }

        /// <summary>
        /// 「更新工程图链接文件」：让 CRX 确认目标 exb 已开完，然后对那张图纸推送
        /// CAXA 原生命令 `CX_DFT_ChangeLinks`（「改变链接文件」）。对话框的逐行代点
        /// 由 PopupAutoYes + ChangeLinksProbe 在后台线程负责；返回 OK 只代表已投出。
        /// </summary>
        public static BridgeResponse RequestChangeLinks(string exbPath)
        {
            return Exec("CHANGE_LINKS", true, exbPath, true);
        }

        /// <summary>
        /// 「批量格式转换」：让 CRX 在图纸环境把目标 exb 的数据层内容另存为 DWG/DXF
        /// （CRxDbDatabase::saveAs，纯数据层写盘：不经 UI、不改 exb 源文件）。
        /// arg = &lt;exb 全路径&gt;|&lt;输出全路径&gt;|&lt;dwgVer 整型&gt;（CRxDb::CRxDbDwgVersion，
        /// 输出格式由扩展名决定：.dwg / .dxf）。
        /// 回包 OK 只代表 saveAs 返回 eOk；判成功以输出文件存在且&gt;0 为准（C# 侧复核）。
        /// </summary>
        public static BridgeResponse RequestExportDwgDxf(string exbPath, string outPath, int dwgVer)
        {
            // 导出前会在 CRX 侧做「更新工程图」预刷新 + 最多 4 次 saveAs 重试（全新打开的 exb 需时间就绪），
            // 全程可能 10~30s；给 60s 且须大于 CRX 内部耗时（CRX 内部 < C# 侧，防误报「CRX 未回应」）。
            return Exec("EXPORT_DWGDXF", true, exbPath + "|" + outPath + "|" + dwgVer.ToString(), true, 60000);
        }

        /// <summary>
        /// 「转换成dxf」：让 CRX 在图纸环境经文档 IDispatch 取 TDExportControl.Export，
        /// 用**图纸环境自己的导出 API** 把目标 exb 转成 dxf（与 EXPORT_DWGDXF 数据层 saveAs 是两条不同通道）。
        /// arg = &lt;exb 全路径&gt;|&lt;输出全路径&gt;|&lt;eZExportDrawingType 整型&gt;。
        /// </summary>
        public static BridgeResponse RequestConvertDxf(string exbPath, string outPath, int dxfType)
        {
            // 大图导出（含 PDF 打印）慢，给 180 s；CRX 内部是同步 Export，超时会回 TIMEOUT
            return Exec("CONVERT_DXF", true, exbPath + "|" + outPath + "|" + dxfType.ToString(), true, 180000);
        }

        // eZExportDrawingType 实测值（_probe-tools\out\tlb\2027\enums.csv:683-712）
        public const int Z_TD_EXPORT_PDF       = 19;
        public const int Z_TD_EXPORT_DWG_R2018 = 28;
        public const int Z_TD_EXPORT_DXF_R2018 = 29;

        // RequestUpdateViews / RequestCmdState / WaitCmdEnded 已于 2026-11-11 12:51 删除：
        //   调用方（另存为dwg-v2、批量转换的 UPDATE_VIEWS 步）全部迁到成熟代点方案后无引用。
        //   CRX 侧 UPDATE_VIEWS / CMD_STATE 命令保留在册（可 EXEC 级使用）。

        /// <summary>
        /// 「图纸环境侧打开 exb」：CRX 内 appContextOpenDocument / activateDocument，
        /// ★ 不碰 caxa3d(Scene) 侧 IZBaseApp.OpenFile2 —— exb 打开一律在 Draft 进程完成。
        /// 已打开则只激活并等空闲；新打开则泵消息等文档在册。回包 OK = 图纸已就绪可做后续操作。
        /// </summary>
        public static BridgeResponse RequestOpenExb(string exbPath)
        {
            // 打开 exb 可能伴随「3D 视图数据源更新」（兄弟工程实测可长达数十秒），
            // 且 CRX 内部三通道兜底最多 20s + 等空闲 8s ⇒ 给 60s，
            // 绝不复用默认 30s，否则 CRX 刚写回包 C# 就先超时、误报「CRX 未回应」。
            return Exec("OPEN_EXB", true, exbPath, true, 60000);
        }

        /// <summary>
        /// 在**后台线程**里等 CRX 桥就绪（冷态时图纸模块/CRX 比 CAXA 主进程晚十几秒加载）。
        /// ★ 绝不堵 UI 线程；★ 等不到就明确报错，绝不回退到 caxa3d 侧 API 去开 exb。
        /// </summary>
        public static bool WaitCrxReady(int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (IsCrxLoaded()) return true;
                System.Threading.Thread.Sleep(300);
            }
            return IsCrxLoaded();
        }

        // （ResolveDxfExportType 与其 DxfVersionPriority 表已删除：另存为dxf 改走 CRX 桥 readExbFile+saveAs，
        //  C# 侧不再需要解析 eZExportDrawingType；批量转换仍由 DlgBatchConvert 走 RequestConvertDxf 桥命令）

        /// <summary>标题栏 3D 信息设默认：CRX 推 CX_DFT_TITLEBLOCK_3DSET（对话框由 Dlg3DInfo 代填）。</summary>
        public static BridgeResponse RequestTitleBlock3DSet(string exbPath)
        {
            return Exec("TITLEBLOCK_3DSET", true, exbPath, true);
        }

        /// <summary>保存并关闭图纸：CRX 按文件名匹配 MDI 子窗口，先原生保存链再 WM_CLOSE。</summary>
        public static BridgeResponse RequestCloseDoc(string fileName)
        {
            return Exec("CLOSEDOC", true, fileName, true);
        }

        /// <summary>
        /// 只读状态：target=1 目标图纸子窗口还在、all=本进程 MDI 子窗口数、q=1 Draft 空闲。
        /// 关前静默门限与「关后窗口是否真的消失」都以它为准（不拿 CLOSEDOC 的 saved=/closed= 自报背书）。
        /// </summary>
        public static BridgeResponse RequestDftState(string fileName)
        {
            return Exec("DFT_STATE", false, fileName, false);
        }

        /// <summary>取 DFT_STATE 回包里 target/all/q 中的布尔位；取不到一律当 false（第三态不装成功）。</summary>
        public static bool DftStateFlag(BridgeResponse r, string key)
        {
            if (r == null || r.Status != "OK" || string.IsNullOrEmpty(r.Message)) return false;
            foreach (string part in r.Message.Split('|'))
            {
                if (part.StartsWith(key + "=")) return part.Substring(key.Length + 1) == "1";
            }
            return false;
        }

        /// <summary>目标图纸的 MDI 子窗口是否还在（关后回查用；NOCRX/TIMEOUT 也算"不确定"，按还在处理）。</summary>
        public static bool IsDrawingOpen(string fileName)
        {
            BridgeResponse r = RequestDftState(fileName);
            if (r == null || r.Status != "OK") return true;
            return DftStateFlag(r, "target");
        }

        /// <summary>握手：确认桥是本次加载的实例，并拿到会话令牌。</summary>
        public static BridgeResponse Handshake()
        {
            var r = Exec("PING", false, null, false);
            if (r != null && r.Status == "PONG" && !string.IsNullOrEmpty(r.Token))
                _token = r.Token;
            return r;
        }

        private static BridgeResponse Exec(string action, bool confirm, string arg, bool requireSession,
                                           int timeoutMs = 30000)
        {
            // 桥没起来就别发请求：否则只能空等 30 秒超时（CRX 随图纸模块自动加载）。
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
                              "请确认图纸环境已打开、%TEMP%\\caxa3dButton_crx.log 有本次 On_kInitAppMsg。"
                };
            }

            var r = SendOnce(action, confirm, arg, timeoutMs);

            // 令牌失效（CRX 在两次点击之间被重新加载过，每次加载都会换新令牌）：
            // 自动重新握手 + 重发一次，用户不必"再点一次"。
            if (r != null && r.Status == "ERR" &&
                (r.Message ?? "").IndexOf("令牌", StringComparison.Ordinal) >= 0)
            {
                _token = null;
                var pong = SendOnce("PING", false, null, 30000);
                if (pong != null && pong.Status == "PONG" && !string.IsNullOrEmpty(pong.Token))
                {
                    _token = pong.Token;
                    r = SendOnce(action, confirm, arg, timeoutMs);
                }
            }
            return r;
        }

        private static BridgeResponse SendOnce(string action, bool confirm, string arg,
                                               int timeoutMs = 30000)
        {
            int seq = Interlocked.Increment(ref _seq);
            WriteRequest(seq, action, confirm, arg);
            return WaitResponse(seq, timeoutMs);
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
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    if (File.Exists(RespPath))
                    {
                        var map = Parse(File.ReadAllText(RespPath, new UTF8Encoding(false)));
                        if (map.TryGetValue("seq", out var s) && s == want)
                        {
                            try { File.Delete(RespPath); } catch { }
                            return ToResponse(map);
                        }
                    }
                }
                catch { /* 文件被 CRX 半写，下一轮再试 */ }
                if (PumpDuringWait != null)
                {
                    try { PumpDuringWait(); } catch { }
                    Thread.Sleep(30);
                }
                else Thread.Sleep(300);
            }
            return new BridgeResponse
            {
                Status = "TIMEOUT",
                Message = "CRX 未在 " + (timeoutMs / 1000) +
                          " 秒内回应（请确认：已打开/预热图纸环境、有前台图纸、且 CRX 已加载；详见 %TEMP%\\caxa3dButton_crx.log）"
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
                Status = map.TryGetValue("status", out var s) ? s : "",
                Model = map.TryGetValue("model", out var m) ? m : "",
                Message = map.TryGetValue("message", out var msg) ? msg : "",
                Doc = map.TryGetValue("doc", out var dc) ? dc : "",
                Token = map.TryGetValue("token", out var tk) ? tk : ""
            };
            if (map.TryGetValue("deleted", out var d) && int.TryParse(d, out var dn)) r.Deleted = dn;
            if (map.TryGetValue("papers", out var p) && !string.IsNullOrEmpty(p))
                r.Papers.AddRange(p.Split('|'));
            return r;
        }

        // （TryExportDrawing 已删除：C# 侧 RCW 走 IZBaseApp→IZDrawingDoc→IZTDExportControl.Export
        //  在跨进程 Scene/Draft 边界上拿不到 Draft 进程内的 IZDrawingDoc（实机 20:45 恒 QI 失败），属死路；
        //  另存为dxf 现统一由 CRX 桥的 EXPORT_DWGDXF 命令在图纸环境内 readExbFile+saveAs 完成）
    }

    internal sealed class BridgeResponse
    {
        public string Status = "";          // OK / ERR / SCAN / PONG / TIMEOUT / NOCRX
        public List<string> Papers = new List<string>();
        public string Model = "";
        public int Deleted;
        public string Message = "";
        public string Doc = "";             // 本命令作用的图纸文档全路径（UPDATE_3DBOM 回包）
        public string Token = "";           // CRX 本次加载的会话令牌（PING 回包携带）
    }
}
