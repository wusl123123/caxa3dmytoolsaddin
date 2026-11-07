// 单元格三色状态模型（11-06 用户指令第 3 条）：
//   黄＝用户在表格里输入过、且与输入前的值不一致（改动尚未被返填确认）
//   绿＝数据返填成功（从 CAXA 共享表读到并写回单元格 / 写进共享表且同表回读一致）
//   红＝数据返填失败（含「没这个属性」与「回读通道不可用」——两者都是第三态，按失败上色但日志分开记账）
// 状态存在 DataGridViewCell.Tag 里（不是 Style），上色走 CellFormatting：
//   好处是排序/整行搬值时可以连 Tag 一起搬，不会被 Style 的残留色污染，也不与
//   「突出重复值」（走 Style.BackColor＝粉底）抢同一格的颜色来源。
// 原 H 列「属性已更改」的红色"●"标记整条退役，由本模型替代。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace caxa3dBatchMaster
{
    /// <summary>单元格数据状态（None＝未染色）。</summary>
    internal enum CellState
    {
        None = 0,
        Edited = 1,   // 黄：用户输入后与输入前不一致
        Filled = 2,   // 绿：返填成功
        Failed = 3    // 红：返填失败／读不到
    }

    internal static class CellStates
    {
        // 三种底色（Win2000 极简风：只染背景，不动字体与前景，避免与表头加粗打架）
        public static readonly Color ColorEdited = Color.FromArgb(255, 245, 157);   // 浅黄
        public static readonly Color ColorFilled = Color.FromArgb(198, 239, 206);   // 浅绿
        public static readonly Color ColorFailed = Color.FromArgb(255, 199, 206);   // 浅红

        public static string StateName(CellState s)
        {
            switch (s)
            {
                case CellState.Edited: return "黄·用户改过";
                case CellState.Filled: return "绿·返填成功";
                case CellState.Failed: return "红·返填失败";
                default: return "无色";
            }
        }

        public static CellState Of(DataGridViewCell cell)
        {
            return cell?.Tag is CellState s ? s : CellState.None;
        }

        public static void Set(DataGridView grid, int row, int col, CellState state)
        {
            if (grid == null || row < 0 || col < 0) return;
            if (row >= grid.Rows.Count || col >= grid.ColumnCount) return;
            var cell = grid.Rows[row].Cells[col];
            CellState old = Of(cell);
            if (old == state) return;
            if (state == CellState.None)
            {
                if (old == CellState.None) return;
                cell.Tag = null;
            }
            else cell.Tag = state;
            try { grid.InvalidateCell(col, row); } catch { }
        }

        /// <summary>整表清除状态（加载/刷新/屏幕清理时调用：换了一张表＝旧染色没有意义）。</summary>
        public static int ClearAll(DataGridView grid)
        {
            if (grid == null) return 0;
            int n = 0;
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row.IsNewRow) continue;
                foreach (DataGridViewCell cell in row.Cells)
                    if (cell.Tag is CellState) { cell.Tag = null; n++; }
            }
            try { grid.Invalidate(); } catch { }
            return n;
        }

        /// <summary>把一格的状态搬到另一格（排序/整行搬值时随值一起走，否则颜色会和值脱节）。</summary>
        public static void Transfer(DataGridView grid, int fromRow, int col, int toRow)
        {
            if (grid == null || col < 0 || col >= grid.ColumnCount) return;
            if (fromRow < 0 || fromRow >= grid.Rows.Count) return;
            if (toRow < 0 || toRow >= grid.Rows.Count) return;
            Set(grid, toRow, col, Of(grid.Rows[fromRow].Cells[col]));
        }

        /// <summary>取整行状态快照（配合 <see cref="RestoreRow"/> 在逐列搬值后原样还原）。</summary>
        public static List<CellState> SnapshotRow(DataGridView grid, int row)
        {
            var list = new List<CellState>();
            if (grid == null || row < 0 || row >= grid.Rows.Count) return list;
            for (int c = 0; c < grid.ColumnCount; c++) list.Add(Of(grid.Rows[row].Cells[c]));
            return list;
        }

        public static void RestoreRow(DataGridView grid, int row, List<CellState> states)
        {
            if (grid == null || states == null || row < 0 || row >= grid.Rows.Count) return;
            for (int c = 0; c < states.Count && c < grid.ColumnCount; c++)
                Set(grid, row, c, states[c]);
        }

        /// <summary>CellFormatting 里上色：只在有状态时改 e.CellStyle，绝不新建 Font（避免每格一个字体对象）。</summary>
        public static void ApplyToStyle(DataGridViewCellFormattingEventArgs e, CellState state)
        {
            if (e?.CellStyle == null || state == CellState.None) return;
            Color back;
            switch (state)
            {
                case CellState.Edited: back = ColorEdited; break;
                case CellState.Filled: back = ColorFilled; break;
                case CellState.Failed: back = ColorFailed; break;
                default: return;
            }
            e.CellStyle.BackColor = back;
            // 选中时保持可辨认：选中底色用同色系深一档，否则当前格会盖回系统蓝，看不出状态
            e.CellStyle.SelectionBackColor = state == CellState.Filled
                ? Color.FromArgb(166, 214, 178)
                : state == CellState.Edited ? Color.FromArgb(255, 226, 122) : Color.FromArgb(244, 158, 168);
        }
    }
}
