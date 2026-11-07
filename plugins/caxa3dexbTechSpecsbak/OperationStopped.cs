// 用户点「停止」时用来打停整条流程的中断信号。
//
// 独立工程里这个类住在 CaxaAutomation.cs（进程内自动化模板）内。本插件的业务代码只做文件与
// 注册表操作，不碰 ICAPI COM，也没有手工槽位调用，所以那份模板整体不随子插件进来（集合体里
// 的子插件一律不带 CaxaAutomation.cs），这里只保留真正被 MainForm 的检查/备份/还原流程使用的异常类型。
using System;
using System.Runtime.InteropServices;

namespace caxa3dexbTechSpecsbak
{
    /// <summary>用户点「停止」时抛出的中断信号（必须 [ComVisible(false)]）。</summary>
    [ComVisible(false)]
    public class OperationStoppedException : Exception
    {
        public OperationStoppedException() : base("操作已被用户停止") { }
    }
}
