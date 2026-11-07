// 表格服务：caxa3dBatchMaster属性表格.xlsx 与 DataGridView 之间的加载/保存，
// 以及固定表头（A-G）与动态自定义属性列（H 列起）的管理。
// 固定列布局（与 caxa3dBatchMaster属性表格.xlsx 严格对齐）：
//   A 是否工程图(★) | B 是否有外链 | C 文件路径 | D 零件名 | E 文件类型
//   F 新零件名 | G 3D零件数量 | H 列起 自定义属性
// 【11-06 用户指令】原 H「属性已更改」列已删除，其后各列自动左移一列；
//   「属性是否已更改」改由单元格三色状态表达（见 CellState.cs），不再占一列。
using System;
using System.IO;

namespace caxa3dBatchMaster
{
    internal static class TableService
    {
        /// <summary>A-G 固定列数（与 caxa3dBatchMaster属性表格.xlsx 一致；原 H「属性已更改」列已删）。</summary>
        public const int FixedColumnCount = 7;

        /// <summary>H 列起自定义属性起始列（0 基索引 = 7）。</summary>
        public const int PropertyColumnStart = 7;

        // ---- 固定列语义索引（0 基；与预设属性表格表头严格对齐）----
        public const int ColHasDrawing      = 0; // A 是否工程图(★)
        public const int ColHasExternalLink = 1; // B 是否有外链（同名.ics）
        public const int ColFilePath        = 2; // C 文件路径
        public const int ColPartName        = 3; // D 零件名（文件名去扩展名，作为统一命名基准）
        public const int ColFileType        = 4; // E 文件类型（ics/exb）
        public const int ColNewPartName     = 5; // F 新零件名（默认=零件名，改名时统一文件名）
        public const int ColPartCount       = 6; // G 3D零件数量

        /// <summary>固定表头文字（第 1 行 A1-G1：含“是否有外链”，不含已删除的“属性已更改”）。</summary>
        public static readonly string[] FixedHeaders =
        {
            "是否工程图", "是否有外链", "文件路径", "零件名", "文件类型", "新零件名", "3D零件数量"
        };

        /// <summary>Excel 列字母（A、B、C…），用于列头显示。</summary>
        public static string ColumnLetter(int index)
        {
            string s = "";
            int n = index + 1;
            while (n > 0)
            {
                int r = (n - 1) % 26;
                s = (char)('A' + r) + s;
                n = (n - 1) / 26;
            }
            return s;
        }

        /// <summary>从 xlsx 加载为 object[,]（0 起始，对应 Excel 第 1 行起）。</summary>
        public static object[,] Load(string path)
        {
            if (!File.Exists(path)) return null;
            var grid = XlsxLite.ReadFirstSheet(path);
            int rows = grid.Count;
            int cols = 0;
            foreach (var r in grid) if (r.Count > cols) cols = r.Count;
            if (rows == 0) return new object[0, 0];

            var data = new object[rows, cols];
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < grid[r].Count; c++)
                    data[r, c] = grid[r][c];
            return data;
        }

        /// <summary>将 object[,]（0 起始）保存为 xlsx。</summary>
        public static void Save(string path, object[,] data)
        {
            int rows = data.GetLength(0);
            int cols = data.GetLength(1);
            var grid = new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
            for (int r = 0; r < rows; r++)
            {
                var row = new System.Collections.Generic.List<string>();
                for (int c = 0; c < cols; c++)
                    row.Add(data[r, c]?.ToString() ?? "");
                grid.Add(row);
            }
            XlsxLite.WriteFirstSheet(path, grid);
        }
    }
}
