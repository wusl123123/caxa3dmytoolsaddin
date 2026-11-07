using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace caxa3dTreeBom
{
    /// <summary>BOM 表格中的一行（对应装配结构树中的一个唯一节点）。</summary>
    [ComVisible(false)]
    internal class BomRow
    {
        /// <summary>层级编码：1 / 1.1 / 1.1.1（仅编码）。</summary>
        public string Code = "";

        /// <summary>层级列显示文本：树形连接符 + 编码（如 "│   ├─ 1.1.3"）。</summary>
        public string TreeText = "";

        /// <summary>层级深度（根 = 1）。</summary>
        public int Level = 1;

        /// <summary>该节点对应的外部链接文件完整路径（虚拟件为空）。</summary>
        public string FilePath = "";

        /// <summary>模型文件名（含扩展名，虚拟件为空）。</summary>
        public string FileName = "";

        /// <summary>CAXA 元素名称（零件/装配体在设计树里的名字）。</summary>
        public string ElementName = "";

        /// <summary>图号（BOM 设置或自定义属性），同时作为同名分组的兜底键。</summary>
        public string PartNumber = "";

        /// <summary>是否为装配体。</summary>
        public bool IsAssembly;

        /// <summary>分层数量：该节点在其直接父级装配中的实际实例数量。</summary>
        public int Quantity = 1;

        /// <summary>总数量：同名件在整棵装配结构树中的累计数量。</summary>
        public int TotalQuantity;

        /// <summary>同名件中首次出现的行使 TotalQuantity 生效，其余行显示 "---"。</summary>
        public bool ShowTotal = true;

        /// <summary>是否存在同名工程图（.exb），存在则填写 "★"。</summary>
        public bool HasDrawing;

        /// <summary>抓取到的缩略图 PNG 临时文件路径（未抓取时为空）。</summary>
        public string ThumbnailPath = "";

        /// <summary>按列表头名称抓取到的属性值（键忽略大小写）。</summary>
        public Dictionary<string, string> Properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>与 Properties 平行的「取值来源」表（键＝表头显示名，值＝来源串）。
        /// 词表四种形态（与 ColumnSourceUi 的解析口径一致，别自创）：
        /// "属性"＝自定义属性共享表命中；"BOM:命中标签"＝BOM 导出兜底命中；"专用:专用词"＝原有专用兜底；
        /// 空串＝三条通道均未取到（第三态，与「取到但是空白」分开记）。</summary>
        public Dictionary<string, string> Sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>装配体在该节点的数量累计系数（父级累计 × 本层数量），用于汇总总数量。</summary>
        internal int Multiplicity = 1;

        /// <summary>对应的 CAXA 元素（IZElement），供“读取属性/抓取缩略图”重新取值；可能为空。</summary>
        internal object Element;
    }
}
