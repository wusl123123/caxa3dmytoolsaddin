using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using interop.ICApiIronCAD;

namespace caxa3dTreeBom
{
    /// <summary>
    /// 树形 BOM 构建器：从 CAXA 3D 当前选中装配体（或场景顶层）递归遍历装配结构，
    /// 产出带层级编码的 BOM 行。
    /// 层级编码：总装 = 1，其子件 = 1.1 / 1.2 …，孙件 = 1.1.1 / 1.1.2 …
    /// 数量：该节点在直接父级装配中的实例数量；
    /// 总数量：同名件在全树中的累计数量（仅首次出现填数值，其余填 "---"）。
    /// </summary>
    [ComVisible(false)]
    internal class BomBuilder
    {
        private readonly CaxaService _svc;
        private readonly Action<string> _log;

        /// <summary>需要按表头名称抓取的自定义属性列名（排除 层级/是否有工程图/缩略图/数量/总数量 等专用列）。</summary>
        public List<string> PropertyHeaders { get; set; } = new List<string>();

        /// <summary>按分组键缓存的自定义属性读取结果（同名件只读一次）。</summary>
        private readonly Dictionary<string, Dictionary<string, string>> _propCache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>与 _propCache 同键的「取值来源」缓存（属性 / BOM:命中标签 / 专用:专用词 / 空＝读不到）。
        /// 存在的意义＝同一元素的 ExportSelfBOM 只向宿主调一次（大装配逐列重算会拖死主线程）。</summary>
        private readonly Dictionary<string, Dictionary<string, string>> _srcCache =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>本轮已告警过的键（去重，避免刷屏）。</summary>
        private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>遍历进度回调（已处理节点数, 说明）。</summary>
        public Action<int, string> Progress { get; set; }

        /// <summary>中止检查：返回 true 时尽快退出，不做任何保存。</summary>
        public Func<bool> Cancelled { get; set; }

        /// <summary>
        /// 附加合并规则字段（从规则文件 caxa3dtreebomaddfix.txt 读取的精确属性名）：
        /// 全部字段一致才视为同一物料；为空表示不启用附加合并。
        /// </summary>
        public List<string> MergeFields { get; set; } = new List<string>(MergeRule.DefaultFields);

        private int _visited;

        public BomBuilder(CaxaService svc, Action<string> log)
        {
            _svc = svc;
            _log = log ?? (delegate { });
        }

        private bool Stop
        {
            get
            {
                Func<bool> c = Cancelled;
                if (c == null) return false;
                try { return c(); } catch { return false; }
            }
        }

        private void Report(string msg)
        {
            Action<int, string> p = Progress;
            if (p != null)
            {
                try { p(_visited, msg); } catch { }
            }
        }

        // ==================== 主流程 ====================

        /// <summary>从给定根节点生成树形 BOM 行集合（根的层级编码依次为 1、2、3…）。</summary>
        public List<BomRow> Build(IList<IZElement> roots)
        {
            var rows = new List<BomRow>();
            _propCache.Clear();
            _srcCache.Clear();
            _warned.Clear();
            _visited = 0;

            if (roots == null || roots.Count == 0)
            {
                _log("未找到可遍历的零部件。");
                return rows;
            }

            string docPath = _svc.CurrentFilePath;
            if (string.IsNullOrEmpty(docPath))
                _log("⚠️ 当前场景尚未保存到磁盘，工程图判定与缩略图将不可用。");

            for (int i = 0; i < roots.Count; i++)
            {
                if (Stop) { _log("已按用户请求停止生成 BOM（未保存任何结果）。"); break; }

                IZElement root = roots[i];
                string code = (i + 1).ToString();
                bool isLast = (i == roots.Count - 1);

                var row = NewRow(code, 1, root, 1, true, docPath, new List<bool>(), isLast);
                row.Multiplicity = 1;
                rows.Add(row);
                _visited++;

                if (!CaxaService.IsAssemblyElement(root)) continue;

                Recurse(root, code, row.Multiplicity, new List<bool>(), docPath, rows);
            }

            ApplyTotals(rows);

            _log("BOM 生成完成：共 " + rows.Count + " 行（含根节点），遍历节点 " + _visited + " 个。");
            return rows;
        }

