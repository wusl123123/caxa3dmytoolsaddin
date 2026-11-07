// 机械移植自工程 caxa3dexbtracker 的 Update3DBomApi.cs（全文件）。仅改 namespace，逻辑未动。
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dcodenameextraction
{
    /// <summary>
    /// 「更新3dbom-无弹窗版」的核心：**不碰界面，直接调 API**。
    ///
    /// 目标方法（2026-11-04 15:40 调研结论）：
    ///   <c>IZBaseApp.UpdateDraftViews(BSTR exbPath, VARIANT_BOOL vbUpdateBOMAlso)</c>
    ///   第二个参数就是"同时更新 BOM" —— 也就是「更新3D明细」这条命令背后的能力。
    ///   本地 v26 interop 里这个成员 **MISSING**（写 `app.UpdateDraftViews(...)` 直接 CS1061），
    ///   所以只能绕开静态绑定，按 IDispatch 动态调。
    ///
    /// ★ 三条通道，顺序 = 从权威到保底 ★
    ///   ① **类型库**（11-04 19:38 起的主路由）：`LoadTypeLibEx(ICApiIronCAD.tlb)` →
    ///      `GetTypeInfoOfGuid(IZBaseApp)` → `ITypeInfo::Invoke(memid=72)`。
    ///      为什么必须这么走：`IDispatch::GetIDsOfNames` 返回 **0x8002801D = TYPE_E_LIBNOTREGISTERED**
    ///      —— 按名字解析要靠注册表里的 TypeLib 登记项，而 CAXA 只做 SxS、根本不登记。
    ///      离机实测（tlbprobe）：`IZBaseApp` = 接口、90 个方法、`cbSizeVft=56`（IDispatch 派生）；
    ///      `UpdateDraftViews` **memid=72 / oVft=648 / vtable 槽 81**，与早期调研完全吻合。
    ///   ② vtable 按槽直调（**槽位只能来自 tlb 的 oVft**，绝不用硬编码常量）。
    ///   ③ IDispatch 按名字取 dispId（只在 TypeLib 哪天被注册后才可能成功）。
    ///
    /// ★ 全程无弹窗、无鼠标、无截图、无对话框哨兵 ★
    ///   调完再等 3 秒"验谎"：若「更新3D明细」竟然自己弹出来，立刻投 IDCANCEL 放掉并记一笔。
    ///
    /// ★ 线程纪律 ★
    ///   必须在 UI 线程（= CAXA 主线程）调：跨线程拿到的 COM 指针上下文不对，轻则失败重则崩。
    /// </summary>
    internal static class Update3DBomApi
    {
        // ── IDispatch 的固定 IID（系统级，与项目无关）──
        private static Guid IID_IDispatch = new Guid("00020400-0000-0000-C000-000000000046");

        private const int DISPATCH_METHOD = 0x1;

        // IDispatch::Invoke 的常见失败码（只用于把错误翻译成人话）
        private const int DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003);
        private const int DISP_E_BADPARAMCOUNT = unchecked((int)0x8002000E);
        private const int E_NOTIMPL = unchecked((int)0x80004001);

        /// <summary>一次调用的结论（全部字段都要进日志，实机排错全靠它）。</summary>
        internal sealed class CallResult
        {
            public bool Ok;
            public string Channel = "";      // "IDispatch(dispId=72)" / "vtable槽81" / ""
            public int Hr;                   // 0 = S_OK
            public string Detail = "";       // 人话描述
        }

        // ============================================================ 主入口

        /// <summary>
        /// 调 `IZBaseApp.UpdateDraftViews(exbPath, updateBomAlso)`。**必须在 UI 线程**。
        /// 返回的 Ok 只代表"宿主接受了这次调用"（HRESULT=S_OK），不代表明细一定变了 ——
        /// 明细的最终结果仍以图纸界面/明细表为准（这条和 CRX 那次"投命令成功≠执行完"同源）。
        /// </summary>
        public static CallResult InvokeUpdateDraftViews(IZBaseApp app, string exbPath,
            bool updateBomAlso, Action<string> log)
        {
            var r = new CallResult();
            if (log == null) log = delegate { };
            if (app == null) { r.Detail = "IZBaseApp 为 null（站点对象没拿到）"; return r; }
            if (string.IsNullOrEmpty(exbPath)) { r.Detail = "目标 exb 路径为空"; return r; }
            if (!File.Exists(exbPath)) { r.Detail = "目标 exb 不存在：" + exbPath; return r; }

            log("api: 目标 exb = " + exbPath);
            DateTime mtimeBefore;
            try
            {
                var fi0 = new FileInfo(exbPath);
                mtimeBefore = fi0.LastWriteTime;
                log("api: 文件 " + fi0.Length + " B，最后写入 " + mtimeBefore.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            catch { mtimeBefore = DateTime.MinValue; }

            IntPtr pUnk = IntPtr.Zero, pDisp = IntPtr.Zero, pBase = IntPtr.Zero, bstrPath = IntPtr.Zero;
            try
            {
                pUnk = Marshal.GetIUnknownForObject(app);
                bstrPath = Marshal.StringToBSTR(exbPath);

                // ════════════════════════════════════════════════════════════
                //  通道 ①：原始类型库 + ITypeInfo（11-04 19:30 实机定位后的正解）
                //  GetIDsOfNames 之所以返回 0x8002801D，是 **TYPE_E_LIBNOTREGISTERED**
                //  —— 名字解析要靠注册表里的 TypeLib 登记项，而 CAXA 只做 SxS 不注册。
                //  方法本身是存在的（ICApiIronCAD.tlb 里 grep 得到 UpdateDraftViews）。
                //  所以直接 LoadTypeLibEx 加载那个 tlb 文件拿 ITypeInfo，用它按 memid 派发，
                //  完全绕开注册表；顺带把 oVft 也读出来（权威槽位，不必再猜 81）。
                // ════════════════════════════════════════════════════════════
                if (EnsureTypeLib(log))
                {
                    Guid iidBase = typeof(IZBaseApp).GUID;
                    int hrQiBase = Marshal.QueryInterface(pUnk, ref iidBase, out pBase);
                    IntPtr pv = (hrQiBase == 0 && pBase != IntPtr.Zero) ? pBase : pUnk;

                    int hrT = TypeLibInvoke(pv, _tlMemId, bstrPath, updateBomAlso);
                    r.Hr = hrT;
                    if (hrT == 0)
                    {
                        r.Ok = true;
                        r.Channel = "typelib(memid=" + _tlMemId + ")";
                        r.Detail = "宿主返回 S_OK（接受的调用已派发，结果以界面为准）";
                        log("api: " + r.Channel + " 调用返回 S_OK");
                        NoteWriteEvidence(exbPath, mtimeBefore, r, log);
                        return r;
                    }
                    log("api: 类型库通道 Invoke 失败 hr=0x" + hrT.ToString("X8") + "（" + HrName(hrT) + "）");

                    // 只有拿到 tlb 里的 oVft（权威偏移），才允许按槽直调；否则一律不碰 vtable
                    if (_tlSlot > 0)
                    {
                        int slot = _tlSlot / IntPtr.Size;
                        if (_tlSlot % IntPtr.Size == 0)
                        {
                            log("api: 退到 vtable 直调槽 " + slot + "（oVft=" + _tlSlot + "，来自类型库，非猜测值）");
                            int hrV = InvokeByVtableAtIndex(pv, slot, exbPath, updateBomAlso);
                            r.Hr = hrV;
                            r.Channel = "vtable槽" + slot;
                            r.Ok = hrV == 0;
                            r.Detail = r.Ok ? "宿主返回 S_OK（vtable 通道，槽位来自类型库）"
                                            : "vtable 通道失败 hr=0x" + hrV.ToString("X8") + "（" + HrName(hrV) + "）";
                            log("api: " + r.Detail);
                            return r;
                        }
                        log("api: 类型库里的 oVft=" + _tlSlot + " 不是指针宽度的整数倍（疑似 32 位类型库），" +
                            "放弃 vtable 直调（避免误调到别的函数把宿主搞崩）");
                    }

                    r.Detail = "类型库通道失败 hr=0x" + hrT.ToString("X8") + "（" + HrName(hrT) + "）";
                    // 不 return：继续走 IDispatch（万一注册表后来被补上）
                }

                // ════════════════════════════════════════════════════════════
                //  通道 ②：IDispatch 按名字取 dispId（依赖 TypeLib 注册，通常不可用）
                // ════════════════════════════════════════════════════════════
                Guid iid = IID_IDispatch;
                int hrQi = Marshal.QueryInterface(pUnk, ref iid, out pDisp);
                if (hrQi != 0 || pDisp == IntPtr.Zero)
                {
                    // 不是 IDispatch ⇒ 槽位前缀不成立，**绝不能**按固定槽硬调
                    r.Hr = hrQi;
                    r.Detail = "IZBaseApp 不支持 IDispatch（QI 失败 hr=0x" + hrQi.ToString("X8") + "）";
                    log("api: " + r.Detail);
                    return r;
                }

                int dispId;
                string nameErr;
                if (!TryGetDispId(pDisp, "UpdateDraftViews", out dispId, out nameErr))
                {
                    log("api: IDispatch 的 GetIDsOfNames 仍取不到「UpdateDraftViews」：" + nameErr +
                        "（TypeLib 未注册，属预期；上面类型库通道的结论才是准的）");
                    if (string.IsNullOrEmpty(r.Detail))
                        r.Detail = "三条通道都没成功（类型库未加载 + TypeLib 未注册）";
                    return r;
                }
                log("api: GetIDsOfNames → dispId=" + dispId);

                int hr = InvokeMethod(pDisp, dispId, bstrPath, updateBomAlso);
                r.Hr = hr;
                if (hr == 0)
                {
                    r.Ok = true;
                    r.Channel = "IDispatch(dispId=" + dispId + ")";
                    r.Detail = "宿主返回 S_OK（接受的调用已派发，结果以界面为准）";
                    log("api: " + r.Channel + " 调用返回 S_OK");
                    NoteWriteEvidence(exbPath, mtimeBefore, r, log);
                    return r;
                }

                log("api: IDispatch Invoke 失败 hr=0x" + hr.ToString("X8") + "（" + HrName(hr) + "）");
                r.Detail = "IDispatch Invoke 失败 hr=0x" + hr.ToString("X8") + "（" + HrName(hr) + "）";
                return r;
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Detail = "调用抛异常：" + ex.GetType().Name + ": " + ex.Message;
                log("api: " + r.Detail);
                return r;
            }
            finally
            {
                if (pBase != IntPtr.Zero) Marshal.Release(pBase);
                if (bstrPath != IntPtr.Zero) Marshal.FreeBSTR(bstrPath);
                if (pDisp != IntPtr.Zero) Marshal.Release(pDisp);
                if (pUnk != IntPtr.Zero) Marshal.Release(pUnk);
            }
        }

        /// <summary>
        /// "到底有没有干活"的实证：比对调用前后 exb 的**修改时间**。
        ///
        /// ★ 只当线索、不当判据 ★
        ///   宿主把明细改在内存里还没存盘时，mtime 不变也属正常；
        ///   但 mtime 变了，基本可以认定它真的写了这张图。
        ///   之所以要这条：`S_OK` 只代表"接受了调用"（和 CRX"投递成功≠执行完"同源），
        ///   没有副作用的成功最难分辨，得给它一个可观测的量。
        /// </summary>
        private static void NoteWriteEvidence(string exbPath, DateTime before, CallResult r, Action<string> log)
        {
            if (before == DateTime.MinValue) return;

            // 让宿主有机会把活干完：DoEvents 泵消息，不用长 Sleep 堵死主线程
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1500)
            {
                try { System.Windows.Forms.Application.DoEvents(); } catch { }
                try { System.Threading.Thread.Sleep(50); } catch { }
            }

            try
            {
                var fi = new FileInfo(exbPath);
                fi.Refresh();
                DateTime after = fi.LastWriteTime;
                string s = after > before.AddMilliseconds(400)
                    ? "exb 已被宿主改写（" + before.ToString("HH:mm:ss") + " → " + after.ToString("HH:mm:ss") + "）"
                    : "exb 修改时间未变（仍 " + after.ToString("HH:mm:ss") + "）——可能只改了内存未存盘，也可能没干活";
                log("api: " + s);
                r.Detail += "；" + s;
            }
            catch (Exception ex) { log("api: 读 exb 时间失败：" + ex.Message); }
        }

        // ============================================================ 类型库通道（权威路线）
        /// <summary>
        /// 类型库加载状态：`_tlInited` 保证只试一次（失败也不反复读盘），
        /// `_tlMemId` / `_tlSlot` 是**从 ICApiIronCAD.tlb 里读出来的权威值**，不是猜的。
        /// </summary>
        private static bool _tlInited;
        private static bool _tlReady;
        private static ITypeInfo _tlBaseInfo;
        private static int _tlMemId = -1;
        private static int _tlSlot = -1;
        private static string _tlPath = "";

        private const int REGKIND_NONE = 0;

        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
        private static extern int LoadTypeLibEx(string szFile, int regKind, out ITypeLib ppTLib);

        /// <summary>ITypeLib（标准 IID `00020402-…`）。只声明到 GetDocumentation，后面的不用。</summary>
        [ComImport, Guid("00020402-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ITypeLib
        {
            // ★ ITypeLib::GetTypeInfoCount 返回 **UINT 本身**，不是 HRESULT（写 out 参数会拿到垃圾值 0）★
            [PreserveSig] uint GetTypeInfoCount();
            [PreserveSig] int GetTypeInfo(uint index, out ITypeInfo ppTInfo);
            [PreserveSig] int GetTypeInfoType(uint index, out int pTKind);
            [PreserveSig] int GetTypeInfoOfGuid(ref Guid guid, out ITypeInfo ppTinfo);
            [PreserveSig] int GetLibAttr(out IntPtr ppTLibAttr);
            [PreserveSig] int GetTypeComp(out IntPtr ppTComp);
            [PreserveSig] int GetDocumentation(int index,
                [MarshalAs(UnmanagedType.BStr)] out string strName,
                [MarshalAs(UnmanagedType.BStr)] out string strDocString,
                out int dwHelpContext,
                [MarshalAs(UnmanagedType.BStr)] out string strHelpFile);
            [PreserveSig] int IsName([MarshalAs(UnmanagedType.LPWStr)] string szNameBuf, uint lHashVal, out int pfName);
        }

        /// <summary>ITypeInfo（标准 IID `00020401-…`）。全用 PreserveSig，拿 HRESULT 而不靠异常。
        ///
        /// ★★ 方法顺序按 oaidl.h 逐字抄，别凭直觉重排（11-04 19:36 离机实测）★★
        ///   真实顺序：GetTypeAttr 打头 → … → GetContainingTypeLib 倒数第 4 → 三个 Release* 收尾。
        ///   早期版本把 GetContainingTypeLib 写在第一个，导致**整串方法串位**：
        ///   GetFuncDesc 实际调到 GetVarDesc（IZBaseApp 没有变量 ⇒ index 0 就失败，函数总数读成 0），
        ///   ReleaseTypeAttr 实际调到 GetFuncDesc ⇒ 传个 IntPtr 进去当场原生 AV。
        ///   这就是"类型库看起来能加载、却一个成员都取不到"的真因。</summary>
        [ComImport, Guid("00020401-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        public interface ITypeInfo
        {
            [PreserveSig] int GetTypeAttr(out IntPtr ppTypeAttr);                          // 3
            [PreserveSig] int GetTypeComp(out IntPtr ppTComp);                             // 4
            [PreserveSig] int GetFuncDesc(uint index, out IntPtr ppFuncDesc);              // 5
            [PreserveSig] int GetVarDesc(uint index, out IntPtr ppVarDesc);                // 6
            [PreserveSig] int GetNames(int memid,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] IntPtr[] rgBstrNames,
                uint cMaxNames, out uint pcNames);                                         // 7
            [PreserveSig] int GetRefTypeOfImplType(uint index, out uint pRefType);         // 8
            [PreserveSig] int GetImplTypeFlags(uint index, out int pImplTypeFlags);        // 9
            [PreserveSig] int GetIDsOfNames(
                [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 1)] string[] rgszNames,
                uint cNames,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] int[] pMemId);      // 10
            [PreserveSig] int Invoke(IntPtr pvInstance, int memid, ushort wFlags,
                ref DISPPARAMS pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr); // 11
            [PreserveSig] int GetDocumentation(int memid,
                [MarshalAs(UnmanagedType.BStr)] out string strName,
                [MarshalAs(UnmanagedType.BStr)] out string strDocString,
                out int dwHelpContext,
                [MarshalAs(UnmanagedType.BStr)] out string strHelpFile);                   // 12
            [PreserveSig] int GetDllEntry(int memid, int invKind, IntPtr pBstrDllName,
                IntPtr pBstrName, IntPtr pwOrdinal);                                       // 13
            [PreserveSig] int GetRefTypeInfo(uint hRefType, out ITypeInfo ppTInfo);        // 14
            [PreserveSig] int AddressOfMember(int memid, int invKind, out IntPtr ppv);     // 15
            [PreserveSig] int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObj); // 16
            [PreserveSig] int GetMops(int memid, [MarshalAs(UnmanagedType.BStr)] out string pBstrMops); // 17
            [PreserveSig] int GetContainingTypeLib(out ITypeLib ppTLB, out uint pIndex);   // 18
            [PreserveSig] void ReleaseTypeAttr(IntPtr pTypeAttr);                          // 19
            [PreserveSig] void ReleaseFuncDesc(IntPtr pFuncDesc);                          // 20
            [PreserveSig] void ReleaseVarDesc(IntPtr pVarDesc);                            // 21
        }

        /// <summary>
        /// 找 `ICApiIronCAD.tlb`。顺序：插件自身目录（插件就装在宿主 bin 下，tlb 与它同目录）
        /// → 宿主 exe 目录 → 2024/2027 已知安装目录（只在同一台机器上兜底）。
        /// </summary>
        private static string FindTypeLib()
        {
            var cands = new System.Collections.Generic.List<string>();
            try
            {
                string dllDir = Path.GetDirectoryName(typeof(Update3DBomApi).Assembly.Location);
                if (!string.IsNullOrEmpty(dllDir)) cands.Add(Path.Combine(dllDir, "ICApiIronCAD.tlb"));
            }
            catch { }
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string exeDir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(exeDir)) cands.Add(Path.Combine(exeDir, "ICApiIronCAD.tlb"));
            }
            catch { }
            cands.Add(@"C:\Program Files\CAXA\CAXA 3D\2027\bin\ICApiIronCAD.tlb");
            cands.Add(@"C:\Program Files\CAXA\CAXA 3D\2024\bin\ICApiIronCAD.tlb");

            foreach (string c in cands)
            {
                try { if (File.Exists(c)) return c; } catch { }
            }
            return null;
        }

        /// <summary>加载类型库并定位 `IZBaseApp` 与 `UpdateDraftViews`（只做一次）。</summary>
        private static bool EnsureTypeLib(Action<string> log)
        {
            if (_tlInited) return _tlReady;
            _tlInited = true;

            try
            {
                string path = FindTypeLib();
                if (path == null)
                {
                    log("api: 没找到 ICApiIronCAD.tlb（插件目录/宿主目录都没有）→ 退回 IDispatch 通道");
                    return false;
                }

                ITypeLib lib;
                int hr = LoadTypeLibEx(path, REGKIND_NONE, out lib);
                if (hr != 0 || lib == null)
                {
                    log("api: LoadTypeLibEx 失败 hr=0x" + hr.ToString("X8") + " path=" + path);
                    return false;
                }
                _tlPath = path;
                log("api: 类型库已加载 " + path);

                ITypeInfo info = FindBaseInfo(lib, log);
                if (info == null)
                {
                    log("api: 类型库里没找到 IZBaseApp 的类型信息");
                    return false;
                }

                int memId = -1, slot = -1;

                // ① 直接问类型信息要 memid（离机实测：hr=0、memid=72，完全不碰注册表）
                try
                {
                    var ids = new int[1];
                    int hrName = info.GetIDsOfNames(new[] { "UpdateDraftViews" }, 1, ids);
                    log("api: ITypeInfo::GetIDsOfNames → hr=0x" + hrName.ToString("X8") + " memid=" + ids[0]);
                    if (hrName == 0 && ids[0] >= 0) memId = ids[0];
                }
                catch (Exception ex) { log("api: ITypeInfo::GetIDsOfNames 异常：" + ex.Message); }

                // ② 遍历 FUNCDESC：拿权威 oVft，并把所有 Draft/BOM 相关成员打进日志
                int scanMemId, scanSlot;
                ScanFunctions(info, log, out scanMemId, out scanSlot);
                if (memId < 0) memId = scanMemId;
                slot = scanSlot;

                if (memId < 0)
                {
                    log("api: 类型库里没有 UpdateDraftViews 这个成员（也可能名字不同）");
                    return false;
                }

                _tlBaseInfo = info;
                _tlMemId = memId;
                _tlSlot = slot;
                _tlReady = true;
                log("api: 类型库权威定位成功 → UpdateDraftViews memid=" + memId +
                    "，oVft=" + slot +
                    (slot > 0 ? "（对应 vtable 槽 " + (slot / IntPtr.Size) + "）" : "（无 vtable 偏移，走 IDispatch 派发）"));
                return true;
            }
            catch (Exception ex)
            {
                log("api: 类型库通道初始化异常：" + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>先按 IID 精确取，取不到再遍历类型库按名字找 `IZBaseApp`。</summary>
        private static ITypeInfo FindBaseInfo(ITypeLib lib, Action<string> log)
        {
            try
            {
                Guid iid = typeof(IZBaseApp).GUID;
                if (iid != Guid.Empty)
                {
                    ITypeInfo ti;
                    int hr = lib.GetTypeInfoOfGuid(ref iid, out ti);
                    if (hr == 0 && ti != null) return ti;
                    log("api: GetTypeInfoOfGuid(IZBaseApp=" + iid.ToString("B") + ") hr=0x" + hr.ToString("X8"));
                }
            }
            catch (Exception ex) { log("api: GetTypeInfoOfGuid 异常：" + ex.Message); }

            uint n = lib.GetTypeInfoCount();
            if (n == 0) return null;
            for (uint i = 0; i < n; i++)
            {
                try
                {
                    ITypeInfo ti;
                    if (lib.GetTypeInfo(i, out ti) != 0 || ti == null) continue;
                    string nm, ds, hf; int hc;
                    if (lib.GetDocumentation((int)i, out nm, out ds, out hc, out hf) != 0) continue;
                    if (nm != null && nm.IndexOf("IZBaseApp", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        log("api: 类型库按名字命中 " + nm + "（index=" + i + "）");
                        return ti;
                    }
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// 遍历 FUNCDESC：找 `UpdateDraftViews`，并把所有名字带 Draft / BOM 的成员打进日志
        /// （实机排错时这几行就是"宿主到底有没有这个能力"的铁证）。
        /// </summary>
        private static void ScanFunctions(ITypeInfo info, Action<string> log, out int memId, out int oVft)
        {
            memId = -1;
            oVft = -1;

            // FUNCDESC 偏移：memid@0；x64 → oVft@40；x86 → oVft@28
            int oVftOff = IntPtr.Size == 8 ? 40 : 28;
            int related = 0;

            for (uint i = 0; i < 2000; i++)
            {
                IntPtr fd;
                if (info.GetFuncDesc(i, out fd) != 0 || fd == IntPtr.Zero) break;   // 越界即止
                try
                {
                    int id = Marshal.ReadInt32(fd, 0);
                    short ov = Marshal.ReadInt16(fd, oVftOff);
                    string nm = NameOf(info, id);

                    if (!string.IsNullOrEmpty(nm) &&
                        (nm.IndexOf("Draft", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         nm.IndexOf("BOM", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        log("api:   成员 " + nm + " memid=" + id + " oVft=" + ov);
                        related++;
                    }
                    if (string.Equals(nm, "UpdateDraftViews", StringComparison.Ordinal))
                    {
                        memId = id;
                        oVft = ov;
                    }
                }
                finally { try { info.ReleaseFuncDesc(fd); } catch { } }
            }
            log("api: 类型库遍历完成，与 Draft/BOM 相关的成员 " + related + " 个");
        }

        /// <summary>取成员的显示名（BSTR 由调用方释放，所有权在 GetNames 这边）。</summary>
        private static string NameOf(ITypeInfo info, int memid)
        {
            var buf = new IntPtr[1];
            uint got;
            if (info.GetNames(memid, buf, 1, out got) != 0 || got < 1 || buf[0] == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringBSTR(buf[0]); }
            catch { return ""; }
            finally { try { Marshal.FreeBSTR(buf[0]); } catch { } }
        }

        /// <summary>用 ITypeInfo 按 memid 派发（不需要 TypeLib 注册，也不依赖槽位猜测）。</summary>
        private static int TypeLibInvoke(IntPtr pvInstance, int memId, IntPtr bstrPath, bool updateBomAlso)
        {
            if (_tlBaseInfo == null) return unchecked((int)0x80004005);

            int vs = VariantSize;
            IntPtr argv = Marshal.AllocHGlobal(vs * 2);
            try
            {
                // rgvarg **逆序**：最后一个参数在 [0]
                WriteBoolVariant(argv, updateBomAlso);
                WriteBstrVariant(new IntPtr(argv.ToInt64() + vs), bstrPath);

                var dp = new DISPPARAMS();
                dp.rgvarg = argv;
                dp.cArgs = 2;
                dp.cNamedArgs = 0;
                dp.rgdispidNamedArgs = IntPtr.Zero;

                return _tlBaseInfo.Invoke(pvInstance, memId, DISPATCH_METHOD, ref dp,
                                          IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            catch (Exception) { return unchecked((int)0x80004005); }
            finally { Marshal.FreeHGlobal(argv); }
        }

        // ============================================================ IDispatch 三件套

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_GetIDsOfNames(IntPtr pThis, ref Guid riid, IntPtr rgszNames,
            uint cNames, uint lcid, out int rgDispId);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_Invoke(IntPtr pThis, int dispIdMember, ref Guid riid, uint lcid,
            ushort wFlags, ref DISPPARAMS pDispParams, IntPtr pVarResult, IntPtr pExcepInfo, IntPtr puArgErr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int D_UpdateDraftViews(IntPtr pThis,
            [MarshalAs(UnmanagedType.BStr)] string exbPath, short updateBomAlso);

        [StructLayout(LayoutKind.Sequential)]
        public struct DISPPARAMS
        {
            public IntPtr rgvarg;             // VARIANT 数组（**逆序**：最后一个参数在 [0]）
            public IntPtr rgdispidNamedArgs;
            public int cArgs;
            public int cNamedArgs;
        }

        /// <summary>Q: 名字 → DispId（顺带证明"这是双接口"：能走通 GetIDsOfNames 就一定有 IDispatch 前缀）。</summary>
        private static bool TryGetDispId(IntPtr pDisp, string name, out int dispId, out string err)
        {
            dispId = -1;
            err = "";
            IntPtr bstrName = IntPtr.Zero, names = IntPtr.Zero;
            try
            {
                IntPtr fn = VtblEntry(pDisp, 5);                  // IDispatch::GetIDsOfNames
                if (fn == IntPtr.Zero) { err = "vtable[5] 为空"; return false; }
                var call = (D_GetIDsOfNames)Marshal.GetDelegateForFunctionPointer(fn, typeof(D_GetIDsOfNames));

                bstrName = Marshal.StringToBSTR(name);
                names = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(names, bstrName);

                Guid iid = Guid.Empty;
                int hr = call(pDisp, ref iid, names, 1, 0, out dispId);
                if (hr != 0)
                {
                    err = "hr=0x" + hr.ToString("X8") + "（" + HrName(hr) + "）";
                    dispId = -1;
                    return false;
                }
                return true;
            }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; return false; }
            finally
            {
                if (names != IntPtr.Zero) Marshal.FreeHGlobal(names);
                if (bstrName != IntPtr.Zero) Marshal.FreeBSTR(bstrName);
            }
        }

        /// <summary>VARIANT 尺寸：x64 = 24（vt+wReserved 8 字节 + 16 字节联合体），x86 = 16；联合体都从偏移 8 起。</summary>
        private static int VariantSize { get { return IntPtr.Size == 8 ? 24 : 16; } }

        private static int InvokeMethod(IntPtr pDisp, int dispId, IntPtr bstrPath, bool updateBomAlso)
        {
            IntPtr fn = VtblEntry(pDisp, 6);                      // IDispatch::Invoke
            if (fn == IntPtr.Zero) return unchecked((int)0x80004005);   // E_FAIL
            var call = (D_Invoke)Marshal.GetDelegateForFunctionPointer(fn, typeof(D_Invoke));

            int vs = VariantSize;
            IntPtr argv = Marshal.AllocHGlobal(vs * 2);
            try
            {
                // ★ 逆序 ★ rgvarg[0] = 最后一个参数(updateBomAlso)，rgvarg[1] = 第一个参数(exbPath)
                WriteBoolVariant(argv, updateBomAlso);
                WriteBstrVariant(new IntPtr(argv.ToInt64() + vs), bstrPath);

                var dp = new DISPPARAMS();
                dp.rgvarg = argv;
                dp.cArgs = 2;
                dp.cNamedArgs = 0;
                dp.rgdispidNamedArgs = IntPtr.Zero;

                Guid iid = Guid.Empty;
                return call(pDisp, dispId, ref iid, 0, DISPATCH_METHOD, ref dp,
                            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            }
            finally { Marshal.FreeHGlobal(argv); }
        }

        private static void WriteBstrVariant(IntPtr p, IntPtr bstr)
        {
            ZeroVariant(p);
            Marshal.WriteInt16(p, 0, 8);                 // VT_BSTR
            Marshal.WriteIntPtr(p, 8, bstr);             // 联合体：BSTR 指针
        }

        private static void WriteBoolVariant(IntPtr p, bool v)
        {
            ZeroVariant(p);
            Marshal.WriteInt16(p, 0, 11);                // VT_BOOL
            Marshal.WriteInt16(p, 8, (short)(v ? -1 : 0));   // VARIANT_TRUE = -1
        }

        private static void ZeroVariant(IntPtr p)
        {
            for (int i = 0; i < VariantSize; i++) Marshal.WriteByte(p, i, 0);
        }

        /// <summary>
        /// 按**指定槽位**直调。槽位必须来自类型库里的 `oVft`（权威），绝不用硬编码 81 ——
        /// 索引错一个就可能是原生 AV，连日志都留不下。
        /// </summary>
        private static int InvokeByVtableAtIndex(IntPtr pUnk, int slot, string exbPath, bool updateBomAlso)
        {
            IntPtr fn = VtblEntry(pUnk, slot);
            if (fn == IntPtr.Zero) return unchecked((int)0x80004005);
            var call = (D_UpdateDraftViews)Marshal.GetDelegateForFunctionPointer(fn, typeof(D_UpdateDraftViews));
            return call(pUnk, exbPath, (short)(updateBomAlso ? -1 : 0));
        }

        private static IntPtr VtblEntry(IntPtr p, int index)
        {
            try
            {
                IntPtr vtbl = Marshal.ReadIntPtr(p);
                if (vtbl == IntPtr.Zero) return IntPtr.Zero;
                return Marshal.ReadIntPtr(vtbl, index * IntPtr.Size);
            }
            catch { return IntPtr.Zero; }
        }

        // ============================================================ Draft 侧通道探测

        private static Guid CLSID_Cx3DIOAPI = new Guid("E59C176D-6FA9-4C81-A1D4-F3C7DC2F260B");
        private static Guid IID_ICx3DIOAPI = new Guid("EECDE072-3EA2-4F5E-95DE-DA5734F25A95");
        private const uint CLSCTX_INPROC_SERVER = 0x1;

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx,
            ref Guid iid, out IntPtr ppv);

        /// <summary>
        /// 探一下图纸侧静默入口 `ICx3DIOAPI` 能不能在**本进程**里 CoCreate 出来
        /// （coclass `Cx3DIOAPI`，纯 SxS：只在 `CAXADraft.manifest` 登记，注册表零登记）。
        ///
        /// 只**探测不调用**：本轮先确认"通不通"，通了下一轮再谈 `UpdateBOM(exb)`。
        /// 理由：3D 侧 `UpdateDraftViews(..., TRUE)` 本身就是"更新视图 + 更新 BOM"，
        /// 再叠一次 Draft 侧 `UpdateBOM` 属于重复更新，语义上不该同时开火。
        /// </summary>
        public static void ProbeDraftSide(Action<string> log)
        {
            if (log == null) log = delegate { };
            IntPtr p = IntPtr.Zero;
            try
            {
                Guid clsid = CLSID_Cx3DIOAPI, iid = IID_ICx3DIOAPI;
                int hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out p);
                if (hr == 0 && p != IntPtr.Zero)
                    log("api: 图纸侧通道 ICx3DIOAPI CoCreate 成功（CLSCTX_INPROC）—— " +
                        "可作为下一步 UpdateBOM(exb) 的静默入口");
                else
                    log("api: 图纸侧通道 ICx3DIOAPI CoCreate 失败 hr=0x" + hr.ToString("X8") +
                        "（纯 SxS 未在进程激活上下文里解析，属预期，不影响 3D 侧通道）");
            }
            catch (Exception ex)
            {
                log("api: 图纸侧通道探测异常：" + ex.Message);
            }
            finally
            {
                if (p != IntPtr.Zero) { try { Marshal.Release(p); } catch { } }
            }
        }

        // ============================================================ 小工具

        private static string HrName(int hr)
        {
            if (hr == 0) return "S_OK";
            if (hr == DISP_E_MEMBERNOTFOUND) return "DISP_E_MEMBERNOTFOUND";
            if (hr == DISP_E_BADPARAMCOUNT) return "DISP_E_BADPARAMCOUNT";
            if (hr == E_NOTIMPL) return "E_NOTIMPL";
            if (hr == unchecked((int)0x80004005)) return "E_FAIL";
            if (hr == unchecked((int)0x80004002)) return "E_NOINTERFACE";
            if (hr == unchecked((int)0x80020009)) return "DISP_E_EXCEPTION";
            if (hr == unchecked((int)0x80040154)) return "REGDB_E_CLASSNOTREG";
            return "0x" + hr.ToString("X8");
        }

        /// <summary>把 3D 侧调用的结果拼成一行给人看的话（状态行 + 日志共用）。</summary>
        public static string Describe(CallResult r)
        {
            if (r == null) return "无结果";
            var sb = new StringBuilder();
            sb.Append(r.Ok ? "API 调用成功" : "API 调用失败");
            if (!string.IsNullOrEmpty(r.Channel)) sb.Append("（通道=").Append(r.Channel).Append("）");
            sb.Append("：").Append(r.Detail);
            return sb.ToString();
        }
    }
}
