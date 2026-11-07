// 批处理核心逻辑：从当前装配体导入、属性批量读取/写入、屏幕清理、
// 列功能（升序排序 / 突出重复值）。直接操作 DataGridView（行 0 = 表头行，行 1 起 = 数据行；
// 列 0-7 对应 A-H 固定列（D=零件名、F=新零件名），从列 8(I) 起为自定义属性列）。
// 底层 COM 全部经 CaxaBatchService / CaxaPropWrite，单元素失败不影响整批。
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using interop.ICApiIronCAD;

namespace caxa3dBatchMaster
{
    internal class BatchProcessor
    {
        private readonly CaxaBatchService _svc;
        private readonly DataGridView _grid;
        private readonly Action<string> _log;
        private readonly Func<bool> _stop;
        private readonly CaxaDocGuard _guard;

        /// <summary>「旧数据」基准：行文件全路径 → (属性列号 → CAXA 里读到的值)。
        /// 由「属性批量读取」在返填成功时写入（只记**真读到**的格子：读到空值也记，空串是合法值；
        /// 「没这个属性」＝不记基准，写侧按「非空即算变化」把它补进共享表）。
        /// 「属性批量写入」只写与这份基准不一致的格子（用户 11-07 指令：只写变化量）。
        /// 按文件全路径而不是行号做键：表格排序后行号会变，路径跟着行走。
        /// 导入 / 清表 / 换表一律清空＝没有旧数据基准。</summary>
        private readonly Dictionary<string, Dictionary<int, string>> _oldValues
            = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);

        public BatchProcessor(CaxaBatchService svc, DataGridView grid, Action<string> log, Func<bool> stopCheck = null)
        {
            _svc = svc;
            _grid = grid;
            _log = log;
            _stop = stopCheck;
            _guard = new CaxaDocGuard(svc == null ? null : svc.App, log, stopCheck);
        }

        /// <summary>长流程每轮泵消息并检查停止标志（铁律 6：停止键可点）。返回 true 表示应中断。</summary>
        private bool ShouldStop()
        {
            try { System.Windows.Forms.Application.DoEvents(); } catch { }
            return _stop != null && _stop();
        }

        private int RowCount => _grid.AllowUserToAddRows ? _grid.Rows.Count - 1 : _grid.Rows.Count;
        private string Cell(int r, int c) => _grid.Rows[r].Cells[c].Value?.ToString() ?? "";
        private void SetCell(int r, int c, string v) => _grid.Rows[r].Cells[c].Value = v;

        /// <summary>程序化返填：写值并染色（用户指令 3——返填成功＝绿、失败＝红）。
        /// 走 SetValueFilled/SetValueFailed 而不是裸 SetCell，是为了让「谁改了格子」在代码里可枚举：
        /// 只有用户手工编辑（CellEndEdit）才允许标黄，程序填的一律绿/红，两套来源不会互相污染。</summary>
        private void SetValueFilled(int r, int c, string v)
        {
            SetCell(r, c, v);
            CellStates.Set(_grid, r, c, CellState.Filled);
        }

        private void SetValueFailed(int r, int c, string v = "")
        {
            if (!string.IsNullOrEmpty(v)) SetCell(r, c, v);
            CellStates.Set(_grid, r, c, CellState.Failed);
        }

        /// <summary>换表/重载表格后调用：旧表的读取值不能拿来当新表的「旧数据」。</summary>
        public void ResetOldValues() { _oldValues.Clear(); }

        /// <summary>记一条「旧数据」基准（属性批量读取返填时逐格调用）。</summary>
        private void SetOldValue(string file, int col, string value)
        {
            if (string.IsNullOrEmpty(file)) return;
            Dictionary<int, string> byCol;
            if (!_oldValues.TryGetValue(file, out byCol))
            {
                byCol = new Dictionary<int, string>();
                _oldValues[file] = byCol;
            }
            byCol[col] = value ?? "";
        }