        /// <summary>递归遍历某个装配体的全部子件。</summary>
        private void Recurse(IZElement parent, string codePrefix, int parentMult,
                             List<bool> ancestorLast, string docPath, List<BomRow> rows)
        {
            if (Stop) return;

            List<IZElement> children = _svc.GetChildren(parent);
            if (children.Count == 0) return;

            // 同层内按“外部链接文件 + 元素名称”合并同一种子件（保持首次出现顺序）
            var groups = new List<ChildGroup>();
            var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (IZElement child in children)
            {
                if (Stop) return;
                if (!CaxaService.IsPartKind(child)) continue;

                if (!CaxaService.IncludedInBom(child))
                {
                    string skipName = CaxaService.SafeElementName(child);
                    if (_warned.Add("skip|" + skipName))
                        _log("⚠️ 跳过不参与 BOM 的元素：" + skipName);
                    continue;
                }

                string name = CaxaService.SafeElementName(child);
                string file = CaxaService.GetLinkedFile(child);
                string key = (string.IsNullOrEmpty(file) ? "virtual|" : file) + "|" + name;

                int gi;
                if (lookup.TryGetValue(key, out gi))
                {
                    groups[gi].Count++;
                }
                else
                {
                    lookup[key] = groups.Count;
                    groups.Add(new ChildGroup
                    {
                        Element = child,
                        Name = name,
                        File = file,
                        IsAssembly = CaxaService.IsAssemblyElement(child),
                        Count = 1
                    });
                }
            }

            int n = 1;
            int total = groups.Count;
            foreach (ChildGroup g in groups)
            {
                if (Stop) return;

                string code = codePrefix + "." + n;
                int level = code.Split('.').Length;
                bool isLast = n == total;

                var row = NewRow(code, level, g.Element, g.Count, g.IsAssembly, docPath, ancestorLast, isLast);
                row.Multiplicity = parentMult * g.Count;
                rows.Add(row);
                _visited++;

                if (g.IsAssembly)
                {
                    var nextAncestors = new List<bool>(ancestorLast);
                    nextAncestors.Add(isLast);
                    Recurse(g.Element, code, row.Multiplicity, nextAncestors, docPath, rows);
                }

                Report("处理 " + g.Name);
                n++;
            }
        }

        // ==================== 行构造 ====================

        private BomRow NewRow(string code, int level, IZElement elem, int qty, bool isAsm,
                              string docPath, List<bool> ancestorLast, bool isLast)
        {
            string name = CaxaService.SafeElementName(elem);
            string file = CaxaService.GetLinkedFile(elem);

            var row = new BomRow
            {
                Code = code,
                TreeText = BuildTreeText(code, level, ancestorLast, isLast),
                Level = level,
                FilePath = file,
                FileName = string.IsNullOrEmpty(file) ? "" : SafeFileName(file),
                ElementName = name,
                Quantity = qty,
                IsAssembly = isAsm,
                Element = elem
            };

            string checkFile = file;
            if (string.IsNullOrEmpty(checkFile) && level == 1) checkFile = docPath;
            row.HasDrawing = CheckDrawing(level, checkFile);

            Dictionary<string, string> srcs;
            Dictionary<string, string> props = ReadPropsCached(elem, file, name, out srcs);
            foreach (var kv in props) row.Properties[kv.Key] = kv.Value;
            foreach (var kv in srcs) row.Sources[kv.Key] = kv.Value;

            row.PartNumber = FindPartNumber(row, elem);
            return row;
        }

        private Dictionary<string, string> ReadPropsCached(IZElement elem, string file, string name)
        {
            Dictionary<string, string> ignore;
            return ReadPropsCached(elem, file, name, out ignore);
        }

