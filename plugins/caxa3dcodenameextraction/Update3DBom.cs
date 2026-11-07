// 机械移植自工程 caxa3dexbtracker 的 Update3DBom.cs（全文件）。仅改 namespace 与项目内资源名，逻辑未动。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dcodenameextraction
{
    /// <summary>「更新3dbom」准备阶段的结论：决定 CRX 往哪张图纸推命令。</summary>
    internal sealed class Update3DBomPlan
    {
        /// <summary>走的是哪条分支（只用于日志/状态行）。</summary>
        public string Mode = "";

        /// <summary>
        /// 目标 exb 全路径。设计环境分支必填（CRX 按它认图纸）；
        /// 图纸环境分支为 null（表示"就用当前前台那张图纸"）。
        /// </summary>
        public string ExbPath;

        /// <summary>人类可读的目标描述（写日志用）。</summary>
        public string Target = "";
    }

    /// <summary>
    /// 「更新3dbom」的两分支实现（需求原文）：
    ///   ① 前台环境是 .ics（设计环境）：所选场景**必须是装配体**（外链 / 虚拟都行）
    ///      → 打开同零件名的 .exb → CRX 确认同名 exb 打开完毕 → 推更新 3D 明细的命令；
    ///   ② 前台环境是 .exb（图纸环境）：确认前台就是图纸 → 直接对这张前台图纸推命令。
    ///
    /// 分工（照抄本工程既有纪律）：
    ///   · **判定与决策在 C#**：前台是 ics 还是 exb、是不是装配体、要开哪个 exb，全在这里定；
    ///   · **执行在 CRX**：exb 是否真的"开完"、是不是活动图纸、命令推给哪张文档，由 CRX 用
    ///     `fileName()` / 图幅表回查确认（C# 的 `ActiveDoc` 在图纸环境里恒回 3D 场景，不可用）。
    ///
    /// ★ COM 一律在 UI 线程（= CAXA 主线程）调用：`ActiveDoc` / `OpenFile2` 换线程会取到 null
    ///   或静默失败；只有"发请求等 CRX 回包"那一段才丢后台（那里有 Thread.Sleep，堵主线程会
    ///   把 CRX 的 WM_TIMER 饿死）。
    /// </summary>
    internal static class Update3DBomFlow
    {
        /// <summary>从 .ics 目录打开同名 exb 后，等图纸模块（CRX）就绪的上限。</summary>
        private const int CrxReadyTimeoutMs = 25000;

        /// <summary>open 之后等"新图纸窗口出现"的上限（只用于日志判断，不作为成败判据）。</summary>
        private const int DraftWindowTimeoutMs = 20000;

        /// <summary>
        /// 准备阶段（UI 线程）：判定前台环境 → 需要时校验装配体并打开同名 exb → 得到目标 exb 路径。
        /// 任何一步不成立都抛异常（由调用方落状态行），绝不"猜着往下走"。
        /// </summary>
        /// <param name="app">站点给的 IZBaseApp（宿主直给，唯一可信的 COM 入口）</param>
        /// <param name="log">日志回调</param>
        /// <param name="pickExb">找不到同名 exb 时由用户指定：入参是建议路径，返回 null 表示取消</param>
        /// <param name="openExb">
        /// true（默认，走 CRX 推命令那条路）= 需要把 exb 真的打开，CRX 才认得到它；
        /// false（走 API 那条路）= **只解析路径、不打开**，因为 `UpdateDraftViews(exbPath, …)`
        /// 自己按路径干活，多开一张图纸只会污染前台环境。
        /// </param>
        /// <param name="waitCrx">是否需要等 CRX 就绪（纯 API 路径完全用不上 CRX，别白等 25 秒）</param>
        public static Update3DBomPlan Prepare(IZBaseApp app, Action<string> log, Func<string, string> pickExb,
            bool openExb, bool waitCrx)
        {
            if (app == null)
                throw new InvalidOperationException(
                    "本插件没拿到 CAXA 站点对象（IZBaseApp），无法执行「更新3dbom」。");
            if (log == null) log = delegate { };

            IntPtr hwnd;
            string title;
            ForegroundEnvKind kind = DrawingProbe.GetForegroundEnv(out hwnd, out title);
            log("更新3dbom：前台文档窗口 = " + Describe(kind, hwnd, title));

            // ── 分支 ②：前台就是 exb 图纸环境 ──
            if (kind == ForegroundEnvKind.Draft)
            {
                if (openExb)
                {
                    log("更新3dbom：前台环境 = 图纸环境（exb）→ 直接对当前前台图纸执行");
                    return new Update3DBomPlan
                    {
                        Mode = "前台图纸",
                        ExbPath = null,
                        Target = string.IsNullOrEmpty(title) ? "当前前台图纸" : title
                    };
                }

                // API 路径要的是**绝对路径**：先从窗口标题里抠（有时是全路径，脏了带 * ），
                // 抠不出来就请用户指定（图纸环境里 C# 拿不到权威 fileName，那是 CRX 的活）。
                log("更新3dbom：前台环境 = 图纸环境（exb）→ 纯 API 模式，需要 exb 绝对路径");
                string fromTitle = PathFromWindowTitle(title);
                if (!string.IsNullOrEmpty(fromTitle) && File.Exists(fromTitle))
                {
                    log("更新3dbom：从窗口标题解析出 exb = " + fromTitle);
                    return new Update3DBomPlan
                    {
                        Mode = "前台图纸（标题解析）",
                        ExbPath = fromTitle,
                        Target = Path.GetFileName(fromTitle)
                    };
                }

                // 标题只给文件名（实机常态："SC08020102.01 壳体.exb*"）。这里不要直接弹文件框：
                // 图纸文件名 = 零件名 + .exb，与 ics 同目录是产线常态，而 `ActiveDoc` 在图纸环境里
                // 仍回 **3D 场景文档**（坑 36）⇒ 拿它的目录 + 同名文件就能定位，无需用户干预。
                // 只有"目录里确实存在同名 exb"才采用；否则照旧退回人工指定，绝不瞎猜。
                string byDir = ResolveExbBySceneDir(app, title, log);
                if (!string.IsNullOrEmpty(byDir))
                {
                    return new Update3DBomPlan
                    {
                        Mode = "前台图纸（同目录定位）",
                        ExbPath = byDir,
                        Target = Path.GetFileName(byDir)
                    };
                }

                log("更新3dbom：窗口标题不是绝对路径（[" + (title ?? "") + "]），请手工指定 exb");
                string picked = pickExb == null ? null : pickExb(title);
                if (string.IsNullOrEmpty(picked))
                    throw new InvalidOperationException(
                        "已取消：没能从图纸窗口标题解析出 exb 绝对路径，也没有指定文件。");
                if (!File.Exists(picked))
                    throw new InvalidOperationException("指定的 exb 不存在：" + picked);
                return new Update3DBomPlan
                {
                    Mode = "前台图纸（人工指定）",
                    ExbPath = picked,
                    Target = Path.GetFileName(picked)
                };
            }

            if (kind == ForegroundEnvKind.Unknown)
                throw new InvalidOperationException(
                    "未能判定当前前台环境（没找到 MDIClient 或没有文档子窗口）。" +
                    "请先把要处理的装配体 .ics 或 .exb 切到前台，再点「更新3dbom」。");

            // ── 分支 ①：前台是设计环境（ics） ──
            IZDoc doc = null;
            try { doc = app.ActiveDoc; }
            catch (Exception ex) { throw new InvalidOperationException("读取活动文档失败：" + ex.Message); }
            if (doc == null)
                throw new InvalidOperationException("设计环境里没有活动文档。请先打开装配体 .ics 并切到前台。");

            IZSceneDoc scene = doc as IZSceneDoc;
            if (scene == null)
                throw new InvalidOperationException(
                    "当前活动文档不是 3D 设计环境（拿不到 IZSceneDoc）。请在装配体 .ics 里点「更新3dbom」，" +
                    "或在 exb 图纸环境里点（那种情况走「前台图纸」分支）。");

            string rawName = null;
            try { rawName = doc.Name; } catch { }
            log("更新3dbom：活动文档 Name = " + (string.IsNullOrEmpty(rawName) ? "(读取失败)" : rawName));

            // ★ 必须是装配体（以"选中元素"为准；外链 / 虚拟不限；不穿透子件）★
            string asmName = "", asmLinkedPath = "";
            bool? isAssembly = DetectAssembly(scene, log, out asmName, out asmLinkedPath);
            if (isAssembly == null)
                throw new InvalidOperationException(
                    "无法判定当前 .ics 是不是装配体：既没有选中元素，GetTopElement 也不可用。" +
                    "请在设计树上选中装配体后再点「更新3dbom」。详见日志。");
            if (isAssembly == false)
                throw new InvalidOperationException(
                    "选中的元素不是装配体（是零件/特征/其它类型）。「更新3dbom」只对装配体执行：" +
                    "请在设计树上点选**装配体那一层**（整个装配节点）后重试，已中止。");

            // ── 目标 exb：外链装配体 = 它自己同名 exb；虚拟 = 活动文档同名优先，都没有再人工指定 ──
            string dir, exbFileName, stem;
            SplitDocName(rawName, out dir, out exbFileName, out stem);

            // ★ 目标 exb 的口径（2026-11-05 13:2x 实机定案）★
            //   旧顺序把「场景同名 exb」排第一 ⇒ 选中外链装配体「SC08020102.012 壳体3」时打开的却是
            //   顶层图纸「SC08020103.00 P80油槽.exb」—— 明细更新到了别的件上（用户报的"打错了装配体"）。
            //   现在：选中的装配体是**外链** ⇒ 只认「它自己同名 exb」，目录按**它自己的 ics**
            //   （标准库件常放在别的文件夹，不在场景目录下）；它自己的图纸不存在就中止，
            //   绝不退回顶层场景图纸（宁可不干，不干错件）。
            //   虚拟装配体（没有外链源）维持旧口径：场景同名优先 —— 那条没有实机反证，不动。
            var candidates = new List<string>();
            bool targetIsLink = false;
            string linkDir = null;
            if (!string.IsNullOrEmpty(asmLinkedPath))
            {
                try { linkDir = Path.GetDirectoryName(asmLinkedPath); } catch { }
                if (!string.IsNullOrEmpty(linkDir))
                {
                    targetIsLink = true;
                    string linkStem = Path.GetFileNameWithoutExtension(asmLinkedPath);
                    if (!string.IsNullOrEmpty(linkStem))
                        candidates.Add(Path.Combine(linkDir, linkStem + ".exb"));
                    if (!string.IsNullOrEmpty(asmName))
                    {
                        string byName = Path.Combine(linkDir, asmName + ".exb");
                        if (!candidates.Contains(byName)) candidates.Add(byName);
                    }
                }
            }
            if (!targetIsLink)
            {
                if (string.IsNullOrEmpty(stem))
                    throw new InvalidOperationException(
                        "活动文档没有可用的文件名（Name=" + (rawName ?? "?") + "），无法定位同零件名 exb。");
                if (!string.IsNullOrEmpty(dir))
                {
                    candidates.Add(Path.Combine(dir, exbFileName));
                    // 选中的是虚拟子装配体时，明细表在它自己同名的 exb 上（排在场景图纸之后）
                    if (!string.IsNullOrEmpty(asmName))
                    {
                        string alt = Path.Combine(dir, asmName + ".exb");
                        if (!candidates.Contains(alt)) candidates.Add(alt);
                    }
                }
            }
            log("更新3dbom：候选 exb = " + string.Join(" | ", candidates.ToArray()));
            if (targetIsLink)
                log("更新3dbom：选中装配体「" + asmName + "」是外链件 ⇒ 目标锁定它自己的图纸（" + linkDir + "），" +
                    "不退回场景同名 exb");

            string exbPath = null;
            foreach (string c in candidates)
                if (File.Exists(c)) { exbPath = c; log("更新3dbom：命中同零件名 exb = " + c); break; }

            if (string.IsNullOrEmpty(exbPath))
            {
                if (targetIsLink)
                    throw new InvalidOperationException(
                        "选中的装配体「" + asmName + "」是外链件，但它自己同名的 exb 不存在（找过：" +
                        string.Join(" | ", candidates.ToArray()) +
                        "）。「更新3dbom」不退回顶层场景图纸执行（那会更新错件）。" +
                        "请先为它存出同名 exb，或在设计树上选中顶层装配体再点。");
                log("更新3dbom：候选 exb 都不存在，需要人工指定");
                exbPath = pickExb == null ? null : pickExb(candidates.Count > 0 ? candidates[0] : exbFileName);
                if (string.IsNullOrEmpty(exbPath))
                    throw new InvalidOperationException(
                        "已取消：没有指定要更新的 exb（期望文件名：" + exbFileName + "）。");
                if (!File.Exists(exbPath))
                    throw new InvalidOperationException("指定的 exb 不存在：" + exbPath);
            }

            if (openExb)
                OpenExbAndWaitReady(app, exbPath, log, waitCrx);
            else
                log("更新3dbom：纯 API 模式 —— 不打开 exb、不等 CRX，直接把路径交给 UpdateDraftViews");

            return new Update3DBomPlan
            {
                Mode = "设计环境→同名exb",
                ExbPath = exbPath,
                Target = Path.GetFileName(exbPath)
            };
        }

        /// <summary>老口径（走 CRX 推命令）：需要打开 exb 并等 CRX 就绪。</summary>
        public static Update3DBomPlan Prepare(IZBaseApp app, Action<string> log, Func<string, string> pickExb)
        {
            return Prepare(app, log, pickExb, true, true);
        }

        /// <summary>
        /// 纯 API 模式的补救入口：**只把 exb 打开**（不推命令、不等 CRX），
        /// 用于"`UpdateDraftViews` 其实要求目标图纸已打开"时的第二次尝试。
        /// </summary>
        public static void OpenExbOnly(IZBaseApp app, string exbPath, Action<string> log)
        {
            OpenExbAndWaitReady(app, exbPath, log, false);
        }

        /// <summary>
        /// 从图纸窗口标题里抠绝对路径：实机标题有两种形态
        /// `C:\...\SC08020102.01 壳体.exb`（全路径）与 `SC08020102.01 壳体.exb*`（只有文件名，* = 已修改）。
        /// 只有前者能直接用来调 API；后者返回空串，交由调用方请用户指定。
        /// </summary>
        private static string PathFromWindowTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            string t = title.Trim();
            while (t.EndsWith("*")) t = t.Substring(0, t.Length - 1).TrimEnd();
            if (t.Length > 1 && t[0] == '"' && t[t.Length - 1] == '"') t = t.Substring(1, t.Length - 2);
            try
            {
                if (Path.IsPathRooted(t) && t.EndsWith(".exb", StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            catch { }
            return "";
        }

        /// <summary>
        /// 图纸窗口标题只有文件名时，用「3D 场景文档所在目录 + 同名 exb」定位绝对路径。
        /// 认不到返回 null（调用方退回人工指定）。
        /// ★ 这条只是**便利定位**，不是权威：权威是 CRX 的 `fileName()`，所以查到就核对 `File.Exists`。
        /// </summary>
        private static string ResolveExbBySceneDir(IZBaseApp app, string windowTitle, Action<string> log)
        {
            string bare = FileNameFromWindowTitle(windowTitle);
            if (string.IsNullOrEmpty(bare) || !bare.EndsWith(".exb", StringComparison.OrdinalIgnoreCase))
                return null;

            string sceneName = null;
            try
            {
                IZDoc d = app.ActiveDoc;      // 图纸环境里恒回 3D 场景文档（坑 36）
                if (d != null) sceneName = d.Name;
            }
            catch (Exception ex)
            {
                log("更新3dbom：读活动文档失败（不影响后续定位）：" + ex.Message);
            }

            if (string.IsNullOrEmpty(sceneName))
            {
                log("更新3dbom：拿不到 3D 场景文档路径，无法按目录定位 exb");
                return null;
            }

            string dir = null;
            try { dir = Path.GetDirectoryName(sceneName); } catch { }
            log("更新3dbom：3D 场景文档 = " + sceneName + "；按同目录找 [" + bare + "]");
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                log("更新3dbom：3D 场景文档没有可用目录");
                return null;
            }

            string direct = Path.Combine(dir, bare);
            if (File.Exists(direct))
            {
                log("更新3dbom：同目录命中 exb = " + direct);
                return direct;
            }

            // 名字含中文/空格时，大小写与 Unicode 形态都可能不同 ⇒ 枚举目录做不区分大小写比对
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.exb"))
                {
                    if (string.Equals(Path.GetFileName(f), bare, StringComparison.OrdinalIgnoreCase))
                    {
                        log("更新3dbom：同目录命中 exb（枚举比对）= " + f);
                        return f;
                    }
                }
            }
            catch (Exception ex)
            {
                log("更新3dbom：枚举目录失败：" + ex.Message);
            }

            log("更新3dbom：同目录里没有 [" + bare + "]");
            return null;
        }

        /// <summary>标题里抠纯文件名：去首尾空白、去路径、去尾随 `*`（未保存标记）。</summary>
        private static string FileNameFromWindowTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            string t = title.Trim();
            if (t.Length > 1 && t[0] == '"' && t[t.Length - 1] == '"') t = t.Substring(1, t.Length - 2).Trim();
            while (t.EndsWith("*")) t = t.Substring(0, t.Length - 1).TrimEnd();
            int p = t.LastIndexOf('\\');
            if (p >= 0) t = t.Substring(p + 1);
            p = t.LastIndexOf('/');
            if (p >= 0) t = t.Substring(p + 1);
            return t;
        }

        // ============================================================ 装配体判定
        /// <summary>
        /// 当前场景是不是装配体（**以"当前选中的元素"为准**，用户 2026-11-04 16:2x 定案）。
        ///
        /// 判据顺序（越靠前越权威）：
        ///   ① **选中集**里第一个元素 `IZElement.Type == Z_ELEMENT_ASSEMBLY(2)` —— 这就是用户
        ///      "在设计树上选中装配体再点按钮"的手工做法，参考 caxa3dcodenameextraction 的
        ///      `SelectionMgr.GetSelectedElementsZArray() → IZArray.Count/Get → IZElement.Type`；
        ///   ② 没有选中时，退到 `GetTopElement() is IZAssembly`（场景唯一顶层元素）。
        ///
        /// ★ 不用 `GetBOMPropertyNameList(ref bool)` 判（2026-11-04 实机反证）★
        ///   实机日志 `装配体判定：GetBOMPropertyNameList → 零件（BOM 属性名 37 项）`，
        ///   而该文档明明是装配体。原因：那个 bool 是 **[in,out] 的"按装配还是按零件读属性名"
        ///   选择器，不是"是不是装配体"的返回值** —— 传 false 进去，它当然报"零件"。
        ///   把 [in,out] 参数当输出结果读，是本工程本轮的第二个"返回值与语义脱钩"（坑 37）。
        ///
        /// ★ **不穿透**（需求原文）：只判选中元素自己，不递归子件、不看子装配。
        /// 外链 / 虚拟件不做任何额外限制。
        /// </summary>
        /// <param name="assemblyName">命中装配体时回填它的元素名（用于定位同名 exb）</param>
        /// <param name="linkedPath">该装配体的外链源（.ics 全路径）；非外链/虚拟件回空串 —— 决定目标 exb 的目录</param>
        private static bool? DetectAssembly(IZSceneDoc scene, Action<string> log,
            out string assemblyName, out string linkedPath)
        {
            assemblyName = "";
            linkedPath = "";

            // ── ① 选中集 ──
            IZElement picked = null;
            IZElement firstAny = null;
            try
            {
                var sm = scene.SelectionMgr;
                bool has = false;
                try { has = sm != null && sm.SelectionsAvailable(); } catch { }
                if (sm != null && has)
                {
                    int total = 0;
                    IZArray arr = null;
                    try { arr = sm.GetSelectedElementsZArray(); } catch { }
                    if (arr != null)
                    {
                        try { arr.Count(out total); } catch { }
                        for (int i = 0; i < total; i++)
                        {
                            object o = null;
                            try { arr.Get(i, out o); } catch { }
                            var e = o as IZElement;
                            if (e == null) continue;
                            if (firstAny == null) firstAny = e;
                            eZElementType t0;
                            try { t0 = e.Type; } catch { continue; }
                            if (t0 == eZElementType.Z_ELEMENT_ASSEMBLY) { picked = e; break; }
                        }
                    }
                    if (arr == null || total == 0)
                    {
                        // 回退：GetSelectedElements() 回 VARIANT(SAFEARRAY)
                        try
                        {
                            var raw = sm.GetSelectedElements() as Array;
                            if (raw != null)
                                foreach (object o in raw)
                                {
                                    var e = o as IZElement;
                                    if (e == null) continue;
                                    if (firstAny == null) firstAny = e;
                                    eZElementType t0;
                                    try { t0 = e.Type; } catch { continue; }
                                    if (t0 == eZElementType.Z_ELEMENT_ASSEMBLY) { picked = e; break; }
                                }
                        }
                        catch (Exception ex) { log("装配体判定：GetSelectedElements 回退失败（" + ex.Message + "）"); }
                    }
                }
                log("装配体判定：选中集 " + (has ? "非空" : "为空"));
            }
            catch (Exception ex)
            {
                log("装配体判定：读取选中集失败（" + ex.Message + "），改看顶层元素");
            }

            IZElement target = picked != null ? picked : firstAny;
            if (target != null)
            {
                eZElementType t = eZElementType.Z_ELEMENT_UNKNOWN;
                try { t = target.Type; } catch (Exception ex) { log("装配体判定：读取 Type 失败（" + ex.Message + "）"); }
                string nm = "";
                try { nm = target.Name ?? ""; } catch { }
                bool isAsm = t == eZElementType.Z_ELEMENT_ASSEMBLY;
                log("装配体判定：选中元素「" + nm + "」Type=" + (int)t + "(" + t + ") → " +
                    (isAsm ? "装配体" : "不是装配体"));
                if (isAsm)
                {
                    assemblyName = nm;
                    bool lk = false;
                    linkedPath = LinkRenameSync.LinkedFilePath(target, out lk);
                    if (!lk) linkedPath = "";
                    log("装配体判定：「" + nm + "」外链源 = " +
                        (string.IsNullOrEmpty(linkedPath) ? "(虚拟件，无外链源)" : "[" + linkedPath + "]"));
                    return true;
                }
                // 选中的不是装配体：明确拒绝，不"穿透"去找它爹、也不改判其它选中项
                return false;
            }

            // ── ② 没有选中 → 顶层元素兜底 ──
            try
            {
                IZElement top = scene.GetTopElement();
                if (top == null)
                {
                    log("装配体判定：既没有选中元素，GetTopElement 也回 null");
                    return null;
                }
                bool isAsm = top is IZAssembly;
                string nm = "";
                try { nm = top.Name ?? ""; } catch { }
                log("装配体判定：未选中，改用顶层元素「" + nm + "」→ " +
                    (isAsm ? "IZAssembly（装配体）" : "非 IZAssembly（零件）"));
                if (isAsm)
                {
                    assemblyName = nm;
                    bool lkTop = false;
                    linkedPath = LinkRenameSync.LinkedFilePath(top, out lkTop);
                    if (!lkTop) linkedPath = "";
                }
                return isAsm;
            }
            catch (Exception ex)
            {
                log("装配体判定：GetTopElement 失败：" + ex.Message);
                return null;
            }
        }

        // ============================================================ 打开同名 exb

        /// <summary>
        /// 用 `OpenFile2` 打开同名 exb，并等图纸模块（CRX）就绪。
        ///
        /// ★ 已知口径（caxa3dCXDFTViewUpdate 实锤）：`OpenFile2` 热路径下**正常也抛异常**
        ///   （"Invalid file format."），文档其实已打开 ⇒ 异常只进日志，不判失败；
        ///   是否真开好由 CRX 侧按 `fileName()` + 图幅表回查（这才是权威信号）。
        /// ★ 打开后**绝不关闭**：这张 exb 就是要更新明细的目标。
        /// </summary>
        internal static void OpenExbAndWaitReady(IZBaseApp app, string exbPath, Action<string> log, bool waitCrx)
        {
            string targetName = "";
            try { targetName = Path.GetFileName(exbPath); } catch { }

            int beforeDraft = DrawingProbe.ListDraftDocs().Count;
            log("更新3dbom：打开前已有 Draft 图纸窗口 " + beforeDraft + " 个");

            // ★ 先看目标图是不是**本来就已经打开**（11-04 19:30 实机：这里白等了 20 秒）★
            //   旧判据是"窗口数增加"，可目标图已打开时计数永远不变 ⇒ 死等满 20 秒超时，
            //   用户看到的就是"从设计环境跳到图纸环境后有明显延迟"。
            //   已打开就既不用 OpenFile2（避免它抛 "Invalid file format."），也不用等。
            IntPtr already = DrawingProbe.FindDraftDocByFileName(targetName);
            if (already != IntPtr.Zero)
            {
                log("更新3dbom：目标图纸已在图纸环境打开（hwnd=0x" + already.ToString("X8") +
                    "）→ 跳过 OpenFile2，不做任何等待");
            }
            else
            {
                bool alreadyOpen = false;
                log("更新3dbom：入口=OpenFile2 打开 " + exbPath);
                PumpMessages(300);
                var sw = Stopwatch.StartNew();
                try
                {
                    app.OpenFile2(exbPath, false, true, out alreadyOpen);
                }
                catch (Exception ex)
                {
                    log("更新3dbom：OpenFile2 返回异常（热路径下正常，继续按 CRX 回查判定）：" + ex.Message);
                }
                log("更新3dbom：OpenFile2 用时 " + sw.ElapsedMilliseconds + " ms，pbAlreadyOpen=" + alreadyOpen);

                // ★ 判据换成"目标文件的窗口存在"，而不是"窗口数增加" ★
                //   宿主把已打开的文档再打开一次时，是"激活"而不是"新开"，计数不变；
                //   而目标图一旦真的就绪，标题里必然出现它的文件名 —— 这才是可靠信号。
                var sw2 = Stopwatch.StartNew();
                bool appeared = WaitUntil("等目标图纸窗口出现", DraftWindowTimeoutMs, delegate
                {
                    return DrawingProbe.FindDraftDocByFileName(targetName) != IntPtr.Zero;
                }, 200, log);
                log("更新3dbom：图纸窗口 " + beforeDraft + " → " + DrawingProbe.ListDraftDocs().Count +
                    "，目标图就绪=" + appeared + "，等待耗时 " + sw2.ElapsedMilliseconds + " ms");
            }

            if (!waitCrx)
            {
                log("更新3dbom：已跳过 CRX 就绪等待（纯 API 模式不需要桥）");
                return;
            }

            // CRX 随图纸模块初始化加载：必须等 marker 就绪，否则后台那一跳会立刻报"桥未加载"
            bool ready = WaitUntil("等图纸命令桥就绪", CrxReadyTimeoutMs, CrxBridge.IsCrxLoaded, 400, log);
            if (!ready)
                throw new InvalidOperationException(
                    "同名 exb 已提交打开，但图纸命令桥（CRX）25 秒内未就绪，已中止。" +
                    "请确认 exb 已在图纸环境打开，并查 %TEMP%\\caxa3dcodenameextraction_crx.log 是否含本次 On_kInitAppMsg。");
            log("更新3dbom：图纸命令桥已就绪，交给 CRX 确认图纸并推命令");
        }

        // ============================================================ 名字解析

        /// <summary>
        /// 从 `IZDoc.Name` 拆出目录、同名 exb 文件名、去后缀的基名。
        /// 实机口径（caxa3dCXDFTViewUpdate 实锤）：IRONCAD 把 exb 当场景加载时
        /// `IZDoc.Name` 形如 <c>零件2.exb.ics</c> ⇒ 去掉 .ics 后若已以 .exb 结尾，
        /// 那就是 exb 本身的名字，**别再补一个 .exb**（否则会变成 零件2.exb.exb）。
        /// </summary>
        private static void SplitDocName(string raw, out string dir, out string exbFileName, out string stem)
        {
            dir = "";
            exbFileName = "";
            stem = "";
            if (string.IsNullOrWhiteSpace(raw)) return;

            string n = raw.Trim();
            try { dir = Path.GetDirectoryName(n) ?? ""; } catch { dir = ""; }
            try { n = Path.GetFileName(n); } catch { }
            if (string.IsNullOrEmpty(n)) return;

            if (n.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
                n = n.Substring(0, n.Length - 4);
            if (n.EndsWith(".exb", StringComparison.OrdinalIgnoreCase))
            {
                exbFileName = n;
                stem = n.Substring(0, n.Length - 4);
            }
            else
            {
                exbFileName = n + ".exb";
                stem = n;
            }
        }

        // ============================================================ 小工具

        private static string Describe(ForegroundEnvKind kind, IntPtr hwnd, string title)
        {
            string k = kind == ForegroundEnvKind.Draft ? "图纸环境(exb)"
                     : kind == ForegroundEnvKind.Scene ? "设计环境(ics)"
                     : "无法判定";
            return k + " hwnd=0x" + hwnd.ToInt64().ToString("X8") + " title=[" + (title ?? "") + "]";
        }

        /// <summary>条件轮询（期间泵消息，避免阻塞 CAXA 主线程）。</summary>
        private static bool WaitUntil(string what, int timeoutMs, Func<bool> condition, int pollMs, Action<string> log)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try { Application.DoEvents(); } catch { }
                try { if (condition()) return true; } catch { }
                Thread.Sleep(pollMs);
            }
            log(what + ": 超时 " + timeoutMs + "ms");
            return false;
        }

        private static void PumpMessages(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                try { Application.DoEvents(); } catch { }
                Thread.Sleep(50);
            }
        }
    }
}
