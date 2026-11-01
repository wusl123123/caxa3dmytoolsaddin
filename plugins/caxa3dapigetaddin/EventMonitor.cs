using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dapigetaddin
{
    /// <summary>
    /// 进程内版本的事件订阅器。
    /// 与独立 EXE 版的差异：
    ///   - 不再使用 vtable 槽位直调，直接用 IZBaseApp / IZDoc 的类型化属性取事件源；
    ///   - 连接点（IConnectionPointContainer.FindConnectionPoint + Advise）挂接机制不变；
    ///   - 所有事件接收器（AppEventsSink / DocEventsSink / ...）与 EXE 版完全相同。
    /// 订阅接口 DIID 取自类型库 ICApiIronCAD.tlb，Guid 必须精确匹配（否则 Advise 返回 CONNECT_E_CANNOTCONNECT）。
    /// </summary>
    internal sealed class EventMonitor : IDisposable
    {
        // 事件接口 DIID（= 各自 dispinterface 的 IID）—— 与 EXE 版完全一致
        private static readonly Guid DiidApp              = new Guid("BF00132F-57F2-458B-A734-D9E0C883C525");
        private static readonly Guid DiidDoc              = new Guid("0E18FE8F-174F-4D46-AB72-C8A65D783FE4");
        private static readonly Guid DiidOperation        = new Guid("2D640B7A-511A-46CD-8050-958AEA611416");
        private static readonly Guid DiidHandle           = new Guid("32CCD216-A79E-4E32-BB57-5E914B57686D");
        private static readonly Guid DiidCatalog          = new Guid("40A3FE22-B015-421B-AF36-85EE3C9D1BAF");
        private static readonly Guid DiidDraw             = new Guid("10388D65-F5B8-484C-8A3B-AE3BBCCEA5BE");
        private static readonly Guid DiidSmartAssemblyEx  = new Guid("72D765CB-CC07-4A70-8427-FE3E72B85BAE");
        private static readonly Guid DiidMechanismMode    = new Guid("BA24D8C4-DD72-4219-8926-D7F65D8CE33A");
        private static readonly Guid DiidDrawingDoc       = new Guid("5EF3E988-1529-4AF2-B0BD-F87407F28C93");

        // 标准 COM 接口 GUID
        private static readonly Guid IID_IConnectionPointContainer =
            new Guid("B196B284-BAB4-101A-B69C-00AA00341D07");

        private readonly IZBaseApp _app;
        private readonly SinkContext _ctx;
        private readonly object _gate = new object();
        private bool _disposed;

        // 接收器实例
        private AppEventsSink _appSink;
        private CatalogEventsSink _sceneCatSink, _dwgCatSink;
        private DocEventsSink _docSink;
        private OperationEventsSink _opSink;
        private HandleEventsSink _hdSink;
        private DrawEventsSink _drawSink;
        private SmartAssemblyExEventsSink _smartSink;
        private MechanismModeEventsSink _mechSink;
        private DrawingDocEventsSink _dwgSink;

        // 连接点 + cookie
        private IConnectionPoint _appCp; private int _appCookie = -1;
        private IConnectionPoint _sceneCatCp; private int _sceneCatCookie = -1;
        private IConnectionPoint _dwgCatCp; private int _dwgCatCookie = -1;
        private IConnectionPoint _docCp; private int _docCookie = -1;
        private IConnectionPoint _opCp; private int _opCookie = -1;
        private IConnectionPoint _hdCp; private int _hdCookie = -1;
        private IConnectionPoint _drawCp; private int _drawCookie = -1;
        private IConnectionPoint _smartCp; private int _smartCookie = -1;
        private IConnectionPoint _mechCp; private int _mechCookie = -1;
        private IConnectionPoint _dwgCp; private int _dwgCookie = -1;

        private string _docName;
        private string _docIdentity;

        // ---- 图纸环境（工程图环境）状态跟踪：COM 层无实体级事件，靠轮询计数器/快照取变化 ----
        private bool _inDrawingEnv;
        private uint _lastDwgNotify;
        private uint _lastBomNotify;
        private string _lastSheetLabel;
        private int _lastSheetCount = -1;
        private int _lastViewCount = -1;
        /// <summary>计数器/快照的基线是否已登记。首次进入图纸文档只记基线、不报 delta，避免误报一个大增量。</summary>
        private bool _dwgBaselineSet;
        /// <summary>上一次轮询到的图纸文档标识（FileGUID ?? Name），用于切换图纸文档时重置基线。</summary>
        private string _lastDrawingDocId;
        /// <summary>诊断日志节流：key → 上次打印时间，避免每 2 秒刷屏。</summary>
        private readonly Dictionary<string, DateTime> _timestamps = new Dictionary<string, DateTime>();

        // ---- CRX 日志轮询 ----
        /// <summary>CRX 桥接日志完整路径（CRX .crx 模块 append 写入，Add-in 读增量）。</summary>
        private static readonly string CrxLogPath = Path.Combine(Path.GetTempPath(), "caxa3d_crx_events.log");
        /// <summary>CRX 日志上次读到的文件位置（字节偏移），实现 tail -f。</summary>
        private long _crxLogOffset;
        /// <summary>CRX 日志文件上次读取时间；如果超过 30 秒没新内容，可能 CRX 没加载。</summary>
        private DateTime _crxLastRead = DateTime.MinValue;

        /// <summary>实时事件回调：category=中文分类，api=API 签名，args=实参文本。可能在非 UI 线程触发。</summary>
        public event Action<string, string, string> ApiEvent;

        /// <summary>应用级事件期望的文档级事件重挂请求（由 UI 线程执行）。</summary>
        public event Action DocReattachRequested;

        public bool IsAppAttached { get { lock (_gate) return _appCookie >= 0; } }
        public bool IsDocAttached { get { lock (_gate) return _docCookie >= 0; } }
        public string DocName { get { lock (_gate) return _docName; } }
        public string DocIdentity { get { lock (_gate) return _docIdentity; } }

        public EventMonitor(IZBaseApp app)
        {
            _app = app;
            _startupTime = DateTime.Now;
            _ctx = new SinkContext
            {
                Report = (c, api, args) =>
                {
                    string logLine = "event[" + c + "] " + api;
                    if (!string.IsNullOrEmpty(args))
                    {
                        string a = args.Length > 200 ? args.Substring(0, 200) + "..." : args;
                        logLine += "  args={" + a + "}";
                    }
                    Diagnostics.Log(logLine);
                    var h = ApiEvent; if (h != null) h(c, api, args);
                },
                RequestDocReattach = () => { var h = DocReattachRequested; if (h != null) h(); }
            };
        }

        /// <summary>Add-in 加载时间，用于判断启动后经过了多久（避免在 CAXA 还没初始化时就刷诊断）。</summary>
        private readonly DateTime _startupTime;

        /// <summary>汇总当前挂接状态，供界面状态栏显示。</summary>
        public string Describe()
        {
            lock (_gate)
            {
                var parts = new List<string>();
                parts.Add("应用事件 " + (_appCookie >= 0 ? "已挂接" : "未挂接"));
                if (_sceneCatCookie >= 0) parts.Add("场景库事件 已挂接");
                if (_dwgCatCookie >= 0) parts.Add("工程图库事件 已挂接");
                parts.Add("文档事件 " + (_docCookie >= 0 ? "已挂接" : "未挂接"));
                if (_opCookie >= 0) parts.Add("操作事件 已挂接");
                if (_hdCookie >= 0) parts.Add("拖拽事件 已挂接");
                if (_drawCookie >= 0) parts.Add("重绘事件 已挂接");
                if (_smartCookie >= 0) parts.Add("智能装配事件 已挂接");
                if (_mechCookie >= 0) parts.Add("机构事件 已挂接");
                if (_dwgCookie >= 0) parts.Add("工程图文档事件 已挂接");
                return string.Join("｜", parts);
            }
        }

        /// <summary>当前是否处于图纸环境（工程图）。</summary>
        public bool InDrawingEnv { get { lock (_gate) return _inDrawingEnv; } }

        /// <summary>
        /// 图纸环境轮询（由 UI 定时器每 2 秒调用一次）。
        /// 原理：图纸环境的实体级操作在 COM 层不产生事件（只有 CRX 层有 reactor），
        /// 因此这里用 COM 能拿到的三个抓手来推断图纸环境的活动：
        ///   1. IZDoc.Type（Z_DRAWING/Z_SCENE）—— 环境/文档类型切换
        ///   2. IZDoc.GetDrawingNotifyCounterLoaded() / GetBOMNotifyCouterLoaded()
        ///      —— CAXA 自身的图纸/BOM 通知计数器，有图纸活动就自增
        ///   3. 图纸文档状态快照（当前图纸页、图纸页数、视图数）—— 对比取变化
        /// 变化通过 ApiEvent 上报，事件名标注为「图纸环境」类。
        /// </summary>
        public void PollDrawingEnv()
        {
            // 在锁内只做「取变化 → 收集」，锁外再上报，避免持锁回调 UI 造成重入问题
            List<string[]> pending = new List<string[]>();

            lock (_gate)
            {
                if (_disposed) return;

                IZDoc doc;
                try { doc = _app.ActiveDoc; }
                catch (Exception ex) { Diagnostics.Log("PollDrawingEnv: ActiveDoc EX " + ex.Message); return; }

                if (doc == null)
                {
                    _inDrawingEnv = false;
                    _lastDrawingDocId = null;
                    _dwgBaselineSet = false;

                    // 启动 30 秒后如果还没拿到 ActiveDoc，每 30 秒打一次状态（帮助排查）
                    string noDocKey = "PollDrawingEnv_nodoc";
                    if (!_timestamps.ContainsKey(noDocKey)) _timestamps[noDocKey] = DateTime.MinValue;
                    double elapsed = (DateTime.Now - _startupTime).TotalSeconds;
                    if (elapsed > 30 && (DateTime.Now - _timestamps[noDocKey]).TotalSeconds > 30)
                    {
                        _timestamps[noDocKey] = DateTime.Now;
                        Diagnostics.Log(string.Format(
                            "PollDrawingEnv: 启动 {0:F0}s 仍无 ActiveDoc，CAXA 可能还在初始化，请耐心等待或新建/打开一个文档",
                            elapsed));
                    }
                    return;
                }

                // ---- 诊断：每 10 秒打印一次 ActiveDoc 的完整画像，帮助理解 CAXA 2027 COM 暴露方式 ----
                // 用时间戳控制频率，避免刷屏
                string diagKey = "PollDrawingEnv_diag";
                if (!_timestamps.ContainsKey(diagKey)) _timestamps[diagKey] = DateTime.MinValue;
                if ((DateTime.Now - _timestamps[diagKey]).TotalSeconds > 10)
                {
                    _timestamps[diagKey] = DateTime.Now;
                    DumpDocDiagnostics(doc);
                }

                // ---- 环境切换判断：遍历所有打开的文档（图纸页可能不挂在 ActiveDoc 上）----
                bool drawEnv = false;
                string dtype = null;
                int typeVal = -1;
                try { dtype = doc.Type.ToString(); typeVal = (int)doc.Type; } catch { }

                // 先检查 ActiveDoc（快速路径）
                uint dwgCounterActive = 0;
                try { dwgCounterActive = doc.GetDrawingNotifyCounterLoaded(); } catch { }
                if (dwgCounterActive > 0) drawEnv = true;
                if (!drawEnv && dtype == "Z_DRAWING") drawEnv = true;
                if (!drawEnv && doc is IZDrawingDoc) drawEnv = true;
                if (!drawEnv)
                {
                    string fname = null;
                    try { fname = doc.Name; } catch { }
                    if (!string.IsNullOrEmpty(fname))
                    {
                        string ext = Path.GetExtension(fname).ToLowerInvariant();
                        if (ext == ".exb" || ext == ".dwg" || ext == ".dxf") drawEnv = true;
                    }
                }

                // ActiveDoc 没命中 → 遍历所有打开的文档
                if (!drawEnv)
                {
                    try
                    {
                        var allDocs = _app.GetAllOpenDocs();
                        var arr = allDocs as Array;
                        if (arr != null)
                        {
                            for (int i = 0; i < arr.Length; i++)
                            {
                                var d = arr.GetValue(i) as IZDoc;
                                if (d == null || d == doc) continue;

                                // 检测 app 级计数器
                                try
                                {
                                    int appCtr = _app.GetDrawingNotifyCounter(d.FileGUID);
                                    if (appCtr > 0) { drawEnv = true; break; }
                                }
                                catch { }

                                // 检测 doc 级计数器
                                try
                                {
                                    uint c = d.GetDrawingNotifyCounterLoaded();
                                    if (c > 0) { drawEnv = true; break; }
                                }
                                catch { }

                                // 检测 Type / QI
                                try { if (d.Type.ToString() == "Z_DRAWING") { drawEnv = true; break; } } catch { }
                                if (d is IZDrawingDoc) { drawEnv = true; break; }
                            }
                        }
                    }
                    catch { }
                }

                string envLabel = drawEnv ? "Z_DRAWING(推断)" : (dtype ?? "未知");
                if (drawEnv != _inDrawingEnv)
                {
                    _inDrawingEnv = drawEnv;
                    _dwgBaselineSet = false;
                    _lastDrawingDocId = null;
                    pending.Add(new[] { "图纸环境",
                        drawEnv ? "已进入图纸环境（" + envLabel + "）"
                                : "退出图纸环境，当前=" + envLabel, "" });
                }

                if (!drawEnv) { }
                else
                {
                    // ---- 切换图纸文档时重置基线 ----
                    string docId = null;
                    try { docId = doc.FileGUID ?? doc.Name; } catch { }
                    if (docId != _lastDrawingDocId)
                    {
                        _lastDrawingDocId = docId;
                        _dwgBaselineSet = false;
                    }

                    bool first = !_dwgBaselineSet;

                    // ---- 2. 图纸通知计数器（CAXA 内部自增 = 图纸环境发生了活动）----
                    try
                    {
                        uint c = doc.GetDrawingNotifyCounterLoaded();
                        if (first) _lastDwgNotify = c;               // 只登记基线
                        else if (c != _lastDwgNotify)
                        {
                            uint delta = c >= _lastDwgNotify ? c - _lastDwgNotify : c;
                            pending.Add(new[] { "图纸环境", "IZDoc.GetDrawingNotifyCounterLoaded()",
                                "计数=" + c + "（本次 +" + delta + "）" });
                            _lastDwgNotify = c;
                        }
                    }
                    catch { }

                    try
                    {
                        uint c = doc.GetBOMNotifyCouterLoaded();
                        if (first) _lastBomNotify = c;
                        else if (c != _lastBomNotify)
                        {
                            uint delta = c >= _lastBomNotify ? c - _lastBomNotify : c;
                            pending.Add(new[] { "图纸环境", "IZDoc.GetBOMNotifyCouterLoaded()",
                                "计数=" + c + "（本次 +" + delta + "，BOM 相关变化）" });
                            _lastBomNotify = c;
                        }
                    }
                    catch { }

                    // ---- 3. 图纸文档状态快照 ----
                    IZDrawingDoc dd = doc as IZDrawingDoc;
                    if (dd != null)
                    {
                        try
                        {
                            int sheets = dd.GetTDSheetsCount();
                            if (first) _lastSheetCount = sheets;
                            else if (sheets != _lastSheetCount)
                            {
                                pending.Add(new[] { "图纸环境", "IZDrawingDoc.GetTDSheetsCount()", "图纸页数=" + sheets });
                                _lastSheetCount = sheets;
                            }

                            var active = dd.ActiveSheet;
                            string label = null;
                            int views = -1;
                            if (active != null)
                            {
                                try { label = active.Label; } catch { }
                                try { views = active.GetTDViewsCount(); } catch { }
                            }

                            if (first)
                            {
                                _lastSheetLabel = label;
                                _lastViewCount = views;
                            }
                            else
                            {
                                if (label != _lastSheetLabel)
                                {
                                    pending.Add(new[] { "图纸环境", "IZDrawingDoc.ActiveSheet.Label",
                                        "当前图纸页=" + (label ?? "(无)") });
                                    _lastSheetLabel = label;
                                }
                                if (views != _lastViewCount)
                                {
                                    pending.Add(new[] { "图纸环境", "IZTDSheet.GetTDViewsCount()",
                                        "当前图纸页视图数=" + views });
                                    _lastViewCount = views;
                                }
                            }
                        }
                        catch (Exception ex) { Diagnostics.Log("PollDrawingEnv: snapshot EX " + ex.Message); }
                    }

                    _dwgBaselineSet = true;
                }
            }

            // 锁外上报
            for (int i = 0; i < pending.Count; i++)
            {
                var p = pending[i];
                Report(p[0], p[1], p[2]);
            }
        }

        /// <summary>走 SinkContext 上报（与事件 sink 同一通道）。</summary>
        private void Report(string category, string api, string args)
        {
            var r = _ctx.Report;
            if (r != null) r(category, api, args);
        }

        /// <summary>
        /// 读取 CRX 桥接日志的增量（tail -f）。CRX .crx 模块在 reactor 回调里 append 每行：
        ///   HH:mm:ss | SOURCE.eventName | arg1 | arg2
        /// 解析后走 SinkContext.Report，统一进 UI 表格。
        /// 2 秒一次调用，零锁依赖，文件不存在时静默跳过（CRX 可能没加载）。
        /// </summary>
        public void ReadCrxEventsLog()
        {
            FileInfo fi;
            try { fi = new FileInfo(CrxLogPath); } catch { return; }
            if (!fi.Exists) return;

            // 文件被重建（CRX 重启、日志被删）→ 偏移归零
            if (fi.Length < _crxLogOffset) _crxLogOffset = 0;

            if (fi.Length == _crxLogOffset) return; // 没新内容

            try
            {
                using (var fs = new FileStream(CrxLogPath, FileMode.Open, FileAccess.Read,
                                                FileShare.ReadWrite))
                {
                    fs.Seek(_crxLogOffset, SeekOrigin.Begin);
                    using (var sr = new StreamReader(fs))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            ParseAndReportCrxLine(line);
                        }
                        _crxLogOffset = fs.Position;
                    }
                }
                _crxLastRead = DateTime.Now;
            }
            catch { /* 文件被 CRX 独占写入时可能短暂 IOException，忽略 */ }
        }

        /// <summary>解析 CRX 日志一行：HH:mm:ss | SOURCE.eventName | arg1 | arg2</summary>
        private void ParseAndReportCrxLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            // 跳过 CRX 自身的 MOD 日志（只保留 EDITOR/DB）
            if (line.Contains("MOD.")) return;

            // 格式：HH:mm:ss | SOURCE.eventName | arg1 | arg2
            string[] parts = line.Split('|');
            if (parts.Length < 2) return;

            string sourceEvent = parts[1].Trim(); // "EDITOR.commandWillStart" / "DB.objectAppended"
            string[] srcEv = sourceEvent.Split('.');
            if (srcEv.Length != 2) return;
            string source = srcEv[0].Trim();  // "EDITOR" / "DB"
            string evName = srcEv[1].Trim();  // "commandWillStart" / "objectAppended"

            // args：拼接剩余段
            string args = "";
            for (int i = 2; i < parts.Length; i++)
            {
                if (i > 2) args += " | ";
                args += parts[i].Trim();
            }

            // 中文分类
            string category = source == "EDITOR" ? "CRX-命令" :
                              source == "DB"      ? "CRX-对象" :
                              "CRX";

            // API 签名：source::eventName（跟 COM 事件格式对齐）
            string api = "CRX_" + source + "::" + evName;

            Report(category, api, args);
        }

        // ---- 图纸环境辅助：安全读取 COM 对象属性 + 诊断 ----

        /// <summary>
        /// 安全读取 COM 对象的属性。用 Reflection 走 late binding，避免 vtable 槽位误调。
        /// </summary>
        private static object SafeDispatchGet(object obj, string name)
        {
            if (obj == null || string.IsNullOrEmpty(name)) return null;
            try
            {
                var t = obj.GetType();
                // 先尝试类型化属性
                var prop = t.GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (prop != null) return prop.GetValue(obj, null);
                // 再尝试字段
                var field = t.GetField(name,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (field != null) return field.GetValue(obj);
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 每 10 秒打印诊断：枚举所有打开文档 + ActiveDoc 完整画像 + late binding 属性。
        /// 帮助理解 CAXA 2027 COM 暴露方式（图纸可能是内嵌页，不是独立 doc）。
        /// </summary>
        private void DumpDocDiagnostics(IZDoc activeDoc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== 文档诊断 =====");

            // 1. 枚举所有打开的文档
            try
            {
                int cnt = _app.GetOpenDocsCount();
                sb.AppendLine("  打开的文档数: " + cnt);
                var all = _app.GetAllOpenDocs();
                if (all != null)
                {
                    var arr = all as Array;
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Length; i++)
                        {
                            var d = arr.GetValue(i) as IZDoc;
                            if (d == null) continue;
                            sb.AppendLine("  --- Doc[" + i + "] ---");
                            DumpOneDoc(sb, d, d == activeDoc);
                            // 读 app 级计数器（per-doc 版本）
                            try
                            {
                                int appCtr = _app.GetDrawingNotifyCounter(d.FileGUID);
                                sb.AppendLine("    App.GetDrawingNotifyCounter(GUID)=" + appCtr);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("  枚举文档 EX: " + ex.Message); }

            // 2. ActiveDoc 上所有可用属性（late binding dump）
            sb.AppendLine("  --- ActiveDoc 全属性 ---");
            try
            {
                var t = activeDoc.GetType();
                foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    try
                    {
                        var val = prop.GetValue(activeDoc, null);
                        string vs = val == null ? "(null)" :
                            (val is string ? "\"" + val + "\"" : val.ToString());
                        if (vs.Length > 100) vs = vs.Substring(0, 100) + "...";
                        sb.AppendLine("    " + prop.PropertyType.Name + " " + prop.Name + " = " + vs);
                    }
                    catch { sb.AppendLine("    " + prop.Name + " = EX"); }
                }
            }
            catch { }

            sb.AppendLine("=====================");
            Diagnostics.Log(sb.ToString().Replace(Environment.NewLine, " | "));
        }

        private static void DumpOneDoc(StringBuilder sb, IZDoc d, bool isActive)
        {
            try { sb.AppendLine("    Name=" + (d.Name ?? "(null)")); } catch { sb.AppendLine("    Name=EX"); }
            try { sb.AppendLine("    Type=" + d.Type + " (" + (int)d.Type + ")"); } catch { }
            try { sb.AppendLine("    GUID=" + d.FileGUID); } catch { }
            try { sb.AppendLine("    GetDrawingNotifyCounterLoaded=" + d.GetDrawingNotifyCounterLoaded()); } catch { }
            try { sb.AppendLine("    GetBOMNotifyCouterLoaded=" + d.GetBOMNotifyCouterLoaded()); } catch { }
            sb.AppendLine("    QI IZSceneDoc=" + (d is IZSceneDoc));
            sb.AppendLine("    QI IZDrawingDoc=" + (d is IZDrawingDoc));
            sb.AppendLine("    是ActiveDoc=" + isActive);
        }

        /// <summary>挂接应用级事件并尝试挂接当前活动文档的文档级事件。</summary>
        public void Start()
        {
            AttachAppEvents();
            ReattachDocEvents();
        }

        // ---------------------------------------------------------------- 应用级事件

        public bool AttachAppEvents()
        {
            lock (_gate)
            {
                if (_disposed || _appCookie >= 0) return _appCookie >= 0;

                // _IZApplicationEvents：DIID BF00132F-... 挂在 IZBaseApp 对象本身
                _appSink = new AppEventsSink(_ctx);
                if (!Attach(_app, DiidApp, _appSink, "IZApplicationEvents", out _appCp, out _appCookie))
                {
                    _appSink = null;
                    return false;
                }

                // 场景库事件源：IZBaseApp.SceneCatalogEvents 属性（类型 ZCatalogEvents）
                AttachCatalogFromProp(_app.SceneCatalogEvents,
                    "IZBaseApp.SceneCatalogEvents", "SceneCatalogEvents",
                    "场景库", out _sceneCatSink, out _sceneCatCp, out _sceneCatCookie);

                // 工程图库事件源：IZBaseApp.DrawingCatalogEvents 属性
                AttachCatalogFromProp(_app.DrawingCatalogEvents,
                    "IZBaseApp.DrawingCatalogEvents", "DrawingCatalogEvents",
                    "工程图库", out _dwgCatSink, out _dwgCatCp, out _dwgCatCookie);

                return true;
            }
        }

        /// <summary>
        /// 从类型化属性（object）取事件源，挂接 _IZCatalogEvents（DIID 40A3FE22-...）。
        /// 进程内直接通过 Marshal.GetIUnknownForObject 拿到事件源的 IUnknown*。
        /// </summary>
        private void AttachCatalogFromProp(object propValue, string what, string tag, string which,
            out CatalogEventsSink sink, out IConnectionPoint cp, out int cookie)
        {
            sink = null; cp = null; cookie = -1;
            if (propValue == null) { Diagnostics.Log(what + " 属性为 null，跳过"); return; }

            IntPtr pSrc = IntPtr.Zero;
            try { pSrc = Marshal.GetIUnknownForObject(propValue); }
            catch (Exception ex) { Diagnostics.Log(what + ": GetIUnknownForObject EX " + ex.Message); return; }

            try
            {
                sink = new CatalogEventsSink(_ctx, which);
                if (!Attach(pSrc, DiidCatalog, sink, tag, out cp, out cookie)) sink = null;
            }
            finally { Marshal.Release(pSrc); }
        }

        // ---------------------------------------------------------------- 文档级事件

        /// <summary>重新挂接文档级事件到当前活动文档（先摘除旧挂接）。</summary>
        public void ReattachDocEvents()
        {
            lock (_gate)
            {
                if (_disposed) return;
                DetachDocEventsLocked();

                IZDoc doc;
                try { doc = _app.ActiveDoc; } catch (Exception ex) { Diagnostics.Log("ActiveDoc EX: " + ex.Message); return; }
                if (doc == null)
                {
                    _docName = null; _docIdentity = null;
                    Diagnostics.Log("doc events: 当前无活动文档，稍后再挂");
                    return;
                }

                try
                {
                    _docSink = new DocEventsSink(_ctx);
                    if (!Attach(doc, DiidDoc, _docSink, "IZDocEvents", out _docCp, out _docCookie))
                    {
                        _docSink = null;
                        return;
                    }
                    _docName = SafeRead(() => doc.Name, "(无)");
                    _docIdentity = SafeRead(() => doc.FileGUID, null) ?? SafeRead(() => doc.Name, null);

                    // --- IZDoc 上直接暴露的事件源属性（进程内不再需要 vtable 槽位直调）---

                    // OperationEvents
                    AttachFromProp(doc.OperationEvents, "IZDoc.OperationEvents", DiidOperation,
                        "IZOperationEvents", out _opSink, out _opCp, out _opCookie,
                        val => new OperationEventsSink(_ctx));

                    // HandleEvents
                    AttachFromProp(doc.HandleEvents, "IZDoc.HandleEvents", DiidHandle,
                        "IZHandleEvents", out _hdSink, out _hdCp, out _hdCookie,
                        val => new HandleEventsSink(_ctx));

                    // DrawEvents
                    AttachFromProp(doc.DrawEvents, "IZDoc.DrawEvents", DiidDraw,
                        "IZDrawEvents", out _drawSink, out _drawCp, out _drawCookie,
                        val => new DrawEventsSink(_ctx));

                    // SmartAssemblyExEvents
                    AttachFromProp(doc.SmartAssemblyExEvents, "IZDoc.SmartAssemblyExEvents", DiidSmartAssemblyEx,
                        "IZSmartAssemblyExEvents", out _smartSink, out _smartCp, out _smartCookie,
                        val => new SmartAssemblyExEventsSink(_ctx));

                    // --- 场景文档：MechanismModeEvents ---
                    IZSceneDoc sceneDoc = doc as IZSceneDoc;
                    if (sceneDoc != null)
                    {
                        AttachFromProp(sceneDoc.MechanismModeEvents,
                            "IZSceneDoc.MechanismModeEvents", DiidMechanismMode,
                            "IZMechanismModeEvents", out _mechSink, out _mechCp, out _mechCookie,
                            val => new MechanismModeEventsSink(_ctx));
                    }
                    else Diagnostics.Log("doc events: 当前文档不是场景文档（QI IZSceneDoc 失败），跳过机构事件");

                    // --- 工程图文档：_IZDrawingDocEvents ---
                    IZDrawingDoc dwgDoc = doc as IZDrawingDoc;
                    if (dwgDoc != null)
                    {
                        _dwgSink = new DrawingDocEventsSink(_ctx);
                        if (!Attach(dwgDoc, DiidDrawingDoc, _dwgSink,
                                "IZDrawingDocEvents", out _dwgCp, out _dwgCookie))
                            _dwgSink = null;
                    }
                    else Diagnostics.Log("doc events: 当前文档不是工程图文档（QI IZDrawingDoc 失败），跳过工程图事件");
                }
                catch (Exception ex) { Diagnostics.Log("attach doc events EX: " + ex.Message); }
            }
        }

        /// <summary>
        /// 从类型化属性 object 取事件源并挂接连接点。
        /// 进程内 Add-in 直接 Marshal.GetIUnknownForObject，不再需要 vtable 槽位。
        /// </summary>
        private void AttachFromProp<T>(object propValue, string what, Guid diid, string tag,
            out T sink, out IConnectionPoint cp, out int cookie,
            Func<object, T> sinkFactory) where T : class
        {
            sink = null; cp = null; cookie = -1;
            if (propValue == null) { Diagnostics.Log(what + " 属性为 null，跳过"); return; }

            IntPtr pSrc = IntPtr.Zero;
            try { pSrc = Marshal.GetIUnknownForObject(propValue); }
            catch (Exception ex) { Diagnostics.Log(what + ": GetIUnknownForObject EX " + ex.Message); return; }

            try
            {
                sink = sinkFactory(propValue);
                if (!Attach(pSrc, diid, sink as object, tag, out cp, out cookie)) sink = null;
            }
            finally { Marshal.Release(pSrc); }
        }

        private static string SafeRead(Func<string> get, string fallback)
        {
            try { return get(); }
            catch { return fallback; }
        }

        // ---------------------------------------------------------------- 通用挂接逻辑

        /// <summary>
        /// 在源对象上找到指定 DIID 的连接点并 Advise 事件接收器。
        /// 找不到连接点（FindConnectionPoint 失败）时回退枚举全部连接点按 IID 匹配。
        /// 与 EXE 版完全相同（纯标准 COM IConnectionPointContainer 机制）。
        /// </summary>
        private static bool Attach(IntPtr pSource, Guid diid, object sink, string tag,
            out IConnectionPoint cp, out int cookie)
        {
            cp = null; cookie = -1;
            if (pSource == IntPtr.Zero) { Diagnostics.Log("attach " + tag + ": 源指针为空"); return false; }

            IntPtr pCpc;
            Guid iidCpc = IID_IConnectionPointContainer;
            int hr = Marshal.QueryInterface(pSource, ref iidCpc, out pCpc);
            if (hr != 0 || pCpc == IntPtr.Zero)
            {
                Diagnostics.Log(string.Format("attach {0}: QI(IConnectionPointContainer) hr=0x{1:X}", tag, hr));
                return false;
            }

            IConnectionPointContainer cpc;
            try { cpc = (IConnectionPointContainer)Marshal.GetObjectForIUnknown(pCpc); }
            catch (Exception ex) { Diagnostics.Log("attach " + tag + ": wrap CPC EX " + ex.Message); Marshal.Release(pCpc); return false; }
            finally { Marshal.Release(pCpc); }

            cp = FindConnectionPoint(cpc, diid, tag);
            if (cp == null) { Diagnostics.Log("attach " + tag + ": 未找到连接点 DIID=" + diid.ToString("B")); return false; }

            try
            {
                cp.Advise(sink, out cookie);
                Diagnostics.Log(string.Format("attach {0}: Advise ok cookie={1}", tag, cookie));
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("attach " + tag + ": Advise EX " + ex.Message);
                try { Marshal.ReleaseComObject(cp); } catch { }
                cp = null; cookie = -1;
                return false;
            }
        }

        /// <summary>重载：接受类型化 RCW 对象（进程内 Add-in 主要入口）。</summary>
        private static bool Attach(object source, Guid diid, object sink, string tag,
            out IConnectionPoint cp, out int cookie)
        {
            cp = null; cookie = -1;
            IntPtr p = IntPtr.Zero;
            try { p = Marshal.GetIUnknownForObject(source); }
            catch { return false; }
            try { return Attach(p, diid, sink, tag, out cp, out cookie); }
            finally { Marshal.Release(p); }
        }

        private static IConnectionPoint FindConnectionPoint(IConnectionPointContainer cpc, Guid diid, string tag)
        {
            Guid g = diid;
            try
            {
                IConnectionPoint cp;
                cpc.FindConnectionPoint(ref g, out cp);
                if (cp != null) return cp;
            }
            catch { }

            // 回退：枚举所有连接点，按 IID 匹配
            try
            {
                IEnumConnectionPoints e;
                cpc.EnumConnectionPoints(out e);
                if (e == null) return null;
                var arr = new IConnectionPoint[1];
                IntPtr fetched = Marshal.AllocCoTaskMem(4);
                try
                {
                    while (e.Next(1, arr, fetched) == 0)
                    {
                        IConnectionPoint c = arr[0];
                        if (c == null) break;
                        Guid cid;
                        try { c.GetConnectionInterface(out cid); } catch { cid = Guid.Empty; }
                        if (cid == diid) return c;
                        Marshal.ReleaseComObject(c);
                        arr[0] = null;
                    }
                }
                finally { Marshal.FreeCoTaskMem(fetched); }
            }
            catch (Exception ex) { Diagnostics.Log("enumerate CPs (" + tag + ") EX: " + ex.Message); }
            return null;
        }

        // ---------------------------------------------------------------- 摘除

        private void DetachDocEventsLocked()
        {
            Unadvise(_docCp, ref _docCookie, "IZDocEvents");
            Unadvise(_opCp, ref _opCookie, "IZOperationEvents");
            Unadvise(_hdCp, ref _hdCookie, "IZHandleEvents");
            Unadvise(_drawCp, ref _drawCookie, "IZDrawEvents");
            Unadvise(_smartCp, ref _smartCookie, "IZSmartAssemblyExEvents");
            Unadvise(_mechCp, ref _mechCookie, "IZMechanismModeEvents");
            Unadvise(_dwgCp, ref _dwgCookie, "IZDrawingDocEvents");
            _docSink = null; _opSink = null; _hdSink = null; _drawSink = null;
            _smartSink = null; _mechSink = null; _dwgSink = null;
            _docIdentity = null;
        }

        private void DetachAppEventsLocked()
        {
            Unadvise(_appCp, ref _appCookie, "IZApplicationEvents");
            Unadvise(_sceneCatCp, ref _sceneCatCookie, "SceneCatalogEvents");
            Unadvise(_dwgCatCp, ref _dwgCatCookie, "DrawingCatalogEvents");
            _appSink = null; _sceneCatSink = null; _dwgCatSink = null;
        }

        private static void Unadvise(IConnectionPoint cp, ref int cookie, string tag)
        {
            if (cp == null) return;
            try
            {
                if (cookie >= 0) cp.Unadvise(cookie);
            }
            catch (Exception ex) { Diagnostics.Log("unadvise " + tag + " EX: " + ex.Message); }
            finally
            {
                try { Marshal.ReleaseComObject(cp); } catch { }
                cookie = -1;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                DetachDocEventsLocked();
                DetachAppEventsLocked();
            }
        }
    }
}