        /// <summary>同一分组键（外链文件＋元素名）只向宿主读一次，返回值＝属性值表，srcOut＝同键的取值来源表。</summary>
        private Dictionary<string, string> ReadPropsCached(IZElement elem, string file, string name,
                                                           out Dictionary<string, string> srcOut)
        {
            string key = (string.IsNullOrEmpty(file) ? "virtual|" : file) + "|" + name;
            Dictionary<string, string> props;
            Dictionary<string, string> srcs;
            if (_propCache.TryGetValue(key, out props) && _srcCache.TryGetValue(key, out srcs))
            {
                srcOut = srcs;
                return props;
            }

            props = _svc.ReadRowProperties(elem, PropertyHeaders, out srcs);
            _propCache[key] = props;
            _srcCache[key] = srcs;
            srcOut = srcs;
            return props;
        }

        /// <summary>
        /// 把逐行来源汇总成「每个表头列的主要来源」（供日志汇总行与列头提示用）。
        /// 口径＝该列**有值**的行里出现次数最多的来源串；同票时按 属性 &gt; BOM &gt; 专用 的优先级定；
        /// 该列所有行都没值＝空串（第三态，与「取到但空白」分开记）。
        /// </summary>
        public Dictionary<string, string> SummarizeSources(IEnumerable<BomRow> rows)
        {
            var summary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (rows == null) return summary;

            var perHeader = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

            foreach (BomRow r in rows)
            {
                if (r == null) continue;
                foreach (string h in PropertyHeaders)
                {
                    string key = (h ?? "").Trim();
                    if (key.Length == 0) continue;

                    string src;
                    if (!r.Sources.TryGetValue(h, out src) && !r.Sources.TryGetValue(key, out src)) src = "";
                    src = (src ?? "").Trim();
                    if (src.Length == 0) continue;   // 没来源＝这一格是第三态，不参与“主要来源”投票

                    Dictionary<string, int> bucket;
                    if (!perHeader.TryGetValue(key, out bucket))
                    {
                        bucket = new Dictionary<string, int>(StringComparer.Ordinal);
                        perHeader[key] = bucket;
                    }
                    int old;
                    bucket.TryGetValue(src, out old);
                    bucket[src] = old + 1;
                }
            }

            foreach (string h in PropertyHeaders)
            {
                string key = (h ?? "").Trim();
                if (key.Length == 0) continue;
                Dictionary<string, int> bucket;
                if (!perHeader.TryGetValue(key, out bucket) || bucket.Count == 0)
                {
                    summary[h] = "";
                    continue;
                }
                string best = "";
                int bestScore = 0, bestRank = int.MaxValue;
                foreach (var kv in bucket)
                {
                    int rank = SourceRank(kv.Key);
                    if (kv.Value > bestScore || (kv.Value == bestScore && rank < bestRank))
                    {
                        best = kv.Key; bestScore = kv.Value; bestRank = rank;
                    }
                }
                summary[h] = best;
            }
            return summary;
        }

        /// <summary>同票时的优先级：属性(0) &gt; BOM(1) &gt; 专用(2) &gt; 其它(3)。</summary>
        private static int SourceRank(string src)
        {
            if (string.Equals(src, "属性", StringComparison.Ordinal)) return 0;
            if (src.StartsWith("BOM:", StringComparison.Ordinal)) return 1;
            if (src.StartsWith("专用:", StringComparison.Ordinal)) return 2;
            return 3;
        }

        private static string FindPartNumber(BomRow row, IZElement elem)
        {
            string pn = "";
            foreach (var kv in row.Properties)
            {
                if (CaxaService.StripScPrefix(kv.Key) == "图号" && !string.IsNullOrWhiteSpace(kv.Value))
                {
                    pn = kv.Value.Trim();
                    break;
                }
            }
            if (string.IsNullOrEmpty(pn) && elem != null)
            {
                string bomPn, bomDesc;
                CaxaService.GetBomSetting(elem, out bomPn, out bomDesc);
                pn = bomPn;
            }
            return pn;
        }

