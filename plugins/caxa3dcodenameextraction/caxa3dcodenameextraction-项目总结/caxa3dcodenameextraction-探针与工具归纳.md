# caxa3dcodenameextraction-探针与工具归纳

## 11. 2026-10-31 合并外部工具（最终态：钣金件尺寸填写）

### 需求演进
1. 先把独立工具 `caxa3dsizeboxread` 的「读取最小包围盒」按钮并入本插件，位置在「钣金件设计环境设默认」正下方（里程碑 19）。
2. 用户裁定：**该按钮替换为独立工具 `caxa3dsheetsizewrite` 的「钣金件尺寸填写」**，原只读功能不再暴露（里程碑 20）。

### 当前实现
- `SizeBoxTool.cs`（内部依赖，保留）：从 `caxa3dsizeboxread` 迁入，包围盒口径与实测槽位结论不变：
  - 零件：`IZPart.GetBody(true)` → `IZBody.GetBoundingBox()`（槽 13）/ `GetTightBoundingBox()`（槽 21），局部 6 值用 `GetTransformToGlobal` + `IZMathPoint.TransformBy`（槽 16）变换 8 角点后再求世界 AABB；
  - 装配体：`IZAssembly.GetBoundingBox(false)`（槽 53，世界坐标）；兜底 `IZSizeBox.Length/Width/Height`；
  - 返回 VARIANT 统一按 `minX,minY,minZ,maxX,maxY,maxZ` 解包，兼容 `float[]`/`double[]`/`object[]`/`ZArray`。
- `SheetSizeTool.cs`：从 `caxa3dsheetsizewrite\SheetSizeTool.cs` 迁入（命名空间改为 `caxa3dcodenameextraction`；`RuleConfig` 改名 `SheetSizeRule`，结果类型加 `SheetSize` 前缀避免歧义）。唯一逻辑改动：`FindFlatPart` 的 5s 等待由 `CaxaAutomation.WaitUntil`（依赖其静态停止标志）改为本地 `WaitFor(condition, timeout, shouldStop)`，与本插件「停止」按钮共用 `_stopRequested`。
- 规则文件 `caxa3dsheetsizewriteaddfix.txt` 随插件输出（csproj `CopyToOutputDirectory` + `build.ps1` 的 `.txt` Extra 已覆盖），加载顺序：CAXA bin → 插件目录 → 当前目录；只取前 2 个引号值（`SC规格` / `SC材料`），后续行仅作说明，缺失时用内置默认 `SC规格/SC材料`。
- `MainForm`：按钮 `_btnSheetSize`「钣金件尺寸填写」，位置 `(214, 252, 200)`（原「读取最小包围盒」位置）；`panelBody` 高 314、窗口 `ClientSize` 620、`MinimumSize` 580（与里程碑 19 一致）。
- 流程：`BtnSheetSize_Click` 先 `SheetSizeTool.Validate`（钣金件 type=20、虚拟件、名称不含「展开」、`文件名=零件名`），不通过弹提示并写日志；通过后走 `RunLongFlow`（禁用其它按钮、启用「停止」与代点、结束统一提示），内部 `SheetSizeTool.Execute`：
  材料（`GetMaterialNameProperty`）→ 板厚（仅 `GetStockProperties`，mm）→ 找「展开<零件名>」（设计树 → `GetFlatPart` → `Unfold`）→ 紧致盒三维排序（最大=长、次大=宽、最小=厚，最小边须等于板厚 ±0.1mm 否则中止且不写属性）→ 写 `SC规格=长x宽`、`SC材料=<材料>板T=厚`（写后回读）→ Refold + 默认配置激活 → 复核属性 → `Doc.Save()` 并校验文件非空。
- `r.Saved == false` 时抛异常走「失败」分支，避免「完成」误报。

### 未迁入部分
原独立窗口及其「停止 / 检查选中 / 窗口前置 / 保存日志 / 清理日志」不迁入——本插件已有统一工具栏与日志区；「停止」复用本插件 `RunLongFlow` 的停止链路。

