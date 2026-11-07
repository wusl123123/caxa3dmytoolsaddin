// CAXA 3D 批量大师 —— 文档生命周期守卫（设计环境保活 / 打开归属判定 / 判脏保存 / 场景级重建）
// 通道：全部 RCW 强类型直调。本工程 Lib\interop.ICApiIronCAD.dll（v26.0.0.0）反射确认以下成员均 PRESENT，
//       因此不需要类型库派发或槽位直调；取证见 _probe-tools\out\caxa3dBatchMaster\interop_check_docguard.csv（2026-11-05 20:28）。
// 权威依据（本机 2027 members.csv 槽位 + ICAPIHELP 官方文档 + 已实测坑）：
//   · IZBaseApp::OpenFile2(槽 30) 末位 out pbAlreadyOpen —— 唯一能区分「本次新开」与「宿主已打开」的官方信号；
//     官方文档："Open an existing file, which can be hidden in background, currently, ONLY Scene file support hidden"
//     ⇒ vbVisible 传 true（外链件 visible=false 语义不明，已实测记为不通路径，不用）。
//   · 已实测坑：OpenFile2 可能「假失败」（hr=0x80004005 / 返回空指针但文件其实已打开）
//     ⇒ 恢复动作 = 泵消息 ~400ms + IZBaseApp::GetOpenDocByName(槽 9) 找回指针，且不据返回值判成败；
//     找回后 alreadyOpen 不可知 ⇒ 保守按「已打开」处理：绝不关闭。
//   · 已实测坑：IZBaseApp::CloseFile(槽 35) 对外链 .ics 是假成功（hr=0 但窗口/文档都还在）
//     ⇒ 关闭一律用 IZDoc::Close2(true)(槽 23)，官方文档 "Close the document without saving"，所以先 Save 再 Close2。
//   · 已实测坑：外链文档 Close2 后 CAXA 内部并不卸载（装配体仍引用它，卸载反而崩装配体）
//     ⇒ 禁用 GetOpenDocsCount / find 当「关闭判据」；本类只用 get_ActiveDoc(槽 11) 回读当「恢复判据」。
//   · IZDoc::get_Name(槽 11) 可能返回标题而非全路径（实机待验）⇒ 一律按「第三态」处理：读不到不判错，只降级证据。
// 2026-11-06 状态：用户要求「属性批量读取/写入直接在装配体设计环境内完成，不要再单独打开关闭」，
//   因此 Capture/Open/Finish/Restore 这条「逐文件开关文档」链目前没有调用方（BatchProcessor 的读写链已改为
//   WalkActiveAssembly 元素索引 + SaveActiveSceneIfModified）。这四个方法先保留，给「刷新工程图」链改造时复用；
//   真正在用的是：BeginReadOnlyScope（只读走查脏计数保护）、SaveActiveSceneIfModified（写完保存装配体一次）、
//   RebuildActiveSceneAndSave（导入后场景级重建并保存）、ActiveDocPath（零件文件时间证据的装配体侧）。
using System;
using System.IO;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    /// <summary>
    /// 一次批次的文档守卫。调用方负责在主线程（STA）使用：Capture → Open/Finish*N → Restore。
    /// 本类不做任何 UI，不弹 MessageBox，只写 %TEMP%\caxa3dBatchMaster_trace.log 与传入的日志委托。
    /// </summary>
    internal sealed class CaxaDocGuard
    {
        private readonly IZBaseApp _app;
        private readonly Action<string> _log;
        private readonly Func<bool> _stop;

        public CaxaDocGuard(IZBaseApp app, Action<string> log, Func<bool> stopCheck)
        {
            _app = app;
            _log = log ?? delegate { };
            _stop = stopCheck;
        }

        // ============================================================ 数据结构

        /// <summary>批次开始时的活动文档快照（IZDoc.get_Name 槽 11）。</summary>
        public sealed class Snapshot
        {
            /// <summary>活动文档名/路径；空字符串＝批次开始时没有活动文档（无需恢复）。</summary>
            public string Name = "";
            /// <summary>快照时活动文档是否 3D 场景文档。</summary>
            public bool WasScene;
            public bool HasTarget { get { return !string.IsNullOrWhiteSpace(Name); } }
        }

        /// <summary>一次打开的结果与归属判定。</summary>
        public sealed class OpenResult
        {
            public IZDoc Doc;
            public string Path = "";
            /// <summary>OpenFile2 的 out pbAlreadyOpen：true＝宿主早就打开着（本次没新增窗口）。</summary>
            public bool AlreadyOpen;
            /// <summary>true＝OpenFile2 抛异常/返回空后靠 GetOpenDocByName 找回，归属不可知（第三态）。</summary>
            public bool OwnershipUnknown;
            /// <summary>本次是否允许由本工具关闭该文档。</summary>
            public bool MayClose { get { return !AlreadyOpen && !OwnershipUnknown; } }
        }

        // ============================================================ 基础工具

        /// <summary>
        /// 主线程安全等待：分段 Sleep(50) + DoEvents，禁裸长 Sleep（铁律 8）。停止信号优先退出。
        /// 用途：OpenFile2/Save 后泵消息再取副作用证据（S_OK 只代表宿主接受了调用）。
        /// </summary>
        public void Pump(int ms)
        {
            if (ms <= 0) return;
            int waited = 0;
            try
            {
                while (waited < ms)
                {
                    if (_stop != null && _stop()) return;
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(50);
                    waited += 50;
                }
                Application.DoEvents();
            }
            catch (Exception ex) { Diagnostics.Trace("Guard.Pump EX: " + ex.Message); }
        }

        private static string SafeName(IZDoc doc)
        {
            if (doc == null) return "";
            try { return doc.Name ?? ""; }        // IZDoc::get_Name 槽 11
            catch (Exception ex) { Diagnostics.Trace("IZDoc.get_Name EX: " + ex.Message); return ""; }
        }

        /// <summary>路径比较：全路径优先，任一侧只是文件名/标题时降级为同名比较；异常返回 false（第三态）。</summary>
        private static bool SameDoc(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try
            {
                string fa = a.Trim().Trim('"');
                string fb = b.Trim().Trim('"');
                if (string.Equals(fa, fb, StringComparison.OrdinalIgnoreCase)) return true;
                if (Path.HasExtension(fa) && Path.HasExtension(fb))
                {
                    try
                    {
                        if (string.Equals(Path.GetFullPath(fa), Path.GetFullPath(fb), StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                    return string.Equals(Path.GetFileName(fa), Path.GetFileName(fb), StringComparison.OrdinalIgnoreCase);
                }
                return string.Equals(Path.GetFileNameWithoutExtension(fa),
                                     Path.GetFileNameWithoutExtension(fb), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) { Diagnostics.Trace("Guard.SameDoc EX: " + ex.Message); return false; }
        }

        private static DateTime SafeWriteTime(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return DateTime.MinValue;
                return File.GetLastWriteTime(path);
            }
            catch { return DateTime.MinValue; }
        }

        private static string TimeText(DateTime t)
        {
            return t == DateTime.MinValue ? "<不可得>" : t.ToString("yyyy-MM-dd HH:mm:ss");
        }

        /// <summary>活动文档（IZBaseApp::get_ActiveDoc 槽 11）；异常/null 一律返回 null。</summary>
        public IZDoc ActiveDocOrNull()
        {
            try { return _app == null ? null : _app.ActiveDoc; }
            catch (Exception ex) { Diagnostics.Trace("get_ActiveDoc EX: " + ex.Message); return null; }
        }

        public IZSceneDoc ActiveSceneOrNull()
        {
            IZDoc doc = ActiveDocOrNull();
            try { return doc as IZSceneDoc; }
            catch (Exception ex) { Diagnostics.Trace("ActiveDoc as IZSceneDoc EX: " + ex.Message); return null; }
        }

        /// <summary>活动文档的文件名/路径（IZDoc.get_Name 槽 11）；读不到返回空串＝第三态，不判错。</summary>
        public string ActiveDocPath()
        {
            return SafeName(ActiveDocOrNull());
        }

        // ============================================================ 批次收尾保存（设计环境内直接写属性用）

        /// <summary>
        /// 批次收尾的统一保存：只保存「当前活动文档」这一个（装配体设计环境），不再逐个零件文件 Save。
        /// 三道判据，逐条留证据：① get_Modified(槽 16) 为假就不调 Save；② IsReadOnly()(槽 8) 为真一律不调 Save
        /// （本工程没有弹窗代点器，只读文档 Save 会卡死在宿主模态「另存为」）；③ Save 后回读 get_Modified 与文件时间。
        /// 返回 true＝真的执行过 Save 且保存后脏标志已清。
        /// 【实机待验：装配体 Save 是否把共享属性回写进各零件 .ics —— 由 BatchProcessor 的零件文件时间对比判，不由这里背书】
        /// </summary>
        public bool SaveActiveSceneIfModified(string what)
        {
            IZDoc doc = ActiveDocOrNull();
            if (doc == null)
            {
                _log("保存跳过：" + what + " 结束时没有活动文档。");
                Diagnostics.Trace("SaveActiveSceneIfModified: ActiveDoc=null (" + what + ")");
                return false;
            }
            string path = SafeName(doc);

            bool modified;
            try { modified = doc.Modified; }
            catch (Exception ex)
            {
                modified = false;
                Diagnostics.Trace("IZDoc.get_Modified EX(按未修改处理) " + what + ": " + ex.Message);
            }
            if (!modified)
            {
                _log("未保存：写完属性后宿主把设计环境文档标为「未修改」（get_Modified=false），本工具不调 Save。"
                    + "若界面属性没变，说明写入本身没生效，看 trace 里 SetValueByName/AddCustomProp 的 hr。");
                Diagnostics.Trace("SaveActiveSceneIfModified: Modified=false，跳过 Save；path=" + (string.IsNullOrEmpty(path) ? "<读不到>" : path));
                return false;
            }

            bool? readOnly = TryIsReadOnly(doc);
            if (readOnly == true)
            {
                _log("设计环境文档为只读，已跳过保存（属性写入结果不会落盘；如需落盘请先解除只读或在 CAXA 里另存）。");
                Diagnostics.Trace("SaveActiveSceneIfModified: IsReadOnly=true，跳过 Save");
                return false;
            }
            if (readOnly == null) Diagnostics.Trace("IZDoc.IsReadOnly 读不到（第三态），按可写继续 Save");
            return SaveWithEvidence(doc, path);
        }

        // ============================================================ 只读脏计数保护

        /// <summary>
        /// 只读走查期间的「场景脏计数」保护：进入时 DisableDirtyCounter(槽 66)、离开时 EnableDirtyCounter(槽 67)。
        /// 官方文档原文："Disable the Scene dirty counter. It is CRITICAL that EnableDirtyCounter is called at the end of the operation."
        /// ⇒ Dispose 必须执行，即使中途抛异常；同时把前后脏计数写进 trace 作取证。
        /// </summary>
        public sealed class ReadOnlyScope : IDisposable
        {
            private readonly IZSceneDoc _scene;
            private readonly CaxaDocGuard _owner;
            private readonly string _what;
            public uint DirtyBefore;
            public uint DirtyAfter;
            public bool CounterKnown;
            private bool _done;

            internal ReadOnlyScope(CaxaDocGuard owner, IZSceneDoc scene, string what)
            {
                _owner = owner; _scene = scene; _what = what;
            }

            public void Dispose()
            {
                if (_done) return;
                _done = true;
                if (_scene == null) return;
                try { _scene.GetDirtyCounter(out DirtyAfter); CounterKnown = true; }
                catch (Exception ex) { Diagnostics.Trace("GetDirtyCounter(after) EX: " + ex.Message); }
                try { _scene.EnableDirtyCounter(); }   // 槽 67，官方要求必须成对调用
                catch (Exception ex) { Diagnostics.Trace("EnableDirtyCounter EX: " + ex.Message); }
                string d = CounterKnown
                    ? ("脏计数 " + DirtyBefore + "→" + DirtyAfter + (DirtyAfter == DirtyBefore ? "（只读未弄脏场景）" : "（只读期间脏计数上升，宿主确实改动了场景）"))
                    : "脏计数 <不可得>";
                Diagnostics.Log("[只读取证] " + _what + "：" + d);
            }
        }

        /// <summary>为一次只读走查开脏计数保护（无活动场景文档时返回一个空作用域，调用方用 using 即可）。</summary>
        public ReadOnlyScope BeginReadOnlyScope(string what)
        {
            IZSceneDoc scene = ActiveSceneOrNull();
            var scope = new ReadOnlyScope(this, scene, what);
            if (scene == null)
            {
                Diagnostics.Trace("[只读取证] " + what + "：无活动 3D 场景文档，脏计数保护跳过");
                return scope;
            }
            try { scene.GetDirtyCounter(out scope.DirtyBefore); scope.CounterKnown = true; }   // 槽 51
            catch (Exception ex) { Diagnostics.Trace("GetDirtyCounter(before) EX: " + ex.Message); }
            try { scene.DisableDirtyCounter(); }                                                // 槽 66
            catch (Exception ex)
            {
                Diagnostics.Trace("DisableDirtyCounter EX: " + ex.Message);
                scope.CounterKnown = false;   // 关不掉就如实说明，不谎报保护生效
            }
            return scope;
        }

        // ============================================================ 快照 / 打开 / 收尾

        /// <summary>批次开始前拍活动文档（=装配体设计环境）。</summary>
        public Snapshot Capture()
        {
            var s = new Snapshot();
            IZDoc doc = ActiveDocOrNull();
            if (doc != null)
            {
                s.Name = SafeName(doc);
                try { s.WasScene = doc is IZSceneDoc; } catch { s.WasScene = false; }
            }
            Diagnostics.Trace("Guard.Capture: ActiveDoc=" + (string.IsNullOrEmpty(s.Name) ? "<无>" : s.Name)
                + " isScene=" + s.WasScene);
            return s;
        }

        /// <summary>
        /// 打开文件并判定归属：已打开的不关、不存；本次新开才由 Finish 收尾。
        /// OpenFile2 假失败时按已实测恢复法找回指针，并把归属标成「不可知」（保守：不关）。
        /// </summary>
        public OpenResult Open(string fullPath)
        {
            var r = new OpenResult { Path = fullPath ?? "" };
            if (_app == null || string.IsNullOrEmpty(fullPath)) return r;
            if (!File.Exists(fullPath)) { Diagnostics.Trace("Guard.Open: 文件不存在 " + fullPath); return r; }

            bool already = false;
            try
            {
                r.Doc = _app.OpenFile2(fullPath, false, true, out already);   // 槽 30
                r.AlreadyOpen = already;
                Diagnostics.Trace("OpenFile2 ok alreadyOpen=" + already + " doc=" + (r.Doc == null ? "null" : "非空")
                    + " <- " + Path.GetFileName(fullPath));
            }
            catch (Exception ex)
            {
                r.OwnershipUnknown = true;
                Diagnostics.Trace("OpenFile2 EX(假失败候选) " + Path.GetFileName(fullPath) + ": " + ex.Message);
            }

            if (r.Doc == null)
            {
                Pump(r.OwnershipUnknown ? 400 : 150);
                try { r.Doc = _app.GetOpenDocByName(fullPath); }             // 槽 9，按完整路径
                catch (Exception ex) { Diagnostics.Trace("GetOpenDocByName EX: " + ex.Message); }
                if (r.Doc != null)
                {
                    r.OwnershipUnknown = true;   // 找回了但不知道是谁打开的 ⇒ 不关
                    Diagnostics.Trace("GetOpenDocByName 找回 " + Path.GetFileName(fullPath) + "（归属不可知，按已打开处理：不关）");
                }
                else
                {
                    Diagnostics.Log("打不开文件：" + fullPath
                        + "（OpenFile2 与 GetOpenDocByName 两路都没拿到文档指针，本行跳过）");
                    r.Doc = null;
                }
            }
            return r;
        }

        /// <summary>
        /// 收尾：判脏保存（saveWhenModified 时）→ 只有本次新开且归属明确才 Close2(true)。
        /// 已打开/归属不可知的文档一律不关；是否保存只由 IZDoc.get_Modified(槽 16) 决定（判脏保存）。
        /// </summary>
        public void Finish(OpenResult o, bool saveWhenModified)
        {
            if (o == null || o.Doc == null) return;
            bool modified;
            try { modified = o.Doc.Modified; }                                // 槽 16
            catch (Exception ex)
            {
                modified = false;
                Diagnostics.Trace("IZDoc.get_Modified EX(按未修改处理) " + Path.GetFileName(o.Path) + ": " + ex.Message);
            }

            if (modified)
            {
                if (!saveWhenModified)
                {
                    Diagnostics.Log("[取证] " + Path.GetFileName(o.Path)
                        + " 纯读之后被宿主标记为「已修改」；本工具不保存、不改动它（若反复出现需查是哪一步弄脏了文档）");
                }
                else if (TryIsReadOnly(o.Doc) == true)
                {
                    Diagnostics.Log("跳过保存：" + Path.GetFileName(o.Path)
                        + " 是只读文档（写入结果不会落盘；如需落盘请先解除只读或在 CAXA 里另存）");
                }
                else SaveWithEvidence(o.Doc, o.Path);
            }

            if (o.MayClose)
            {
                try { o.Doc.Close2(true); }                                   // 槽 23：关闭且不保存（Save 已在上面按脏判定做过）
                catch (Exception ex) { Diagnostics.Log("关闭文档失败：" + Path.GetFileName(o.Path) + " -> " + ex.Message); }
            }
            else
            {
                Diagnostics.Trace("不关闭：" + Path.GetFileName(o.Path)
                    + (o.OwnershipUnknown ? "（归属不可知）" : "（宿主已打开）"));
            }
        }

        /// <summary>保存一个文档，并用文件最后写入时间 + 保存后脏标志作权威副作用证据。</summary>
        public bool SaveWithEvidence(IZDoc doc, string path)
        {
            DateTime before = SafeWriteTime(path);
            try { doc.Save(); }                                               // 槽 9：Save by default name
            catch (Exception ex)
            {
                Diagnostics.Log("保存失败：" + (string.IsNullOrEmpty(path) ? "<路径不可得>" : path) + " -> " + ex.Message);
                return false;
            }
            Pump(300);
            DateTime after = SafeWriteTime(path);
            bool modAfter;
            try { modAfter = doc.Modified; } catch { modAfter = true; }
            string ev;
            if (after != DateTime.MinValue && before != DateTime.MinValue && after > before)
                ev = "文件时间 " + TimeText(before) + "→" + TimeText(after) + "（已落盘）";
            else if (after == DateTime.MinValue)
                ev = "文件时间不可得（IZDoc.Name 未返回全路径，第三态）；保存后 get_Modified=" + modAfter;
            else
                ev = "文件时间未变（" + TimeText(after) + "）；保存后 get_Modified=" + modAfter
                     + (modAfter ? "＝仍未落盘" : "＝标志已清但时间未变，按第三态记账");
            Diagnostics.Log("[保存取证] " + (string.IsNullOrEmpty(path) ? "<文档名不可得>" : Path.GetFileName(path)) + "：" + ev);
            return !modAfter;
        }

        // ============================================================ 恢复设计环境

        /// <summary>
        /// 批次结束后恢复设计环境（用户要求：装配体不能因为批量读取被关掉）。三级阶梯，每级都用回读判据：
        ///   1) ActiveDoc 还是目标文档 → 什么都没丢，直接返回；
        ///   2) GetOpenDocByName 找得到 → set_ActiveDoc(槽 12) 激活，回读 ActiveDoc.Name 一致才算成；
        ///   3) 找不到（真的被关了）→ OpenFile2 重新打开 + 泵消息 + 回读；仍不一致就如实报失败并给出路径让用户手动打开。
        /// </summary>
        public bool Restore(Snapshot snap)
        {
            if (snap == null || !snap.HasTarget)
            {
                Diagnostics.Trace("Guard.Restore: 批次开始前没有活动文档，无需恢复");
                return true;
            }
            string file = SafeFileDisplay(snap.Name);

            IZDoc cur = ActiveDocOrNull();
            if (cur != null && SameDoc(SafeName(cur), snap.Name))
            {
                _log("设计环境完好：装配体「" + file + "」仍在活动状态（未被关闭）。");
                return true;
            }

            // 阶梯 2：文档还在表内，只是不再是活动文档
            IZDoc found = FindByName(snap.Name);
            if (found != null)
            {
                try { _app.ActiveDoc = found; }                               // 槽 12 set_ActiveDoc
                catch (Exception ex) { Diagnostics.Trace("set_ActiveDoc EX: " + ex.Message); }
                Pump(200);
                string back = SafeName(ActiveDocOrNull());
                if (SameDoc(back, snap.Name))
                {
                    _log("设计环境已恢复激活：装配体「" + file + "」（文档未卸载，只是失去活动状态）。");
                    return true;
                }
                Diagnostics.Trace("set_ActiveDoc 回读不一致 back=" + (string.IsNullOrEmpty(back) ? "<空>" : back)
                    + " expect=" + snap.Name + " → 进入重开阶梯");
            }

            // 阶梯 3：重新打开
            _log("装配体设计环境已不在活动文档中，正在重新打开：" + file);
            OpenResult re = Open(snap.Name);
            if (re.Doc == null)
            {
                _log("恢复失败：无法重新打开装配体「" + file + "」，请在 CAXA 3D 里手动打开它"
                    + (string.IsNullOrWhiteSpace(snap.Name) ? "" : "（路径：" + snap.Name + "）"));
                return false;
            }
            if (SameDoc(SafeName(ActiveDocOrNull()), snap.Name))
            {
                _log("已重新打开装配体设计环境：" + file);
                return true;
            }
            try { _app.ActiveDoc = re.Doc; } catch (Exception ex) { Diagnostics.Trace("set_ActiveDoc(重开后) EX: " + ex.Message); }
            Pump(200);
            bool ok = SameDoc(SafeName(ActiveDocOrNull()), snap.Name);
            _log(ok
                ? "已重新打开并激活装配体设计环境：" + file
                : "已发出打开命令但活动文档回读不一致（第三态）：" + file + "，请确认界面是否已显示该装配体");
            return ok;
        }

        private IZDoc FindByName(string name)
        {
            if (_app == null || string.IsNullOrWhiteSpace(name)) return null;
            try
            {
                IZDoc d = _app.GetOpenDocByName(name);                        // 槽 9
                if (d != null) return d;
            }
            catch (Exception ex) { Diagnostics.Trace("GetOpenDocByName(恢复) EX: " + ex.Message); }
            Pump(400);
            try { return _app.GetOpenDocByName(name); }
            catch (Exception ex) { Diagnostics.Trace("GetOpenDocByName(恢复·重试) EX: " + ex.Message); return null; }
        }

        private static string SafeFileDisplay(string name)
        {
            try { return string.IsNullOrEmpty(name) ? "<无名>" : Path.GetFileNameWithoutExtension(name); }
            catch { return name; }
        }

        // ============================================================ 场景级重建并保存（用户指定：导入后执行）

        /// <summary>
        /// 场景级重建当前 3D 场景并保存装配体文件。每一步单独 try/catch 并把异常原文写 trace
        /// （官方文档里 IZSceneDoc::RegenerateParts 标注 NOT YET IMPLEMENTED 的是 bAllParts=false 分支，故传 true）。
        /// 判成功不看返回值，看这组权威副作用：元素数（GetChildrenElementsCount 槽 18）不减、
        /// 脏计数回落、get_Modified 由 true→false、装配体文件 LastWriteTime 变新。
        /// </summary>
        public bool RebuildActiveSceneAndSave()
        {
            IZDoc doc = ActiveDocOrNull();
            IZSceneDoc scene = doc as IZSceneDoc;
            if (scene == null)
            {
                _log("场景重建跳过：当前没有活动的 3D 场景文档（请先在设计环境打开装配体）。");
                Diagnostics.Trace("Rebuild: ActiveDoc=" + (doc == null ? "null" : "非场景文档"));
                return false;
            }

            string path = SafeName(doc);
            string file = SafeFileDisplay(path);
            int childBefore = SafeChildCount(scene);
            uint dirtyBefore; bool knownBefore = TryDirty(scene, out dirtyBefore);
            DateTime mtimeBefore = SafeWriteTime(path);
            bool modBefore = SafeModified(doc);

            _log("开始场景级重建：" + file);

            // ① 重建装配体下所有零件（IZSceneDoc::RegenerateParts 槽 21，官方文档：bAllParts=false 分支未实现 ⇒ 传 true）
            Step("RegenerateParts(true)", delegate { scene.RegenerateParts(true); });
            // ② 顶层装配体自身更新（IZAssembly::Update 槽 7）
            IZElement top = FirstTopElement(scene);
            IZAssembly topAsm = null;
            try { topAsm = top as IZAssembly; } catch { }
            if (topAsm != null) Step("IZAssembly.Update(顶层装配体)", delegate { topAsm.Update(); });
            else Diagnostics.Trace("Rebuild: 顶层元素不是装配体（" + (top == null ? "null" : SafeElemName(top)) + "），跳过 IZAssembly.Update");
            // ③ 场景更新（IZSceneDoc::Update 槽 36，官方文档 "Update scene."）
            Step("Scene.Update", delegate { scene.Update(); });
            // ④ 设计树/图形刷新（options=0 即 eZDrawOption.Z_DRAW_DEFAULT=0；官方文档：UpdateBrowser "currently options = 0"）
            Step("Scene.UpdateBrowser(0=Z_DRAW_DEFAULT)", delegate { scene.UpdateBrowser(0); });
            Step("Scene.UpdateGraphics(0=Z_DRAW_DEFAULT)", delegate { scene.UpdateGraphics(0); });
            // ⑤ 强制重画视口（IZSceneDoc::Redraw 槽 34，官方文档 "Force the scene to be redrawn."）
            Step("Scene.Redraw", delegate { scene.Redraw(); });

            // ⑥ 保存装配体（先泵 ~1.5 s 再取副作用，S_OK 只代表宿主接受了调用）
            //     预检：文档只读时不调 Save——宿主对只读/未命名文档的 Save 可能弹「另存为」模态框，
            //     模态框会占死主线程（本工程没有弹窗代点器），宁可如实跳过并留证据。
            bool? readOnly = TryIsReadOnly(doc);
            bool saved;
            if (readOnly == true)
            {
                _log("装配体文档为只读，已跳过保存（重建仍已执行）；如需落盘请先在 CAXA 里解除只读或另存。");
                saved = false;
            }
            else
            {
                if (readOnly == null) Diagnostics.Trace("IZDoc.IsReadOnly 读不到（第三态），按可写继续 Save");
                saved = SaveWithEvidence(doc, path);
            }

            Pump(1200);
            int childAfter = SafeChildCount(scene);
            uint dirtyAfter; bool knownAfter = TryDirty(scene, out dirtyAfter);
            bool modAfter = SafeModified(doc);
            DateTime mtimeAfter = SafeWriteTime(path);

            string dirtyText = (knownBefore && knownAfter)
                ? ("脏计数 " + dirtyBefore + "→" + dirtyAfter)
                : "脏计数 <不可得>";
            string childText = "元素数 " + (childBefore < 0 ? "<不可得>" : childBefore.ToString())
                + "→" + (childAfter < 0 ? "<不可得>" : childAfter.ToString());
            string modText = "已修改标志 " + modBefore + "→" + modAfter;
            string mtText = (mtimeBefore == DateTime.MinValue || mtimeAfter == DateTime.MinValue)
                ? "文件时间 <不可得（IZDoc.Name 未给全路径，第三态）>"
                : ("文件时间 " + TimeText(mtimeBefore) + "→" + TimeText(mtimeAfter)
                   + (mtimeAfter > mtimeBefore ? "（已落盘）" : "（未变，未判成功）"));
            bool ok = saved && (childAfter < 0 || childBefore < 0 || childAfter >= childBefore)
                      && !modAfter && (mtimeAfter > mtimeBefore || mtimeBefore == DateTime.MinValue);
            _log("场景重建收尾：" + file + "｜" + childText + "｜" + dirtyText + "｜" + modText + "｜" + mtText
                + "｜判定=" + (ok ? "已重建并保存" : "未确认（见 trace 分步日志）"));
            Diagnostics.Trace("Rebuild verdict ok=" + ok);
            return ok;
        }

        /// <summary>重建链的分步执行：任何一步抛异常都只记账，不中断后面的步骤。</summary>
        private void Step(string what, Action act)
        {
            try { act(); Diagnostics.Trace("Rebuild step ok: " + what); }
            catch (Exception ex)
            {
                string t = ex.Message;
                string tag = t.IndexOf("0x80004001", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "（HRESULT 0x80004001 = 宿主未实现该 API）" : "";
                Diagnostics.Trace("Rebuild step FAIL: " + what + " -> " + t);
                _log("重建步骤「" + what + "」未完成：" + t + tag);
            }
        }

        private static int SafeChildCount(IZSceneDoc scene)
        {
            try { return scene.GetChildrenElementsCount(); }                  // 槽 18
            catch (Exception ex) { Diagnostics.Trace("GetChildrenElementsCount EX: " + ex.Message); return -1; }
        }

        private static bool TryDirty(IZSceneDoc scene, out uint value)
        {
            value = 0u;
            try { scene.GetDirtyCounter(out value); return true; }            // 槽 51
            catch (Exception ex) { Diagnostics.Trace("GetDirtyCounter EX: " + ex.Message); return false; }
        }

        private static bool SafeModified(IZDoc doc)
        {
            try { return doc != null && doc.Modified; }                       // 槽 16
            catch (Exception ex) { Diagnostics.Trace("IZDoc.get_Modified EX: " + ex.Message); return false; }
        }

        /// <summary>文档是否只读（IZDoc::IsReadOnly 槽 8；本工程 interop v26 反射确认它是**方法** `bool IsReadOnly()` 而不是属性）。
        /// 返回 null＝读不到，按第三态处理（调用方自行决定，不谎报）。</summary>
        private static bool? TryIsReadOnly(IZDoc doc)
        {
            if (doc == null) return null;
            try { return doc.IsReadOnly(); }
            catch (Exception ex)
            {
                Diagnostics.Trace("IZDoc.IsReadOnly EX(第三态): " + ex.Message);
                return null;
            }
        }

        /// <summary>场景顶层第一个零部件元素（拿 IZAssembly 用；读不到返回 null＝第三态）。</summary>
        private IZElement FirstTopElement(IZSceneDoc scene)
        {
            try
            {
                object raw = scene.GetChildElements();                        // 槽 7
                foreach (object o in CaxaBatchService.UnpackArray(raw))
                {
                    IZElement e = o as IZElement;
                    if (e != null && CaxaBatchService.IsPartKind(e)) return e;
                }
            }
            catch (Exception ex) { Diagnostics.Trace("FirstTopElement EX: " + ex.Message); }
            return null;
        }

        private static string SafeElemName(IZElement e)
        {
            try { return e == null ? "<null>" : (e.Name ?? "<空>"); }
            catch { return "<读不到>"; }
        }
    }
}