        /// <summary>层级列的树形前缀："│   " / "    " 组合 + "├─ "/"└─ "。</summary>
        private static string BuildTreeText(string code, int level, List<bool> ancestorLast, bool isLast)
        {
            if (level <= 1) return code;

            var sb = new StringBuilder();
            for (int i = 0; i < ancestorLast.Count; i++)
                sb.Append(ancestorLast[i] ? "    " : "│   ");
            sb.Append(isLast ? "└─ " : "├─ ");
            sb.Append(code);
            return sb.ToString();
        }

        private static string SafeFileName(string path)
        {
            try { return Path.GetFileName(path); }
            catch { return path; }
        }

        // ==================== 属性回读 ====================

        /// <summary>
        /// 重新读取已有行的表头属性与工程图标记（用户可能在 CAXA 里改过属性），
        /// 同分组键只读一次；返回实际处理的行数。
        /// </summary>
        public int RefreshProperties(List<BomRow> rows)
        {
            if (rows == null || rows.Count == 0) return 0;
            _propCache.Clear();
            _srcCache.Clear();

            int changed = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                if (Stop) break;

                BomRow row = rows[i];
                IZElement elem = row.Element as IZElement;

                if (elem != null)
                {
                    Dictionary<string, string> props = ReadPropsCached(elem, row.FilePath, row.ElementName);
                    row.Properties.Clear();
                    foreach (var kv in props) row.Properties[kv.Key] = kv.Value;
                    row.PartNumber = FindPartNumber(row, elem);
                }
                else if (_warned.Add("lost|" + row.Code))
                {
                    _log("⚠️ " + row.Code + " " + row.ElementName + " 对应的元素已失效，属性保持不变。");
                }

                row.HasDrawing = CheckDrawing(row.Level, row.FilePath);
                changed++;
                Report("回读属性 " + (i + 1) + "/" + rows.Count);
            }
            if (!Stop) ApplyTotals(rows);
            return changed;
        }

        // ==================== 数量汇总 ====================

        /// <summary>按当前规则重新汇总总数量（供界面切换合并规则后即时重算）。</summary>
        public void RecomputeTotals(List<BomRow> rows)
        {
            if (rows == null || rows.Count == 0) return;
            ApplyTotals(rows);
        }

        /// <summary>
        /// 汇总总数量。
        /// 基础规则：同名件（文件名 / 虚拟件用元素名，忽略大小写）累计，首次出现的行填数值，其余填 "---"。
        /// 附加规则（AddMergeRule=true）：SC图号/SC名称/SC规格/SC材料 四项全部一致（区分大小写、
        /// 缺一项即不合并、禁止用其它字段兜底）才视为同一物料，合并总数量。
        /// </summary>
        private void ApplyTotals(List<BomRow> rows)
        {
            // ① 基础规则：同名件累计
            var baseTotals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (BomRow r in rows)
            {
                string key = TotalsKey(r);
                if (string.IsNullOrEmpty(key)) continue;
                baseTotals[key] = baseTotals.TryGetValue(key, out int v) ? v + r.Multiplicity : r.Multiplicity;
            }

            // ② 附加规则：规则字段签名累计（Ordinal = 区分大小写）
            Dictionary<string, int> signTotals = null;
            List<string> fields = MergeFields;
            if (fields != null && fields.Count > 0)
            {
                signTotals = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (BomRow r in rows)
                {
                    string sign = MergeSignature(r);
                    if (sign == null) continue;
                    signTotals[sign] = signTotals.TryGetValue(sign, out int v) ? v + r.Multiplicity : r.Multiplicity;
                }
            }

            var shownBase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var shownSign = new HashSet<string>(StringComparer.Ordinal);

            foreach (BomRow r in rows)
            {
                if (signTotals != null)
                {
                    string sign = MergeSignature(r);
                    if (sign != null)
                    {
                        if (shownSign.Add(sign))
                        {
                            r.ShowTotal = true;
                            r.TotalQuantity = signTotals[sign];
                        }
                        else r.ShowTotal = false;
                        continue;
                    }
                    // 属性不全：不参与附加合并，回落到基础规则
                }

                string key = TotalsKey(r);
                if (string.IsNullOrEmpty(key))
                {
                    r.ShowTotal = true;
                    r.TotalQuantity = r.Quantity;
                    continue;
                }
                if (shownBase.Add(key))
                {
                    r.ShowTotal = true;
                    r.TotalQuantity = baseTotals[key];
                }
                else r.ShowTotal = false;
            }
        }