### 验证状态
- `dotnet build -c Release -p:Platform=x64`：0 error 0 warning。
- `build.ps1` 全量构建 + 暂存通过（DLL 与规则 txt 已进 `src\plugins\caxa3dcodenameextraction\`）。
- `caxa-slot-check.ps1`：MISMATCH 仍为基线 3 处（`SheetMetalOps.cs:66/68/69` 死常量），无新增。
- CAXA 内实点「钣金件尺寸填写」：**待用户实测**。

## 12. 2026-11-03 与独立工程 `caxa3ddiffrename` 对齐 + 功能区四分（里程碑 22）

### 12.1 文件层面（谁从哪来）
| 类 | 来源 | 说明 |
| --- | --- | --- |
| `CaxaAutomation` / `PopupAutoYes` / `Diagnostics` / `SheetMetalOps` | `caxa3ddiffrename` 同名文件整份同步 | 命名空间与项目名字符串批量替换；`caxa3ddiffrenamesheetsizewriteaddfix.txt` 改回本插件名 `caxa3dsheetsizewriteaddfix.txt` |
| `MsaaAccess` / `AutoCloseBox` / `FlowGate` / `LinkPickDialog` / `ReplaceElementProbe` / `ChangeLinksProbe` / `DiffRenameTool` | 新增 | 面板代点、提示框自动关、流程互斥、业务弹窗填写、替换/改链接两条链路、带图独立的文件复制与命令投递 |
| `MainForm` | 同步后 `BuildUi` **与参考工程一致**（只换资源 txt 名） | 其余方法（RenameCore / ReplacePartCore / UpdateDrawingLinksCore / WarmUp 系列 / RunLongFlow）与独立工程同一份实现 |
| 未同步 | `Addin.cs` / `DiffRenameForm.cs` | 集合体不需要自建 Addin 入口；独立工程的第二个工具窗口在集合体里没有第二个 Ribbon 按钮可用，功能已并入主窗口「带图独立」 |

### 12.2 窗口布局契约（最终版：照抄参考工程）
- **不再分区**。用户拿参考工程 `caxa3ddiffrename` 的实机截图定稿：「界面按这个」——
  中间那版四 `GroupBox` 方案已废弃，`BuildUi` 直接与参考工程逐行一致（只把资源 txt 名改成本插件的）。
- 结构是两层 `Dock=Top` 面板 + 填满的日志框：
  `panelTop`（44 px，8 个工具按钮 `y=8`）→ `panelBody`（316 px，`Padding=10`）→ `_txtLog(Dock=Fill)`。
- `panelBody` 内部从上到下：预热状态灯按钮（`10,6,150`）→ 原零件名 → 新零件名 →
  分离规则 + 自动图号分离 → 带图独立/重命名/状态字（`y=174`）→ 4 列 2 行按钮表格（`BtnW=238, GapX=8, Row1=218, Row2=252`）。
- 4 列按钮表格只改 `BtnW/GapX/Col0` 三处常量，不用逐个手写 `(x,y,w)`；`_lblStatus` 用 `AutoSize=true` 挂在 `_btnRename.Right+12`。
- 客户区 1000×620 / 最小 1016×580；日志区 `Dock=Fill` 吃剩余高度（省下的 150 px 全给日志）。
- **离屏取证**：用临时 WinForms 宿主（`new MainForm(null)` + `DrawToBitmap`）截图确认无裁切、无重叠；
  这是没有 CAXA 时验证布局的最省事通道（构造函数里 `TryConnect` 失败会被 catch，不影响布局渲染）。

### 12.3 新增按钮与判据口径
| 按钮 | 权威判据 | 关键约束 |
| --- | --- | --- |
| 带图独立 | 设计树里出现外链指向新 ics 的元素 | 三步链 = 替换 → 改名 → 更新图纸链接；每步用与单按钮完全相同的实现 |
| 替换零部件 | 同上（遍历设计树，不看活选择） | 面板提交后面板 3 秒关、外链真正换好约 20 秒，等待给到 120 秒 |
| 更新工程图链接文件 | CRX 日志 `DB.snapshot … +N -M` 增量 | 按**文件名**认行；对话框是模态的，驱动逻辑必须跑在后台线程 |
| 图纸环境预热 | CRX 探针出现（`CRX module loaded`） | 载体 = 同目录 `caxa3d图纸环境预热.exb`；成功后 `CMD:CLOSERAW` 当场关掉 |
| 零部件重新生成 | 元素名回读 + 界面刷新 | `Update`(7) + 钣金 `Refold`/`Activate` |

### 12.4 CRX 侧同批改动
- `CloseDocWindows` 删掉「未匹配就关全部 MDI 子窗口」的 `fallback-all`，改为只关
  「标题含目标文件名 **且** class = `AfxFrameOrView140u`」的 MDI 子窗口；匹配不上只回 `name-mismatch`，一个都不关。
- `CMD:CLOSEDOC` 空文件名直接 `closedoc-rejected`；新增 `CMD:CLOSERAW|<fileName>`（同匹配规则但不存盘）。
- 命令表补 `CX_DFT_ChangeLinks` / `EditLinks`。
- C# 侧再加一道保险：`CloseDrawingByName` 投命令前先确认目标图纸窗口真的在（3 秒），不在就只报告不动手。
- 编译入口：`build-crx.ps1` + `重编CRX.bat`（原因与 MSB6001 坑见第 12 节踩坑记录）。