        /// <summary>取「旧数据」基准：没读过该行返回 false（＝没有旧值可比较，写侧按「非空即算变化」处理并单独记账）。</summary>
        private bool TryGetOldValue(string file, int col, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(file)) return false;
            Dictionary<int, string> byCol;
            if (!_oldValues.TryGetValue(file, out byCol)) return false;
            return byCol.TryGetValue(col, out value);
        }

        /// <summary>确保存在表头行（第 0 行 A-H 固定文字，I 列起自定义属性名来自当前表头）。</summary>
        public void EnsureHeaderRow()
        {
            if (_grid.ColumnCount < TableService.FixedColumnCount)
                _grid.ColumnCount = TableService.FixedColumnCount;
            if (_grid.Rows.Count == 0 || (_grid.AllowUserToAddRows && _grid.Rows.Count == 1 && _grid.Rows[0].IsNewRow))
                _grid.Rows.Add();
            for (int c = 0; c < TableService.FixedColumnCount; c++)
                _grid.Rows[0].Cells[c].Value = TableService.FixedHeaders[c];
            _grid.Rows[0].Frozen = true;
        }

        /// <summary>获取 H 列起的自定义属性名列表（第 0 行）。</summary>
        private List<string> GetPropNames()
        {
            var list = new List<string>();
            if (RowCount == 0) return list;
            for (int c = TableService.PropertyColumnStart; c < _grid.ColumnCount; c++)
                list.Add(Cell(0, c));
            return list;
        }

        // ==================== 屏幕清理 ====================

        public void ClearTable()
        {
            while (RowCount > 1) _grid.Rows.RemoveAt(1);
            EnsureHeaderRow();
            // 行被整批删掉 ⇒ 三色状态与「重复值」粉红标记都失去归属，一并清掉（残留色比没色更误导）
            int clearedStates = CellStates.ClearAll(_grid);
            int clearedDup = ClearDuplicateMarks();
            _oldValues.Clear();      // 旧数据基准随行一起作废（下一轮必须重新「属性批量读取」才有比较基准）
            _log($"已清空表格数据（保留表头）｜清除单元格三色状态 {clearedStates} 个、重复值底色标记 {clearedDup} 个。");
        }

        /// <summary>清掉所有「突出重复值」的底色，返回清掉的格数。</summary>
        private int ClearDuplicateMarks()
        {
            int n = 0;
            foreach (var (r, c) in _dupHighlighted.ToList())
            {
                if (r < 1 || r >= _grid.Rows.Count || c < 0 || c >= _grid.ColumnCount) continue;
                _grid.Rows[r].Cells[c].Style.BackColor = Color.Empty;
                n++;
            }
            _dupHighlighted.Clear();
            try { _grid.Invalidate(); } catch { }   // CellFormatting 按标记上色 ⇒ 标记清掉后必须重绘
            return n;
        }

        // ==================== 走查索引缓存（读写链与选中联动共用一次遍历） ====================

        private AssemblyWalk _walkCache;
        private DateTime _walkCacheAt = DateTime.MinValue;
        private string _walkCacheDoc = "";
        private const int WalkCacheTtlSeconds = 120;

        /// <summary>缓存一份走查结果：读/写链跑完自己调一次，选中联动就能零成本复用同一份索引。</summary>
        private void CacheWalk(AssemblyWalk w)
        {
            _walkCache = w;
            _walkCacheAt = DateTime.Now;
            try { _walkCacheDoc = _guard.ActiveDocPath() ?? ""; } catch { _walkCacheDoc = ""; }
        }

        /// <summary>让缓存失效（改名、导入、换表之后：设计树或表里的名字变了，旧索引不能再用于选中）。</summary>
        public void InvalidateWalkCache() { _walkCache = null; _walkCacheAt = DateTime.MinValue; _walkCacheDoc = ""; }

        /// <summary>换表/清表后调：旧表的「上次联动到谁」与新表毫无关系，必须归零，否则点同名行会静默不联动。</summary>
        public void ResetSelectionSync() { _lastSyncedName = null; InvalidateWalkCache(); }

        /// <summary>取索引：命中缓存直接用；过期／换文档／forceRefresh 才重走一遍（走查是只读的，套脏计数保护）。</summary>
        private AssemblyWalk GetWalk(bool forceRefresh)
        {
            string docNow;
            try { docNow = _guard.ActiveDocPath() ?? ""; } catch { docNow = ""; }
            bool fresh = !forceRefresh && _walkCache != null
                && string.Equals(docNow, _walkCacheDoc, StringComparison.OrdinalIgnoreCase)
                && (DateTime.Now - _walkCacheAt).TotalSeconds < WalkCacheTtlSeconds;
            if (fresh)
            {
                Diagnostics.Trace("选中联动·用缓存索引（" + _walkCacheAt.ToString("HH:mm:ss") + " 建，"
                    + _walkCache.Visited + " 个元素、" + _walkCache.ElementsByPartName.Count + " 个名字键）");
                return _walkCache;
            }
            AssemblyWalk w;
            using (_guard.BeginReadOnlyScope("选中联动·装配体穿透走查"))
                w = _svc.WalkActiveAssembly(false, false);
            CacheWalk(w);
            return w;
        }

        // ==================== 行选中联动（用户指令 2） ====================

        /// <summary>
        /// 表格选中某一行 → 在 CAXA 设计环境里选中**所有同名零件名**的零部件实例（零件/装配体/钣金/刻面/线框/轮廓）。
        /// 名字取 D 列（零件名），D 空则退回 C+E 的文件名。通道与判据见 CaxaBatchService.SelectElementsByName。
        /// 只在「目标名字真的变了」时写 UI 日志（连点同一行的不同单元格不该刷屏）；trace 每帧都写。
        /// </summary>
        public string SyncSelectionFromRow(int row)
        {
            if (row < 1) return null;                       // 表头行不联动
            if (row >= RowCount) return null;
            string partName = Cell(row, TableService.ColPartName).Trim();
            if (string.IsNullOrEmpty(partName))
            {
                string ft = Cell(row, TableService.ColFileType);
                string fn = Path.GetFileNameWithoutExtension(RowFile(row));
                partName = (string.IsNullOrWhiteSpace(ft) ? fn : Path.GetFileNameWithoutExtension(fn + "." + ft)).Trim();
            }
            if (string.Equals(partName, _lastSyncedName, StringComparison.OrdinalIgnoreCase)) return null;
            _lastSyncedName = partName;
            if (string.IsNullOrEmpty(partName)) { Diagnostics.Trace("选中联动：第 " + (row + 1) + " 行零件名为空＝不联动"); return null; }

            AssemblyWalk w = GetWalk(false);
            if (w == null || w.Visited == 0)
            {
                Diagnostics.Trace("选中联动：走查不到任何元素（当前不是装配体设计环境？）");
                return "选中联动「" + partName + "」没执行：当前设计环境走查不到零部件元素。";
            }
            var res = _svc.SelectElementsByName(w, partName, _stop);
            string text = "选中联动：第 " + (row + 1) + " 行零件名「" + partName + "」→ " + res.Detail;
            if (!res.Ok)
            {
                _lastSyncedName = null;      // 失败下次点同一行还要再试一次（不能因为名字没变就静默）
                Diagnostics.Log("[选中联动失败] " + text);
                return text;
            }
            Diagnostics.Trace(text);
            return null;                      // 成功：静默（设计树里看得见就是证据），只在失败时占 UI 日志
        }

        private string _lastSyncedName;

        // ==================== 从当前装配体导入 ====================

        /// <summary>
        /// 从当前 CAXA 3D 激活装配体穿透遍历，导入全部组件（去重并汇总数量）。
        /// 走查本身是只读的：用脏计数保护（DisableDirtyCounter 槽 66 / Enable 槽 67）包住，并在 trace 记录前后脏计数。
        /// rebuildAndSave=true（用户指定：导入后场景级重建设计环境并保存）时，导入完再重建+保存装配体。
        /// </summary>
        public int ImportAssembly(bool excludeSuppressed, bool excludeBomExcluded, bool rebuildAndSave)
        {
            AssemblyWalk walk;
            using (_guard.BeginReadOnlyScope("从装配体导入·穿透走查"))
                walk = _svc.WalkActiveAssembly(excludeSuppressed, excludeBomExcluded);
            var comps = walk.Components;
            if (comps.Count == 0)
            {
                _log("未从当前装配体收集到任何外部链接组件（请在设计环境打开装配体后再导入）。");
                return 0;
            }

            ClearTable();
            int n = 0;
            var propNames = GetPropNames();
            foreach (var ci in comps)
            {
                string partName = Path.GetFileNameWithoutExtension(ci.Name);   // 零件名（去扩展名）
                string modelFile = Path.Combine(ci.Path, partName + ".ics");
                int r = _grid.Rows.Add();
                // 导入的 A-G 全是程序从装配体返填的 ⇒ 绿（用户指令 3：返填成功标绿）；A/B 判定不了＝红（第三态按失败上色，日志另记）
                bool hasDrw = CaxaBatchService.HasDrawing(modelFile);
                bool hasExt = CaxaBatchService.HasExternalLink(modelFile);
                // E 文件类型：取设计环境里该零部件的真实类型（零件/装配/钣金，用户指令 4）；取不到类型时回退扩展名
                string typeText = ComponentTypeText(walk, ci) ?? ci.Ext;
                SetValueFilled(r, TableService.ColHasDrawing, hasDrw ? "★" : "");
                SetValueFilled(r, TableService.ColHasExternalLink, hasExt ? "是" : "否");
                SetValueFilled(r, TableService.ColFilePath, ci.Path);
                SetValueFilled(r, TableService.ColPartName, partName);                  // D 零件名
                SetValueFilled(r, TableService.ColFileType, typeText);
                SetValueFilled(r, TableService.ColNewPartName, partName);               // F 新零件名（默认=零件名）
                SetValueFilled(r, TableService.ColPartCount, QuantityText(ci.Count));   // G 3D零件数量
                n++;
            }
            _log($"从当前装配体导入完成，共 {n} 个唯一组件（重复组件已按数量汇总）；E 列「文件类型」已填设计环境零部件类型（零件/装配/钣金）。");
            if (rebuildAndSave) _guard.RebuildActiveSceneAndSave();
            return n;
        }

        /// <summary>
        /// 取某组件在设计环境里的真实类型文字（零件/装配/钣金，用户指令 4）。
        /// 优先用「外链文件全路径」在走查索引里定位元素，再读 IZElement.Type（eZElementType）；
        /// 若该组件就是装配体本身，则用走查的 RootElement；取不到类型时返回 null（调用方回退扩展名）。
        /// </summary>
        private string ComponentTypeText(AssemblyWalk w, ComponentInfo ci)
        {
            // ci.Name 来自 AddComponent 的 Path.GetFileName(linked)，**已含扩展名**（如「折弯板.ics」），
            // 必须再 GetFileNameWithoutExtension 去掉后再拼 ci.Ext，否则会变成「折弯板.ics.ics」双扩展名，
            // 与 ElementsByLink 的键（linked＝「折弯板.ics」）对不上 → 取不到元素 → 回退成 ci.Ext(=ics)。
            // 对照 RowFile / ImportAssembly 的 partName 都用 GetFileNameWithoutExtension(ci.Name) 取零件名，这里口径要一致。
            string modelFile = Path.Combine(ci.Path, Path.GetFileNameWithoutExtension(ci.Name) + "." + ci.Ext);
            IZElement elem = null;
            if (!string.IsNullOrEmpty(w.AssemblyPath)
                && string.Equals(CaxaBatchService.KeyOf(modelFile), CaxaBatchService.KeyOf(w.AssemblyPath), StringComparison.OrdinalIgnoreCase))
                elem = w.RootElement;
            if (elem == null)
            {
                List<IZElement> list;
                if (w.ElementsByLink.TryGetValue(CaxaBatchService.KeyOf(modelFile), out list) && list.Count > 0)
                    elem = list[0];
            }
            if (elem == null) return null;
            int t = LinkRenameV2.ElementTypeOf(elem);
            return LinkRenameV2.KindTextOf(t);   // null＝不在支持范围（如刻面/线框），调用方回退扩展名
        }

        public static string QuantityText(int count) => count > 0 ? count.ToString() : "1";

        // ==================== 属性批量读取 ====================

        /// <summary>表格这一行的目标 3D 组件文件全路径（C 路径 + D 零件名 + .ics）。
        /// 注意：E 列「文件类型」填的是设计环境类别（零件/装配/钣金，用户指令 4），是给人看的展示信息，
        /// **不能当文件扩展名用**——否则会拼出 "part.零件" 这种非法路径，使 TryFindElement 的精确路径匹配整批失效
        /// （这是指令 4 落地时引入的回归，现纠正）。CAXA 3D 的零件/装配文档一律是 .ics，图纸才是 .exb，
        /// 所以组件路径固定用 .ics；即便扩展名与记录不符，TryFindElement 还有「按文件名唯一命中」的兜底阶梯。</summary>
        private string RowFile(int r)
        {
            string partName = Cell(r, TableService.ColPartName);
            return Path.Combine(Cell(r, TableService.ColFilePath), partName + ".ics");
        }

        private static DateTime StampOf(string path)
        {
            try { return (string.IsNullOrEmpty(path) || !File.Exists(path)) ? DateTime.MinValue : File.GetLastWriteTime(path); }
            catch { return DateTime.MinValue; }
        }

        private static string StampText(DateTime t)
        {
            return t == DateTime.MinValue ? "<不可得>" : t.ToString("yyyy-MM-dd HH:mm:ss");
        }

        /// <summary>
        /// 属性批量读取（2026-11-06 重写，用户指定：参考 caxa3dTreeBom，读取直接在装配体设计环境内完成）：
        ///   · 一次穿透走查当前装配体，建「外链文件全路径 → 元素」索引（CaxaBatchService.WalkActiveAssembly）；
        ///   · 每行按路径在索引里取元素，直接 GetCustomPropManager + GetCustomPropAsString 读，
        ///     **不再 OpenFile2/Save/Close2 任何零件文件**（旧的 CaxaDocGuard 逐文件开关链整条撤掉）；
        ///   · 走查是只读的，用脏计数保护（Disable 槽 66 / Enable 槽 67）包住，前后脏计数落 trace；
        ///   · 判据分三态分开记账：读到值 / 读到空值 / 没这个属性（=第三态，不打印成空值）；命不到元素的行单独计数，
        ///     不回退去打开文件（那是本轮明确要求取消的行为）。
        /// 【编译期已验：走查 + 属性读取 API；元素索引命中率与读到的值需实机核对】
        /// </summary>
        public int ReadProperties()
        {
            var props = GetPropNames();
            if (props.Count == 0 || props.TrueForAll(string.IsNullOrWhiteSpace))
            {
                _log("表格第 1 行(H 列起)未设置自定义属性名称。");
                return 0;
            }
            _svc.ResetBomColumnTrack();     // 本轮哪些列走了 BOM 引擎通道，收尾要逐列点名（用户指令 1）
            _oldValues.Clear();             // 每次读取重建基准：上一轮读到的旧值不能拿来判这一轮的变化

            AssemblyWalk walk;
            using (_guard.BeginReadOnlyScope("属性批量读取·装配体内走查"))
                walk = _svc.WalkActiveAssembly(false, false);

            // —— 预热（用户指令 1：首点读不到、二点才读到）——
            // 根因假设＝COM 子系统冷初始化：① 若首遍走查 0 元素，先泵消息再走查一次（场景尚未就绪）；
            // ② 取一个样本元素做一次「作废」的共享表读取 + BOM 快照，逼出属性管理器 / BOM 引擎的懒加载，
            //    使后续每一行的真实读取第一次就能命中，无需用户点第二次。两层都是读操作，套只读保护。
            if (walk != null && walk.Visited == 0)
            {
                try { System.Windows.Forms.Application.DoEvents(); } catch { }
                using (_guard.BeginReadOnlyScope("属性批量读取·冷场重试走查"))
                    walk = _svc.WalkActiveAssembly(false, false);
            }
            if (walk != null && walk.Visited > 0)
            {
                IZElement warmup = walk.RootElement;
                if (warmup == null)
                    foreach (var kv in walk.ElementsByLink) { if (kv.Value.Count > 0) { warmup = kv.Value[0]; break; } }
                if (warmup != null)
                {
                    try
                    {
                        using (_guard.BeginReadOnlyScope("属性批量读取·预热属性子系统"))
                        {
                            string _w; CaxaBatchService.TryGetCustomProp(warmup, "PartName", out _w);  // 逼出共享属性管理器
                            BomValueReader.GetBomSnapshot(warmup);                                       // 逼出 BOM 引擎
                        }
                        System.Windows.Forms.Application.DoEvents();
                    }
                    catch (Exception ex) { Diagnostics.Trace("属性读取预热异常（忽略，继续）：" + ex.Message); }
                }
            }

            CacheWalk(walk);      // 同一份索引给选中联动复用（一次点击走一遍，别每次移动单元格都重走）

            if (walk == null || walk.Visited == 0)
            {
                _log("属性批量读取未执行：当前设计环境走查不到任何零部件元素（请在 CAXA 3D 设计环境里打开装配体后再读）。");
                return 0;
            }

            int hit = 0, miss = 0, cellFilled = 0, cellEmpty = 0, cellAbsent = 0;
            int cellGreen = 0, cellRed = 0;          // 实际染色的格数（不用公式推算，日志里的数必须数得出来）
            var absentByProp = new Dictionary<string, int>(StringComparer.Ordinal);
            var missSamples = new List<string>();

            for (int r = 1; r < RowCount; r++)
            {
                if (ShouldStop()) { _log("已收到停止信号，属性读取中断（已读到的行保留，未打开过任何文件所以无需恢复设计环境）。"); break; }

                string full = RowFile(r);
                IZElement target; string how;
                if (!CaxaBatchService.TryFindElement(walk, full, out target, out how))
                {
                    miss++;
                    if (missSamples.Count < 5) missSamples.Add(Path.GetFileName(full));
                    Diagnostics.Trace("读取未命中：表格第 " + (r + 1) + " 行 " + full + " → " + how);
                    cellRed += MarkRowFailed(r, props.Count);   // 整行属性列没被返填＝红（用户指令 3）
                    continue;
                }

                var absent = new List<string>();
                var values = _svc.ReadRowProperties(target, props, absent);
                foreach (string a in absent)
                {
                    cellAbsent++;
                    int n0; absentByProp.TryGetValue(a, out n0);
                    absentByProp[a] = n0 + 1;
                }
                // 「没这个属性」和「属性值为空」是两个不同的判据，不能同一格计两次
                var absentSet = new HashSet<string>(absent, StringComparer.OrdinalIgnoreCase);
                for (int c = TableService.PropertyColumnStart;
                     c < props.Count + TableService.PropertyColumnStart && c < _grid.ColumnCount; c++)
                {
                    string pn = Cell(0, c);
                    if (string.IsNullOrWhiteSpace(pn)) continue;
                    if (!values.TryGetValue(pn, out string v)) continue;
                    v = v ?? "";
                    if (absentSet.Contains(pn.Trim()))
                    {
                        // 第三态：共享表里没有这个属性 ⇒ 值保持原样（不写空串盖掉用户已有内容），只标红
                        CellStates.Set(_grid, r, c, CellState.Failed);
                        cellRed++;
                        continue;
                    }
                    SetValueFilled(r, c, v);              // 读到＝返填成功 ⇒ 绿（空串也算读到，是合法值）
                    SetOldValue(full, c, v);              // 旧数据基准＝CAXA 里真读到的值（写侧只写与它不一致的格子）
                    cellGreen++;
                    if (string.IsNullOrEmpty(v)) cellEmpty++; else cellFilled++;
                }

                hit++;
                Diagnostics.Trace("读取命中：第 " + (r + 1) + " 行 → " + how + "｜" + Path.GetFileName(full));
            }

            string absentText = "无";
            if (absentByProp.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in absentByProp) parts.Add(kv.Key + "×" + kv.Value);
                absentText = string.Join("、", parts.ToArray());
            }
            _log("属性批量读取完成（全程在当前装配体设计环境内直接读元素，未打开/未关闭任何零件文件）："
                + $"命中 {hit} 行、未命中 {miss} 行｜走查 {walk.Visited} 个零部件元素、{walk.Linked} 个外链实例、索引 {walk.ElementCount} 个文件"
                + $"｜单元格：读到值 {cellFilled} 个、读到空值 {cellEmpty} 个、没这个属性 {cellAbsent} 个（{absentText}）"
                + $"｜染色：绿 {cellGreen} 格、红 {cellRed} 格（红＝没这个属性 {cellAbsent} ＋ 未命中行整行标红 {cellRed - cellAbsent}）"
                + $"｜内部字段映射生效 {PropFieldMap.Count} 条｜映射文件 {PropFieldMap.SafePath()}"
                + $"｜BOM 引擎通道列：{( _svc.BomColumns.Count == 0 ? "本轮没有列走 BOM 通道" : BomFieldNotes.OfFields(_svc.BomColumns) + "（值来自 BOM 导出，重量=PartMass＝计算重量；清单文件 " + BomFieldList.EffectivePath + "）")}"
                + $"｜示例：表头「重量」→ 候选内部字段「{string.Join("→", PropFieldMap.Candidates("重量"))}」（按顺序试、命中即停；写侧只用第一个）"
                + $"｜列头注明：{(BomFieldNotes.ToolTipFor("重量").Length > 0 ? "「重量」列已挂 BOM 来源注明" : "「重量」列的候选都不在 BOM 清单里＝没挂注明（要挂请把它写进清单文件）")}"
                + (miss > 0 ? $"｜未命中样列：{string.Join("、", missSamples.ToArray())}（＝该文件不是当前装配体的实例，或 C 列路径写法与装配体记录的不一致；详见 trace）" : "")
                + "。【待验：读取结果需实机与 CAXA 属性对话框核对】");
            return hit;
        }

        /// <summary>整行属性列标红（该行没找到元素＝所有属性列都没返填）。返回实际染色的格数。</summary>
        private int MarkRowFailed(int r, int propColumnCount)
        {
            int n = 0;
            for (int c = TableService.PropertyColumnStart; c < TableService.PropertyColumnStart + propColumnCount
                && c < _grid.ColumnCount; c++)
            {
                CellStates.Set(_grid, r, c, CellState.Failed);
                n++;
            }
            return n;
        }

        // ==================== 属性批量写入 ====================

        /// <summary>
        /// 属性批量写入（2026-11-07 重写，用户指令：只把「与旧数据不一致」的单元格填进对应行零部件的
        /// 自定义属性共享表，属性名取该列的列头）：
        ///   · 「旧数据」＝本轮「属性批量读取」成功返填时记下的基准（_oldValues，按文件全路径＋列号，排序后行号变了也不串）；
        ///     用户改了哪格就只写哪格；没先读过（没有基准）的行按「非空即算变化」处理，并在日志里单独点名；
        ///   · 同一行的多个变化格合成一次 CaxaPropWrite.WriteElementProps(target, dict)（写共享表：对所有链接实例生效）；
        ///     列头经 PropFieldMap.Resolve 得到内部字段名；空值＝删除该属性（走 RemoveByName 槽 13）；
        ///   · 写完立即从**同一张共享表**逐格回读（CaxaBatchService.TryGetSharedProp），回读一致才算成功；
        ///     回读通道拿不到（mgrState != "共享表"）记「不可用」第三态，按失败上色但单独计数，不冒充成功；
        ///   · 回读成功的格子把基准更新成新值（同一次运行里连点两次不会重复写）；
        ///   · 收尾只保存一次当前装配体设计环境（判脏 + 只读预检，见 CaxaDocGuard.SaveActiveSceneIfModified），不再逐个零件 Save；
        ///   · 「宿主保存装配体时是否把共享属性回写进各零件 .ics」这条推断不给编译背书：写完前后对比零件文件 LastWriteTime，
        ///     把「变了几个」直接打进日志，实机看这一行就有结论。
        /// 【编译期已验：走查 + 写入 + 回读 API；生效/落盘需实机核对】
        /// </summary>
        public int WriteChangedProperties()
        {
            var props = GetPropNames();
            if (props.Count == 0 || props.TrueForAll(string.IsNullOrWhiteSpace))
            {
                _log("表格第 1 行(H 列起)未设置自定义属性名称。");
                return 0;
            }

            AssemblyWalk walk = _svc.WalkActiveAssembly(false, false);   // 写入链不套脏计数保护：属性写入本来就该弄脏场景
            if (walk == null || walk.Visited == 0)
            {
                _log("属性批量写入未执行：当前设计环境走查不到任何零部件元素（请在 CAXA 3D 设计环境里打开装配体后再写）。");
                return 0;
            }

            // 落盘证据·写前快照：装配体文件 + 各行目标文件
            string asmPath = _guard.ActiveDocPath();
            DateTime asmBefore = StampOf(asmPath);
            var fileBefore = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var fileOrder = new List<string>();
            for (int r = 1; r < RowCount; r++)
            {
                string f = RowFile(r);
                if (!fileBefore.ContainsKey(f)) { fileBefore[f] = StampOf(f); fileOrder.Add(f); }
            }

            int hit = 0, miss = 0, written = 0, removed = 0, failed = 0;
            int greenCells = 0, redCells = 0;         // 本轮实际染色格数（回读一致＝绿、失败/第三态＝红），日志必须数得出来
            int rowsChanged = 0, rowsUnchanged = 0, rowsNoBaseline = 0, rowsWritten = 0;
            int cellsChanged = 0;                     // 判定为「与旧数据不一致」的格子数（＝本轮计划写入的属性条数）
            int verified = 0, verifyFail = 0, verifyUnknown = 0;   // 逐格回读的三种结局
            var failSamples = new List<string>();
            var noBaselineSamples = new List<string>();
            string metaFieldUsed = "";                // 首个「列头→内部字段」映射（日志点名用，只在真的发生映射时非空）

            for (int r = 1; r < RowCount; r++)
            {
                if (ShouldStop()) { _log("已收到停止信号，属性写入中断（已写入的行仍会收尾保存装配体）。"); break; }

                string full = RowFile(r);
                IZElement target; string how;
                if (!CaxaBatchService.TryFindElement(walk, full, out target, out how))
                {
                    miss++;
                    Diagnostics.Trace("写入未命中：表格第 " + (r + 1) + " 行 " + full + " → " + how);
                    MarkRowFailed(r, props.Count);       // 没找到元素＝本行属性格一个也没写成＝红
                    continue;
                }
                hit++;

                // —— 收集「与旧数据不一致」的格子：属性名＝列头，空列头不参与 ——
                // 同一行多个变化格合成一次写入（共享表）；同一内部字段被两列抢用时只写第一列并点名，不静默覆盖。
                var dict = new Dictionary<string, string>(StringComparer.Ordinal);
                var changedCols = new List<int>();
                var changedVals = new List<string>();
                var changedFields = new List<string>();
                bool hasBaseline = false;
                for (int c = TableService.PropertyColumnStart;
                     c < props.Count + TableService.PropertyColumnStart && c < _grid.ColumnCount; c++)
                {
                    string header = Cell(0, c).Trim();
                    if (string.IsNullOrEmpty(header)) continue;
                    string cur = Cell(r, c);
                    string old; bool known = TryGetOldValue(full, c, out old);
                    if (known) hasBaseline = true;
                    bool changed = known
                        ? !string.Equals(cur, old ?? "", StringComparison.Ordinal)
                        : !string.IsNullOrEmpty(cur);        // 没有旧数据基准：非空即算变化（单独记账）
                    if (!changed) continue;

                    string field = PropFieldMap.Resolve(header);
                    if (dict.ContainsKey(field))
                    {
                        if (failSamples.Count < 5)
                            failSamples.Add(Path.GetFileNameWithoutExtension(full) + "（列「" + header
                                + "」与前一个变化列都映射到内部字段「" + field + "」，本行只写了前一列）");
                        continue;
                    }
                    dict[field] = cur;
                    changedCols.Add(c); changedVals.Add(cur); changedFields.Add(field);
                    if (metaFieldUsed.Length == 0 && !string.Equals(field, header, StringComparison.Ordinal))
                        metaFieldUsed = "「" + header + "」→「" + field + "」（映射文件 " + PropFieldMap.SafePath() + "）";
                }

                if (dict.Count == 0) { rowsUnchanged++; continue; }   // 与旧数据完全一致＝这一行什么都不写
                if (!hasBaseline)
                {
                    rowsNoBaseline++;
                    if (noBaselineSamples.Count < 5) noBaselineSamples.Add(Path.GetFileNameWithoutExtension(full));
                }
                rowsChanged++;
                cellsChanged += dict.Count;

                var oc = CaxaPropWrite.WriteElementProps(target, dict);
                written += oc.Written; removed += oc.Removed; failed += oc.Failed;
                Diagnostics.Trace(string.Format("写入：第 {0} 行 {1}｜管理器={2}｜变化格 {3} 个｜written={4} removed={5} failed={6}｜{7}",
                    r + 1, Path.GetFileName(full), oc.Manager, dict.Count, oc.Written, oc.Removed, oc.Failed, oc.LastError));
                if (oc.Written + oc.Removed > 0) rowsWritten++;

                // 权威判据：逐格从「写入用的那张表」（共享表）回读，回读一致才算成功
                for (int i = 0; i < changedCols.Count; i++)
                {
                    int c = changedCols[i];
                    string cur = changedVals[i];
                    string field = changedFields[i];
                    string back; string mgrState;
                    bool found = CaxaBatchService.TryGetSharedProp(target, field, out back, out mgrState);
                    bool readbackKnown = mgrState == "共享表";
                    bool ok;
                    if (!readbackKnown) { ok = false; verifyUnknown++; }   // 第三态：共享表指针都没拿到，不判成败
                    else ok = string.IsNullOrEmpty(cur)
                        ? (!found || string.IsNullOrEmpty(back))
                        : (found && string.Equals(back, cur, StringComparison.Ordinal));

                    if (ok)
                    {
                        verified++;
                        SetValueFilled(r, c, cur);        // 回读一致＝返填成功→绿
                        greenCells++;
                        SetOldValue(full, c, cur);        // 基准跟上新值：同一次运行里再点一次不会重复写
                        continue;
                    }

                    CellStates.Set(_grid, r, c, CellState.Failed);
                    redCells++;
                    if (readbackKnown)
                    {
                        verifyFail++;
                        if (failSamples.Count < 5)
                            failSamples.Add(Path.GetFileNameWithoutExtension(full) + "（列「" + Cell(0, c).Trim()
                                + "」回读=" + (found ? "「" + (back ?? "") + "」" : "读不到") + "）");
                        Diagnostics.Log("[回读不一致] 第 " + (r + 1) + " 行 " + Path.GetFileName(full)
                            + "：写入「" + cur + "」但共享表回读 " + (found ? "「" + (back ?? "") + "」" : "读不到")
                            + "；写入通道=" + oc.Manager + " " + oc.LastError);
                    }
                    else if (failSamples.Count < 5)
                        failSamples.Add(Path.GetFileNameWithoutExtension(full) + "（回读通道不可用：" + mgrState + "）");
                }
            }

            // 收尾：只保存当前装配体设计环境这一个文档（判脏 + 只读预检）
            bool dirtyWork = (written + removed) > 0;
            bool saved = dirtyWork ? _guard.SaveActiveSceneIfModified("属性批量写入") : false;
            if (!dirtyWork)
                _log("本轮没有任何属性写入成功（写入 " + written + "、清空 " + removed + "、失败 " + failed + "），不保存装配体。");

            // 落盘证据·写后：装配体文件 + 零件文件各变了几个
            DateTime asmAfter = StampOf(asmPath);
            int partChanged = 0, partKnown = 0;
            var partSamples = new List<string>();
            foreach (string f in fileOrder)
            {
                DateTime b = fileBefore[f];
                DateTime a = StampOf(f);
                if (b == DateTime.MinValue || a == DateTime.MinValue) continue;
                partKnown++;
                if (a > b) { partChanged++; if (partSamples.Count < 5) partSamples.Add(Path.GetFileName(f) + " " + StampText(b) + "→" + StampText(a)); }
            }

            string asmText = (asmBefore == DateTime.MinValue || asmAfter == DateTime.MinValue)
                ? "装配体文件时间 <不可得（第三态）>"
                : ("装配体文件时间 " + StampText(asmBefore) + "→" + StampText(asmAfter)
                   + (asmAfter > asmBefore ? "（已落盘）" : "（未变）"));
            _log("属性批量写入完成（只写与旧数据不一致的格子、属性名取列头；全程在当前装配体设计环境内直接写元素，"
                + "未打开/未关闭任何零件文件）："
                + $"变化行 {rowsChanged} 行、无变化跳过 {rowsUnchanged} 行、无旧数据基准 {rowsNoBaseline} 行（按「非空即算变化」处理）"
                + $"｜命中 {hit} 行、未命中 {miss} 行（走查 {walk.Visited} 个元素、索引 {walk.ElementCount} 个文件）"
                + $"｜计划写 {cellsChanged} 格（＝属性条数）｜写入 {written} 个、清空 {removed} 个、失败 {failed} 个｜写过的行 {rowsWritten} 个"
                + $"｜共享表逐格回读一致 {verified} 格、不一致 {verifyFail} 格、回读通道不可用 {verifyUnknown} 格"
                + $"｜染色：绿 {greenCells} 格、红 {redCells} 格"
                + (noBaselineSamples.Count > 0
                    ? "｜无基准样列：" + string.Join("、", noBaselineSamples.ToArray()) + "（建议先「属性批量读取」再改数据）"
                    : "")
                + (metaFieldUsed.Length > 0 ? "｜内部字段映射生效：" + metaFieldUsed : "")
                + $"｜装配体保存={(saved ? "已执行且脏标志已清" : "未执行/未确认")}，{asmText}"
                + $"｜零件文件时间变化 {partChanged}/{partKnown} 个"
                + (partChanged > 0 ? "（说明宿主保存装配体时确实回写了零件文件：" + string.Join("、", partSamples.ToArray()) + "）"
                                   : "（没有一个零件 .ics 被改写：属性可能只落在装配体里，需实机确认打开零件后属性是否还在）")
                + (failSamples.Count > 0 ? "｜问题样列：" + string.Join("、", failSamples.ToArray()) : "")
                + "。【待验：写入生效与落盘范围均需实机核对】");
            Diagnostics.Trace(string.Format(
                "WriteChangedProperties verdict: hit={0} miss={1} rowsChanged={2} rowsUnchanged={3} rowsNoBaseline={4} cellsChanged={5} written={6} removed={7} failed={8} rowsWritten={9} verified={10} verifyFail={11} verifyUnknown={12} saved={13} partChanged={14}/{15}",
                hit, miss, rowsChanged, rowsUnchanged, rowsNoBaseline, cellsChanged, written, removed, failed,
                rowsWritten, verified, verifyFail, verifyUnknown, saved, partChanged, partKnown));
            return rowsWritten;
        }

        // ==================== 文件改名（V2.0 联动链，用户指令 5） ====================

        /// <summary>
        /// 逐行比对 D「零件名」与 F「新零件名」，不一致＝按 F 走 V2.0 联动链（通道选定原文：「全盘复刻 V2.0 联动链」）。
        /// 链本体在 LinkRenameV2（双闸 → 改名前回填 SC* → set_Name → holderScene.SaveAs(Z_LINKS_IGNORE) → 补链 → exb 改名 → 删旧 ics → 结论行），
        /// 本方法只负责「表格 → 元素」对号、染色记账和收尾保存。
        /// 与旧的磁盘级改名（CaxaBatchService.RenameFileOnDisk）的区别：本链改的是**设计环境里的实例＋它的外链指向＋新 ics/新 exb/旧 ics**，
        /// 不是只挪文件；所以收尾必须保存一次场景文档——V2.0 把新外链指向写在内存里，不保存的话关掉场景重开就回到旧指向（参考 MainForm 那条注释）。
        /// 行↔元素用装配体穿透走查索引（与属性读写链同一张表），不逐行 OpenFile2。
        /// 判成功只认链里那行「═══ 结论（外链件…」与其中四个回读字段，set_Name/SaveAs 的 hr 一律不算（铁律：eOk 不算）。
        /// 「停止」只在**行与行之间**生效：单行链中途掐断就是半成品（名字改了、ics 没切），这是有意的取舍，日志里写明。
        /// 【编译期已验：链成员签名（反射实测 interop v26）与染色/计数路径；每行的真实副作用需实机核对】
        /// </summary>
        public int RenameFiles()
        {
            if (_svc == null || _svc.App == null) { _log("未连接 CAXA 3D，文件改名不执行。"); return 0; }
            if (_svc.ActiveSceneDoc == null)
            {
                _log("文件改名不执行：前台不是 3D 设计环境（活动文档不是场景文档）。请在装配体里操作。");
                return 0;
            }

            AssemblyWalk walk;
            using (_guard.BeginReadOnlyScope("文件改名·装配体穿透走查（建行↔元素索引，只读）"))
                walk = _svc.WalkActiveAssembly(false, false);
            if (walk == null || walk.Visited == 0)
            {
                _log("文件改名不执行：当前设计环境走查不到任何零部件元素。");
                return 0;
            }

            // 用户指令 2a：改名链用的图号分离规则＝**本项目**的规则文件（caxa3dBatchMaster图号分离规则.txt），
            // 不是参考工程 caxa3dcodenameextraction 那一份；每次开跑把生效路径与条数点名，便于核对改的是不是当前这份。
            _log("文件改名开始：图号分离规则＝" + SplitRules.EffectivePath + "（生效 " + SplitRules.Current().Count + " 条）"
                + "｜分流表（用户 11-06 指令 2c 原文）：1 零件→重命名-V2.0→更新工程图链接文件；"
                + "2 装配→重命名-V2.0→更新工程图链接文件→更新3dbom（自己开一次图纸环境）；"
                + "20 钣金→重命名-V2.0→钣金件设默认→更新工程图链接文件→标题栏设默认（同上，自己开一次图纸环境）；"
                + "0/3/-1→NULL＝「不在支持范围」本行中止，不猜"
                + "｜虚拟件（无外链 .ics，指令 2d）＝只改零件名＋图号分离，不碰任何文件。");

            int done = 0, rejected = 0, aborted = 0, notFound = 0, same = 0, noInput = 0;
            int unsupported = 0, virtualDone = 0, stepIncomplete = 0;   // 2c/2d：类型不在支持范围 / 虚拟件最小链 / 图纸侧步骤没跑全的行
            int greenCells = 0, redCells = 0;
            int propsWritten = 0, propsVerified = 0, propsVerifyFail = 0, propsUnknown = 0;
            var samples = new List<string>();

            for (int r = 1; r < RowCount; r++)
            {
                if (ShouldStop())
                {
                    _log("已收到停止信号，文件改名在行边界中断（已完成的行不回滚：链里只有「打到结论行」的行才算完成）。");
                    break;
                }
                if (r == 1) continue;   // 第 2 行＝装配体自己：不参与改名（闸 2 也会拒它，这里把同一条口径摆在明处）

                string partName = Cell(r, TableService.ColPartName).Trim();
                string newName = Cell(r, TableService.ColNewPartName).Trim();
                if (string.IsNullOrEmpty(partName) || string.IsNullOrEmpty(newName)) { noInput++; continue; }
                if (string.Equals(partName, newName, StringComparison.Ordinal)) { same++; continue; }

                string full = RowFile(r);
                IZElement target; string how;
                if (!CaxaBatchService.TryFindElement(walk, full, out target, out how))
                {
                    notFound++;
                    // 连元素都定位不到＝这一行根本没动手，按「失败」上色（不是「没改」）
                    CellStates.Set(_grid, r, TableService.ColPartName, CellState.Failed);
                    CellStates.Set(_grid, r, TableService.ColNewPartName, CellState.Failed);
                    redCells += 2;
                    Diagnostics.Trace("改名未命中：表格第 " + (r + 1) + " 行 " + full + " → " + how);
                    if (samples.Count < 5) samples.Add(partName + "→" + newName + "（设计环境里定位不到该元素：" + how + "）");
                    continue;
                }

                // ── 11-06 用户指令 2c/2d：先分「虚拟件 / 外链件」，外链件再按元素类型分流 ──
                bool isVirtual = string.IsNullOrEmpty(CaxaBatchService.GetLinkedFile(target));
                int etype = LinkRenameV2.ElementTypeOf(target);
                string kind = LinkRenameV2.KindTextOf(etype);

                if (!isVirtual && kind == null)
                {
                    // 用户表格原文：0 / 3 / -1 ⇒ NULL ⇒「不在支持范围」直接中止，不猜
                    unsupported++;
                    CellStates.Set(_grid, r, TableService.ColPartName, CellState.Failed);
                    CellStates.Set(_grid, r, TableService.ColNewPartName, CellState.Failed);
                    redCells += 2;
                    if (samples.Count < 5)
                        samples.Add(partName + "→" + newName + "（type=" + etype + " 不在支持范围：只认 1=零件 2=装配 20=钣金，一个字节都没动）");
                    Diagnostics.Trace(string.Format("改名中止：第 {0} 行 type={1} 不在支持范围（虚拟件={2}）", r + 1, etype, isVirtual));
                    continue;
                }

                // 改名前的属性回填（在链的双闸之后、任何改动之前被调用；参考把这段放在 prompt 回调里，同一个位置）
                var preWrite = new Action<IZElement, string>(delegate(IZElement el, string nn)
                {
                    SplitOutcome outc = DrawingNoSplitter.TrySplit(nn);
                    if (!outc.Matched)
                    {
                        _log("v2: ⓪ 图号分离规则没命中新名「" + nn + "」⇒ 本轮不回填属性（" + outc.Reason + "）");
                        return;
                    }
                    var values = new Dictionary<string, string>(StringComparer.Ordinal);
                    var shown = new List<string>();
                    foreach (KeyValuePair<string, string> kv in outc.Values)
                    {
                        string field = PropFieldMap.Resolve(kv.Key);
                        values[field] = kv.Value;
                        shown.Add(field + "=「" + kv.Value + "」" +
                            (string.Equals(field, kv.Key, StringComparison.Ordinal) ? "" : "（表头「" + kv.Key + "」）"));
                    }
                    CaxaPropWrite.WriteOutcome w = CaxaPropWrite.WriteElementProps(el, values);
                    propsWritten += w.Written + w.Removed;
                    _log("v2: ⓪ 回填 " + values.Count + " 条 → 管理器=" + w.Manager +
                        "｜写入 " + w.Written + "、清空 " + w.Removed + "、失败 " + w.Failed +
                        "：" + string.Join("、", shown.ToArray()) +
                        (w.Failed > 0 && !string.IsNullOrEmpty(w.LastError) ? "｜最后失败：" + w.LastError : ""));

                    // 回读只认共享表；「读不到」是第三态，单独计数，绝不并进失败或不写
                    foreach (KeyValuePair<string, string> kv in values)
                    {
                        string v; string mgrState;
                        if (!CaxaBatchService.TryGetSharedProp(el, kv.Key, out v, out mgrState))
                        {
                            propsUnknown++;
                            _log("v2: ⓪ 回读通道不可用：" + kv.Key + "（" + mgrState + "）＝不能证明写进去了");
                            continue;
                        }
                        if (string.Equals(v ?? "", kv.Value ?? "", StringComparison.Ordinal)) propsVerified++;
                        else { propsVerifyFail++; _log("v2: ⓪ 回读不一致：" + kv.Key + " 期望「" + kv.Value + "」实际「" + v + "」"); }
                    }
                });

                LinkRenameV2.Result rr = isVirtual
                    ? LinkRenameV2.RunVirtual(target, newName, preWrite, _log)
                    : LinkRenameV2.Run(_svc.App, target, newName, preWrite, _log);

                if (!rr.Concluded)
                {
                    // 没打到结论行＝这条链在我们眼里没跑完。Mutated 分两类：闸拒绝（没动手）与半途中止（动过手），日志必须分开写
                    if (rr.Mutated) aborted++; else rejected++;
                    CellStates.Set(_grid, r, TableService.ColPartName, CellState.Failed);
                    CellStates.Set(_grid, r, TableService.ColNewPartName, CellState.Failed);
                    redCells += 2;
                    if (samples.Count < 5)
                        samples.Add(partName + "→" + newName + "（" + (rr.Mutated ? "半途中止：" : "准入拒绝：") +
                            (string.IsNullOrEmpty(rr.SkipReason) ? "链未打到结论行" : rr.SkipReason) + "）");
                    continue;
                }

                if (isVirtual) virtualDone++;
                done++;
                SetValueFilled(r, TableService.ColPartName, newName);          // D 同步成新名＝返填成功＝绿
                CellStates.Set(_grid, r, TableService.ColNewPartName, CellState.Filled);
                greenCells += 2;

                // 图纸侧后续步骤（分流表 ②③④）：只对**外链件**跑，虚拟件按用户指令 2d 到此为止。
                if (!isVirtual)
                {
                    string stepState = DrawingChain.RunAfterRename(_svc, target, etype, rr, _log, ShouldStop);
                    rr.DrawingStepState = stepState;
                    if (!string.IsNullOrEmpty(stepState)
                        && (stepState.IndexOf("✘", StringComparison.Ordinal) >= 0
                            || stepState.IndexOf("未执行", StringComparison.Ordinal) >= 0))
                        stepIncomplete++;
                }
            }

            // 收尾：V2.0 的新外链指向只在内存里，不存这一发，重开场景就回到旧指向（参考同一条）
            bool saved = false;
            if (done > 0) saved = _guard.SaveActiveSceneIfModified("文件改名（V2.0 链收尾·场景文档）");
            if (done > 0) ResetSelectionSync();   // 名字变了：按名索引与「上次联动到谁」全部作废
            else InvalidateWalkCache();

            _log("文件改名完成（V2.0 联动链，全程在当前装配体设计环境内操作）："
                + $"完成 {done} 行（其中虚拟件最小链 {virtualDone} 行）、准入拒绝 {rejected} 行、半途中止 {aborted} 行、元素定位不到 {notFound} 行、"
                + $"新旧名相同 {same} 行、D/F 有一栏为空 {noInput} 行、类型不在支持范围 {unsupported} 行"
                + $"｜图纸侧后续步骤未跑全 {stepIncomplete} 行（详见逐行「chain:」日志）"
                + $"｜属性回填：写 {propsWritten} 条、回读一致 {propsVerified} 条、不一致 {propsVerifyFail} 条、回读通道不可用 {propsUnknown} 条"
                + $"｜染色：绿 {greenCells} 格、红 {redCells} 格"
                + $"｜场景保存={(done == 0 ? "本轮没有完成的行＝不保存" : (saved ? "已执行且脏标志已清" : "未执行/未确认"))}"
                + (samples.Count > 0 ? "｜问题样列：" + string.Join("、", samples.ToArray()) : "")
                + "。【待验：每行的外链指向/新 ics 落盘/exb 改名/旧 ics 删除都要实机核对；被拒绝与被中止的行不重试】");
            Diagnostics.Trace(string.Format(
                "RenameFiles verdict: done={0} rejected={1} aborted={2} notFound={3} same={4} noInput={5} propsWritten={6} propsOk={7} propsBad={8} propsUnknown={9} saved={10}",
                done, rejected, aborted, notFound, same, noInput, propsWritten, propsVerified, propsVerifyFail, propsUnknown, saved));
            return done;
        }


        // ==================== 图号分离（用户指令 6） ====================

        /// <summary>
        /// 逐行把 D 列（零件名）按图号分离规则切段，填回表格里同名的 SC* 属性列。
        /// 口径（用户选定）：按 A→E 顺序自动试配段数；**只填表格单元格＋三色标记**，不写 3D 属性、不保存装配体。
        /// 染色：填进去的格＝绿；该行没匹配上＝它缺的那几个目标格＝红；表格里压根没有的目标列＝只写日志（没有格子可染）。
        /// </summary>
        public int SplitDrawingNumbers()
        {
            var rules = SplitRules.Current();
            _log("图号分离开始：规则 " + rules.Count + " 条（" + SplitRules.EffectivePath + "），"
                + "按 A→E 顺序自动试配段数；只写表格单元格，不碰 3D 属性、不保存装配体。");

            // 目标列：表头（第 1 行）文字 == 规则字段名。所有规则用到的字段名先去重，一次定位。
            var wanted = new List<string>();
            foreach (SplitRule r in rules)
                foreach (string f in r.Fields)
                    if (!wanted.Contains(f, StringComparer.Ordinal)) wanted.Add(f);
            var colOf = new Dictionary<string, int>(StringComparer.Ordinal);
            var missingCols = new List<string>();
            foreach (string f in wanted)
            {
                int found = -1;
                for (int c = TableService.PropertyColumnStart; c < _grid.ColumnCount; c++)
                    if (string.Equals(Cell(0, c).Trim(), f, StringComparison.Ordinal)) { found = c; break; }
                if (found >= 0) colOf[f] = found; else missingCols.Add(f);
            }
            if (missingCols.Count > 0)
                _log("表格里没有这些目标列，分离出来的值无处可填（本轮跳过它们）：" + string.Join("、", missingCols.ToArray()));

            int rows = 0, matched = 0, unmatched = 0, filled = 0, redCells = 0;
            var ruleHits = new Dictionary<string, int>(StringComparer.Ordinal);
            var unmatchSamples = new List<string>();

            for (int r = 1; r < RowCount; r++)
            {
                if (ShouldStop()) { _log("已收到停止信号，图号分离中断（已填的行保留）。"); break; }
                string partName = Cell(r, TableService.ColPartName).Trim();
                if (string.IsNullOrEmpty(partName)) continue;
                rows++;

                var outc = DrawingNoSplitter.TrySplit(partName);
                if (!outc.Matched)
                {
                    unmatched++;
                    if (unmatchSamples.Count < 5)
                        unmatchSamples.Add(Path.GetFileName(partName) + "（" + outc.Reason + "）");
                    // 没匹配上＝这行的目标格全标红（用户指令 3：失败标红）
                    foreach (var kv in colOf)
                    {
                        CellStates.Set(_grid, r, kv.Value, CellState.Failed);
                        redCells++;
                    }
                    Diagnostics.Trace("图号分离未命中：第 " + (r + 1) + " 行「" + partName + "」→ " + outc.Reason);
                    continue;
                }

                matched++;
                int n0; ruleHits.TryGetValue(outc.Rule.Text, out n0);
                ruleHits[outc.Rule.Text] = n0 + 1;
                foreach (var kv in outc.Values)
                {
                    int col;
                    if (!colOf.TryGetValue(kv.Key, out col)) continue;
                    SetValueFilled(r, col, kv.Value);
                    filled++;
                }
                Diagnostics.Trace("图号分离：第 " + (r + 1) + " 行「" + partName + "」按「" + outc.Rule.Text
                    + "」切 " + outc.Segments + " 段 → " + string.Join("、", ToTexts(outc)));
            }

            var hitText = new List<string>();
            foreach (var kv in ruleHits) hitText.Add(kv.Key.Substring(0, 1) + "×" + kv.Value);
            _log("图号分离完成：处理 " + rows + " 行｜命中 " + matched + " 行（" 
                + (hitText.Count > 0 ? string.Join("、", hitText.ToArray()) : "无") + "）｜没匹配上 " + unmatched + " 行"
                + "｜填入单元格 " + filled + " 个（绿）、标红 " + redCells + " 个"
                + (missingCols.Count > 0 ? "｜缺目标列 " + missingCols.Count + " 个（" + string.Join("、", missingCols.ToArray()) + "）" : "")
                + (unmatchSamples.Count > 0 ? "｜未命中样例：" + string.Join("；", unmatchSamples.ToArray()) : "")
                + "｜本次不写 3D 属性、不保存装配体。");
            return matched;
        }

        private static IEnumerable<string> ToTexts(SplitOutcome o)
        {
            foreach (var kv in o.Values) yield return kv.Key + "＝" + kv.Value;
        }

        // ==================== 刷新工程图 ====================

        /// <summary>单次「打开图纸」的结果与归属判定（图纸文档不走 Guard 的 3D 找回路径，理由见 OpenDrawing 注释）。</summary>
        private sealed class DrawingOpenState
        {
            public bool AlreadyOpen;        // OpenFile2 的 out pbAlreadyOpen＝宿主早就打开着这张图
            public bool OwnershipUnknown;   // OpenFile2 抛异常＝out 值不可信，归属不可知（第三态）
            public bool Opened;             // 至少没有「文件不存在」这类硬失败
            public int PopupsClicked;       // 本次打开窗口期内被代点掉的弹窗数（Arm 每次清零，故取撤防时的读数）
            public string Note = "";
        }

        /// <summary>
        /// 刷新工程图（11-06 用户指令 4，通道选定原文：「自带 CRX 桥（与参考同构）」）。
        /// 参考基准 = caxa3dmytoolsaddin\plugins\caxa3dCXDFTViewUpdate：逐行「打开 → 处理弹窗 → 保存 → 关闭」，
        /// 其中「保存 + 关闭」必须由图纸进程内的 CRX 投原生命令完成——远程 COM 对 .exb 调 Save/Close 是**假成功**
        /// （hr=0 但只写出 xxx.exb.ics，图纸没存、窗口没关；实机已证），所以本链一次都不再调 IZDoc.Save/Close2。
        ///
        /// 为什么不用 CaxaDocGuard.Open：它的「假失败找回」走 GetOpenDocByName，而该方法只枚举 3D 场景文档、
        /// 对 .exb 恒返回空（已实测）——拿它判图纸会一律误报「打不开文件」。这里改为直调 OpenFile2 并只信
        /// out pbAlreadyOpen 这一个官方信号；拿不到就记 OwnershipUnknown，保守地「只保存不关闭」。
        ///
        /// 冷启动铁律：CRX 没加载时绝不投递任何会动手的命令——先预热（唯一入口＝专用空图纸 .exb），
        /// 预热失败即整条链中止，不试别的入口。
        ///
        /// 本链只动磁盘上的图纸，不写表格单元格 ⇒ 不参与三色标记（用户指令 3 的三色只覆盖「程序往格子里填值」的链）。
        /// </summary>
        public int RefreshDrawings()
        {
            if (_svc == null || _svc.App == null) { _log("未连接 CAXA 3D，刷新工程图不执行。"); return 0; }

            var targets = new List<string>();
            int scanned = 0;
            for (int r = 1; r < RowCount; r++)
            {
                string dir = Cell(r, TableService.ColFilePath);
                string name = Cell(r, TableService.ColPartName);
                if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(name)) continue;
                scanned++;
                string exb = Path.Combine(dir, name + ".exb");
                if (File.Exists(exb) && !targets.Contains(exb, StringComparer.OrdinalIgnoreCase))
                    targets.Add(exb);
            }

            if (targets.Count == 0)
            {
                _log("刷新工程图：走查 " + scanned + " 行没有发现同名 .exb（模型目录里就没有图纸），本步无事可做。");
                return 0;
            }
            _log("刷新工程图：待处理图纸 " + targets.Count + " 个（来自 " + scanned + " 行，同名去重）。");

            // ---------- 桥就绪（含预热）；冷启动不投破坏性命令 ----------
            if (!ViewBridge.IsCrxLoaded())
            {
                _log("view: 桥探针状态＝" + ViewBridge.MarkerState() + " ⇒ 先预热图纸环境（OpenFile2 专用空图纸 .exb）。");
                if (!ViewBridge.WarmUpDrawingEnv(_svc.App, 30000, _log))
                {
                    _log("刷新工程图中止：预热失败＝CRX 桥不可用，绝不改用远程 COM 保存图纸（那是已实测的假成功路径）。"
                        + "请确认安装把 caxa3dBatchMasterView.crx 放进 CAXADraft\\Bin 且 CxAutorun.cx 里有对应那一行。");
                    Diagnostics.Trace("RefreshDrawings verdict: ABORT bridge-unavailable targets=" + targets.Count);
                    return 0;
                }
            }
            else if (!ViewBridge.EnsureToken(10000, _log))
            {
                _log("刷新工程图中止：桥探针在但 PING 拿不到会话令牌＝不投任何会动手的命令。");
                Diagnostics.Trace("RefreshDrawings verdict: ABORT no-token targets=" + targets.Count);
                return 0;
            }

            int doneModified = 0, doneClean = 0, partialFail = 0, warnUnchanged = 0, notFound = 0, saveOnly = 0;
            int consecutiveNoReply = 0, popupTotal = 0;

            foreach (string exb in targets)
            {
                if (ShouldStop()) { _log("已收到停止信号，刷新工程图在行边界中断（已完成的行不回退）。"); break; }

                string fileName = Path.GetFileName(exb);
                DateTime before = StampOf(exb);
                long logFrom = ViewBridge.CurrentLogOffset();
                int popupBase = PopupAutoYes.ClickedCount;   // 代点计数进程级累计，本行只取差值

                DrawingOpenState st = OpenDrawing(exb);
                if (!st.Opened)
                {
                    notFound++;
                    _log("── 图纸 " + fileName + " ：打开失败（" + st.Note + "），跳过本行。");
                    continue;
                }

                // 「保存 + 关闭」的动词选择：
                //   · CLOSEDOC＝按文件名定位那张图纸 → 投保存 → 等 mtime 变 → WM_CLOSE（本次新开/归属不可知都走它）；
                //   · SAVE＝只保存到盘、不关窗——宿主本来就打开着这张图时不敢替用户关窗。
                // 两个动词都带 arg=图纸文件名：CRX 侧 arg 非空即按文件名定位，不依赖「当前图纸」是谁（实测更稳）。
                string verb = st.AlreadyOpen ? "SAVE" : "CLOSEDOC";
                if (verb == "SAVE") saveOnly++;
                _log("── 图纸 " + fileName + "：" + verb + "（alreadyOpen=" + st.AlreadyOpen +
                     " 归属不可知=" + st.OwnershipUnknown + "）" +
                     (st.OwnershipUnknown && verb == "CLOSEDOC" ? "｜OpenFile2 假失败，按文件名定位，最坏情况多关一张同名图，不会动别的图纸" : "") +
                     (string.IsNullOrEmpty(st.Note) ? "" : "｜" + st.Note));

                BridgeReply rep;
                PopupAutoYes.Arm(_log);
                try
                {
                    rep = ViewBridge.Request(verb, fileName, true, 90000, _log);
                }
                catch (OperationStoppedException) { _log("刷新工程图被用户停止。"); throw; }
                finally { PopupAutoYes.Disarm(); }

                int popupHit = PopupAutoYes.ClickedCount - popupBase;   // 计数器进程级累计，本行只取差值
                popupTotal += popupHit;

                // 判据顺序：先（CLOSEDOC 时）给日志一个短窗口等 close-done，再从日志取原文行——
                // 反过来的话取到的是「等之前」的内容，把「还没写」误当成「没生效」。
                bool closed = false;
                if (verb == "CLOSEDOC")
                    closed = ViewBridge.LogHas(logFrom, "close-done", 5000, _log) ||
                             rep.Message.IndexOf("close-done", StringComparison.OrdinalIgnoreCase) >= 0;
                string closeLine = ViewBridge.LastLogLine(logFrom, "close-done");
                string modifiedLine = ViewBridge.LastLogLine(logFrom, "modified=");
                DateTime after = StampOf(exb);
                bool grew = after != DateTime.MinValue && before != DateTime.MinValue && after > before;
                // modified= 来自 CRX 的两处：回包 message（close-done … modified=N）与日志 MOD.state 行；两处都没有＝第三态
                bool modifiedYes = (rep.Message + " " + modifiedLine).IndexOf("modified=1", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!rep.Ok)
                {
                    // TIMEOUT＝通道无响应，不等于没生效 ⇒ 仍要单独记账并去 CRX 日志核对
                    partialFail++;
                    if (rep.Status == "TIMEOUT") consecutiveNoReply++;
                    _log("图纸 " + fileName + " ：部分失败（" + rep.Describe() + "）｜CRX 日志：" +
                         (string.IsNullOrEmpty(closeLine) ? "<无 close-done 行>" : closeLine) +
                         "｜文件时间 " + StampText(before) + "→" + StampText(after) +
                         "｜本行代点弹窗 " + popupHit + " 个");
                    if (consecutiveNoReply >= 2)
                    {
                        _log("连续 " + consecutiveNoReply + " 行投递后无回包＝判定桥已失效（图纸进程可能已退出），"
                            + "剩余行不再投递。");
                        break;
                    }
                    continue;
                }

                consecutiveNoReply = 0;
                if (verb == "SAVE")
                {
                    // SAVE 不关窗，只看有没有真的写盘
                    if (grew)
                    {
                        doneModified++;
                        _log("图纸 " + fileName + " ：完成·有改动（只保存未关窗）｜文件时间 " + StampText(before) + "→" +
                            StampText(after) + "｜" + (string.IsNullOrEmpty(modifiedLine) ? rep.Message : modifiedLine) +
                            "｜本行代点弹窗 " + popupHit + " 个");
                    }
                    else if (modifiedYes)
                    {
                        warnUnchanged++;
                        _log("图纸 " + fileName + " ：警告＝CRX 报 modified=1 但文件时间没变（" + StampText(after) +
                            "）＝第三态，需实机核对是否真落盘，不能算成功。");
                    }
                    else
                    {
                        doneClean++;
                        _log("图纸 " + fileName + " ：完成·无改动（图纸本来就不脏，保存命令空跑；文件时间不变属正常）｜" +
                            (string.IsNullOrEmpty(modifiedLine) ? rep.Message : modifiedLine));
                    }
                }
                else if (!closed)
                {
                    partialFail++;
                    _log("图纸 " + fileName + " ：部分失败＝回包 OK 但日志里 " + ViewBridge.LogPath +
                        " 没有 close-done（5 秒窗口内）＝不能当成已关窗（第三态）。文件时间 " +
                        StampText(before) + "→" + StampText(after) + "｜本行代点弹窗 " + popupHit + " 个");
                }
                else if (grew)
                {
                    doneModified++;
                    _log("图纸 " + fileName + " ：完成·有改动（保存并关闭）｜文件时间 " + StampText(before) + "→" +
                        StampText(after) + "｜" + (string.IsNullOrEmpty(closeLine) ? rep.Message : closeLine) +
                        "｜本行代点弹窗 " + popupHit + " 个");
                }
                else if (modifiedYes)
                {
                    warnUnchanged++;
                    _log("图纸 " + fileName + " ：警告＝已关窗且 CRX 报 modified=1，但文件时间没变（" + StampText(after) +
                        "）＝图可能没存上，需实机核对。" + (string.IsNullOrEmpty(closeLine) ? rep.Message : closeLine));
                }
                else
                {
                    doneClean++;
                    _log("图纸 " + fileName + " ：完成·无改动（已关窗；modified=0 且文件时间不变，属正常）｜" +
                        (string.IsNullOrEmpty(closeLine) ? rep.Message : closeLine));
                }
            }

            int ok = doneModified + doneClean;
            _log("刷新工程图汇总：完成 " + ok + "（其中有改动 " + doneModified + "、无改动 " + doneClean +
                 "，只保存不关窗 " + saveOnly + "）／部分失败 " + partialFail + "／打开失败 " + notFound +
                 "／警告(报改动但文件未变) " + warnUnchanged + "／目标 " + targets.Count +
                 "；本链累计代点弹窗 " + popupTotal + " 个（发现 " + PopupAutoYes.SeenCount + " 个）。");
            _log("说明：本链不写表格单元格，故不参与三色标记；三色只覆盖「程序往格子里填值」的链（属性读写、文件改名、图号分离）。");
            _log("提醒：改名链已把同名 .exb 一起改名，但**图纸内部的旧零件指向不由本功能修改**——本链只在图纸进程内做打开/保存/关闭；" +
                 "图纸里指向旧 ics 的引用要靠 CAXA 原生「替换零部件」，实机若发现图纸打不开或图框丢失请按这条路线处理。");
            Diagnostics.Trace("RefreshDrawings verdict: ok=" + ok + " modified=" + doneModified + " clean=" + doneClean +
                " saveOnly=" + saveOnly + " partialFail=" + partialFail + " notFound=" + notFound +
                " warnUnchanged=" + warnUnchanged + " targets=" + targets.Count +
                " popupClicked=" + popupTotal + "/" + PopupAutoYes.SeenCount);
            // 图纸链可能改变活动文档（预热/打开图纸），走查缓存与选中联动的基准都要作废
            InvalidateWalkCache();
            return ok;
        }

        /// <summary>打开一张图纸：只信 OpenFile2 的 out pbAlreadyOpen；异常按「假失败」记账并把归属标为不可知（保守＝不关窗）。
        /// 图纸文档指针不参与判定（远程 COM 对 .exb 的 Save/Close 是假成功，拿指针也没用）。
        /// OpenFile2 实测会阻塞到图纸真正打开（含「数据源更新→是」弹窗）才返回，所以代点器必须先布防再调用。</summary>
        private DrawingOpenState OpenDrawing(string exb)
        {
            var s = new DrawingOpenState();
            string fileName = Path.GetFileName(exb);
            if (!File.Exists(exb)) { s.Note = "文件不存在"; return s; }

            PopupAutoYes.Arm(_log);
            try
            {
                int clickBase = PopupAutoYes.ClickedCount;
                bool already = false;
                try
                {
                    // 返回的图纸文档指针刻意不用：远程 COM 对它调 Save/Close 是实测过的假成功，
                    // 只用 out pbAlreadyOpen 这一个官方信号判归属（C# 侧留变量是为了签名对齐，不然后人误以为漏了处理）
                    IZDoc drawingDoc = _svc.App.OpenFile2(exb, false, true, out already);
                    if (drawingDoc != null) Diagnostics.Trace("OpenDrawing: 拿到图纸文档指针但按铁律不用（远程 COM 动手＝假成功）：" + fileName);
                    s.AlreadyOpen = already;
                    s.Opened = true;
                }
                catch (Exception ex)
                {
                    // 参考实测：OpenFile2 对 .exb 抛异常也常常是真打开了（假失败）；指针拿不到，归属只能按不可知
                    s.OwnershipUnknown = true;
                    s.Opened = true;
                    s.Note = "OpenFile2 抛异常（假失败候选）：" + ex.Message;
                }
                // 弹窗可能在 OpenFile2 返回后才出现（宿主延后弹保存/警告框），给它一个短窗口把剩下的点掉
                _guard.Pump(1200);
                s.PopupsClicked = PopupAutoYes.ClickedCount - clickBase;
                if (s.PopupsClicked > 0)
                    s.Note += (string.IsNullOrEmpty(s.Note) ? "" : "｜") + "打开窗口期内代点 " + s.PopupsClicked + " 个弹窗";
            }
            finally { PopupAutoYes.Disarm(); }
            return s;
        }

        // ==================== 列功能 ====================

        public int SortColumnAscending(int col)
        {
            if (col < 0 || col >= _grid.ColumnCount || RowCount <= 1) return 0;

            // 第 2 行（网格索引 1）＝「从装配体导入」识别出的**当前打开的 .ics（装配体自己）**。
            // 它处于打开状态、改名会报错，属硬编码锁定行 ⇒ 排序永远不移动它，只对索引 2 起的数据行排序。
            const int LockedRow = 1;
            if (RowCount <= LockedRow + 1)
            {
                _log($"按升序排序：列 {TableService.ColumnLetter(col)} 无数据行（第 2 行装配体锁定），未做改动。");
                return 0;
            }
            int clearedDup = ClearDuplicateMarks();   // 行序一变，旧粉底的坐标就指向别的行了 ⇒ 排序前先清

            var rows = new List<(int rowIndex, string[] values, List<CellState> states)>();
            for (int r = LockedRow + 1; r < RowCount; r++)
            {
                var values = new string[_grid.ColumnCount];
                for (int c = 0; c < _grid.ColumnCount; c++)
                    values[c] = _grid.Rows[r].Cells[c].Value?.ToString() ?? "";
                // 状态必须跟着值一起搬：只搬值会让「绿/红」留在原位，等于给新数据盖旧结论
                var states = CellStates.SnapshotRow(_grid, r);
                rows.Add((r, values, states));
            }

            var ordered = rows
                .OrderBy(x => string.IsNullOrWhiteSpace(x.values[col]) ? 1 : 0)
                .ThenBy(x => double.TryParse(x.values[col], out var numeric) ? numeric : double.MaxValue)
                .ThenBy(x => x.values[col], StringComparer.OrdinalIgnoreCase)
                .ToList();

            for (int i = 0; i < ordered.Count; i++)
            {
                for (int c = 0; c < _grid.ColumnCount; c++)
                    _grid.Rows[i + LockedRow + 1].Cells[c].Value = ordered[i].values[c];
                CellStates.RestoreRow(_grid, i + LockedRow + 1, ordered[i].states);
            }

            _log($"按升序排序：列 {TableService.ColumnLetter(col)} 已完成（第 2 行＝当前打开的装配体 .ics，锁定不参与排序；"
                + $"本次排序 {ordered.Count} 行" + (clearedDup > 0 ? "，清除重复值标记 " + clearedDup + " 个" : "") + "）。");
            return ordered.Count;
        }

        private readonly HashSet<(int r, int c)> _dupHighlighted = new HashSet<(int r, int c)>();

        /// <summary>某格是否带「突出重复值」标记。取色优先级由 CellFormatting 执行：
        /// **重复值粉底 &gt; 三色状态**——否则导入时全表已刷绿，粉底会被状态色盖住，功能看着像没生效
        /// （11-07 实机反馈；旧实现只写 cell.Style.BackColor，被 CellFormatting 覆写）。</summary>
        public bool IsDuplicateCell(int row, int col) => _dupHighlighted.Contains((row, col));

        /// <summary>突出重复值＝对指定列做重复值排查，重复格由 CellFormatting 画粉底（优先级高于三色）。
        /// 重复值是临时排查工具，重新排序/换列/清表都会清掉。</summary>
        public int HighlightDuplicateValues(int col)
        {
            if (col < 0 || col >= _grid.ColumnCount) return 0;

            foreach (var (r, c) in _dupHighlighted.ToList())
            {
                if (c == col && r >= 1 && r < _grid.Rows.Count)
                    _grid.Rows[r].Cells[c].Style.BackColor = Color.Empty;
            }
            _dupHighlighted.RemoveWhere(h => h.c == col);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int r = 1; r < RowCount; r++)
            {
                string v = Cell(r, col);
                if (string.IsNullOrWhiteSpace(v)) continue;
                counts[v] = counts.TryGetValue(v, out int n) ? n + 1 : 1;
            }

            int marked = 0;
            for (int r = 1; r < RowCount; r++)
            {
                string v = Cell(r, col);
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (counts[v] > 1)
                {
                    _grid.Rows[r].Cells[col].Style.BackColor = Color.Pink;
                    _dupHighlighted.Add((r, col));
                    marked++;
                }
            }

            _log($"突出重复值：列 {TableService.ColumnLetter(col)} 共 {marked} 个重复单元格已标粉底（选中该列时据此排查；"
                + "粉底优先于绿/红/黄状态色显示）。");
            try { _grid.Invalidate(); } catch { }
            return marked;
        }
    }
}
