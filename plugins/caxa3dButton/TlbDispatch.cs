// TLB 类型库派发通道（通道阶梯第二级：RCW 直调 → ★本文件★ → 槽位直调 → 命令派发 → UI 代点）。
// 用途：v26 interop 缺成员（CS1061）但 2027 TLB 有时，按 memid 派发，不写注册表、不猜槽位。
// 移植自 caxa3dexbtracker\Update3DBomApi.cs 的 ITypeLib/ITypeInfo 声明与加载链（离机实测过的顺序）。
//
// ★ 三个 AV 地雷（照抄 donor 的教训，别凭直觉改）★
//   1. ITypeLib::GetTypeInfoCount 返回 **UINT 本身**，不是 HRESULT；写成 out 参数会拿到垃圾 0。
//   2. ITypeInfo 方法顺序必须逐字按 oaidl.h：GetTypeAttr 打头 → GetContainingTypeLib 倒数第 4 → 三个 Release* 收尾。
//      重排一个就整串串位（GetFuncDesc 实际调到 GetVarDesc、ReleaseTypeAttr 实际调到 GetFuncDesc ⇒ 原生 AV）。
//   3. 绝不对接口调 GetImplTypeFlags（声明保留只为占住槽位，调用即 AV）。
//
// 第三态纪律：加载不到 tlb / 找不到接口 / 找不到成员 / Invoke 非 S_OK，各自记一行原因并返回 HRESULT，
// 由调用方决定是否降级到槽位直调，并把降级理由写进日志。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace caxa3dButton
{
    /// <summary>一个 [out] 参数：Vt 指明被指向存储的类型，ByRef 指向调用方持有的存储。</summary>
    internal sealed class TlbOutArg
    {
        public ushort Vt;        // VT_BSTR=8 / VT_I4=3 / VT_R8=5
        public IntPtr ByRef;     // BSTR* / int* / double*

        public static TlbOutArg Bstr(IntPtr pBstrSlot) { return new TlbOutArg { Vt = 8, ByRef = pBstrSlot }; }
        public static TlbOutArg Int(IntPtr pIntSlot) { return new TlbOutArg { Vt = 3, ByRef = pIntSlot }; }
        public static TlbOutArg Double(IntPtr pDoubleSlot) { return new TlbOutArg { Vt = 5, ByRef = pDoubleSlot }; }
    }

    internal static class TlbDispatch
    {
        private const int REGKIND_NONE = 0;
        private const ushort DISPATCH_METHOD = 0x1;
        private const int E_FAIL = unchecked((int)0x80004005);

        private static bool _inited;
        private static string _path;
        private static ITypeLib _lib;
        private static readonly Dictionary<string, ITypeInfo> _byName =
            new Dictionary<string, ITypeInfo>(StringComparer.Ordinal);
        private static readonly Dictionary<string, int> _memIds =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>VARIANT 尺寸：x64=24（vt+3×reserved 共 8 字节 + 16 字节联合体），联合体从偏移 8 起。</summary>
        public static int VariantSize { get { return IntPtr.Size == 8 ? 24 : 16; } }

        public static string TypeLibPath { get { return _path ?? "<未加载>"; } }

        [DllImport("oleaut32.dll", CharSet = CharSet.Unicode)]
        private static extern int LoadTypeLibEx(string szFile, int regKind, out ITypeLib ppTLib);

        [ComImport, Guid("00020402-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITypeLib
        {
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

        [ComImport, Guid("00020401-0000-0000-C000-000000000046"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITypeInfo
        {
            [PreserveSig] int GetTypeAttr(out IntPtr ppTypeAttr);                          // 3
            [PreserveSig] int GetTypeComp(out IntPtr ppTComp);                             // 4
            [PreserveSig] int GetFuncDesc(uint index, out IntPtr ppFuncDesc);              // 5
            [PreserveSig] int GetVarDesc(uint index, out IntPtr ppVarDesc);                // 6
            [PreserveSig] int GetNames(int memid,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] IntPtr[] rgBstrNames,
                uint cMaxNames, out uint pcNames);                                         // 7
            [PreserveSig] int GetRefTypeOfImplType(uint index, out uint pRefType);         // 8
            [PreserveSig] int GetImplTypeFlags(uint index, out int pImplTypeFlags);        // 9（禁调，仅占位）
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

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPPARAMS
        {
            public IntPtr rgvarg;               // VARIANT 数组，★逆序★：最后一个参数在 [0]
            public IntPtr rgdispidNamedArgs;
            public int cArgs;
            public int cNamedArgs;
        }

        // ---------------------------------------------------------------- 加载

        /// <summary>找 ICApiIronCAD.tlb：插件目录（装在宿主 bin 下，tlb 同目录）→ 宿主 exe 目录 → 已知安装目录。</summary>
        private static string FindTypeLib()
        {
            var cands = new List<string>();
            try
            {
                string dir = Path.GetDirectoryName(typeof(TlbDispatch).Assembly.Location);
                if (!string.IsNullOrEmpty(dir)) cands.Add(Path.Combine(dir, "ICApiIronCAD.tlb"));
            }
            catch { }
            try
            {
                string dir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                if (!string.IsNullOrEmpty(dir)) cands.Add(Path.Combine(dir, "ICApiIronCAD.tlb"));
            }
            catch { }
            cands.Add(@"C:\Program Files\CAXA\CAXA 3D\2027\bin\ICApiIronCAD.tlb");
            cands.Add(@"C:\Program Files\CAXA\CAXA 3D\2024\bin\ICApiIronCAD.tlb");
            foreach (string c in cands) { try { if (File.Exists(c)) return c; } catch { } }
            return null;
        }

        private static bool EnsureLib(Action<string> log)
        {
            if (_inited) return _lib != null;
            _inited = true;
            try
            {
                string path = FindTypeLib();
                if (path == null) { log("tlb: 没找到 ICApiIronCAD.tlb（插件目录/宿主目录/已知安装目录都没有）"); return false; }
                ITypeLib lib;
                int hr = LoadTypeLibEx(path, REGKIND_NONE, out lib);
                if (hr != 0 || lib == null) { log("tlb: LoadTypeLibEx 失败 " + ProbeCommon.Hr(hr) + " path=" + path); return false; }
                _lib = lib;
                _path = path;
                log("tlb: 已加载（REGKIND_NONE，不写注册表）" + path + "，类型数=" + lib.GetTypeInfoCount());
                return true;
            }
            catch (Exception ex) { log("tlb: 加载异常 " + ProbeCommon.Hr(ex) + " " + ex.Message); return false; }
        }

        /// <summary>按接口名取 ITypeInfo：先 IID 精确取，取不到再遍历类型库按名字全等匹配。</summary>
        private static ITypeInfo TypeInfo(string ifaceName, Action<string> log)
        {
            ITypeInfo cached;
            if (_byName.TryGetValue(ifaceName, out cached)) return cached;
            if (!EnsureLib(log)) return null;

            ITypeInfo ti = null;
            try
            {
                Type t = Type.GetType("interop.ICApiIronCAD." + ifaceName + ", interop.ICApiIronCAD");
                if (t != null)
                {
                    Guid iid = t.GUID;
                    if (iid != Guid.Empty)
                    {
                        int hr = _lib.GetTypeInfoOfGuid(ref iid, out ti);
                        if (hr != 0 || ti == null)
                        {
                            log("tlb: GetTypeInfoOfGuid(" + ifaceName + "=" + iid.ToString("B") + ") " + ProbeCommon.Hr(hr));
                            ti = null;
                        }
                    }
                }
            }
            catch (Exception ex) { log("tlb: 取 IID 异常 " + ProbeCommon.Hr(ex)); }

            if (ti == null)
            {
                uint n = _lib.GetTypeInfoCount();
                for (uint i = 0; i < n && ti == null; i++)
                {
                    try
                    {
                        ITypeInfo cur;
                        if (_lib.GetTypeInfo(i, out cur) != 0 || cur == null) continue;
                        string nm, ds, hf; int hc;
                        if (_lib.GetDocumentation((int)i, out nm, out ds, out hc, out hf) != 0) continue;
                        if (string.Equals(nm, ifaceName, StringComparison.Ordinal)) ti = cur;
                    }
                    catch { }
                }
            }

            if (ti == null) { log("tlb: 类型库里没有接口 " + ifaceName); return null; }
            _byName[ifaceName] = ti;
            return ti;
        }

        private static int MemId(ITypeInfo ti, string ifaceName, string member, Action<string> log)
        {
            string key = ifaceName + "::" + member;
            int cached;
            if (_memIds.TryGetValue(key, out cached)) return cached;
            try
            {
                var ids = new int[1];
                int hr = ti.GetIDsOfNames(new string[] { member }, 1, ids);
                if (hr != 0 || ids[0] < 0)
                {
                    log("tlb: GetIDsOfNames(" + key + ") " + ProbeCommon.Hr(hr) + " memid=" + ids[0]);
                    return -1;
                }
                _memIds[key] = ids[0];
                return ids[0];
            }
            catch (Exception ex) { log("tlb: GetIDsOfNames(" + key + ") 异常 " + ProbeCommon.Hr(ex)); return -1; }
        }

        // ---------------------------------------------------------------- 派发

        /// <summary>
        /// 按 memid 派发一个方法。args 按**声明顺序**给（内部自动逆序填 rgvarg），全部是 [out] 参数。
        /// pVarResult 非零时承接 [retval]（调用方分配 VariantSize 字节并清零）。
        /// 返回 HRESULT；任何一级失败都记一行原因，绝不静默折算成功。
        /// </summary>
        public static int InvokeMethod(string ifaceName, string member, object instance,
            List<TlbOutArg> args, IntPtr pVarResult, Action<string> log)
        {
            if (instance == null) return E_FAIL;
            ITypeInfo ti = TypeInfo(ifaceName, log);
            if (ti == null) return E_FAIL;
            int memid = MemId(ti, ifaceName, member, log);
            if (memid < 0) return E_FAIL;

            IntPtr pUnk = Marshal.GetIUnknownForObject(instance);
            if (pUnk == IntPtr.Zero) return E_FAIL;
            int vs = VariantSize;
            int n = args == null ? 0 : args.Count;
            IntPtr argv = n > 0 ? Marshal.AllocHGlobal(vs * n) : IntPtr.Zero;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    // 声明顺序第 i 个 → rgvarg[n-1-i]
                    IntPtr p = new IntPtr(argv.ToInt64() + (long)(n - 1 - i) * vs);
                    for (int b = 0; b < vs; b++) Marshal.WriteByte(p, b, 0);
                    Marshal.WriteInt16(p, 0, (short)(args[i].Vt | 0x4000));   // VT_BYREF
                    Marshal.WriteIntPtr(p, 8, args[i].ByRef);
                }

                var dp = new DISPPARAMS();
                dp.rgvarg = argv;
                dp.cArgs = n;
                dp.cNamedArgs = 0;
                dp.rgdispidNamedArgs = IntPtr.Zero;

                int hr = ti.Invoke(pUnk, memid, DISPATCH_METHOD, ref dp, pVarResult, IntPtr.Zero, IntPtr.Zero);
                log("tlb: " + ifaceName + "::" + member + " memid=" + memid + " → " + ProbeCommon.Hr(hr));
                return hr;
            }
            catch (Exception ex)
            {
                log("tlb: Invoke(" + ifaceName + "::" + member + ") 异常 " + ProbeCommon.Hr(ex) + " " + ex.Message);
                return E_FAIL;
            }
            finally
            {
                if (argv != IntPtr.Zero) Marshal.FreeHGlobal(argv);
                Marshal.Release(pUnk);
            }
        }

        /// <summary>分配一个清零的 VARIANT 承接 [retval]；读法见 ReadRetValObject。</summary>
        public static IntPtr AllocResult()
        {
            IntPtr p = Marshal.AllocHGlobal(VariantSize);
            for (int i = 0; i < VariantSize; i++) Marshal.WriteByte(p, i, 0);
            return p;
        }

        public static void FreeResult(IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            try
            {
                short vt = Marshal.ReadInt16(p, 0);
                IntPtr val = Marshal.ReadIntPtr(p, 8);
                if (val == IntPtr.Zero) return;
                if (vt == 8) Marshal.FreeBSTR(val);
                else if (vt == 9 || vt == 13) Marshal.Release(val);   // VT_DISPATCH / VT_UNKNOWN
            }
            catch { }
            finally { Marshal.FreeHGlobal(p); }
        }

        /// <summary>从 [retval] VARIANT 取 COM 对象（VT_DISPATCH/VT_UNKNOWN）；取完 Release 自己那份引用。</summary>
        public static object ReadRetValObject(IntPtr pVarResult)
        {
            if (pVarResult == IntPtr.Zero) return null;
            short vt = Marshal.ReadInt16(pVarResult, 0);
            if (vt != 9 && vt != 13) return null;
            IntPtr p = Marshal.ReadIntPtr(pVarResult, 8);
            if (p == IntPtr.Zero) return null;
            try
            {
                object o = Marshal.GetObjectForIUnknown(p);
                return o;
            }
            finally { Marshal.Release(p); }
        }

        /// <summary>从 [retval] VARIANT 取 double（VT_R8 / VT_R4 / VT_I4）；取不到返回 false。</summary>
        public static bool ReadRetValDouble(IntPtr pVarResult, out double v)
        {
            v = 0;
            if (pVarResult == IntPtr.Zero) return false;
            short vt = Marshal.ReadInt16(pVarResult, 0);
            if (vt == 5) { v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(pVarResult, 8)); return true; }
            if (vt == 4) { v = Marshal.ReadInt32(pVarResult, 8); return true; }
            if (vt == 3) { v = Marshal.ReadInt32(pVarResult, 8); return true; }
            return false;
        }
    }
}