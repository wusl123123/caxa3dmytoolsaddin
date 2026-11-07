// CAXA 3D 自定义属性写入（vtable 直调，移植自 CaxaAutomation 已验证路径；2026-11-06 补空值/新属性两条回退）
// 写属性走 IZCustomPropMgr::SetValueByName（槽 10，2027 TLB 实测稳定）；
//   优先在"共享"管理器上写（规则 14.7：所有自定义属性强制写共享表＝对所有链接实例生效），
//   拿不到共享管理器则退回 get_CustomPropMgr(true) 再取一次；两条都失败就如实报「没拿到管理器」，不假装写成功。
// 三条写入姿势（依据：规则第 14 节第 4/5 条 + caxa3dcodenameextraction 的实测 CaxaAutomation）：
//   · 非空值  → SetValueByName(槽 10)；失败（多为「该属性名还不存在」）→ AddCustomProp(槽 14, vbReplaceExistingOne=true)。
//   · 空值    → RemoveByName(槽 13)（实测 SetValueByName 传空串会 E_INVALIDARG）；删除失败才退回写空串。
//   · 每次调用都把 hr（十六进制原文）写进 trace，判成功只看调用方的回读，不看 hr=0。
// 读属性见 CaxaBatchService（RCW 强类型 GetCustomPropAsString，槽 18）。
// 用 vtable 直调而非 RCW 属性，是为了绕开部分安装内嵌类型库对 interop 成员描述不一致的问题
//   （这些成员在工程 interop v26 里反射都是 PRESENT，通道阶梯上 RCW 更优先，但本路径已在实机跑过，不换）。
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    [ComVisible(false)]
    internal static class CaxaPropWrite
    {
        private static readonly Guid IID_IZElement = new Guid("69EF5583-A26D-4913-A2F6-DBFF73E1B8A3");
        private static readonly Guid IID_IZCustomPropMgr = new Guid("2ABEB1AE-964D-4259-AF70-2BC3E8E0AAB0");

        private const int SlotElemGetCustomPropManager = 42; // IZElement::GetCustomPropManager
        private const int SlotElemGetCustomPropMgr = 24;     // IZElement::get_CustomPropMgr
        private const int SlotCpmIsSharedMgr = 7;            // IZCustomPropMgr::IsSharedMgr
        private const int SlotCpmSetValueByName = 10;        // IZCustomPropMgr::SetValueByName
        private const int SlotCpmRemoveByName = 13;          // IZCustomPropMgr::RemoveByName
        private const int SlotCpmAddCustomProp = 14;         // IZCustomPropMgr::AddCustomProp
        private const short VT_BSTR = 8;

        /// <summary>一次批量写入的结果记账（Written+Removed+Failed = Total；管理器为 null 时全记 Failed）。</summary>
        public sealed class WriteOutcome
        {
            public int Total;
            public int Written;
            public int Removed;
            public int Failed;
            /// <summary>实际写到的管理器：selector 0、1 且 IsSharedMgr=true / get_CustomPropMgr(true) / none。</summary>
            public string Manager = "none";
            /// <summary>最后一次失败的说明（含 hr 原文），只用于日志，不参与判成功。</summary>
            public string LastError = "";
        }

        #region vtable 基础设施

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetCustomPropMgr(IntPtr self, short vbShared, out IntPtr ppMgr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetCustomPropManager(IntPtr self, int selector, out IntPtr ppMgr);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnGetShort(IntPtr self, out short value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnBstrArg(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string bstrName);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnSetValueByName(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string bstrName, IntPtr pvarValue);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnAddCustomProp(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] string bstrName, IntPtr pvarValue, short vbReplaceExistingOne);

        [DllImport("oleaut32.dll")] private static extern int VariantClear(IntPtr pvarg);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQuery(IntPtr lpAddress,
            out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);

        /// <summary>调用前探测：槽位函数指针必须落在已提交的可执行内存内（识别短 vtable 代理）。</summary>
        private static bool LooksCallable(IntPtr fn)
        {
            if (fn == IntPtr.Zero) return false;
            MEMORY_BASIC_INFORMATION mbi;
            if (VirtualQuery(fn, out mbi,
                    (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION))) == IntPtr.Zero)
                return false;
            const uint MEM_COMMIT = 0x1000;
            return mbi.State == MEM_COMMIT && (mbi.Protect & 0xF0) != 0;
        }

        private static IntPtr Slot(IntPtr pIface, int slot)
        {
            IntPtr vt = Marshal.ReadIntPtr(pIface);
            return Marshal.ReadIntPtr(vt, slot * IntPtr.Size);
        }

        #endregion

        #region 取共享管理器 + 写入

        /// <summary>获取元素上的"共享"CustomPropMgr（优先 selector 0/1 + IsSharedMgr 校验，回退 get_CustomPropMgr(vbShared=-1)）。</summary>
        private static IntPtr GetSharedCustomPropMgr(IntPtr pElem, WriteOutcome outcome)
        {
            foreach (int selector in new int[] { 0, 1 })
            {
                IntPtr pMgr = TryGetCustomPropManager(pElem, selector);
                if (pMgr == IntPtr.Zero) continue;
                if (TryIsSharedMgr(pMgr))
                {
                    outcome.Manager = "GetCustomPropManager(" + selector + ")+IsSharedMgr=true";
                    return pMgr;
                }
                Marshal.Release(pMgr);
            }
            IntPtr pProp = GetCustomPropMgrLegacy(pElem);
            if (pProp != IntPtr.Zero)
            {
                // 共享表铁律（规则 14.7/14.9）：vbShared=TRUE 只是名义，仍要 IsSharedMgr 确认才算共享表
                if (TryIsSharedMgr(pProp))
                {
                    outcome.Manager = "get_CustomPropMgr(true)+IsSharedMgr=true";
                    return pProp;
                }
                Marshal.Release(pProp);
            }
            Diagnostics.Log("GetSharedCustomPropMgr: 三个候选都没有 IsSharedMgr=true，按共享表铁律本次不读写任何其它表");
            outcome.Manager = "none";
            return IntPtr.Zero;
        }

        private static IntPtr TryGetCustomPropManager(IntPtr pElem, int selector)
        {
            try
            {
                IntPtr fn = Slot(pElem, SlotElemGetCustomPropManager);
                if (!LooksCallable(fn)) return IntPtr.Zero;
                FnGetCustomPropManager f =
                    (FnGetCustomPropManager)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetCustomPropManager));
                IntPtr pMgr;
                int hr = f(pElem, selector, out pMgr);
                Diagnostics.Trace(string.Format("GetCustomPropManager(selector={0}) hr=0x{1:X} ptr=0x{2:X}",
                    selector, hr, pMgr.ToInt64()));
                return hr == 0 ? pMgr : IntPtr.Zero;
            }
            catch (Exception ex) { Diagnostics.Log("GetCustomPropManager EX: " + ex.Message); return IntPtr.Zero; }
        }

        private static IntPtr GetCustomPropMgrLegacy(IntPtr pElem)
        {
            try
            {
                IntPtr fn = Slot(pElem, SlotElemGetCustomPropMgr);
                if (!LooksCallable(fn)) return IntPtr.Zero;
                FnGetCustomPropMgr f =
                    (FnGetCustomPropMgr)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetCustomPropMgr));
                IntPtr pMgr;
                int hr = f(pElem, -1, out pMgr);   // -1 = VARIANT_TRUE：get_CustomPropMgr(Boolean vbShared) 要共享表
                Diagnostics.Trace(string.Format("get_CustomPropMgr(true) hr=0x{0:X} ptr=0x{1:X}", hr, pMgr.ToInt64()));
                return hr == 0 ? pMgr : IntPtr.Zero;
            }
            catch (Exception ex) { Diagnostics.Trace("get_CustomPropMgr EX: " + ex.Message); return IntPtr.Zero; }
        }

        private static bool TryIsSharedMgr(IntPtr pMgr)
        {
            try
            {
                IntPtr fn = Slot(pMgr, SlotCpmIsSharedMgr);
                if (!LooksCallable(fn)) return false;
                FnGetShort f = (FnGetShort)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnGetShort));
                short shared;
                int hr = f(pMgr, out shared);
                return hr == 0 && shared != 0;
            }
            catch { return false; }
        }

        /// <summary>在 VARIANT(BSTR) 参数上调一个槽位方法；返回 hr，异常返回 0x80004005 并记 trace。</summary>
        private static int CallWithBstrValue(IntPtr pMgr, int slot, string name, string value, string label)
        {
            IntPtr fn = Slot(pMgr, slot);
            if (!LooksCallable(fn))
            {
                Diagnostics.Log(label + "(" + name + ") 跳过：函数指针无效");
                return unchecked((int)0x80004005);
            }
            IntPtr pVar = Marshal.AllocCoTaskMem(24);
            for (int k = 0; k < 24; k++) Marshal.WriteByte(pVar, k, 0);
            try
            {
                Marshal.WriteInt16(pVar, 0, VT_BSTR);
                Marshal.WriteIntPtr(pVar, 8, Marshal.StringToBSTR(value ?? ""));
                int hr;
                if (slot == SlotCpmSetValueByName)
                {
                    FnSetValueByName f = (FnSetValueByName)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnSetValueByName));
                    hr = f(pMgr, name, pVar);
                }
                else
                {
                    FnAddCustomProp g = (FnAddCustomProp)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnAddCustomProp));
                    hr = g(pMgr, name, pVar, -1);   // -1 = VARIANT_TRUE：vbReplaceExistingOne，同名直接覆盖
                }
                Diagnostics.Trace(string.Format("{0}({1}) hr=0x{2:X}", label, name, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log(label + "(" + name + ") EX: " + ex.Message);
                Diagnostics.Trace(label + "(" + name + ") EX: " + ex.Message);
                return unchecked((int)0x80004005);
            }
            finally { VariantClear(pVar); Marshal.FreeCoTaskMem(pVar); }
        }

        /// <summary>RemoveByName(槽 13)：只有一个 BSTR 参数，单独走简单委托。</summary>
        private static int CallRemoveByName(IntPtr pMgr, string name)
        {
            IntPtr fn = Slot(pMgr, SlotCpmRemoveByName);
            if (!LooksCallable(fn))
            {
                Diagnostics.Log("RemoveByName(" + name + ") 跳过：函数指针无效");
                return unchecked((int)0x80004005);
            }
            try
            {
                FnBstrArg f = (FnBstrArg)Marshal.GetDelegateForFunctionPointer(fn, typeof(FnBstrArg));
                int hr = f(pMgr, name);
                Diagnostics.Trace(string.Format("RemoveByName({0}) hr=0x{1:X}", name, hr));
                return hr;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("RemoveByName(" + name + ") EX: " + ex.Message);
                return unchecked((int)0x80004005);
            }
        }

        /// <summary>写一个属性值（含空值与「属性还不存在」两条回退）。返回 written/removed/failed 之一。</summary>
        private static string WriteOne(IntPtr pMgr, string name, string value, WriteOutcome outcome)
        {
            bool isEmpty = string.IsNullOrEmpty(value);

            if (isEmpty)
            {
                // 规则 14.4：空值 = 删除该属性（实测 SetValueByName 传空串会 E_INVALIDARG）
                if (CallRemoveByName(pMgr, name) == 0) return "removed";
                if (CallWithBstrValue(pMgr, SlotCpmSetValueByName, name, "", "SetValueByName(空值回退)") == 0)
                    return "written";
                outcome.LastError = "空值删除失败 RemoveByName(13) 与 SetValueByName(10) 均未返回 hr=0（见 trace）";
                return "failed";
            }

            int hr = CallWithBstrValue(pMgr, SlotCpmSetValueByName, name, value, "SetValueByName");
            if (hr == 0) return "written";

            // 属性名在该元素上还不存在时 SetValueByName 会失败 → AddCustomProp(槽 14) 建一个
            int hr2 = CallWithBstrValue(pMgr, SlotCpmAddCustomProp, name, value, "AddCustomProp");
            if (hr2 == 0) return "written";

            outcome.LastError = "SetValueByName hr=0x" + hr.ToString("X")
                + "，AddCustomProp hr=0x" + hr2.ToString("X");
            return "failed";
        }

        #endregion

        #region 对外

        /// <summary>把一组属性名=值写入元素（优先共享管理器）。逐条记账，单条失败不影响其余。</summary>
        public static WriteOutcome WriteElementProps(IZElement elem, IDictionary<string, string> values)
        {
            var outcome = new WriteOutcome();
            if (elem == null || values == null || values.Count == 0) return outcome;
            outcome.Total = values.Count;

            IntPtr pUnk = Marshal.GetIUnknownForObject(elem);
            IntPtr pElem = IntPtr.Zero;
            Guid gElem = IID_IZElement;
            int hr = Marshal.QueryInterface(pUnk, ref gElem, out pElem);
            Marshal.Release(pUnk);
            if (hr != 0 || pElem == IntPtr.Zero)
            {
                outcome.Failed = outcome.Total;
                outcome.LastError = "QI(IZElement) 失败 hr=0x" + hr.ToString("X");
                Diagnostics.Log("WriteElementProps: " + outcome.LastError);
                return outcome;
            }

            try
            {
                IntPtr pMgr = GetSharedCustomPropMgr(pElem, outcome);
                if (pMgr == IntPtr.Zero)
                {
                    outcome.Failed = outcome.Total;
                    outcome.LastError = "未取得 CustomPropMgr（共享表两条通道都没拿到指针）";
                    Diagnostics.Log("WriteElementProps: " + outcome.LastError);
                    return outcome;
                }
                try
                {
                    foreach (KeyValuePair<string, string> kv in values)
                    {
                        string r = WriteOne(pMgr, kv.Key, kv.Value ?? "", outcome);
                        if (r == "written") outcome.Written++;
                        else if (r == "removed") outcome.Removed++;
                        else outcome.Failed++;
                    }
                }
                finally { Marshal.Release(pMgr); }
            }
            finally { Marshal.Release(pElem); }
            return outcome;
        }

        /// <summary>写入单个属性的便捷入口（供只有单条属性的调用方使用）。</summary>
        public static bool WriteElementProp(IZElement elem, string name, string value)
        {
            var r = WriteElementProps(elem, new Dictionary<string, string> { { name, value } });
            return r.Written + r.Removed == r.Total && r.Total > 0;
        }

        #endregion
    }
}
