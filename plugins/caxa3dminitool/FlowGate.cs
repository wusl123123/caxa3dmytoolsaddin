// 长流程互斥闸门（铁律 6：进程级互斥 + 必须有「停止」入口）。
// 「零件置换」要在 CAXA 主线程上跑 UI 代点长等待链，争用的是 CAXA 活选择集与原生面板，
// 同一时刻只允许一条流程；内核切换的定时器分片另由 _kernelRunning 互斥。
// BusyBy 记录归属：关窗只打停自己启动的流程。
using System;
using System.Threading;

namespace caxa3dminitool
{
    internal static class FlowGate
    {
        public static volatile bool Busy;
        public static volatile bool StopRequested;
        private static string _busyBy = "";
        private static readonly object _sync = new object();

        // ---- 主线程心跳 ----
        // 主线程每完成一次轮询就 Touch 一下；后台扫描线程发现「流程还挂着、但心跳停了 N 秒」
        // 就把当时的顶层窗口写成一行取证。
        // 实机 11-03 17:32：主线程卡在一次设计树 COM 走查里再没出来，trace 到那行为止
        // **一声不响** —— 分不清是「一张模态框挂着没人点」还是「根本没有框、COM 调用自己堵死」，
        // 而这正是唯一需要证据的时刻（规则坑 157：静默的观测通道等于没有观测）。
        private static int _beat;

        /// <summary>主线程报到：记当前 TickCount（跨线程只有这一个 int 写，无需加锁）。</summary>
        public static void Touch()
        {
            Volatile.Write(ref _beat, Environment.TickCount);
        }

        /// <summary>距主线程上一次报到的毫秒数；未开始过流程返回 0（相减取负，跨过 25 天回绕也算对）。</summary>
        public static int IdleMs()
        {
            int b = Volatile.Read(ref _beat);
            if (b == 0) return 0;
            return Environment.TickCount - b;
        }

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
            Touch();                        // 心跳从流程开始这一刻算起
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
