using System;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dapigetaddin
{
    /// <summary>事件上报回调：category=中文分类，api=API 签名，args=实参文本。</summary>
    internal delegate void ApiEventReporter(string category, string api, string args);

    /// <summary>事件接收器的运行上下文（上报 + 请求重挂文档级事件）。</summary>
    internal sealed class SinkContext
    {
        public ApiEventReporter Report;
        public Action RequestDocReattach;
    }

    /// <summary>把事件参数格式化为可读文本（进程内版本：直接 is 转型读 IZDoc.Name）。</summary>
    internal static class ApiArgs
    {
        public static string Describe(object o)
        {
            if (o == null) return "null";
            if (o is string s) return "\"" + s + "\"";
            if (o is bool b) return b ? "True" : "False";
            if (o is int || o is long || o is short || o is uint || o is ushort) return o.ToString();
            // 图纸环境对象优先识别（图文档本身也是 IZDoc，必须先判图纸，否则会被当成普通文档）
            if (o is IZTDSheet sh) { try { return "[图纸页] " + sh.Label; } catch { return "[IZTDSheet*]"; } }
            if (o is IZDrawingDoc dd)
            {
                try { var a = dd.ActiveSheet; return "[图纸文档] 当前图纸页=" + (a != null ? a.Label : "(无)"); }
                catch { return "[IZDrawingDoc*]"; }
            }
            // 接口对象：尝试读取其文档名（进程内直接 is 转型，不再需要跨进程 QI）
            if (o is IZDoc d) { try { return "[文档] " + d.Name; } catch { return "[IZDoc*]"; } }
            // 其他 COM 对象：late binding 尝试读 Name / Label / Caption / ID
            try
            {
                var t = o.GetType();
                string typeName = t.Name;
                // __ComObject 的 Name 没意义，尝试读 COM 对象的实际类型
                if (typeName == "__ComObject")
                {
                    try { typeName = t.InvokeMember("GetType", System.Reflection.BindingFlags.DeclaredOnly, null, o, null)?.GetType().Name ?? "__ComObject"; }
                    catch { }
                }
                var nameProp = t.GetProperty("Name", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (nameProp != null)
                {
                    try { return "[" + typeName + "] Name=\"" + nameProp.GetValue(o, null) + "\""; } catch { }
                }
                var labelProp = t.GetProperty("Label", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                if (labelProp != null)
                {
                    try { return "[" + typeName + "] Label=\"" + labelProp.GetValue(o, null) + "\""; } catch { }
                }
                return "[" + typeName + "]";
            }
            catch { return "[对象]"; }
        }

        public static string Join(params object[] args)
        {
            if (args == null || args.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Describe(args[i]));
            }
            return sb.ToString();
        }
    }

    // =========================================================================
    // _IZApplicationEvents  (DIID BF00132F-57F2-458B-A734-D9E0C883C525)
    // =========================================================================
    [ComVisible(true)]
    [Guid("BF00132F-57F2-458B-A734-D9E0C883C525")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IAppEvents
    {
        [DispId(1)] void ActiveDocChanged(object doc);
        [DispId(2)] void AppDestroyNotify();
        [DispId(3)] void DocumentLoaded(object fileName, object docName);
        [DispId(4)] void DocumentCreated(object doc);
        [DispId(5)] void DocumentOpened(object doc);
        [DispId(6)] void DocumentClosed(object docName);
        [DispId(7)] void DocumentPreClose(object doc);
        [DispId(8)] void DocumentWithFrameOpened(object doc);
        [DispId(9)] void NewWindowCreated(object window, object doc);
        [DispId(10)] void PreSplitView(object window, object mode, object bOverride);
        [DispId(11)] void ActiveViewChanged(object view);
        [DispId(12)] void PreViewDestroyed(object view);
        [DispId(13)] void DocumentPreOpened(object doc);
        [DispId(14)] void FrameClientCreated(object window, object frame);
        [DispId(15)] void CommandHelp(object cmdId, object bHandled);
        [DispId(16)] void PreCloseMainframe();
        [DispId(17)] void PreCloseMainframe2(object bOverride);
    }

    [ComVisible(true)]
    [Guid("E1000001-1111-4AAA-9BBB-000000000001")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class AppEventsSink : IAppEvents
    {
        private readonly SinkContext _ctx;
        internal AppEventsSink(SinkContext ctx) { _ctx = ctx; }

        private void R(string api, params object[] a)
        {
            _ctx.Report("应用", "IZApplicationEvents::" + api, ApiArgs.Join(a));
        }

        public void ActiveDocChanged(object doc) { R("ActiveDocChanged(IZDoc* pDoc)", doc); _ctx.RequestDocReattach?.Invoke(); }
        public void AppDestroyNotify() { R("AppDestroyNotify()"); }
        public void DocumentLoaded(object f, object d) { R("DocumentLoaded(BSTR, BSTR)", f, d); }
        public void DocumentCreated(object doc) { R("DocumentCreated(IZDoc* pDoc)", doc); _ctx.RequestDocReattach?.Invoke(); }
        public void DocumentOpened(object doc) { R("DocumentOpened(IZDoc* pDoc)", doc); _ctx.RequestDocReattach?.Invoke(); }
        public void DocumentClosed(object n) { R("DocumentClosed(BSTR fileName)", n); _ctx.RequestDocReattach?.Invoke(); }
        public void DocumentPreClose(object doc) { R("DocumentPreClose(IZDoc* pDoc)", doc); _ctx.RequestDocReattach?.Invoke(); }
        public void DocumentWithFrameOpened(object doc) { R("DocumentWithFrameOpened(IZDoc* pDoc)", doc); _ctx.RequestDocReattach?.Invoke(); }
        public void NewWindowCreated(object w, object d) { R("NewWindowCreated(IZWindow*, IZDoc*)", w, d); }
        public void PreSplitView(object w, object m, object o) { R("PreSplitView(IZWindow*, int, VARIANT_BOOL*)", w, m, o); }
        public void ActiveViewChanged(object v) { R("ActiveViewChanged(IZWindow* pView)", v); }
        public void PreViewDestroyed(object v) { R("PreViewDestroyed(object pView)", v); }
        public void DocumentPreOpened(object doc) { R("DocumentPreOpened(IZDoc* pDoc)", doc); }
        public void FrameClientCreated(object w, object f) { R("FrameClientCreated(IZWindow*, IZWindow*)", w, f); }
        public void CommandHelp(object id, object h) { R("CommandHelp(unsigned long, VARIANT_BOOL*)", id, h); }
        public void PreCloseMainframe() { R("PreCloseMainframe()"); }
        public void PreCloseMainframe2(object o) { R("PreCloseMainframe2(object bOverride)", o); }
    }

    // =========================================================================
    // _IZDocEvents  (DIID 0E18FE8F-174F-4D46-AB72-C8A65D783FE4)
    // =========================================================================
    [ComVisible(true)]
    [Guid("0E18FE8F-174F-4D46-AB72-C8A65D783FE4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IDocEvents
    {
        [DispId(1)] void PreprocessDroppedObj(object a, object b, object c, object d, object e);
        [DispId(2)] void PostProcessDroppedObj(object a, object b, object c, object d, object e);
        [DispId(3)] void SelectionChanged();
        [DispId(4)] void PreActiveConfigChange();
        [DispId(5)] void PostActiveConfigChange();
        [DispId(6)] void DocDestroyNotify(object doc);
        [DispId(7)] void DocSaveNotify(object fileName);
        [DispId(8)] void DocSaveAsNotify(object fileName);
        [DispId(9)] void DocModifyNotify(object fileName, object modified);
        [DispId(10)] void AllEDOObjectsLoaded(object doc);
        [DispId(11)] void DocPreSaveNotify(object doc);
        [DispId(12)] void PostHSFLoaded();
        [DispId(13)] void DocSavedCoreDataNotify(object doc);
        [DispId(14)] void DocSaveCompleteNotify(object doc, object ok, object a, object b);
    }

    [ComVisible(true)]
    [Guid("E1000001-1111-4AAA-9BBB-000000000002")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class DocEventsSink : IDocEvents
    {
        private readonly SinkContext _ctx;
        internal DocEventsSink(SinkContext ctx) { _ctx = ctx; }

        private void R(string api, params object[] a)
        {
            _ctx.Report("文档", "IZDocEvents::" + api, ApiArgs.Join(a));
        }

        public void PreprocessDroppedObj(object a, object b, object c, object d, object e)
        { R("PreprocessDroppedObj(...)", a, b, c, d, e); }
        public void PostProcessDroppedObj(object a, object b, object c, object d, object e)
        { R("PostProcessDroppedObj(...)", a, b, c, d, e); }
        public void SelectionChanged() { R("SelectionChanged()"); }
        public void PreActiveConfigChange() { R("PreActiveConfigChange()"); }
        public void PostActiveConfigChange() { R("PostActiveConfigChange()"); }
        public void DocDestroyNotify(object doc) { R("DocDestroyNotify(IZDoc*)", doc); _ctx.RequestDocReattach?.Invoke(); }
        public void DocSaveNotify(object n) { R("DocSaveNotify(BSTR fileName)", n); }
        public void DocSaveAsNotify(object n) { R("DocSaveAsNotify(BSTR fileName)", n); }
        public void DocModifyNotify(object n, object m) { R("DocModifyNotify(BSTR, VARIANT_BOOL)", n, m); }
        public void AllEDOObjectsLoaded(object doc) { R("AllEDOObjectsLoaded(IZDoc*)", doc); }
        public void DocPreSaveNotify(object doc) { R("DocPreSaveNotify(IZDoc*)", doc); }
        public void PostHSFLoaded() { R("PostHSFLoaded()"); }
        public void DocSavedCoreDataNotify(object doc) { R("DocSavedCoreDataNotify(IZDoc*)", doc); }
        public void DocSaveCompleteNotify(object doc, object ok, object a, object b)
        { R("DocSaveCompleteNotify(IZDoc*, VARIANT_BOOL, BSTR, BSTR)", doc, ok, a, b); }
    }

    // =========================================================================
    // _IZOperationEvents  (DIID 2D640B7A-511A-46CD-8050-958AEA611416)
    // =========================================================================
    [ComVisible(true)]
    [Guid("2D640B7A-511A-46CD-8050-958AEA611416")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IOperationEvents
    {
        [DispId(1)] void OnDelete(object sceneDoc, object bOverride);
        [DispId(2)] void OnUndoRedo(object bUndo);
        [DispId(3)] void OnPaste(object sceneDoc);
    }

    [ComVisible(true)]
    [Guid("E1000001-1111-4AAA-9BBB-000000000003")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class OperationEventsSink : IOperationEvents
    {
        private readonly SinkContext _ctx;
        internal OperationEventsSink(SinkContext ctx) { _ctx = ctx; }
        private void R(string api, params object[] a) { _ctx.Report("操作", "IZOperationEvents::" + api, ApiArgs.Join(a)); }

        public void OnDelete(object s, object o) { R("OnDelete(IZSceneDoc*, VARIANT_BOOL)", s, o); }
        public void OnUndoRedo(object u) { R("OnUndoRedo(VARIANT_BOOL bUndo)", u); }
        public void OnPaste(object s) { R("OnPaste(IZSceneDoc*)", s); }
    }

    // =========================================================================
    // _IZHandleEvents  (DIID 32CCD216-A79E-4E32-BB57-5E914B57686D)
    // =========================================================================
    [ComVisible(true)]
    [Guid("32CCD216-A79E-4E32-BB57-5E914B57686D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IHandleEvents
    {
        [DispId(1)] void OnBeginDrag(object handle, object x, object y);
        [DispId(2)] void OnDrag(object handle, object x, object y);
        [DispId(3)] void OnEndDrag(object handle, object x, object y);
    }

    [ComVisible(true)]
    [Guid("E1000001-1111-4AAA-9BBB-000000000004")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class HandleEventsSink : IHandleEvents
    {
        private readonly SinkContext _ctx;
        internal HandleEventsSink(SinkContext ctx) { _ctx = ctx; }
        private void R(string api, params object[] a) { _ctx.Report("拖拽", "IZHandleEvents::" + api, ApiArgs.Join(a)); }

        public void OnBeginDrag(object h, object x, object y) { R("OnBeginDrag(ZHandle*, int, int)", h, x, y); }
        public void OnDrag(object h, object x, object y) { R("OnDrag(ZHandle*, int, int)", h, x, y); }
        public void OnEndDrag(object h, object x, object y) { R("OnEndDrag(ZHandle*, int, int)", h, x, y); }
    }

    // =========================================================================
    // _IZCatalogEvents  (DIID 40A3FE22-B015-421B-AF36-85EE3C9D1BAF)
    //   来源：IZBaseApp::SceneCatalogEvents / DrawingCatalogEvents
    // =========================================================================
    [ComVisible(true)]
    [Guid("40A3FE22-B015-421B-AF36-85EE3C9D1BAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface ICatalogEvents
    {
        [DispId(1)] void CatalogOpened(object catalog);
        [DispId(2)] void CatalogSavedCoreData(object a, object b, object c, object d);
        [DispId(3)] void OnClickCatalogEntry(object a, object b, object c, object d, object e, object f, object g);
    }

    [ComVisible(true)]
    [Guid("E1000001-1111-4AAA-9BBB-000000000005")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class CatalogEventsSink : ICatalogEvents
    {
        private readonly SinkContext _ctx;
        private readonly string _which;
        internal CatalogEventsSink(SinkContext ctx, string which) { _ctx = ctx; _which = which; }
        private void R(string api, params object[] a) { _ctx.Report("目录", _which + "::" + api, ApiArgs.Join(a)); }

        public void CatalogOpened(object c) { R("CatalogOpened(IZCatalog*)", c); }
        public void CatalogSavedCoreData(object a, object b, object c, object d) { R("CatalogSavedCoreData(...)", a, b, c, d); }
        public void OnClickCatalogEntry(object a, object b, object c, object d, object e, object f, object g)
        { R("OnClickCatalogEntry(...)", a, b, c, d, e, f, g); }
    }

    // =========================================================================
    // _IZDrawingDocEvents  (DIID 5EF3E988-1529-4AF2-B0BD-F87407F28C93)
    // =========================================================================
    [ComVisible(true)]
    [Guid("5EF3E988-1529-4AF2-B0BD-F87407F28C93")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IDrawingDocEvents
    {
        [DispId(1)] void OnActiveSheetChanged(object sheet);
        [DispId(2)] void OnAddSheet(object sheet);
        [DispId(3)] void OnDeleteSheet(object sheet);
    }

    [ComVisible(true)]
    [Guid("E1000001-1111-4AAA-9BBB-000000000006")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class DrawingDocEventsSink : IDrawingDocEvents
    {
        private readonly SinkContext _ctx;
        internal DrawingDocEventsSink(SinkContext ctx) { _ctx = ctx; }
        private void R(string api, params object[] a) { _ctx.Report("工程图", "IZDrawingDocEvents::" + api, ApiArgs.Join(a)); }

        public void OnActiveSheetChanged(object s) { R("OnActiveSheetChanged(IZTDSheet*)", s); }
        public void OnAddSheet(object s) { R("OnAddSheet(IZTDSheet*)", s); }
        public void OnDeleteSheet(object s) { R("OnDeleteSheet(IZTDSheet*)", s); }
    }

    // =========================================================================
    // _IZDrawEvents  (DIID 10388D65-F5B8-484C-8A3B-AE3BBCCEA5BE)
    // =========================================================================
    [ComVisible(true)]
    [Guid("10388D65-F5B8-484C-8A3B-AE3BBCCEA5BE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IDrawEvents
    {
        [DispId(1)] void OnPreDraw(object render);
        [DispId(2)] void OnPostDraw(object render);
    }

    [ComVisible(true)]
    [Guid("E1000001-1111-4AAA-9BBB-000000000007")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class DrawEventsSink : IDrawEvents
    {
        private readonly SinkContext _ctx;
        private long _lastTicks;
        internal DrawEventsSink(SinkContext ctx) { _ctx = ctx; }

        public void OnPreDraw(object r) { Throttled("OnPreDraw(IZRender*)", r); }
        public void OnPostDraw(object r) { Throttled("OnPostDraw(IZRender*)", r); }

        // 重绘事件按帧触发，节流到每 2 秒最多一条
        private void Throttled(string api, object r)
        {
            long now = DateTime.UtcNow.Ticks;
            long prev = System.Threading.Interlocked.Read(ref _lastTicks);
            if (now - prev < TimeSpan.TicksPerSecond * 2) return;
            System.Threading.Interlocked.Exchange(ref _lastTicks, now);
            _ctx.Report("绘制", "IZDrawEvents::" + api, ApiArgs.Join(r));
        }
    }

    // =========================================================================
    // _IZSmartAssemblyExEvents  (DIID 72D765CB-CC07-4A70-8427-FE3E72B85BAE)
    // =========================================================================
    [ComVisible(true)]
    [Guid("72D765CB-CC07-4A70-8427-FE3E72B85BAE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface ISmartAssemblyExEvents
    {
        [DispId(1)] void OnPreMatchPair(object a, object b, object c, object d, object e, object f, object g);
        [DispId(2)] void OnPreParameterMatch(object a, object b, object c, object d, object e);
        [DispId(3)] void OnPrePositioning(object a, object b, object c, object d, object e);
        [DispId(4)] void OnPostPositioning(object a, object b, object c, object d, object e);
    }

    [ComVisible(true)]
    [Guid("E1000001-1111-4AAA-9BBB-000000000008")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class SmartAssemblyExEventsSink : ISmartAssemblyExEvents
    {
        private readonly SinkContext _ctx;
        internal SmartAssemblyExEventsSink(SinkContext ctx) { _ctx = ctx; }
        private void R(string api, params object[] a) { _ctx.Report("智能装配", "IZSmartAssemblyExEvents::" + api, ApiArgs.Join(a)); }

        public void OnPreMatchPair(object a, object b, object c, object d, object e, object f, object g)
        { R("OnPreMatchPair(...)", a, b, c, d, e, f, g); }
        public void OnPreParameterMatch(object a, object b, object c, object d, object e) { R("OnPreParameterMatch(...)", a, b, c, d, e); }
        public void OnPrePositioning(object a, object b, object c, object d, object e) { R("OnPrePositioning(...)", a, b, c, d, e); }
        public void OnPostPositioning(object a, object b, object c, object d, object e) { R("OnPostPositioning(...)", a, b, c, d, e); }
    }

    // =========================================================================
    // _IZMechanismModeEvents  (DIID BA24D8C4-DD72-4219-8926-D7F65D8CE33A)
    // =========================================================================
    [ComVisible(true)]
    [Guid("BA24D8C4-DD72-4219-8926-D7F65D8CE33A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IMechanismModeEvents
    {
        [DispId(1)] void OnCollision(object a, object b);
    }

    [ComVisible(true)]
    [Guid("E1000001-1111-4AAA-9BBB-000000000009")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class MechanismModeEventsSink : IMechanismModeEvents
    {
        private readonly SinkContext _ctx;
        internal MechanismModeEventsSink(SinkContext ctx) { _ctx = ctx; }
        private void R(string api, params object[] a) { _ctx.Report("机构", "IZMechanismModeEvents::" + api, ApiArgs.Join(a)); }

        public void OnCollision(object a, object b) { R("OnCollision(...)", a, b); }
    }
}
