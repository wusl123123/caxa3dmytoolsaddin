// 机械移植自参考工程 caxa3dcodenameextraction\CaxaAutomation.cs（行 32-48）的两个纯数据类。
// 仅改 namespace；逻辑未动。SheetMetalOps.ReadFirstElementCustomProps 依赖它们。
using System.Collections.Generic;

namespace caxa3dBatchMaster
{
    internal sealed class FieldResult
    {
        public string Value;    // null = 未找到
        public string Source;    // 来源描述（用于日志/验证）
    }

    /// <summary>选中元素的自定义属性读取结果。</summary>
    internal sealed class ElementCustomProps
    {
        public string DocName = "";
        public string ElementName = "";
        public string ElementKind = "元素";   // 零件 / 装配体 / 元素
        /// <summary>是否命中了有效的选中零部件（false = 未选中，调用方不应展示/写回）。</summary>
        public bool HasElement = false;
        /// <summary>目标字段名 -> 读取结果（值 + 来源）。</summary>
        public Dictionary<string, FieldResult> Fields = new Dictionary<string, FieldResult>();
    }
}
