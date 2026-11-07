// CAXA 3D 进程内自动化助手（模板）
// 用法：把 caxa3dBatchMaster 替换为项目名；业务代码全部通过本类访问 CAXA 对象。
//
// 三条口径（不要凭记忆改）：
//   1. 优先用 interop RCW 的强类型属性/方法 —— RCW 已经把 vtable 封装好了（app.ActiveDoc / doc.Save()）。
//      只有 interop 里「没声明」的成员，才用 OpenSlot<T> 手工取槽位。
//   2. 手工槽位公式：槽 = 基接口成员数(CAXA 恒为 0) + 声明序号 + 7（IUnknown 3 + IDispatch 4）。
//      禁止按 DispId 推导（属性 get/set 共用一个 DispId 却在 vtable 各占一槽）。
//   3. 会话对象归 CAXA 所有：Dispose 只清引用，禁止 ReleaseComObject（会把 CAXA 自己的对象释放掉）。
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    /// <summary>进程内会话：只持有 Addin site 给出的 IZBaseApp RCW。</summary>
    internal sealed class CaxaSession : IDisposable
    {
        public IZBaseApp AppObject;

        // 供 caxa3dmytoolsaddin 移植件（SheetMetalOps 等）使用：IZBaseApp 接口指针（vtable 直调用）。
        // 参考工程的 CaxaSession 同时持有 AppObject(object) 与 BaseApp(IntPtr)；本项目 AppObject 已固化为 IZBaseApp，
        // 故额外补 BaseApp 字段即可，ConnectFromSite 会一并赋值。
        public IntPtr BaseApp;

        public void Dispose()
        {
            // 不调用 Marshal.ReleaseComObject：该接口指针由 CAXA 持有，
            // 释放会把宿主自己的对象计数打乱（表现为后续调用随机 RPC_E_* 或进程崩）。
            AppObject = null;
            if (BaseApp != IntPtr.Zero) { try { Marshal.Release(BaseApp); } catch { } BaseApp = IntPtr.Zero; }
        }
    }

    /// <summary>已验证可用的 IID（完整值从 _probe-tools\out\tlb\&lt;年份&gt;\members.csv 取，不要手抄省略段）。</summary>
    internal static class CaxaIid
    {
        public static readonly Guid IZBaseApp  = new Guid("A5B9AF7D-228B-4ED1-BA42-D51318F7EA20");
        public static readonly Guid IZSceneDoc = new Guid("3845C6DA-89D6-43F7-8B10-4BE36E0E758B");
        public static readonly Guid IZDoc      = new Guid("408F2326-F171-443B-A9EF-4AB250A2B1A8");
        public static readonly Guid IZElement  = new Guid("69EF5583-A26D-4913-A2F6-DBFF73E1B8A3");
    }

    /// <summary>
    /// 手工槽位句柄：QI 成功后持有原始接口指针 + 对应委托。
    /// 用完必须 Dispose（内部 Marshal.Release），否则每轮泄漏一个引用计数。
    /// </summary>
    internal sealed class ComSlot<TDelegate> : IDisposable where TDelegate : class
    {
        public TDelegate Call;
        public int Hr;
        private IntPtr _ppv;

        internal ComSlot(IntPtr ppv, TDelegate call, int hr)
        {
            _ppv = ppv;
            Call = call;
            Hr = hr;
        }

        public void Dispose()
        {
            if (_ppv != IntPtr.Zero) { Marshal.Release(_ppv); _ppv = IntPtr.Zero; }
            Call = null;
        }
    }

    internal static class CaxaAutomation
    {
        // ================================================================
        // 连接
        // ================================================================

        /// <summary>从 Addin site 建立会话，并做一次廉价读取确认通道畅通。</summary>
        public static CaxaSession ConnectFromSite(IZBaseApp siteApp)
        {
            if (siteApp == null) throw new InvalidOperationException("Addin site has no Application.");

            try { Diagnostics.Log("ConnectFromSite: ApiVersion = " + siteApp.ApiVersion); }
            catch (Exception ex) { Diagnostics.Log("ConnectFromSite: ApiVersion 读取异常 " + ex.Message); }

            return new CaxaSession { AppObject = siteApp };
        }

        /// <summary>会话是否仍然有效（读一个便宜属性，不碰 vtable）。</summary>
        public static bool Ping(CaxaSession session)
        {
            if (session == null || session.AppObject == null) return false;
            try
            {
                string _ = session.AppObject.ApplicationVersion;
                return true;
            }
            catch (Exception ex)
            {
                Diagnostics.Log("Ping 失败: " + ex.Message);
                return false;
            }
        }

        // ================================================================
        // 文档
        // ================================================================

        /// <summary>取活动文档（IZBaseApp.get_ActiveDoc，槽 11）；没有文档返回 null。</summary>
        public static IZDoc GetActiveDoc(CaxaSession session)
        {
            if (session == null || session.AppObject == null) return null;
            try { return session.AppObject.ActiveDoc; }
            catch (Exception ex)
            {
                Diagnostics.Log("GetActiveDoc 失败: " + ex.Message);
                return null;
            }
        }

        /// <summary>取活动文档并转 IZSceneDoc；当前不是 3D 场景文档时返回 null（先判空再动手）。</summary>
        public static IZSceneDoc GetActiveSceneDoc(CaxaSession session, out IZDoc doc)
        {
            doc = GetActiveDoc(session);
            if (doc == null) return null;

            var scene = doc as IZSceneDoc;
            if (scene == null)
            {
                Diagnostics.Log("GetActiveSceneDoc: 活动文档不是 3D 场景文档（type=" + doc.GetType().Name + "）");
                return null;
            }
            return scene;
        }

        /// <summary>
        /// 保存活动文档（IZDoc.Save，槽 9）。
        /// 注意：IZDoc 在 TLB 里没有「文件全路径」成员（无 GetFullName / FullFileName），
        /// 所以校验必须由调用方传入已知路径；不传路径时只调 Save 并记日志，不做成功判定。
        /// </summary>
        public static bool SaveActiveDoc(CaxaSession session, string knownFilePath = null)
        {
            IZDoc doc = GetActiveDoc(session);
            if (doc == null) { Diagnostics.Log("SaveActiveDoc: 没有活动文档"); return false; }

            try
            {
                doc.Save();
            }
            catch (Exception ex)
            {
                Diagnostics.Log("SaveActiveDoc: Save 抛异常 " + ex.Message);
                return false;
            }

            if (string.IsNullOrEmpty(knownFilePath))
            {
                Diagnostics.Log("SaveActiveDoc: 已调用 Save（未传路径，无法校验字节数）");
                return true;
            }

            // 判成功只看权威副作用：文件真实存在且字节数 > 0
            var fi = new System.IO.FileInfo(knownFilePath);
            bool ok = fi.Exists && fi.Length > 0;
            Diagnostics.Log("SaveActiveDoc: " + knownFilePath + " -> " + (ok ? "ok " + fi.Length + "B" : "文件不存在或为空"));
            return ok;
        }

        // ================================================================
        // 手工槽位（仅在 interop 未声明该成员时使用）
        // ================================================================

        /// <summary>
        /// QI 到 iid 后取 vtable 第 slot 槽，返回可直接调用的委托。
        /// 防护：slot 必须 >= 7（IDispatch 之前是 IUnknown 3 + IDispatch 4）；指针为空即失败返回 null。
        /// 注意：槽位错一位就是进程崩溃，新接口第一次调用必须先用锚点 API 验证
        ///       （IZDoc.Save=9 / IZBaseApp.get_ActiveDoc=11 / IZElement.set_Name=10）。
        /// </summary>
        public static ComSlot<TDelegate> OpenSlot<TDelegate>(object comObj, Guid iid, int slot, string tag)
            where TDelegate : class
        {
            if (comObj == null) { Diagnostics.Log("[" + tag + "] OpenSlot: comObj 为空"); return null; }
            if (slot < 7) { Diagnostics.Log("[" + tag + "] OpenSlot: slot " + slot + " 非法（必须 >= 7）"); return null; }

            IntPtr pUnk = IntPtr.Zero;
            IntPtr ppv = IntPtr.Zero;
            try
            {
                pUnk = Marshal.GetIUnknownForObject(comObj);
                Guid g = iid;
                int qiHr = Marshal.QueryInterface(pUnk, ref g, out ppv);
                if (qiHr != 0 || ppv == IntPtr.Zero)
                {
                    Diagnostics.Log("[" + tag + "] OpenSlot: QI 失败 hr=0x" + qiHr.ToString("X8"));
                    return null;
                }

                IntPtr vtbl = Marshal.ReadIntPtr(ppv);
                if (vtbl == IntPtr.Zero) { Marshal.Release(ppv); Diagnostics.Log("[" + tag + "] OpenSlot: vtbl 为空"); return null; }

                IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
                if (fn == IntPtr.Zero) { Marshal.Release(ppv); Diagnostics.Log("[" + tag + "] OpenSlot: slot " + slot + " 为空"); return null; }

                var del = Marshal.GetDelegateForFunctionPointer(fn, typeof(TDelegate)) as TDelegate;
                Diagnostics.Log("[" + tag + "] OpenSlot: slot " + slot + " -> 0x" + fn.ToInt64().ToString("X16"));
                return new ComSlot<TDelegate>(ppv, del, 0);
            }
            catch (Exception ex)
            {
                if (ppv != IntPtr.Zero) { Marshal.Release(ppv); }
                Diagnostics.Log("[" + tag + "] OpenSlot 异常: " + ex.Message);
                return null;
            }
            finally
            {
                if (pUnk != IntPtr.Zero) { Marshal.Release(pUnk); }
            }
        }

        // ================================================================
        // 等待原语（STA 铁律：绝不用 WaitForExit / Thread.Sleep 长等）
        // ================================================================

        /// <summary>进程级停止标志：静态字段，FormClosing 置位，WaitUntil 每轮检查。</summary>
        public static volatile bool StopRequested;

        /// <summary>
        /// 轮询等待条件成立，期间持续泵消息，保证界面不假死、停止键可点。
        /// 命中停止标志抛 OperationStoppedException，向上冒泡终止整条链。
        /// </summary>
        public static void WaitUntil(Func<bool> condition, int timeoutMs, string what)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (StopRequested) throw new OperationStoppedException();
                Application.DoEvents();
                if (condition()) return;
                Thread.Sleep(50);
            }
            throw new TimeoutException(what + " 超时 " + timeoutMs + "ms");
        }

        /// <summary>固定等待一小段时间也要泵消息，禁止裸 Thread.Sleep 长等。</summary>
        public static void PumpDelay(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>用户点「停止」时抛出的中断信号（必须 [ComVisible(false)]）。</summary>
    [ComVisible(false)]
    public class OperationStoppedException : Exception
    {
        public OperationStoppedException() : base("操作已被用户停止") { }
    }
}
