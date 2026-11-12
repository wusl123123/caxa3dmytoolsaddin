// 小工具长流程等待原语（从 caxa3dminitool\MiniToolsForm.cs 抽出的静态层）。
// LayoutBridge / ReplacePanelDriver 注入点：等待必须走主线程消息泵（DoEvents + 轮询），
// 同时 FlowGate.Touch 心跳喂后台看门狗、响应「停止」（置 StopRequested 抛 OperationStoppedException）。
// STA 铁律：禁 WaitForExit / 禁裸 Thread.Sleep 死等。
using System;
using System.Windows.Forms;

namespace caxa3dButton
{
    internal static class MiniFlow
    {
        /// <summary>带 DoEvents 与停止检查的轮询等待；所有长等待都走这一条。</summary>
        internal static bool WaitUntil(string what, int timeoutMs, Func<bool> condition, int pollMs)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(timeoutMs);
            while (DateTime.Now < deadline)
            {
                FlowGate.Touch();       // 主线程还活着：后台看门狗靠这一行判断有没有堵在某个调用里
                try { Application.DoEvents(); } catch { }
                if (FlowGate.StopRequested) throw new OperationStoppedException();
                try { if (condition()) return true; }
                catch (OperationStoppedException) { throw; }
                catch (Exception ex) { Diagnostics.Trace(what + " 条件读取异常：" + ex.Message); }
                System.Threading.Thread.Sleep(pollMs);
            }
            Diagnostics.Log(what + ": 超时 " + timeoutMs + "ms");
            return false;
        }

        /// <summary>长轮询专用：等待期间抑制逐次 COM 调用的细粒度诊断（否则一次等待能刷出几十万字节日志）。</summary>
        internal static bool WaitQuietly(string what, int timeoutMs, Func<bool> condition, int pollMs)
        {
            Diagnostics.PollQuiet = true;
            try { return WaitUntil(what, timeoutMs, condition, pollMs); }
            finally { Diagnostics.PollQuiet = false; }
        }
    }
}
