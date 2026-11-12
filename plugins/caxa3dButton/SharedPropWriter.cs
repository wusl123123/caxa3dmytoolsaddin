// 共享自定义属性表写入底座（从 PartSpecFillTool 抽出的最小子集）。
// 铁律（v003.049 规则第 14 节 7 / 共享表唯一铁律）：
//   1. 判据只有 IZCustomPropMgr::IsSharedMgr()（槽7）= true
//   2. 三条候选全试：GetCustomPropManager(0/1)（槽42，优先）→ get_CustomPropMgr(-1)（槽24，回退）
//   3. 全否即返回空，绝不兜底换表（零件级表是另一回事）
//   4. SetValueByName 失败（属性不存在）→ AddCustomProp(槽14，vbReplaceExistingOne=true)
//   5. 写入判据 = SetValueByName 后 GetValueByName 回读逐字一致；HRESULT 不算成功
//
// 通道来源：caxa3dBatchMaster CaxaPropWrite 同源（已实机）；caxa3ddiffrename SheetSizeTool 同款；
//   PartSpecFillTool 完整实现（本文件从中抽最小子集）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    internal static class SharedPropWriter
    {
        // IZElement 槽位
        private const int SlotElemGetCustomPropManager = 42; // IZElement::GetCustomPropManager(int)（优先）
        private const int SlotElemGetCustomPropMgr = 24;     // IZElement::get_CustomPropMgr(vbShared)（回退）

        // IZCustomPropMgr 槽位
        private const int SlotCpmIsSharedMgr = 7;            // IZCustomPropMgr::IsSharedMgr
        private const int SlotCpmGetValueByName = 9;         // IZCustomPropMgr::GetValueByName
        private const int SlotCpmSetValueByName = 10;         // IZCustomPropMgr::SetValueByName
        private const int SlotCpmAddCustomProp = 14;         // IZCustomPropMgr::AddCustomProp

        private static readonly Guid IID_IZElement = new Guid("69EF5583-A26D-4913-A2F6-DBFF73E1B8A3");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetShort(IntPtr self, out short pv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetCustomPropManager(IntPtr self, int selector, out IntPtr ppMgr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetCustomPropMgr(IntPtr self, short vbShared, out IntPtr ppMgr);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetValueByName(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string bstrName, IntPtr pVar);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSetValueByName(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string bstrName, IntPtr pVar);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnAddCustomProp(IntPtr self, [MarshalAs(UnmanagedType.BStr)] string bstrName, IntPtr pVar, short vbReplaceExistingOne);

        private const short VT_BSTR = 8;

        [DllImport("oleaut32.dll")]
        private static extern int VariantClear(IntPtr pvarg);

        // ==================================================================== 公开入口

        /// <summary>
        /// 把 values 字典逐个写进 elem 所在的**共享**自定义属性表。
        /// 任一字段写失败即返回 false 并带出 err；共享表拿不到按铁律不动。
        /// </summary>
        public static bool WriteNamedSharedProps(IZElement elem, Dictionary<string, string> values,
            string tag, Action<string> log, out string err)
        {
            err = null;
            IntPtr pMgr = GetSharedCustomPropMgr(elem, tag, log);
            if (pMgr == IntPtr.Zero)
            {
                err = "共享自定义属性表获取失败（三候选都无 IsSharedMgr=true），按共享表铁律不写";
                return false;
            }
            try
            {
                foreach (var kv in values)
                {
                    string e;
                    if (!EnsureSharedProp(pMgr, kv.Key, kv.Value, tag, log, out e)) { err = e; return false; }
                }
                return true;
            }
            finally { Marshal.Release(pMgr); }
        }

        // ==================================================================== 共享表获取（三候选 + IsSharedMgr 铁律）

        private static IntPtr GetSharedCustomPropMgr(IZElement elem, string tag, Action<string> log)
        {
            IntPtr pUnk = IntPtr.Zero;
            try { pUnk = Marshal.GetIUnknownForObject(elem); }
            catch (Exception ex) { log(tag + ": 共享表：GetIUnknownForObject EX " + ProbeCommon.Hr(ex)); return IntPtr.Zero; }

            IntPtr pElem = IntPtr.Zero;
            try
            {
                Guid iid = IID_IZElement;
                int qi = Marshal.QueryInterface(pUnk, ref iid, out pElem);
                if (qi != 0 || pElem == IntPtr.Zero)
                {
                    log(tag + ": 共享表：QI IZElement(69EF5583) 失败");
                    return IntPtr.Zero;
                }

                // 优先：GetCustomPropManager(selector 0/1)（槽42）
                foreach (int selector in new int[] { 0, 1 })
                {
                    IntPtr pMgr;
                    try
                    {
                        int hr = ProbeCommon.Fn<FnGetCustomPropManager>(pElem, SlotElemGetCustomPropManager)(pElem, selector, out pMgr);
                        log(tag + ": 共享表：GetCustomPropManager(selector=" + selector + ") hr=0x" + hr.ToString("X") +
                            " ptr=0x" + pMgr.ToInt64().ToString("X"));
                        if (hr != 0 || pMgr == IntPtr.Zero) continue;
                        if (IsSharedMgr(pMgr, tag, log)) return pMgr;
                        Marshal.Release(pMgr);
                    }
                    catch (Exception ex) { log(tag + ": 共享表：GetCustomPropManager(" + selector + ") EX " + ProbeCommon.Hr(ex)); }
                }

                // 回退：get_CustomPropMgr(vbShared=-1)（槽24）
                try
                {
                    IntPtr pMgr;
                    int hr = ProbeCommon.Fn<FnGetCustomPropMgr>(pElem, SlotElemGetCustomPropMgr)(pElem, (short)(-1), out pMgr);
                    log(tag + ": 共享表：get_CustomPropMgr(vbShared=-1) hr=0x" + hr.ToString("X") +
                        " ptr=0x" + pMgr.ToInt64().ToString("X"));
                    if (hr == 0 && pMgr != IntPtr.Zero)
                    {
                        if (IsSharedMgr(pMgr, tag, log)) return pMgr;
                        Marshal.Release(pMgr);
                    }
                }
                catch (Exception ex) { log(tag + ": 共享表：get_CustomPropMgr EX " + ProbeCommon.Hr(ex)); }

                log(tag + ": 共享表：三个候选都没有 IsSharedMgr=true——按共享表铁律不读写");
                return IntPtr.Zero;
            }
            finally
            {
                if (pElem != IntPtr.Zero) Marshal.Release(pElem);
                Marshal.Release(pUnk);
            }
        }

        private static bool IsSharedMgr(IntPtr pMgr, string tag, Action<string> log)
        {
            try
            {
                short shared;
                int hr = ProbeCommon.Fn<FnGetShort>(pMgr, SlotCpmIsSharedMgr)(pMgr, out shared);
                log(tag + ": 共享表：IsSharedMgr hr=0x" + hr.ToString("X") + " value=" + shared);
                return hr == 0 && shared != 0;
            }
            catch (Exception ex) { log(tag + ": 共享表：IsSharedMgr EX " + ProbeCommon.Hr(ex)); return false; }
        }

        // ==================================================================== 属性写入（SetValueByName→AddCustomProp 回退 + 回读判据）

        private static bool EnsureSharedProp(IntPtr pMgr, string name, string value,
            string tag, Action<string> log, out string err)
        {
            err = null;
            string before;
            bool readOk = SharedGetValueByName(pMgr, name, out before);
            log(tag + ": 「" + name + "」原值=" + (readOk ? "\"" + before + "\"" : "<读不到/不存在>"));

            if (!SharedSetValueByName(pMgr, name, value))
            {
                log(tag + ": SetValueByName(" + name + ") 失败 → 回退 AddCustomProp(槽14)");
                if (!SharedAddCustomProp(pMgr, name, value))
                {
                    err = "SetValueByName 与 AddCustomProp(槽14) 均失败";
                    log(tag + ": " + err);
                    return false;
                }
            }

            string got;
            bool ok = SharedGetValueByName(pMgr, name, out got) &&
                      string.Equals(got, value, StringComparison.Ordinal);
            log(tag + ": 「" + name + "」回读=\"" + got + "\"（" + (ok ? "一致" : "不一致") + "）");
            if (!ok) err = "「" + name + "」写后回读不一致（回读=\"" + got + "\"）";
            return ok;
        }

        private static bool SharedGetValueByName(IntPtr pMgr, string name, out string value)
        {
            value = null;
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int k = 0; k < 24; k++) Marshal.WriteByte(pVar, k, 0);
            try
            {
                int hr = ProbeCommon.Fn<FnGetValueByName>(pMgr, SlotCpmGetValueByName)(pMgr, name, pVar);
                if (hr != 0) { Diagnostics.Log("shared-prop: GetValueByName(" + name + ") hr=0x" + hr.ToString("X")); return false; }
                value = VariantToString(pVar);
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("shared-prop: GetValueByName(" + name + ") EX " + ProbeCommon.Hr(ex));
                return false;
            }
            finally { VariantClear(pVar); Marshal.FreeCoTaskMem(pVar); }
        }

        private static bool SharedSetValueByName(IntPtr pMgr, string name, string value)
        {
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int k = 0; k < 24; k++) Marshal.WriteByte(pVar, k, 0);
            try
            {
                Marshal.WriteInt16(pVar, 0, VT_BSTR);
                Marshal.WriteIntPtr(pVar, 8, Marshal.StringToBSTR(value ?? ""));
                int hr = ProbeCommon.Fn<FnSetValueByName>(pMgr, SlotCpmSetValueByName)(pMgr, name, pVar);
                Diagnostics.Log("shared-prop: SetValueByName(" + name + ") hr=0x" + hr.ToString("X"));
                return hr == 0;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("shared-prop: SetValueByName(" + name + ") EX " + ProbeCommon.Hr(ex));
                return false;
            }
            finally { VariantClear(pVar); Marshal.FreeCoTaskMem(pVar); }
        }

        private static bool SharedAddCustomProp(IntPtr pMgr, string name, string value)
        {
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int k = 0; k < 24; k++) Marshal.WriteByte(pVar, k, 0);
            try
            {
                Marshal.WriteInt16(pVar, 0, VT_BSTR);
                Marshal.WriteIntPtr(pVar, 8, Marshal.StringToBSTR(value ?? ""));
                int hr = ProbeCommon.Fn<FnAddCustomProp>(pMgr, SlotCpmAddCustomProp)(pMgr, name, pVar, (short)(-1));
                Diagnostics.Log("shared-prop: AddCustomProp(" + name + ") hr=0x" + hr.ToString("X"));
                return hr == 0;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("shared-prop: AddCustomProp(" + name + ") EX " + ProbeCommon.Hr(ex));
                return false;
            }
            finally { VariantClear(pVar); Marshal.FreeCoTaskMem(pVar); }
        }

        // ==================================================================== VARIANT 工具

        private static string VariantToString(IntPtr pVar)
        {
            short vt = Marshal.ReadInt16(pVar);
            switch (vt)
            {
                case 0: case 1: return "";                                 // VT_EMPTY / VT_NULL
                case 2: return Marshal.ReadInt16(pVar, 8).ToString();      // VT_I2
                case 3: return Marshal.ReadInt32(pVar, 8).ToString();      // VT_I4
                case 4:                                                    // VT_R4
                    {
                        byte[] b = new byte[4];
                        Marshal.Copy(pVar + 8, b, 0, 4);
                        return BitConverter.ToSingle(b, 0).ToString(CultureInfo.InvariantCulture);
                    }
                case 5:                                                    // VT_R8
                    {
                        byte[] b = new byte[8];
                        Marshal.Copy(pVar + 8, b, 0, 8);
                        return BitConverter.ToDouble(b, 0).ToString(CultureInfo.InvariantCulture);
                    }
                case 8:                                                    // VT_BSTR
                    {
                        IntPtr pBstr = Marshal.ReadIntPtr(pVar, 8);
                        return pBstr == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(pBstr);
                    }
                case 11: return Marshal.ReadInt16(pVar, 8) != 0 ? "True" : "False";   // VT_BOOL
                default: return "<vt=" + vt + ">";
            }
        }
    }
}