        /// <summary>
        /// 附加合并签名：规则字段必须都存在且非空才返回签名（缺一项返回 null）。
        /// 严格按属性值比对，用 \u0001 分隔避免串值，禁止任何兜底取值。
        /// </summary>
        private string MergeSignature(BomRow r)
        {
            List<string> fields = MergeFields;
            if (fields == null || fields.Count == 0) return null;

            var sb = new StringBuilder();
            foreach (string f in fields)
            {
                string v;
                if (!r.Properties.TryGetValue(f, out v)) return null;
                if (string.IsNullOrWhiteSpace(v)) return null;
                sb.Append(v.Trim()).Append('\u0001');
            }
            return sb.ToString();
        }

        private static string TotalsKey(BomRow r)
        {
            if (!string.IsNullOrEmpty(r.FileName)) return r.FileName;
            return r.ElementName;
        }

        /// <summary>
        /// 是否有工程图：先查模型同级同名 .exb；
        /// 第一行装配体（层级 1）再查同名 ics 文件夹内的同名 .exb。
        /// </summary>
        private bool CheckDrawing(int level, string modelFile)
        {
            string file = modelFile;
            if (string.IsNullOrEmpty(file) && level == 1)
            {
                try { file = _svc.CurrentFilePath; } catch { }
            }
            if (CaxaService.HasDrawing(file)) return true;
            if (level == 1) return CaxaService.HasDrawingInAssemblyFolder(file);
            return false;
        }

        // ==================== 缩略图 ====================

        /// <summary>为 BOM 中每个唯一文件抓取缩略图，并回填到所有同文件行。</summary>
        public int CaptureThumbnails(List<BomRow> rows)
        {
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (BomRow r in rows)
            {
                string key = string.IsNullOrEmpty(r.FileName) ? r.ElementName : r.FileName;
                if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(r.FilePath)) continue;
                if (!byName.ContainsKey(key)) byName[key] = r.FilePath;
            }

            string docPath = _svc.CurrentFilePath;
            int ok = 0, fail = 0;

            foreach (var kv in byName)
            {
                if (Stop) { _log("已按用户请求停止抓取缩略图（未保存任何结果）。"); break; }

                string err;
                string png = _svc.CaptureThumbnail(kv.Value, out err);
                if (!string.IsNullOrEmpty(png))
                {
                    foreach (BomRow r in rows)
                    {
                        string key = string.IsNullOrEmpty(r.FileName) ? r.ElementName : r.FileName;
                        if (string.Equals(key, kv.Key, StringComparison.OrdinalIgnoreCase))
                            r.ThumbnailPath = png;
                    }
                    ok++;
                }
                else
                {
                    fail++;
                    _log("缩略图抓取失败：" + kv.Key + "（" + err + "）");
                }
                Report("缩略图 " + (ok + fail) + "/" + byName.Count);
            }

