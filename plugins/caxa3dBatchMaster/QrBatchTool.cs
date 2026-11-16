// 「工程图附加二维码」业务主体（2026-11-15 移植自 caxa3dqrcode\QrCodeTool.cs，11-15 实机整链 verified）。
//
// 与 caxa3dqrcode 的差异（数据源换轴，链路本体不动）：
//   · 目标来源＝主窗体表格「列表识别的行」（D 列零件名非空的数据行），不是场景选区；
//   · 属性组合＝模板字段查表头同名列（H 列起，与「分类打包」同口径），值取表格行，
//     不开 ics、不做 COM 属性读取（表格列即「属性批量读取」落盘的共享表值）；
//   · exb 定位＝C 列目录 + D/F 列零件名（同名 .exb，与「分类打包」复制口径一致），
//     不再用 ActiveDoc 文件夹推断。
//
// 链路（每行一次，全部沿用 caxa3dqrcode 实机定案）：
//   表格行属性 → QRCoder 制作二维码 PNG → 打开该行同名 exb（OpenFile2）
//   → 定窗名（句柄差集，基线在投递前采，坑 204）→ 开图就绪/稳定闸 → 等 CRX marker
//   → 前置 → LISTENTS 实体类名取证 → GETSCALE 读图纸比例（第三态回落兜底 1:1，绝不瞎折算）
//   → 定位规则按图幅匹配（命中 = 规则坐标 × 比例分母；未命中回落 0,0）
//   → 比例补偿：落图目标 mm = 需求成品 mm × 比例分母；宿主比例因子 = 落图 ÷ PNG@96DPI
//   → DELETE_QR 先删目标点上的旧二维码（deleted=0 = 正常首插，不阻断）
//   → BITMAP_NATIVE 投宿主 CX_DFT_InsertBitmap + NativeBitmapInsert 后台代点
//   → QR_STATE delta>0（唯一权威）→ SAVE 落盘（mtime/长度变化）→ 可选 CLOSERAW 关图。
//
// 三态纪律：属性组合（表格列命中数）、二维码制作（PNG 落盘 > 0 字节）、exb 插入
//   （delta>0 + SAVE 后文件变化）各记各的，绝不互相背书；失败行如实计入 CSV，不重试。
//
// TLB 为什么走不通（通道阶梯说明，移植时随行）：
//   ① 图纸文档：ActiveDoc 恒回 3D 场景文档、四个 ICAPI 枚举入口一律看不见 exb（坑 158/159）；
//   ② ICAPI 没有"往 exb 插入图片/实体"的 API ⇒ 只能 C# 定窗前置 + CRX 权威执行（规则第 11/34 节）；
//   ③ 图纸比例/图幅：IZDrawingDoc 无该读取入口，CRX PaperTable.CurrentPaper 是唯一实证通道。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    internal sealed class QrRunResult
    {
        public bool PropsOk;          // 表格列属性命中 ≥1（verified）/ 全空（attempt）
        public bool InsertVerified;   // delta>0 且 SAVE 落盘变化
        public string Payload = "";
        public string PngPath = "";
        public string ExbPath = "";
        public double ScaleDenom;     // 生效的比例分母（GETSCALE 识别或兜底）
        public string InsertPoint = ""; // 生效插入点（规则命中或兜底）
        public string Summary = "";
    }

    [ComVisible(false)]
    internal static class QrBatchTool
    {
        private const string Tag = "qr";

        // ---------------------------------------------------------------- 主流程（一行一次）

        public static QrRunResult Run(IZBaseApp app, string exbPath, string payload, string partName,
            int sizeMmW, int sizeMmH, double scaleDenom, string fallbackPoint, bool closeAfter,
            Action<string> log, Func<bool> stop)
        {
            var r = new QrRunResult();
            r.ExbPath = exbPath ?? "";
            if (app == null) { r.Summary = "未连接 CAXA（第三态）"; log(Tag + ": " + r.Summary); return r; }
            if (string.IsNullOrEmpty(exbPath) || !File.Exists(exbPath))
            { r.Summary = "同名 exb 不存在（第三态，未写图纸）：" + exbPath; log(Tag + ": " + r.Summary); return r; }

            // ★ 宿主在开图/生成之后会弹「当前有3D视图数据源更新，要更新对应所有视图吗？」，
            //   这个 #32770 占住主线程 ⇒ 二维码命令停在等待输入、QR_STATE delta 恒 0。
            //   每轮 Run 开头激活 150 s 后台守护，白名单内自动点「是」（不干预其它对话框）；
            //   必须放在最前面：上一轮残留的提示框要在 OpenFile2 之前就被清掉。
            NativeQrDialog.ArmViewGuard(150, log);
            r.PropsOk = !string.IsNullOrEmpty(payload);

            // 1) 二维码制作（QRCoder → PNG；磁盘产物 > 0 字节才算 verified）
            if (!BuildQr(payload, partName, log, out string png, out int n, out int pngPx))
            {
                r.Summary = "二维码制作失败（第三态），属性组合 " + (r.PropsOk ? "verified" : "attempt");
                log(Tag + ": " + r.Summary);
                return r;
            }
            r.PngPath = png;
            log(Tag + ": 二维码 PNG 已生成（磁盘文件 > 0 字节）= " + png + "；矩阵 " + n + "x" + n +
                "（1 模块静区），像素 " + pngPx + "x" + pngPx + " @96DPI = " + FormatMm(pngPx / 96.0 * 25.4) + " mm");

            // 2) 差集基线必须在 OpenFile2 投递之前采（坑 204）；按类名认 Draft，不按 pid 过滤
            HashSet<long> baseline = SnapshotDraftHandles();
            bool draftWarm = baseline.Count > 0;
            bool crxWarm = ViewBridge.IsCrxLoaded();
            log(Tag + ": 图纸环境判据：Draft 文档窗口 " + baseline.Count + " 个、CRX marker " +
                (crxWarm ? "在" : "不在") + "（" + (draftWarm || crxWarm ? "本进程已进过图纸环境" : "冷态") + "）");
            log(Tag + ": 风险提示：业务 exb 自动 OpenFile2 有 dataexchange.dll AV 闪退记录（技能 06 第 11 节，热态不消除）；沿用 caxa3dqrcode 用户定案执行，失败即判死、不重试");

            // 热路径：投递前先泵消息，别把主线程逼到最坏时机（技能 06 第 11 节②）
            // ★ 开图期弹窗代点（参考「刷新工程图」FormatConverter/BatchProcessor 同款 PopupAutoYes）：
            //   OpenFile2 前布防、稳定闸过/败后 finally 撤防。开图纸阶段可能弹的框全部兜住：
            //   8D 视图更新→是、尺寸检查→取消（不重建失效尺寸）、其余带「是/确定」的 #32770→代点。
            //   与 NativeQrDialog 白名单守护并存（答案一致＝双保险）；字段映射/文件框等专用链路
            //   均不在本段。必须在 NativeBitmapInsert.Start 之前撤防：插入段「图像」参数框由专用
            //   填写器先填插入点/比例再点「确定」，通用代点器抢点会把没填参数的空框提交掉。
            PopupAutoYes.Arm(log);
            IntPtr hwnd = IntPtr.Zero;
            string winWhy = "";
            try
            {
                if (!PumpMessages(1500, stop)) { r.Summary = "用户停止"; return r; }

                // 3) 打开该行 exb：返回值/异常都是固定噪音，以窗口真现 + CRX marker 为权威
                bool alreadyOpen = false;
                try { app.OpenFile2(exbPath, false, true, out alreadyOpen); }
                catch (Exception ex) { log(Tag + ": OpenFile2 异常（exb 固定噪音，吞掉）" + ex.Message); }
                log(Tag + ": OpenFile2 已投递 " + exbPath + "（alreadyOpen=" + alreadyOpen + "，仅代表入队，不算成功）");

                // 4) 定窗名：优先句柄差集；同名 exb 原本已打开（无新窗口）时按 MDI 活动图纸视图接管
                if (!WaitForNewDrawingWindow(baseline, 8000, log, stop, out hwnd, out winWhy))
                {
                    // exb 上一轮已打开时 OpenFile2 不再产生新窗口，差集白等；
                    // 一律回退到 MDI 活动图纸视图，并如实标注「身份未独立确认」第三态。
                    IntPtr act = FindActiveDraftView();
                    if (act != IntPtr.Zero)
                    {
                        hwnd = act;
                        winWhy = "句柄差集未命中（" + winWhy + "），按 MDI 活动图纸视图接管；身份未独立确认（第三态）";
                    }
                }
                if (hwnd == IntPtr.Zero)
                {
                    r.Summary = "二维码制作 verified、exb 插入 capability：打开未确认，" + winWhy + "（不通知 CRX，绝不盲写）";
                    log(Tag + ": " + r.Summary);
                    return r;
                }
                log(Tag + ": 定窗名 OK hwnd=0x" + hwnd.ToInt64().ToString("X") + "；" + winWhy);

                // 4' 开图就绪/稳定闸：OpenFile2 返回 / 窗口真现 ≠ 文档就绪（CAXA 还要跑收尾与
                //    内嵌 3D 视图更新）。连续 stableMs 无模态框、CRX PING 通、目标窗口仍在才放行。
                string stableWhy;
                if (!WaitForDrawingStable(hwnd, 3000, 60000, log, stop, out stableWhy))
                {
                    int cleaned = NativeBitmapInsert.CloseResidualDialogs();
                    if (cleaned > 0)
                        log(Tag + ": 稳定闸失败后已清理宿主残留对话框 " + cleaned + " 个（下次开图不再被堵）");
                    r.Summary = "二维码制作 verified、exb 插入 capability：开图未稳定，" + stableWhy + "（不前置、不通知 CRX）";
                    log(Tag + ": " + r.Summary);
                    return r;
                }
            }
            finally { PopupAutoYes.Disarm(); }   // 开图段结束（含异常/停止/失败路径）一律撤防，绝不带进插入段

            // 通知闸：等 CRX 自己的加载信号（marker），冷态绝不投会动手的命令（坑 146/151）
            if (!WaitForCrx(30000, log, stop))
            {
                int cleaned = NativeBitmapInsert.CloseResidualDialogs();
                if (cleaned > 0) log(Tag + ": 通知闸失败后已清理宿主残留对话框 " + cleaned + " 个");
                r.Summary = "二维码制作 verified、exb 插入 capability：CRX marker 未出现（图纸环境未加载），未投递动手命令";
                log(Tag + ": " + r.Summary);
                return r;
            }
            log(Tag + ": 通知闸 OK：CRX marker 已就位（图纸环境已加载）");

            // 5) 前置（只在确实最小化时才还原，绝不改已可见窗口的状态）
            if (!FrontDrawingWindow(hwnd, log, out winWhy))
            {
                r.Summary = "二维码制作 verified、exb 插入 capability：前置失败 " + winWhy + "（不通知 CRX）";
                log(Tag + ": " + r.Summary);
                return r;
            }

            // 6) 实体类名取证（只读 dump 模型空间类名分布 + imageRef 计数）：
            //    确认 CX_DFT_InsertBitmap 所画实体落为 CRxDbImageRef —— 先删后附的判定依据。
            BridgeReply le = ViewBridge.Request("LISTENTS", "", false, 10000, log);
            log(Tag + ": 实体类名取证：" + (le != null && le.Ok ? le.Message : "第三态 " + (le == null ? "TIMEOUT" : le.Status)));

            // 6') 自动识别图纸比例（第三态回落兜底值；语义兼容 2.5 与 0.4 两种存储形态，归一为分母）
            double denom = scaleDenom;
            BridgeReply sc = ViewBridge.Request("GETSCALE", "", false, 10000, log);
            if (sc != null && sc.Ok && TryParseScale(sc.Message, out double sRaw))
            {
                denom = sRaw >= 1.0 - 1e-9 ? sRaw : 1.0 / sRaw;
                log(Tag + ": 图纸比例自动识别：paperScale 原始值=" + FormatMm(sRaw) +
                    " ⇒ 按 1:" + FormatMm(denom) + " 解释" +
                    (Math.Abs(denom - scaleDenom) > 1e-9
                        ? "（覆盖兜底值 1:" + FormatMm(scaleDenom) + "）"
                        : "（与兜底值一致）"));
            }
            else
            {
                log(Tag + ": 图纸比例自动识别失败（" + (sc == null ? "TIMEOUT" : sc.Status + " " + sc.Message) +
                    "）＝第三态，回落兜底值 1:" + FormatMm(scaleDenom) + "（不折算）");
            }
            r.ScaleDenom = denom;

            // 7) 定位规则：规则坐标 = 1:1 图幅左上角基准；按实机图幅宽高匹配规则名，
            //    插入点 = 规则坐标 × 比例分母。未命中/不可用 = 第三态回落兜底插入点。
            string effPoint = fallbackPoint;
            {
                double pw = 0, ph = 0;
                bool sizeOk = sc != null && sc.Ok &&
                              TryParseDoubleField(sc.Message, "w", out pw) &&
                              TryParseDoubleField(sc.Message, "h", out ph);
                string rulePath, ruleErr;
                List<InsertRule.Entry> rules = InsertRule.Load(out rulePath, out ruleErr);
                if (rules == null)
                    log(Tag + ": 定位规则不可用（" + ruleErr + "）＝第三态，插入点回落兜底值「" + fallbackPoint + "」");
                else if (!sizeOk)
                    log(Tag + ": 定位规则已加载 " + rules.Count + " 条，但实机图幅宽高未读到（GETSCALE 回包无 w/h）" +
                        "＝第三态，插入点回落兜底值「" + fallbackPoint + "」");
                else if (InsertRule.TryMatch(rules, pw, ph, out InsertRule.Entry hit, out string mWhy))
                {
                    effPoint = InsertRule.Format(hit.X * denom) + "," + InsertRule.Format(hit.Y * denom);
                    log(Tag + ": ★ 定位规则" + mWhy + "：1:1 基准 (" + InsertRule.Format(hit.X) + "," +
                        InsertRule.Format(hit.Y) + ") × 比例分母 " + InsertRule.Format(denom) +
                        " ⇒ 插入点 (" + effPoint + ")（覆盖兜底值「" + fallbackPoint + "」）");
                }
                else
                    log(Tag + ": 定位规则未命中：" + mWhy + "＝第三态，插入点回落兜底值「" + fallbackPoint + "」");
            }
            r.InsertPoint = effPoint;

            // 8) 比例补偿换算：需求成品 mm × 图纸比例分母 ⇒ 落图目标 mm；宿主比例因子 = 落图 ÷ PNG@96DPI
            double pngMm = pngPx / 96.0 * 25.4;
            double insertMm = sizeMmW * denom;
            double imgScale = pngMm > 0.0001 ? insertMm / pngMm : 0.0;
            log(Tag + ": 比例补偿换算：需求成品 " + sizeMmW + "x" + sizeMmH + " mm × 图纸比例分母 " +
                FormatMm(denom) + " ⇒ 落图目标 " + FormatMm(insertMm) + " mm；PNG " + pngPx + "x" + pngPx +
                " @96DPI = " + FormatMm(pngMm) + " mm；宿主比例因子 = " + FormatMm(imgScale));
            if (sizeMmW != sizeMmH)
                log(Tag + ": ⚠ 尺寸 " + sizeMmW + "x" + sizeMmH + " 非正方：二维码本身正方，按宽度 " + sizeMmW + " mm 换算比例因子");

            // 8') 先删后插：旧二维码残留在目标点会让 QR_STATE 记账错乱 + 图上叠两枚，
            //     故插入前按目标点精确删除（桥端按 CRxDbImageRef::getGeomExtents 判
            //     目标点 ±1mm 落包围盒内才 erase，其它图片不动）。deleted=0 = 正常首插；
            //     失败 = 第三态不阻断插入链。必须在本调用之后才投 BITMAP_NATIVE（before 计数才准）。
            //     ★ 坑 45：deleted/eraseFail 只是取证，不作成败判据（erase 返回值不可信）。
            BridgeReply del = ViewBridge.Request("DELETE_QR", effPoint, true, 10000, log);
            if (del != null && del.Ok)
                log(Tag + ": 先删后插：目标点 (" + effPoint + ") 删除旧图片实体 " +
                    (TryParseIntField(del.Message, "deleted", out int d) ? d : -1) +
                    " 个（deleted=0 = 正常首插；回包 " + del.Message + "）");
            else
                log(Tag + ": 先删后插失败（" + (del == null ? "TIMEOUT" : del.Status + " " + del.Message) +
                    "）＝第三态，不阻断插入链继续");

            // 9) 通知 CRX：投宿主「插入图片」命令 + 代填文件对话框/图像参数框 + 权威回读 + 保存
            if (stop != null && stop()) { r.Summary = "用户停止"; return r; }
            NativeBitmapInsert.Start(png, effPoint, imgScale, log, stop);
            BridgeReply native = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (stop != null && stop()) { r.Summary = "用户停止"; NativeBitmapInsert.Finish(); return r; }
                native = ViewBridge.Request("BITMAP_NATIVE", "", true, 15000, log);
                if (native.Ok) break;
                if (native.Message.IndexOf("db-not-ready", StringComparison.OrdinalIgnoreCase) < 0) break;
                log(Tag + ": 图纸数据库尚未就绪（CRX 未动手），等 1 s 后重探 " + (attempt + 1) + "/4");
                if (!PumpMessages(1000, stop)) { r.Summary = "用户停止"; NativeBitmapInsert.Finish(); return r; }
            }
            if (native == null || !native.Ok)
            {
                NativeBitmapInsert.DrainLogs(log);
                NativeBitmapInsert.Finish();
                string st = native == null ? "NOCRX" : native.Status;
                string ms = native == null ? "<无回包>" : native.Message;
                r.Summary = "二维码制作 verified、exb 插入 attempt：宿主插入图片命令 BITMAP_NATIVE " + st + " " + ms;
                log(Tag + ": " + r.Summary);
                return r;
            }

            // 模态框占住 UI 线程期间不投任何文件桥请求；主线程只等后台代点器的状态
            var dlgSw = Stopwatch.StartNew();
            while (dlgSw.ElapsedMilliseconds < 150000)
            {
                if (stop != null && stop() || !PumpMessages(200, stop))
                {
                    r.Summary = "用户停止";
                    NativeBitmapInsert.Finish();
                    NativeBitmapInsert.CloseResidualDialogs();   // 坑 41：停止后清残留，别堵死下一轮
                    return r;
                }
                NativeBitmapInsert.DrainLogs(log);
                if (NativeBitmapInsert.LastError.Length > 0 ||
                    (!NativeBitmapInsert.Found && dlgSw.ElapsedMilliseconds > 45000)) break;
                if (NativeBitmapInsert.Done) break;
            }
            NativeBitmapInsert.DrainLogs(log);
            log(Tag + ": bmp-dlg 代点 found=" + NativeBitmapInsert.Found + " filled=" + NativeBitmapInsert.Filled +
                " opened=" + NativeBitmapInsert.Opened + " imageDlg=" + NativeBitmapInsert.ImageDlgFound +
                " pointFilled=" + NativeBitmapInsert.PointFilled + " scaleFilled=" + NativeBitmapInsert.ScaleFilled +
                " okClicked=" + NativeBitmapInsert.OkClicked + " err=" + NativeBitmapInsert.LastError);
            // 失败即判死不赌第二次：没命中文件框 / 路径没填进 / 没点到「打开」，都不进入 QR_STATE 轮询。
            if (!NativeBitmapInsert.Found || !NativeBitmapInsert.Filled || !NativeBitmapInsert.Opened)
            {
                NativeBitmapInsert.Finish();
                r.Summary = "二维码制作 verified、exb 插入 attempt：插入图片对话框代点未走通（found=" + NativeBitmapInsert.Found +
                            " filled=" + NativeBitmapInsert.Filled + " opened=" + NativeBitmapInsert.Opened +
                            "）；" + NativeBitmapInsert.LastError;
                log(Tag + ": " + r.Summary);
                return r;
            }
            NativeBitmapInsert.Finish();

            // 10) 等实体增量：只轮询同一个权威副作用信号，不重投命令（失败不赌第二次）
            BridgeReply state = null;
            bool deltaOk = false;
            var stateSw = Stopwatch.StartNew();
            string lastSig = "";
            long nextHint = 15000;
            bool escSent = false;
            while (stateSw.ElapsedMilliseconds < 180000)
            {
                if (stop != null && stop()) { r.Summary = "用户停止"; return r; }

                state = ViewBridge.Request("QR_STATE", "", false, 5000, log);
                if (state.Ok)
                {
                    int delta = ReadIntField(state.Message, "delta");
                    int pending = ReadIntField(state.Message, "pending");
                    string sig = "delta=" + delta + " pending=" + pending;
                    if (sig != lastSig) { log(Tag + ": QR_STATE " + state.Message); lastSig = sig; }
                    if (delta > 0)
                    {
                        deltaOk = true;
                        if (delta > 1)
                            log(Tag + ": ⚠ delta=" + delta + " > 1：可能重复插入，请检查图纸上二维码数量");
                        break;
                    }
                    if (pending == 0 && delta <= 0) break;   // 回调已跑完仍无增量 = 真失败
                    // 11-15 实机坑：重跑场景宿主插入命令滞留（pending=1 永挂）⇒ 60 s 时投 ESC
                    // 取消滞留命令，让 commandEnded 落地 → pending 归 0 → 下一轮按真失败退出。
                    if (pending != 0 && !escSent && stateSw.ElapsedMilliseconds > 60000)
                    {
                        escSent = true;
                        log(Tag + ": ⚠ 60 s 仍 pending=" + pending + " 无增量：宿主插入命令疑似滞留，投 ESC 取消（不吊满 180 s）");
                        NativeBitmapInsert.SendEsc(hwnd);
                    }
                }
                if (stateSw.ElapsedMilliseconds > nextHint)
                {
                    log(Tag + ": 仍在等待插入…（已等 " + (stateSw.ElapsedMilliseconds / 1000) + " s / 180 s）");
                    nextHint += 15000;
                }
                if (!PumpMessages(250, stop)) { r.Summary = "用户停止"; return r; }
            }
            if (!deltaOk)
            {
                r.Summary = "二维码制作 verified、exb 插入 attempt：宿主插入图片命令未获实体增量、180 s 内未落图（第三态）" +
                            (state == null ? "<无回包>" : state.Status + " " + state.Message) +
                            "；视图更新提示守护处理 " + NativeQrDialog.ViewPromptsHandled + " 次" +
                            (NativeQrDialog.SizeCheckHandled > 0
                                ? "（其中尺寸检查 " + NativeQrDialog.SizeCheckHandled + " 次已代点「取消」）" : "") +
                            (NativeQrDialog.ViewPromptPresent() ? "（当前仍挂着白名单提示框）" : "");
                log(Tag + ": " + r.Summary);
                return r;
            }

            // 11) 保存（唯一权威 = 盘上 mtime/长度变化）
            long before = 0;
            DateTime beforeTime = DateTime.MinValue;
            try { var fi = new FileInfo(exbPath); before = fi.Length; beforeTime = fi.LastWriteTime; } catch { }
            BridgeReply save = ViewBridge.Request("SAVE", "", true, 10000, log);
            bool saved = save.Ok;
            if (saved)
            {
                try
                {
                    var fi = new FileInfo(exbPath);
                    saved = fi.Length > 0 && (fi.LastWriteTime > beforeTime || fi.Length != before);
                }
                catch { saved = false; }
                if (!saved) log(Tag + ": SAVE 已投出但盘上文件未变化（第三态，按未保存处理）");
            }
            r.InsertVerified = deltaOk && saved;

            // 12) 可选关图：只在 verified（已落盘）且用户勾选时执行；失败/停止路径绝不关图。
            if (closeAfter && r.InsertVerified)
            {
                BridgeReply close = ViewBridge.Request("CLOSERAW", Path.GetFileName(exbPath), true, 30000, log);
                log(Tag + ": 收尾关图 CLOSERAW " + close.Describe());
            }

            r.Summary = "属性组合 " + (r.PropsOk ? "verified" : "attempt") + "、二维码制作 verified、exb 插入 " +
                        (r.InsertVerified ? "verified" : "attempt（实体已画、保存未回读到盘上变化）") +
                        "；delta>0 判据 " + (deltaOk ? "OK" : "FAIL") + "；SAVE=" + (save == null ? "<无>" : save.Status) +
                        "；比例 1:" + FormatMm(denom) + "；插入点(" + effPoint + ")";
            log(Tag + ": " + r.Summary);
            return r;
        }

        // ---------------------------------------------------------------- 二维码

        /// <summary>QRCoder(ECC L) → 1 模块静区 → PNG（磁盘产物）。
        /// 显式 SetResolution(96,96)：宿主 1× 物理尺寸 = 像素/96*25.4 mm，比例换算才有确定基准。</summary>
        public static bool BuildQr(string payload, string partName,
            Action<string> log, out string pngPath, out int n, out int pngPx)
        {
            pngPath = ""; n = 0; pngPx = 0;
            if (string.IsNullOrEmpty(payload)) { log(Tag + ": 二维码内容为空，拒绝制作（第三态）"); return false; }
            try
            {
                bool[,] core;
                using (var gen = new QRCoder.QRCodeGenerator())
                {
                    var data = gen.CreateQrCode(payload, QRCoder.QRCodeGenerator.ECCLevel.L);
                    var mm = data.ModuleMatrix;                 // List<BitArray>：行 × 列
                    int w = mm.Count, h = mm[0].Count;
                    core = new bool[w, h];
                    for (int r = 0; r < w; r++)
                        for (int c = 0; c < h; c++)
                            core[r, c] = mm[r][c];
                }

                const int quiet = 1;                // 最小可扫静区（caxa3dqrcode 11-15 用户定案：4 → 1）
                int side = core.GetLength(0) + quiet * 2;
                n = side;
                bool[,] m = new bool[side, side];
                for (int r = 0; r < core.GetLength(0); r++)
                    for (int c = 0; c < core.GetLength(1); c++)
                        m[r + quiet, c + quiet] = core[r, c];

                const int px = 8;
                pngPx = side * px;
                using (var bmp = new Bitmap(pngPx, pngPx, PixelFormat.Format32bppArgb))
                {
                    bmp.SetResolution(96F, 96F);    // 宿主按 DPI 折算物理尺寸的确定基准
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        using (var brush = new SolidBrush(Color.Black))
                            for (int r = 0; r < side; r++)
                                for (int c = 0; c < side; c++)
                                    if (m[r, c]) g.FillRectangle(brush, c * px, r * px, px, px);
                    }
                    string safe = partName ?? "qr";
                    foreach (char ch in Path.GetInvalidFileNameChars()) safe = safe.Replace(ch, '_');
                    // 11-15 实机坑：固定文件名重跑时与已嵌入文档的图片同名，宿主「图像」框不认新文件
                    // （id=2072 滞留上一行路径、图片下拉空）⇒ 命令滞留 pending=1、delta 恒 0。
                    // 文件名加时间戳保证每次唯一，宿主永远当"新图片"走完整插入链。
                    pngPath = Path.Combine(Path.GetTempPath(), "caxa3dBatchMaster_qr_" + safe + "_" +
                                           DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".png");
                    bmp.Save(pngPath, ImageFormat.Png);
                }
                var fi = new FileInfo(pngPath);
                if (!fi.Exists || fi.Length == 0) { log(Tag + ": PNG 落盘判据失败（第三态）"); return false; }
                log(Tag + ": PNG " + fi.Length + " B；矩阵 " + side + "x" + side);
                return true;
            }
            catch (Exception ex) { log(Tag + ": 二维码制作 EX " + ex.Message); return false; }
        }

        /// <summary>毫米值格式化：最多 3 位小数、不变文化（日志与比例因子共用）。</summary>
        private static string FormatMm(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>解析回包里 key=&lt;浮点&gt;（如 w=420 / h=297）；读不到/非法 = 第三态返回 false。</summary>
        private static bool TryParseDoubleField(string message, string key, out double v)
        {
            v = 0;
            if (string.IsNullOrEmpty(message)) return false;
            int i = message.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return false;
            int j = i + key.Length + 1, end = j;
            while (end < message.Length && (char.IsDigit(message[end]) || message[end] == '.' ||
                   message[end] == '-' || message[end] == 'e' || message[end] == 'E')) end++;
            if (end == j) return false;
            return double.TryParse(message.Substring(j, end - j),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out v) && Math.Abs(v) > 1e-9;
        }

        /// <summary>解析回包里 key=&lt;整数&gt;（允许 0，如 deleted=0）；读不到返回 false（第三态）。</summary>
        private static bool TryParseIntField(string message, string key, out int v)
        {
            v = 0;
            if (string.IsNullOrEmpty(message)) return false;
            int i = message.IndexOf(key + "=", StringComparison.Ordinal);
            if (i < 0) return false;
            int j = i + key.Length + 1, end = j;
            while (end < message.Length && (char.IsDigit(message[end]) || message[end] == '-')) end++;
            if (end == j) return false;
            return int.TryParse(message.Substring(j, end - j),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        }

        /// <summary>解析 GETSCALE 回包 scale=&lt;值&gt;；读不到/非法/零 = 第三态返回 false，绝不折算。</summary>
        private static bool TryParseScale(string message, out double sRaw)
        {
            sRaw = 0;
            if (string.IsNullOrEmpty(message)) return false;
            int i = message.IndexOf("scale=", StringComparison.Ordinal);
            if (i < 0) return false;
            int j = i + 6, end = j;
            while (end < message.Length && (char.IsDigit(message[end]) || message[end] == '.' ||
                   message[end] == '-' || message[end] == 'e' || message[end] == 'E')) end++;
            if (!double.TryParse(message.Substring(j, end - j),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out sRaw)) return false;
            return sRaw > 1e-9;
        }

        /// <summary>从 CRX 回包 message 里按 key= 取整数；读不到返回 -999（第三态，绝不折算成 0）。</summary>
        private static int ReadIntField(string text, string key)
        {
            if (string.IsNullOrEmpty(text)) return -999;
            foreach (string part in text.Split(new char[] { ' ', '|', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!part.StartsWith(key + "=", StringComparison.Ordinal)) continue;
                int v;
                if (int.TryParse(part.Substring(key.Length + 1), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out v)) return v;
            }
            return -999;
        }

        // ---------------------------------------------------------------- 定窗名 / 前置（照抄 caxa3dqrcode 实机定案）

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessId();

        private const uint GA_ROOT = 2;
        private const int WM_MDIGETACTIVE = 0x0229;

        private static bool IsDraftChild(IntPtr h)
        {
            var cls = new StringBuilder(256);
            GetClassName(h, cls, cls.Capacity);
            return cls.ToString().IndexOf("AfxFrameOrView", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsMdiClient(IntPtr h)
        {
            var cls = new StringBuilder(128);
            GetClassName(h, cls, cls.Capacity);
            return cls.ToString() == "MDIClient";
        }

        /// <summary>全部 Draft 文档视图句柄：按类名认、不按 pid 过滤（exb 可能在 Draft 进程）。</summary>
        public static List<IntPtr> ListDraftHandles()
        {
            var list = new List<IntPtr>();
            EnumWindows(delegate (IntPtr top, IntPtr l)
            {
                EnumChildWindows(top, delegate (IntPtr h, IntPtr l2)
                {
                    if (IsDraftChild(h)) list.Add(h);
                    return true;
                }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        /// <summary>差集基线：必须在 OpenFile2 投递之前采（坑 204）。</summary>
        public static HashSet<long> SnapshotDraftHandles()
        {
            var set = new HashSet<long>();
            foreach (IntPtr h in ListDraftHandles()) set.Add(h.ToInt64());
            return set;
        }

        /// <summary>MDIClient 的活动图纸视图（与 CRX FindActiveDrawingWindow 同款判据）。</summary>
        private static IntPtr FindActiveDraftView()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate (IntPtr top, IntPtr l)
            {
                if (found != IntPtr.Zero) return false;
                if (!IsWindowVisible(top)) return true;
                EnumChildWindows(top, delegate (IntPtr h, IntPtr l2)
                {
                    if (found != IntPtr.Zero || !IsMdiClient(h)) return true;
                    IntPtr mem = Marshal.AllocHGlobal(4);
                    try
                    {
                        Marshal.WriteInt32(mem, 0);
                        IntPtr act = SendMessage(h, WM_MDIGETACTIVE, IntPtr.Zero, mem);
                        if (act != IntPtr.Zero && IsWindow(act) && IsDraftChild(act)) found = act;
                    }
                    finally { Marshal.FreeHGlobal(mem); }
                    return true;
                }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>消息泵：热路径投递前泵 1~2 s；等待链一律 DoEvents，禁 WaitForExit / 长 Sleep。</summary>
        private static bool PumpMessages(int ms, Func<bool> stop)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (stop != null && stop()) return false;
                Application.DoEvents();
                Thread.Sleep(50);
            }
            return true;
        }

        /// <summary>等窗口真现：OpenFile2 之后新出现的 Draft 句柄才是本轮打开的那张图。</summary>
        private static bool WaitForNewDrawingWindow(HashSet<long> baseline, int timeoutMs,
            Action<string> log, Func<bool> stop, out IntPtr hwnd, out string why)
        {
            hwnd = IntPtr.Zero; why = "";
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (stop != null && stop()) { why = "用户停止"; return false; }
                Application.DoEvents();
                var fresh = new List<IntPtr>();
                foreach (IntPtr h in ListDraftHandles())
                    if (!baseline.Contains(h.ToInt64()) && IsWindow(h)) fresh.Add(h);
                if (fresh.Count > 0)
                {
                    IntPtr act = FindActiveDraftView();
                    hwnd = (act != IntPtr.Zero && fresh.Contains(act)) ? act : fresh[fresh.Count - 1];
                    why = "句柄差集命中（投递前 " + baseline.Count + " 个 → 新增 " + fresh.Count + " 个）";
                    return true;
                }
                Thread.Sleep(100);
            }
            why = "OpenFile2 投递后 " + timeoutMs + " ms 内没有新的 Draft 文档窗口（窗口真现未确认）";
            return false;
        }

        /// <summary>本进程可见的标准模态对话框数（#32770）。</summary>
        private static int CountVisibleModalWindows()
        {
            int n = 0;
            uint me = GetCurrentProcessId();
            EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                var cls = new StringBuilder(128);
                GetClassName(h, cls, cls.Capacity);
                if (cls.ToString() != "#32770") return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == me) n++;
                return true;
            }, IntPtr.Zero);
            return n;
        }

        /// <summary>
        /// 开图就绪/稳定闸：窗口真现后必须连续 stableMs 满足「无模态框 + CRX PING 通 + 目标窗口仍在」。
        /// PING 是唯一允许在开图期间投的只读请求（坑 146）。
        /// </summary>
        private static bool WaitForDrawingStable(IntPtr hwnd, int stableMs, int timeoutMs,
            Action<string> log, Func<bool> stop, out string why)
        {
            why = "";
            var total = Stopwatch.StartNew();
            var probe = Stopwatch.StartNew();
            int quietMs = 0;
            int pingMiss = 0;
            bool pingOk = false;
            while (total.ElapsedMilliseconds < timeoutMs)
            {
                if (stop != null && stop()) { why = "用户停止"; return false; }
                Application.DoEvents();
                if (!IsWindow(hwnd)) { why = "目标图纸窗口已消失（开图被取消或宿主已退出）"; return false; }
                int modal = CountVisibleModalWindows();
                if (probe.ElapsedMilliseconds >= 1000)
                {
                    probe.Restart();
                    BridgeReply ping = ViewBridge.Request("PING", "", false, 2000, null);
                    pingOk = ping.Ok;
                    pingMiss = pingOk ? 0 : pingMiss + 1;
                    log(Tag + ": 开图稳定探针 modal=" + modal + " ping=" + (pingOk ? "OK" : ping.Status));
                    // 坑 41：ping 连续 3 次不通且未见模态框 ⇒ 大概率是宿主残留对话框堵死 CRX UI 线程。
                    if (pingMiss >= 3)
                    {
                        int cleaned = NativeBitmapInsert.CloseResidualDialogs();
                        log(Tag + ": PING 连续 " + pingMiss + " 次不通" +
                            (cleaned > 0 ? "，已自动清理宿主残留对话框 " + cleaned + " 个" :
                             "，未发现可关闭的残留对话框（请检查 CAXA 界面是否有弹窗）"));
                        pingMiss = 0;
                    }
                }
                quietMs = (modal == 0 && pingOk) ? quietMs + 120 : 0;
                if (quietMs >= stableMs)
                {
                    why = "连续 " + stableMs + " ms 无模态框 + CRX PING 通 + 目标窗口仍在";
                    log(Tag + ": 开图稳定 OK：" + why);
                    return true;
                }
                Thread.Sleep(120);
            }
            why = timeoutMs + " ms 内未达到连续 " + stableMs + " ms 稳定（模态框 / CRX 响应 / 窗口判据未全绿）";
            return false;
        }

        /// <summary>通知闸：等 CRX 自己的加载信号（marker）；冷态绝不投会动手的命令。</summary>
        private static bool WaitForCrx(int timeoutMs, Action<string> log, Func<bool> stop)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (stop != null && stop()) return false;
                if (ViewBridge.IsCrxLoaded()) return true;
                Application.DoEvents();
                Thread.Sleep(100);
            }
            return ViewBridge.IsCrxLoaded();
        }

        private static bool FrontDrawingWindow(IntPtr h, Action<string> log, out string why)
        {
            why = "";
            IntPtr root = GetAncestor(h, GA_ROOT);
            // 20:13/20:15 实机：无条件 SW_RESTORE 会把最大化的 CAXA 主框架 / MDI 图纸子窗
            // 还原成浮动小窗口 ⇒ 本轮只在确实最小化时才还原；MDI 子窗只提升 Z 序，不 ShowWindow。
            if (root != IntPtr.Zero)
            {
                if (IsIconic(root)) ShowWindow(root, 9);   // 仅最小化才 SW_RESTORE
                BringWindowToTop(root);
                SetForegroundWindow(root);
            }
            BringWindowToTop(h);
            RECT rc;
            GetWindowRect(h, out rc);
            if (rc.Left <= -30000)
            {
                why = "窗口仍在后台态（矩形 " + rc.Left + "），坐标不可信";
                return false;
            }
            log(Tag + ": 前置 OK（未改变窗口状态）hwnd=0x" + h.ToInt64().ToString("X") +
                " maximized=" + (root != IntPtr.Zero && IsZoomed(root)) +
                " iconic=" + (root != IntPtr.Zero && IsIconic(root)));
            return true;
        }
    }
}
