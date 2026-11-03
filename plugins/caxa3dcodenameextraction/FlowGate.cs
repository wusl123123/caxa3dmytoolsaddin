// 跨窗口流程互斥闸门（铁律 6：进程级互斥）。
// 本插件有两个工具窗口（MainForm「带图改名及图号分离」与 DiffRenameForm「带图独立」），
// 长流程都会争用同一个 CRX 请求文件（%TEMP%\caxa3dcodenameextraction_request.txt），必须串行。
// BusyBy 记录归属窗口：关窗只打停自己启动的流程，不误伤另一个窗口的流程。
using System;

namespace caxa3dcodenameextraction
{
    internal static class FlowGate
    {
        public static volatile bool Busy;
        public static volatile bool StopRequested;
        private static string _busyBy = "";
        private static readonly object _sync = new object();

        /// <summary>申请独占流程；已被占用返回 false（调用方负责提示用户）。</summary>
        public static bool TryAcquire(string owner, string flowName)
        {
            lock (_sync)
            {
                if (Busy) return false;
                Busy = true;
                StopRequested = false;      // 清掉上一次「停止」留下的标志，否则 WaitUntil 立刻中断
                _busyBy = owner ?? "";
            }
            Diagnostics.Trace("FlowGate: " + flowName + " 已取得流程独占（owner=" + _busyBy + "）");
            return true;
        }

        /// <summary>当前是否有 owner 窗口启动的流程在跑（关窗、停止按钮只对自己的流程生效）。</summary>
        public static bool IsBusyBy(string owner)
        {
            return Busy && string.Equals(_busyBy, owner ?? "", StringComparison.Ordinal);
        }

        public static void Release()
        {
            lock (_sync)
            {
                Busy = false;
                _busyBy = "";
            }
            Diagnostics.Trace("FlowGate: 流程独占已释放");
        }
    }
}