            // 顶层场景行（未保存或顶层无外链）尝试用当前场景文件补抓
            if (!string.IsNullOrEmpty(docPath) && File.Exists(docPath))
            {
                foreach (BomRow r in rows)
                {
                    if (r.Level != 1 || !string.IsNullOrEmpty(r.ThumbnailPath) || !string.IsNullOrEmpty(r.FilePath)) continue;
                    string err;
                    string png = _svc.CaptureThumbnail(docPath, out err);
                    if (!string.IsNullOrEmpty(png)) r.ThumbnailPath = png;
                }
            }

            _log("缩略图抓取完成：成功 " + ok + " 个，失败 " + fail + " 个。");
            return ok;
        }

        private class ChildGroup
        {
            public IZElement Element;
            public string Name;
            public string File;
            public bool IsAssembly;
            public int Count;
        }
    }

    /// <summary>
    /// 附加合并规则文件（caxa3dtreebomaddfix.txt）的定位与解析。
    /// 解析规则：只抓取引号 " " 之间的字段名；以 # 开头的行是说明，整行忽略
    /// （说明里即使出现引号也不会被当成规则，做到说明与规则隔离）。
    /// </summary>
    [ComVisible(false)]
    internal static class MergeRule
    {
        /// <summary>规则文件首行说明（说明中含 ""，靠 # 行隔离，不参与解析）。</summary>
        public const string Header = "#规则说明,抓取\"\"之间的字段#";

        /// <summary>规则文件缺省内容使用的字段。</summary>
        public static readonly string[] DefaultFields = { "SC图号", "SC名称", "SC规格", "SC材料" };

        /// <summary>用户可编辑的规则文件：%APPDATA%\caxa3dTreeBom\caxa3dtreebomaddfix.txt。</summary>
        public static string RuleFilePath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "caxa3dTreeBom");
                return Path.Combine(dir, "caxa3dtreebomaddfix.txt");
            }
        }

        /// <summary>确保规则文件存在（优先从程序目录播种同名文件，缺失时按默认规则生成）。</summary>
        public static string EnsureFile()
        {
            string path = RuleFilePath;

            // 合并到工具箱后没有独立程序目录：种子优先取插件 DLL 所在目录
            string seedDir = Path.GetDirectoryName(typeof(MergeRule).Assembly.Location);
            if (string.IsNullOrEmpty(seedDir)) seedDir = AppDomain.CurrentDomain.BaseDirectory;

            try
            {
                if (File.Exists(path)) return path;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string seed = Path.Combine(seedDir, "caxa3dtreebomaddfix.txt");
                if (!File.Exists(seed)) seed = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "caxa3dtreebomaddfix.txt");
                if (File.Exists(seed)) File.Copy(seed, path, false);
                else File.WriteAllText(path, BuildDefaultText(), new UTF8Encoding(true));
            }
            catch
            {
                // 用户目录不可写时退化为插件目录
                try
                {
                    path = Path.Combine(seedDir, "caxa3dtreebomaddfix.txt");
                    if (!File.Exists(path)) File.WriteAllText(path, BuildDefaultText(), new UTF8Encoding(true));
                }
                catch { }
            }
            return path;
        }

        public static string BuildDefaultText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(Header);
            foreach (string f in DefaultFields) sb.AppendLine("\"" + f + "\"");
            return sb.ToString();
        }

        /// <summary>读取规则字段：跳过 # 说明行，只取引号内文本；无引号的行忽略。</summary>
        public static List<string> Load(string path)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return list;

            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch { return list; }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in lines)
            {
                if (raw == null) continue;
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line[0] == '#') continue;   // 说明行整行隔离

                foreach (string token in ExtractQuoted(line))
                {
                    string f = token.Trim();
                    if (f.Length == 0) continue;
                    if (seen.Add(f)) list.Add(f);
                }
            }
            return list;
        }

        private static List<string> ExtractQuoted(string line)
        {
            var result = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                int start = line.IndexOf('"', i);
                if (start < 0) break;
                int end = line.IndexOf('"', start + 1);
                if (end < 0) break;
                result.Add(line.Substring(start + 1, end - start - 1));
                i = end + 1;
            }
            return result;
        }
    }
}
