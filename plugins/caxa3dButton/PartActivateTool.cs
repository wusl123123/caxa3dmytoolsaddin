// 激活 / 取消激活零件（移植自 caxa3dtracker，v4）。
// ★ 写＝命令派发 WM_COMMAND 53111（IsEngineAfxLg STRINGTABLE 53111「激活\取消激活」＝Ctrl+Shift+A）。
// ★ TLB 全域穷举零「零件激活状态」读 API ⇒ 状态显示不做，一切以 CAXA 视口/设计树为准。
// 判据纪律：PostMessage 仅入队证据不折算成功。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dButton
{
    [System.Runtime.InteropServices.ComVisible(false)]
    internal static class PartActivateTool
    {
        private const int Z_ELEMENT_PART = 1;             // eZElementType.Z_ELEMENT_PART
        private const int Z_ELEMENT_ASSEMBLY = 2;         // eZElementType.Z_ELEMENT_ASSEMBLY
        private const int Z_ELEMENT_SHEETMETAL_PART = 20; // eZElementType.Z_ELEMENT_SHEETMETAL_PART

        private const int WM_COMMAND = 0x0111;
        private const int CmdActivateToggle = 53111;      // 0xCF77「激活\取消激活」＝Ctrl+Shift+A
        private const int SW_RESTORE = 9;

        // eZElementState 全集（2027 enums.csv）——探针逐个读，留实机定标证据
        private static readonly KeyValuePair<string, int>[] StateNames = new KeyValuePair<string, int>[]
        {
            new KeyValuePair<string, int>("SUPPRESSED", 0),
            new KeyValuePair<string, int>("INVALID", 1),
            new KeyValuePair<string, int>("HIDDEN", 2),
            new KeyValuePair<string, int>("NOFACE", 3),
            new KeyValuePair<string, int>("VALID", 4),
            new KeyValuePair<string, int>("LINKED", 0x10000000),
        };

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int maxCount);

        private static string _lastEvidenceKey;   // 上次落证据的零件 id 集（变化才再落，防 1 秒刷屏）
        private static bool _forceEvidence;       // 点过切换命令后强制下一轮再落一次

        /// <summary>强制下一轮门控重新落全状态探针证据。</summary>
        public static void RequestEvidenceRefresh() { _forceEvidence = true; }

        /// <summary>读活选择集中「零件级元素」（零件1/装配2/钣金件20）。读失败返回 false（第三态）。</summary>
        public static bool TryReadSelectedParts(IZBaseApp app, out List<IZElement> parts, out string reason)
        {
            parts = new List<IZElement>();
            reason = null;

            string err;
            IZSceneDoc scene = ProbeCommon.GetScene(app, out err);
            if (scene == null) { reason = err; return false; }

            object raw;
            try { raw = scene.SelectionMgr.GetSelectedElements(); }   // SelectionMgr=槽27 / GetSelectedElements=槽10
            catch (Exception ex) { reason = "选择集读不到（第三态）" + ProbeCommon.Hr(ex); return false; }

            var flat = new List<object>();
            ProbeCommon.Flatten(raw, flat, 0);
            foreach (object o in flat)
            {
                var el = o as IZElement;
                if (el == null) continue;
                int type;
                try { type = (int)el.Type; } catch { continue; }
                if (type == Z_ELEMENT_PART || type == Z_ELEMENT_ASSEMBLY || type == Z_ELEMENT_SHEETMETAL_PART)
                    parts.Add(el);
            }
            return true;
        }

        /// <summary>单件某状态读值（GetStateStatus 槽17）。读不到返回 null（第三态）。</summary>
        private static bool? ReadState(IZElement el, int stateType)
        {
            try { return el.GetStateStatus((eZElementState)stateType); }
            catch { return null; }
        }

        /// <summary>全状态探针：逐个 eZElementState + 子节点数落一行证据（激活前后对照定标用）。</summary>
        private static void LogStateEvidence(IZElement el, Action<string> log)
        {
            if (log == null) return;
            string name = ProbeCommon.Safe(() => el.Name, "<读不到名>");
            int id; try { id = el.Id; } catch { id = -1; }
            int children; try { children = el.GetChildrenCount(); } catch { children = -1; }

            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in StateNames)
            {
                bool? v = ReadState(el, kv.Value);
                sb.Append(kv.Key).Append('=').Append(v == null ? "?" : (v.Value ? "1" : "0")).Append(' ');
            }
            sb.Append("children=").Append(children);
            log("activate-evidence: " + name + "(id=" + id + ") " + sb.ToString());
        }

        /// <summary>
        /// 门控（MainForm 定时器/按钮可用时调用）。v4：不做激活状态推断，只报「有无选中零件」。
        /// 返回 state：-1=读不到 / 0=无选中零件 / 2=有零件选中（可点切换）。
        /// 选择集（零件 id 集）变化或被强制时，落一次全状态探针证据（供留档）。
        /// </summary>
        public static int GateState(IZBaseApp app, out string summary, Action<string> log)
        {
            summary = null;

            List<IZElement> parts; string reason;
            if (!TryReadSelectedParts(app, out parts, out reason)) { summary = reason; return -1; }
            if (parts.Count == 0) { _lastEvidenceKey = null; summary = "未选中零件"; return 0; }

            var key = new StringBuilder();
            foreach (IZElement p in parts)
            {
                int id; try { id = p.Id; } catch { id = -1; }
                key.Append(id).Append(',');
            }
            string keyStr = key.ToString();
            if (_forceEvidence || keyStr != _lastEvidenceKey)
            {
                _lastEvidenceKey = keyStr;
                _forceEvidence = false;
                foreach (IZElement p in parts) LogStateEvidence(p, log);
            }

            summary = string.Format(CultureInfo.InvariantCulture, "选中零件 {0} 个", parts.Count);
            return 2;
        }

        /// <summary>
        /// 执行「激活\取消激活」切换：前置主框架 → 投递 WM_COMMAND 53111（Ctrl+Shift+A 等价）。
        /// 返回中文判词（仅入队证据；TLB 无激活状态读 API，无法回读验证，是否生效以 CAXA 为准）。
        /// </summary>
        public static string Toggle(IZBaseApp app, Action<string> log)
        {
            List<IZElement> parts; string reason;
            if (!TryReadSelectedParts(app, out parts, out reason))
                return "第三态：" + reason + "。请重新选中零件后再试";
            if (parts.Count == 0)
                return "当前没有选中零件：请先在 CAXA 视口/设计树里选中零件，再点本按钮";

            IntPtr frame = FindCaxaMainFrame();
            if (frame == IntPtr.Zero)
                return "未找到 CAXA 主框架窗口（class 以 Afx: 开头、本进程、标题含 CAXA）：命令无法投递";
            if (IsIconic(frame)) { try { ShowWindow(frame, SW_RESTORE); } catch { } }
            bool fg = false; try { fg = SetForegroundWindow(frame); } catch { }

            bool posted = PostMessageW(frame, WM_COMMAND, (IntPtr)CmdActivateToggle, IntPtr.Zero);
            if (log != null)
                log("activate: 主框架=0x" + frame.ToInt64().ToString("X") + " 前置=" + fg +
                    " PostMessage WM_COMMAND 53111 (0xCF77) posted=" + posted +
                    "（仅入队证据；TLB 无激活状态读 API，无法回读验证）");
            if (!posted) return "WM_COMMAND 53111 投递失败（PostMessage 返回 false）";
            _forceEvidence = true;   // 激活状态可能变了，下一轮门控重新落探针
            return string.Format(CultureInfo.InvariantCulture,
                "已投递「激活/取消激活」命令 53111（Ctrl+Shift+A 等价，作用对象=当前选中 {0} 个零件）——是否生效以 CAXA 视口/设计树为准",
                parts.Count);
        }

        // ---------------------------------------------------------------- 主框架查找（坑 121 口径，取自 caxa3dtracker/CornerReliefTool）

        /// <summary>本进程 + 可见 + 顶层 + class 以 Afx: 开头 + 标题含 CAXA/IronCAD。</summary>
        private static IntPtr FindCaxaMainFrame()
        {
            IntPtr found = IntPtr.Zero;
            int curPid = Process.GetCurrentProcess().Id;
            EnumWindows(delegate (IntPtr h, IntPtr _)
            {
                if (found != IntPtr.Zero) return true;
                if (!IsWindowVisible(h)) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (pid != curPid) return true;
                if (!WindowClass(h).StartsWith("Afx:", StringComparison.OrdinalIgnoreCase)) return true;
                string t = WindowTitle(h);
                if (t.IndexOf("CAXA", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    t.IndexOf("IronCAD", StringComparison.OrdinalIgnoreCase) >= 0)
                    found = h;
                return true;
            }, IntPtr.Zero);
            return found;
        }

        private static string WindowClass(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassNameW(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string WindowTitle(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
