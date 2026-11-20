using System;

namespace caxa3dToolboxBackup
{
    /// <summary>
    /// 用户点「停止」后由长流程抛出、向上冒泡终止当前备份/还原（与 Add-in 版行为一致）。
    /// 原定义在 CaxaAutomation.cs，独立 EXE 不需要任何 ICAPI 代码，故拆到本文件。
    /// </summary>
    public class OperationStoppedException : Exception
    {
        public OperationStoppedException() : base("操作已被用户停止") { }
    }
}
