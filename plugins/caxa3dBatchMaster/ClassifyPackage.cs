// 「分类打包」引擎（11-10 用户指令）：按用户输入的自定义属性，把表格列表里的零部件分组复制到指定文件夹。
//   · 属性值来自表格第 0 行表头同名的列（与「图号分离」/「属性批量读取」同一口径，H 列起）；
//     本功能不派发宿主命令、不读写 3D 属性、不打开/保存任何文档 —— 数据源是表格 + 磁盘文件，
//     ICAPI 只用来取「当前打开的设计环境零件名」（CaxaBatchService.ActiveDocPartName）。
//   · 指定了属性名：每个不同属性值建一个同名子文件夹（非法字符清洗），ics + exb 一起复制；
//     属性值为空的行归到「未填写」，一件都不丢。
//   · 没指定属性名：全部复制到一个与「打开的设计环境零件名」同名的子文件夹。
//   · 判成功只看权威副作用信号：目标文件存在且 Length > 0（不看 File.Copy 返回，它返回 void）。
//   · 绝不覆盖目标里已有的文件：内容相同=跳过，内容不同=另存「(2)」副本，全部落 CSV 清单。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace caxa3dBatchMaster
{
    /// <summary>一次分类打包的结果（给弹窗状态行用；明细一律进日志 + CSV）。</summary>
    // public：PopupClassifyPackage 的 public 构造函数把它放进委托签名，可见性必须一致（否则 CS0051）
    public sealed class ClassifyResult
    {
        public bool Ok;
        public string Message = "";
        public int Rows;
        public int Groups;
        public int CopiedIcs;
        public int CopiedExb;
        public int MissingIcs;
        public int MissingExb;
        public int Skipped;
        public int Renamed;
        public int Failed;
        // 11-10 追加：格式转换（参考 caxa3dButton「批量格式转换」）
        public int ConvertParts;   // 跑了转换的件数
        public int ConvertSkip;    // 因图纸环境未预热等被跳过的件数
        public string CsvPath = "";
    }

    internal sealed class ClassifyPackage
    {
        private readonly DataGridView _grid;
        private readonly CaxaBatchService _svc;
        private readonly Action<string> _log;
        private readonly Func<bool> _stop;

        private const string EmptyGroup = "未填写";
        private static readonly char[] IllegalChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };
        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        public ClassifyPackage(DataGridView grid, CaxaBatchService svc, Action<string> log, Func<bool> stop)
        {
            _grid = grid;
            _svc = svc;
            _log = log;
            _stop = stop;
        }

        private int RowCount => _grid.AllowUserToAddRows ? _grid.Rows.Count - 1 : _grid.Rows.Count;
        private string Cell(int r, int c)
        {
            if (r < 0 || r >= _grid.Rows.Count || c < 0 || c >= _grid.ColumnCount) return "";
            return _grid.Rows[r].Cells[c].Value?.ToString() ?? "";
        }

        private bool ShouldStop()
        {
            try { Application.DoEvents(); } catch { }   // 铁律 6：长流程泵消息，停止键可点
            return _stop != null && _stop();
        }

        private void Log(string msg) { try { _log?.Invoke(msg); } catch { } }

        // ============================================================ 入口

        public ClassifyResult Run(string destRoot, string propName, ConvertOptions opt)
        {
            var res = new ClassifyResult();
            opt = opt ?? new ConvertOptions();   // 兜空：默认复制 ics+exb，不转换
            propName = (propName ?? "").Trim();
            destRoot = (destRoot ?? "").Trim();

            if (string.IsNullOrEmpty(destRoot))
            {
                res.Message = "没有指定存放文件夹，分类打包未执行。";
                Log("分类打包中止：" + res.Message);
                return res;
            }
            try
            {
                Directory.CreateDirectory(destRoot);
                if (!Directory.Exists(destRoot)) throw new IOException("目录创建后仍不存在");
            }
            catch (Exception ex)
            {
                res.Message = "存放文件夹不可用：" + ex.Message;
                Log("分类打包中止：" + res.Message);
                return res;
            }

            if (RowCount <= 1)
            {
                res.Message = "表格里没有数据行，分类打包未执行。";
                Log("分类打包中止：" + res.Message);
                return res;
            }

            // ---- 1. 属性列定位（表头＝第 0 行文字，H 列起；精确优先，唯一包含匹配兜底） ----
            int propCol = -1;
            if (propName.Length > 0)
            {
                propCol = FindPropertyColumn(propName);
                if (propCol < 0)
                {
                    var names = new List<string>();
                    for (int c = TableService.PropertyColumnStart; c < _grid.ColumnCount; c++)
                    {
                        string h = Cell(0, c).Trim();
                        if (h.Length > 0 && names.Count < 20) names.Add(h);
                    }
                    res.Message = "表格里找不到属性列「" + propName + "」，分类打包未执行。";
                    Log("分类打包中止：" + res.Message
                        + " 现有属性列：" + (names.Count > 0 ? string.Join("、", names.ToArray()) : "（无）")
                        + "。属性值取自表格列，请先点「属性批量读取」或核对表头。");
                    return res;
                }
                Log("分类打包开始：属性列＝第 " + (propCol + 1) + " 列「" + Cell(0, propCol).Trim()
                    + "」｜存放文件夹＝" + destRoot);
            }
            else
            {
                Log("分类打包开始：未指定属性名 ⇒ 全部列表文件复制到「打开的设计环境零件名」同名文件夹｜存放文件夹＝" + destRoot);
            }

            // ---- 2. 未指定属性时的单一文件夹名 ----
            string allGroupName = "";
            if (propCol < 0)
            {
                allGroupName = SafeFolderName(DesignEnvName(out string src));
                Log("设计环境零件名＝「" + allGroupName + "」（来源：" + src + "）⇒ 全部文件进这一个文件夹。");
            }

            // ---- 3. 逐行复制 ----
            var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 原值 → 实际文件夹名
            bool usedEmptyGroup = false;
            var csv = new List<string>();
            csv.Add("行号,分组,零件名,扩展名,源文件,目标文件,结果,说明");

            // 源目录兜底：C 列空时，用当前设计环境文档所在目录（与表格 C 列同一口径＝ics 所在目录）
            string fallbackDir = "";
            try
            {
                string docPath = _svc?.ActiveDocFilePath ?? "";
                if (!string.IsNullOrEmpty(docPath)) fallbackDir = Path.GetDirectoryName(docPath) ?? "";
            }
            catch { }

            for (int r = 1; r < RowCount; r++)
            {
                if (ShouldStop()) { Log("已收到停止信号，分类打包中断（已复制的文件保留）。"); break; }
                if (_grid.Rows[r].IsNewRow) continue;

                string partName = Cell(r, TableService.ColPartName).Trim();
                if (partName.Length == 0) continue;
                res.Rows++;

                string rawGroup = propCol >= 0 ? Cell(r, propCol).Trim() : "";
                string group;
                if (propCol < 0) group = allGroupName;
                else
                {
                    if (rawGroup.Length == 0)
                    {
                        group = EmptyGroup;
                        usedEmptyGroup = true;
                        Log("第 " + (r + 1) + " 行「" + partName + "」的属性值为空 ⇒ 归入「" + EmptyGroup + "」。");
                    }
                    else
                    {
                        string known;
                        if (groups.TryGetValue(rawGroup, out known)) group = known;
                        else
                        {
                            group = SafeFolderName(rawGroup);
                            groups[rawGroup] = group;
                            if (!string.Equals(group, rawGroup, StringComparison.Ordinal))
                                Log("分组名「" + rawGroup + "」含非法字符或过长 ⇒ 落盘为「" + group + "」。");
                        }
                    }
                }

                string targetDir;
                try
                {
                    targetDir = Path.Combine(destRoot, group);
                    Directory.CreateDirectory(targetDir);
                    if (!Directory.Exists(targetDir)) throw new IOException("子目录创建后仍不存在");
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    Log("第 " + (r + 1) + " 行「" + partName + "」建目录失败：" + ex.Message);
                    csv.Add(CsvLine((r + 1).ToString(), group, partName, "", "", "", "失败", "建目录失败：" + ex.Message));
                    continue;
                }

                string dir = Cell(r, TableService.ColFilePath).Trim();
                if (dir.Length == 0) dir = fallbackDir;
                if (dir.Length == 0)
                {
                    res.Failed++;
                    Log("第 " + (r + 1) + " 行「" + partName + "」C 列文件路径为空且当前设计环境无路径 ⇒ 跳过。");
                    csv.Add(CsvLine((r + 1).ToString(), group, partName, "", "", "", "失败", "源目录为空"));
                    continue;
                }

                // 文件基准名：优先 D 列零件名；改名后 D 列未同步时，用 F 列「新零件名」兜底（只在其 ics 真实存在时）
                string baseName = partName;
                string ics = Path.Combine(dir, baseName + ".ics");
                if (!File.Exists(ics))
                {
                    string newName = Cell(r, TableService.ColNewPartName).Trim();
                    if (newName.Length > 0 && !string.Equals(newName, partName, StringComparison.Ordinal))
                    {
                        string alt = Path.Combine(dir, newName + ".ics");
                        if (File.Exists(alt))
                        {
                            baseName = newName;
                            ics = alt;
                            Log("第 " + (r + 1) + " 行 D 列「" + partName + "」的 ics 不存在 ⇒ 按 F 列新零件名「" + newName + "」兜底。");
                        }
                    }
                }

                foreach (string ext in new[] { ".ics", ".exb" })
                {
                    if (ShouldStop()) { Log("已收到停止信号，分类打包中断（已复制的文件保留）。"); break; }
                    // 11-10 追加：未勾选复制该源格式则跳过（ics/exb 默认都勾，保持原复制行为）
                    if (ext == ".ics" && !opt.CopyIcs) continue;
                    if (ext == ".exb" && !opt.CopyExb) continue;
                    string src = Path.Combine(dir, baseName + ext);
                    if (!File.Exists(src))
                    {
                        if (ext == ".ics") res.MissingIcs++; else res.MissingExb++;
                        csv.Add(CsvLine((r + 1).ToString(), group, partName, ext, src, "", "缺文件", "源文件不存在"));
                        continue;
                    }

                    string dst = Path.Combine(targetDir, Path.GetFileName(src));
                    string outcome, note;
                    try
                    {
                        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                        {
                            res.Skipped++;
                            outcome = "跳过"; note = "源文件与目标文件是同一个";
                        }
                        else if (File.Exists(dst))
                        {
                            var a = new FileInfo(src); var b = new FileInfo(dst);
                            if (a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc)
                            {
                                res.Skipped++;
                                outcome = "跳过"; note = "目标已有同名且内容一致";
                            }
                            else
                            {
                                dst = UniquePath(dst);
                                File.Copy(src, dst, false);
                                res.Renamed++;
                                outcome = "另存副本"; note = "目标已有同名但内容不同，保留双方：" + Path.GetFileName(dst);
                            }
                        }
                        else
                        {
                            File.Copy(src, dst, false);
                            outcome = "已复制"; note = "";
                        }

                        // 权威副作用信号：目标存在且非空（不看 API 返回）
                        if (outcome == "已复制" || outcome == "另存副本")
                        {
                            var fi = new FileInfo(dst);
                            if (!fi.Exists || fi.Length <= 0) throw new IOException("复制后目标不存在或为空");
                            if (ext == ".ics") res.CopiedIcs++; else res.CopiedExb++;
                        }
                    }
                    catch (Exception ex)
                    {
                        res.Failed++;
                        outcome = "失败"; note = ex.Message;
                        Log("第 " + (r + 1) + " 行「" + partName + "」复制 " + ext + " 失败：" + ex.Message);
                    }
                    csv.Add(CsvLine((r + 1).ToString(), group, partName, ext, src, dst, outcome, note));
                }

                // ---- 3.5 格式转换（参考 caxa3dButton「批量格式转换」）：3D 用 RCW 直导，2D 走图纸环境 CRX 桥 ----
                // 源读 srcDir（原件所在目录），产物落 outDir（打包目标文件夹），两者可能不同（未勾复制时尤其）。
                if (opt.AnyConvert && !ShouldStop())
                {
                    res.ConvertParts++;
                    string conv;
                    try
                    {
                        conv = new FormatConverter(_svc.App, _log, _stop).ConvertOne(dir, targetDir, baseName, opt);
                        Log("第 " + (r + 1) + " 行「" + partName + "」格式转换：" + conv);
                    }
                    catch (Exception ex)
                    {
                        res.ConvertSkip++;
                        conv = "异常(" + ex.Message + ")";
                        Log("第 " + (r + 1) + " 行「" + partName + "」格式转换异常：" + ex.Message);
                    }
                    csv.Add(CsvLine((r + 1).ToString(), group, partName, "", "", "", "格式转换", conv));
                }

                if (ShouldStop()) break;
            }

            res.Groups = propCol < 0 ? (res.Rows > 0 ? 1 : 0) : groups.Count + (usedEmptyGroup ? 1 : 0);

            // ---- 4. CSV 清单（权威证据，UTF-8 BOM，Excel 直接打开不乱码） ----
            try
            {
                // 精度到毫秒：同一秒内用户连点两次「执行分类打包」也不会把上一份清单覆盖掉（离屏实测 MODE 3/4 暴露）
                string csvPath = Path.Combine(destRoot, "分类打包清单-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".csv");
                using (var sw = new StreamWriter(csvPath, false, new UTF8Encoding(true)))
                    foreach (string line in csv) sw.WriteLine(line);
                res.CsvPath = csvPath;
            }
            catch (Exception ex) { Log("分类打包清单写入失败（不影响已复制的文件）：" + ex.Message); }

            res.Ok = true;
            res.Message = "分类打包结束：处理 " + res.Rows + " 行｜分组 " + res.Groups
                + " 个｜复制 ics " + res.CopiedIcs + " / exb " + res.CopiedExb
                + "｜同名另存 " + res.Renamed + "｜跳过 " + res.Skipped
                + "｜缺文件 ics " + res.MissingIcs + " / exb " + res.MissingExb
                + "｜失败 " + res.Failed
                + (opt.AnyConvert ? "｜格式转换 " + res.ConvertParts + " 件" + (res.ConvertSkip > 0 ? "（跳过 " + res.ConvertSkip + "）" : "") : "")
                + (res.CsvPath.Length > 0 ? "｜清单 " + res.CsvPath : "");
            Log(res.Message);
            Log("说明：属性名留空＝按「打开的设计环境零件名」建单一文件夹全量复制；"
                + "属性名指定后＝按该属性值分文件夹。属性值取自表格表头同名列，未先「属性批量读取」的表请先读一次。"
                + "同名不同内容一律另存副本，不覆盖任何已有文件。"
                + "勾选了 2D 转换（dwg/dxf/pdf）而图纸环境未预热时，转换会跳过并提示：请先在主窗体点「图纸环境预热」。");
            if (opt.Any3D) Log("3D 转换（x_t/stp/sat）走 RCW 直导（ics 第一个对象），不依赖图纸环境；产物与 ics/exb 落同一目标文件夹。");
            return res;
        }

        // ============================================================ 属性列定位

        private int FindPropertyColumn(string propName)
        {
            // ① 精确（去首尾空格、忽略大小写）
            for (int c = TableService.PropertyColumnStart; c < _grid.ColumnCount; c++)
                if (string.Equals(Cell(0, c).Trim(), propName, StringComparison.OrdinalIgnoreCase)) return c;

            // ② 唯一包含匹配（例如输入「材料」而表头是「SC材料」）；多命中不猜，交回用户
            int hit = -1, count = 0;
            for (int c = TableService.PropertyColumnStart; c < _grid.ColumnCount; c++)
            {
                string h = Cell(0, c).Trim();
                if (h.Length == 0) continue;
                if (h.IndexOf(propName, StringComparison.OrdinalIgnoreCase) >= 0
                    || propName.IndexOf(h, StringComparison.OrdinalIgnoreCase) >= 0)
                { hit = c; count++; }
            }
            if (count == 1)
            {
                Log("属性列精确匹配没命中，唯一包含匹配落到「" + Cell(0, hit).Trim() + "」（第 " + (hit + 1) + " 列）。");
                return hit;
            }
            if (count > 1) Log("属性名「" + propName + "」包含匹配命中 " + count + " 列，不猜、不打包，请写完整列名。");
            return -1;
        }

        // ============================================================ 设计环境零件名

        /// <summary>优先取活动文档名（IZDoc.get_Name 槽 11）；取不到退回表格第 2 行＝当前装配体自身。</summary>
        private string DesignEnvName(out string source)
        {
            string n = "";
            try { n = _svc?.ActiveDocPartName ?? ""; } catch { }
            if (!string.IsNullOrWhiteSpace(n)) { source = "活动文档名（IZDoc.get_Name，槽 11）"; return n.Trim(); }

            string row2 = Cell(1, TableService.ColPartName).Trim();
            if (!string.IsNullOrEmpty(row2)) { source = "表格第 2 行装配体（活动文档名不可得的兜底）"; return row2; }
            source = "活动文档名与表格第 2 行均不可得，使用占位名";
            return "未命名设计环境";
        }

        // ============================================================ 文件名 / 路径安全

        /// <summary>属性值 → 合法文件夹名：非法字符换下划线、去首尾空白与点、避开 Windows 保留名、限长 80。</summary>
        private static string SafeFolderName(string raw)
        {
            string s = (raw ?? "").Trim();
            foreach (char ch in IllegalChars) s = s.Replace(ch, '_');
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s) if (ch >= 32 && ch != 127) sb.Append(ch);
            s = sb.ToString().Trim().TrimEnd('.', ' ');
            if (s.Length > 80) s = s.Substring(0, 80).TrimEnd('.', ' ');
            if (s.Length == 0) return EmptyGroup;
            foreach (string r in ReservedNames)
                if (string.Equals(s, r, StringComparison.OrdinalIgnoreCase)) return "_" + s;
            return s;
        }

        /// <summary>同名不同内容时生成「名 (2).ext」「名 (3).ext」…，绝不覆盖。</summary>
        private static string UniquePath(string path)
        {
            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, name + " (" + Guid.NewGuid().ToString("N").Substring(0, 8) + ")" + ext);
        }

        private static string CsvLine(params string[] fields)
        {
            var parts = new string[fields.Length];
            for (int i = 0; i < fields.Length; i++)
            {
                string v = fields[i] ?? "";
                if (v.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
                    v = "\"" + v.Replace("\"", "\"\"") + "\"";
                parts[i] = v;
            }
            return string.Join(",", parts);
        }
    }
}
