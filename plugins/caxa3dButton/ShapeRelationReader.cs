// 一条 IZShapeRelation 的完整只读读取（草图约束与智能标注尺寸共用）。
//
// ★ 通道阶梯（规则 03-com-api §14）★
//   槽 7/8/9/12/13/14/16 → v26 interop PRESENT ⇒ RCW 直调（顶层）。
//   槽 18 GetSmartDimPrefixAndPostfix / 槽 19 GetSmartDimToleranceTypeAndValues / 槽 20 GetOwner
//     → 2027 TLB 有（memid 12/13/14）、v26 interop MISSING（写 RCW 即 CS1061，caxa-interop-check 实证）
//     ⇒ 先走类型库派发（TlbDispatch，按 memid，不写注册表）；非 S_OK 才降级槽位直调，并把降级理由记进 Channel。
//
// ★ 多表面串位防护 ★：Z 系列「QI 任何接口都成功，但 IDispatch 只认默认接口成员」。
//   扩展槽位只在 RCW 锚点（Id / GetRelationType）读出合理值后才调；锚点失败一律跳过扩展读取，
//   记一行原因，绝不拿一个可能是别的表面的指针去戳槽 18/19/20（索引错一个就是原生 AV）。
//
// 单位：GetValue 的量纲随关系类型 —— 长度类＝米（×1000 给毫米），角度类＝弧度（×180/π 给度）。
//   两列各自只在对应类型下给值，另一列记 N/A，不折算、不混用。
// 第三态：每列独立 try/catch，读不到记 <FAIL:hr>，绝不写 0/false 冒充读到的值。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    internal sealed class RelationInfo
    {
        public int Id = -1;
        public string Type = "<读不到>";
        public int TypeValue = -1;
        public bool IsDimension;

        public bool LockRead;
        public bool Locked;
        public string IsLocked = "<读不到>";
        public string IsValid = "<读不到>";
        public string Bidirectional = "<读不到>";

        public bool ValueRead;
        public double ValueRaw;
        public string Value = "<读不到>";
        public string ValueMm = "N/A";
        public string ValueDeg = "N/A";

        public string RelatedShapes = "<读不到>";
        public int RelatedCount = -1;

        public string Prefix = "<读不到>";
        public string Postfix = "<读不到>";
        public int ToleranceTypeValue = -1;
        public string ToleranceType = "<读不到>";
        public string UpperTolerance = "<读不到>";
        public string LowerTolerance = "<读不到>";

        public string Owner = "<读不到>";
        public int OwnerId = -1;
        public string OwnerName = "<读不到>";
        public string OwnerType = "<读不到>";

        public bool AnchorsOk;
        public string Channel = "";
    }

    internal static class ShapeRelationReader
    {
        private const int SlotPrefixPostfix = 18;   // IZShapeRelation::GetSmartDimPrefixAndPostfix
        private const int SlotTolerance = 19;       // IZShapeRelation::GetSmartDimToleranceTypeAndValues
        private const int SlotOwner = 20;           // IZShapeRelation::GetOwner

        // 长度类关系：GetValue 单位米。角度类：GetValue 单位弧度。
        private static readonly HashSet<int> LengthTypes = new HashSet<int> { 1, 7, 15, 19 };
        private static readonly HashSet<int> AngleTypes = new HashSet<int> { 8, 21, 23 };
        // 智能标注可能落到的尺寸类关系（Dimension / Distance / Width / DistanceEX / Angle / AdvAngle / AngleEX）
        private static readonly HashSet<int> DimTypes = new HashSet<int> { 1, 7, 8, 15, 19, 21, 23 };

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnPrefixPostfix(IntPtr self,
            [MarshalAs(UnmanagedType.BStr)] out string prefix,
            [MarshalAs(UnmanagedType.BStr)] out string postfix);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnTolerance(IntPtr self, out int tolType, out double upper, out double minus);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int FnOwner(IntPtr self, out IntPtr ppOwner);

        public static string ToleranceName(int v)
        {
            switch (v)
            {
                case 0: return "None(无)";
                case 1: return "Limit(极限)";
                case 2: return "PlusMinus(正负)";
                case 3: return "PlusPlus(双双正)";
                case 4: return "MinusMinus(双双负)";
                case 5: return "Symmetric(对称)";
                case 6: return "Invalid(无效)";
                case 7: return "Fit(配合)";
                default: return "<未知值>";
            }
        }

        /// <summary>读一条关系。rel 为 null 时返回 null（调用方自己记第三态）。</summary>
        public static RelationInfo Read(object raw, Action<string> log, string indent)
        {
            var r = raw as IZShapeRelation;
            if (r == null) return null;
            var info = new RelationInfo();
            var reasons = new List<string>();

            // ---- 锚点：RCW 直调（槽7 / 槽13）。这两项读不出就不碰扩展槽位。----
            try { info.Id = r.Id; } catch (Exception ex) { reasons.Add("Id " + ProbeCommon.Hr(ex)); }
            try
            {
                var t = r.GetRelationType();
                info.TypeValue = (int)t;
                info.Type = t.ToString();
            }
            catch (Exception ex) { reasons.Add("GetRelationType " + ProbeCommon.Hr(ex)); }

            info.AnchorsOk = info.Id >= 0 && info.TypeValue >= 0;
            info.IsDimension = DimTypes.Contains(info.TypeValue);
            if (!info.AnchorsOk)
            {
                info.Channel = "锚点读失败（" + string.Join(" / ", reasons.ToArray()) + "）⇒ 跳过扩展槽位 18/19/20，防多表面串位";
                log(indent + "REL 锚点失败：" + info.Channel);
                ReadBasic(r, info, log, indent);
                return info;
            }

            ReadBasic(r, info, log, indent);

            // ---- 扩展三件：类型库派发优先，非 S_OK 降级槽位直调 ----
            IntPtr pRel = IntPtr.Zero;
            IntPtr pUnk = ProbeCommon.ToUnknown(r);
            try
            {
                if (pUnk != IntPtr.Zero)
                {
                    Guid iid = typeof(IZShapeRelation).GUID;
                    int hrQ = ProbeCommon.QI(pUnk, iid, out pRel);
                    if (hrQ != 0) reasons.Add("QI(IZShapeRelation) " + ProbeCommon.Hr(hrQ));
                }
            }
            finally { if (pUnk != IntPtr.Zero) Marshal.Release(pUnk); }

            ReadPrefixPostfix(r, pRel, info, reasons, log, indent);
            ReadTolerance(r, pRel, info, reasons, log, indent);
            ReadOwner(r, pRel, info, reasons, log, indent);

            if (pRel != IntPtr.Zero) Marshal.Release(pRel);
            info.Channel = string.Join(" | ", reasons.ToArray());
            if (info.Channel.Length == 0) info.Channel = "RCW(7/8/9/12/13/14/16) + 类型库派发(12/13/14) 全 S_OK";
            return info;
        }

        private static void ReadBasic(IZShapeRelation r, RelationInfo info, Action<string> log, string indent)
        {
            try { info.Locked = r.IsLocked(); info.LockRead = true; info.IsLocked = info.Locked ? "true" : "false"; }   // 槽8
            catch (Exception ex) { info.IsLocked = "<FAIL:" + ProbeCommon.Hr(ex) + ">"; }

            try { info.IsValid = r.IsValid() ? "true" : "false"; }   // 槽9
            catch (Exception ex) { info.IsValid = "<FAIL:" + ProbeCommon.Hr(ex) + ">"; }

            try { info.Bidirectional = r.IsBidirectionalRelation() ? "true" : "false"; }   // 槽12
            catch (Exception ex) { info.Bidirectional = "<FAIL:" + ProbeCommon.Hr(ex) + ">"; }

            try   // 槽16：量纲随类型
            {
                info.ValueRaw = r.GetValue();
                info.ValueRead = true;
                info.Value = ProbeCommon.Meters(info.ValueRaw);
                if (LengthTypes.Contains(info.TypeValue)) info.ValueMm = ProbeCommon.Mm(info.ValueRaw);
                else info.ValueMm = "N/A(非长度类)";
                if (AngleTypes.Contains(info.TypeValue))
                    info.ValueDeg = (info.ValueRaw * 180.0 / Math.PI).ToString("F6", CultureInfo.InvariantCulture);
                else info.ValueDeg = "N/A(非角度类)";
            }
            catch (Exception ex) { info.Value = "<FAIL:" + ProbeCommon.Hr(ex) + ">"; }

            try   // 槽14：相关几何
            {
                object shapes = r.GetRelatedShapes();
                var flat = new List<object>();
                ProbeCommon.Flatten(shapes, flat, 0);
                info.RelatedCount = flat.Count;
                var names = new List<string>();
                foreach (object o in flat)
                {
                    var el = o as IZElement;
                    if (el != null)
                    {
                        string nm = ProbeCommon.Safe(() => el.Name, "?");
                        int id = -1; try { id = el.Id; } catch { }
                        names.Add(nm + "(Id=" + id + ")");
                    }
                    else names.Add(ProbeCommon.Describe(o));
                }
                info.RelatedShapes = flat.Count == 0 ? "<空>" : string.Join(" ; ", names.ToArray());
            }
            catch (Exception ex) { info.RelatedShapes = "<FAIL:" + ProbeCommon.Hr(ex) + ">"; }
        }

        // ---------------------------------------------------------------- 槽18 / memid12

        private static void ReadPrefixPostfix(IZShapeRelation r, IntPtr pRel, RelationInfo info,
            List<string> reasons, Action<string> log, string indent)
        {
            IntPtr pPrefix = IntPtr.Zero, pPostfix = IntPtr.Zero;
            IntPtr hPrefix = Marshal.AllocHGlobal(IntPtr.Size);
            IntPtr hPostfix = Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                Marshal.WriteIntPtr(hPrefix, IntPtr.Zero);
                Marshal.WriteIntPtr(hPostfix, IntPtr.Zero);
                var args = new List<TlbOutArg> { TlbOutArg.Bstr(hPrefix), TlbOutArg.Bstr(hPostfix) };
                int hr = TlbDispatch.InvokeMethod("IZShapeRelation", "GetSmartDimPrefixAndPostfix", r, args, IntPtr.Zero, log);
                if (hr == 0)
                {
                    pPrefix = Marshal.ReadIntPtr(hPrefix);
                    pPostfix = Marshal.ReadIntPtr(hPostfix);
                    info.Prefix = pPrefix == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(pPrefix);
                    info.Postfix = pPostfix == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(pPostfix);
                    reasons.Add("前后缀=类型库派发 S_OK");
                    return;
                }
                reasons.Add("前后缀类型库派发 " + ProbeCommon.Hr(hr) + " ⇒ 降级槽18");
            }
            catch (Exception ex) { reasons.Add("前后缀类型库派发异常 " + ProbeCommon.Hr(ex) + " ⇒ 降级槽18"); }
            finally
            {
                if (pPrefix != IntPtr.Zero) Marshal.FreeBSTR(pPrefix);
                if (pPostfix != IntPtr.Zero) Marshal.FreeBSTR(pPostfix);
                Marshal.FreeHGlobal(hPrefix);
                Marshal.FreeHGlobal(hPostfix);
            }

            if (pRel == IntPtr.Zero) { info.Prefix = info.Postfix = "<无接口指针>"; return; }
            try
            {
                string pre, post;
                int hr = ProbeCommon.Fn<FnPrefixPostfix>(pRel, SlotPrefixPostfix)(pRel, out pre, out post);
                if (hr == 0) { info.Prefix = pre ?? ""; info.Postfix = post ?? ""; reasons.Add("前后缀槽18 S_OK"); }
                else { info.Prefix = info.Postfix = "<FAIL:" + ProbeCommon.Hr(hr) + ">"; reasons.Add("前后缀槽18 " + ProbeCommon.Hr(hr)); }
            }
            catch (Exception ex) { info.Prefix = info.Postfix = "<FAIL:" + ProbeCommon.Hr(ex) + ">"; reasons.Add("前后缀槽18 异常"); }
        }

        // ---------------------------------------------------------------- 槽19 / memid13

        private static void ReadTolerance(IZShapeRelation r, IntPtr pRel, RelationInfo info,
            List<string> reasons, Action<string> log, string indent)
        {
            IntPtr hType = Marshal.AllocHGlobal(4);
            IntPtr hUp = Marshal.AllocHGlobal(8);
            IntPtr hLo = Marshal.AllocHGlobal(8);
            try
            {
                Marshal.WriteInt32(hType, 0);
                Marshal.WriteInt64(hUp, 0);
                Marshal.WriteInt64(hLo, 0);
                var args = new List<TlbOutArg> { TlbOutArg.Int(hType), TlbOutArg.Double(hUp), TlbOutArg.Double(hLo) };
                int hr = TlbDispatch.InvokeMethod("IZShapeRelation", "GetSmartDimToleranceTypeAndValues", r, args, IntPtr.Zero, log);
                if (hr == 0)
                {
                    info.ToleranceTypeValue = Marshal.ReadInt32(hType);
                    info.ToleranceType = ToleranceName(info.ToleranceTypeValue);
                    info.UpperTolerance = ProbeCommon.Meters(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(hUp)));
                    info.LowerTolerance = ProbeCommon.Meters(BitConverter.Int64BitsToDouble(Marshal.ReadInt64(hLo)));
                    reasons.Add("公差=类型库派发 S_OK");
                    return;
                }
                reasons.Add("公差类型库派发 " + ProbeCommon.Hr(hr) + " ⇒ 降级槽19");
            }
            catch (Exception ex) { reasons.Add("公差类型库派发异常 " + ProbeCommon.Hr(ex) + " ⇒ 降级槽19"); }
            finally { Marshal.FreeHGlobal(hType); Marshal.FreeHGlobal(hUp); Marshal.FreeHGlobal(hLo); }

            if (pRel == IntPtr.Zero) { info.ToleranceType = "<无接口指针>"; return; }
            try
            {
                int tt; double up, lo;
                int hr = ProbeCommon.Fn<FnTolerance>(pRel, SlotTolerance)(pRel, out tt, out up, out lo);
                if (hr == 0)
                {
                    info.ToleranceTypeValue = tt;
                    info.ToleranceType = ToleranceName(tt);
                    info.UpperTolerance = ProbeCommon.Meters(up);
                    info.LowerTolerance = ProbeCommon.Meters(lo);
                    reasons.Add("公差槽19 S_OK");
                }
                else { info.ToleranceType = "<FAIL:" + ProbeCommon.Hr(hr) + ">"; reasons.Add("公差槽19 " + ProbeCommon.Hr(hr)); }
            }
            catch (Exception ex) { info.ToleranceType = "<FAIL:" + ProbeCommon.Hr(ex) + ">"; reasons.Add("公差槽19 异常"); }
        }

        // ---------------------------------------------------------------- 槽20 / memid14

        private static void ReadOwner(IZShapeRelation r, IntPtr pRel, RelationInfo info,
            List<string> reasons, Action<string> log, string indent)
        {
            IntPtr pResult = TlbDispatch.AllocResult();
            try
            {
                int hr = TlbDispatch.InvokeMethod("IZShapeRelation", "GetOwner", r, null, pResult, log);
                if (hr == 0)
                {
                    object owner = TlbDispatch.ReadRetValObject(pResult);
                    if (owner == null) { info.Owner = "<retval 非对象>"; reasons.Add("归属=类型库派发 S_OK 但 retval 非对象"); }
                    else { FillOwner(owner, info); reasons.Add("归属=类型库派发 S_OK"); }
                    return;
                }
                reasons.Add("归属类型库派发 " + ProbeCommon.Hr(hr) + " ⇒ 降级槽20");
            }
            catch (Exception ex) { reasons.Add("归属类型库派发异常 " + ProbeCommon.Hr(ex) + " ⇒ 降级槽20"); }
            finally { TlbDispatch.FreeResult(pResult); }

            if (pRel == IntPtr.Zero) { info.Owner = "<无接口指针>"; return; }
            IntPtr pOwner = IntPtr.Zero;
            try
            {
                int hr = ProbeCommon.Fn<FnOwner>(pRel, SlotOwner)(pRel, out pOwner);
                if (hr != 0) { info.Owner = "<FAIL:" + ProbeCommon.Hr(hr) + ">"; reasons.Add("归属槽20 " + ProbeCommon.Hr(hr)); return; }
                if (pOwner == IntPtr.Zero) { info.Owner = "<空指针>"; reasons.Add("归属槽20 S_OK 但指针空"); return; }
                object o = Marshal.GetObjectForIUnknown(pOwner);
                FillOwner(o, info);
                reasons.Add("归属槽20 S_OK");
            }
            catch (Exception ex) { info.Owner = "<FAIL:" + ProbeCommon.Hr(ex) + ">"; reasons.Add("归属槽20 异常"); }
            finally { if (pOwner != IntPtr.Zero) Marshal.Release(pOwner); }
        }

        private static void FillOwner(object owner, RelationInfo info)
        {
            info.Owner = ProbeCommon.Describe(owner);
            var el = owner as IZElement;
            if (el == null) return;
            try { info.OwnerId = el.Id; } catch { }
            info.OwnerName = ProbeCommon.Safe(() => el.Name, "<读不到>");
            info.OwnerType = ProbeCommon.Safe(() => el.Type.ToString(), "<读不到>");
        }

        // ---------------------------------------------------------------- CSV 行

        public const string CsvHeader =
            "RelationId,RelationType,TypeValue,IsDimension,IsLocked,IsValid,Bidirectional," +
            "ValueRaw,ValueMm,ValueDeg,RelatedCount,RelatedShapes," +
            "Prefix,Postfix,ToleranceType,ToleranceTypeValue,UpperTolerance,LowerTolerance," +
            "OwnerId,OwnerName,OwnerType,Owner,Channel";

        public static string[] ToRow(RelationInfo i)
        {
            return new string[]
            {
                ProbeCommon.Int(i.Id), i.Type, ProbeCommon.Int(i.TypeValue), i.IsDimension ? "true" : "false",
                i.IsLocked, i.IsValid, i.Bidirectional,
                i.Value, i.ValueMm, i.ValueDeg, ProbeCommon.Int(i.RelatedCount), i.RelatedShapes,
                i.Prefix, i.Postfix, i.ToleranceType, ProbeCommon.Int(i.ToleranceTypeValue),
                i.UpperTolerance, i.LowerTolerance,
                ProbeCommon.Int(i.OwnerId), i.OwnerName, i.OwnerType, i.Owner, i.Channel
            };
        }

        public static string LogLine(RelationInfo i)
        {
            return string.Format(CultureInfo.InvariantCulture,
                "REL Id={0} Type={1}({2}) 尺寸类={3} IsLocked={4} IsValid={5} Value={6} mm={7} deg={8} 前缀=\"{9}\" 后缀=\"{10}\" 公差={11} 上={12} 下={13} 归属={14}({15},Id={16}) 相关几何={17} 通道={18}",
                i.Id, i.Type, i.TypeValue, i.IsDimension, i.IsLocked, i.IsValid,
                i.Value, i.ValueMm, i.ValueDeg, i.Prefix, i.Postfix,
                i.ToleranceType, i.UpperTolerance, i.LowerTolerance,
                i.OwnerName, i.OwnerType, i.OwnerId, i.RelatedShapes, i.Channel);
        }
    }
}