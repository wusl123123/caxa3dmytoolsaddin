# caxa3dcodenameextraction-项目总结

## 1. 项目定位
CAXA 3D 2027（x64）内置 Add-in：**带图改名及图号分离**。
选中零部件 → 输入新零件名 → 按规则拆分图号/名称等 → 回填自定义属性 → 保存 ics →
打开同目录同名 exb 并自动确认更新 → 保存并关闭图纸 → 焦点回到设计环境。

技术栈：C# / .NET Framework 4.8 / WinForms / x64；图纸环境操作由 CRX 桥（C++，crxrxEntryPoint）完成。

## 2. 目录结构
| 路径 | 作用 |
| --- | --- |
| `Addin.cs` | Add-in 入口（IZAddinServer），注册功能区按钮、隐藏主窗口 |
| `MainForm.cs` | 主界面 + 重命名主流程 + 等待原语 + 焦点回归 |
| `CaxaAutomation.cs` | CAXA COM 底层调用（vtable 槽位、选中元素、外部链接、属性、改名、保存） |
| `SizeBoxTool.cs` | 选中元素包围盒读取（迁自 `caxa3dsizeboxread`；只读，ICAPI 接口直调） |
| `SheetSizeTool.cs` | 钣金件尺寸填写核心（迁自 `caxa3dsheetsizewrite`：校验/展开件/板厚/回填/默认配置/保存） |
| `caxa3dsheetsizewriteaddfix.txt` | 尺寸回填规则（SC规格 / SC材料 / 材料字段名），随插件输出 |
| `CrxBridge.cs` | C# ↔ CRX 文件桥（请求文件 + 日志增量读取 + seq 回执） |
| `PopupAutoYes.cs` | 后台代点「是/确定」、文件对话框自动填入目标 ics |
| `caxa3dcodenameextraction-crx/` | CRX 桥工程（CX_open / CX_save / CX_DFT_ViewSave / CX_DFT_ViewClose） |
| `deploy.ps1` / `restore.ps1` | 一键安装 / 卸载（配置、清单、注册表、CRX） |
| `register-crx.ps1` / `unregister-crx.ps1` | 单独注册 / 卸载 CRX（界面按钮调用） |
| `Lib/interop.ICApiIronCAD.dll`、`Lib/ICApiAddin.snk` | CAXA 类型库、强命名密钥 |
| `caxa3d2027关键设置\` | CAXA 3D 2027 人工配置文件（ucx/属性模板/BOM/2D配置）+ 关键设置截图，随编译输出打包供人工导入/参考；安装脚本不复制进 CAXA 安装目录 |

编译：`dotnet build caxa3dcodenameextraction.csproj -c Release -p:Platform=x64`
产物：`bin\x64\Release\net48\`（Release 后处理自动 mt 生成清单 → 剥离 interop clrClass → 嵌 RT_MANIFEST #2 → sn 重签）

## 3. 关键实现要点
- **连接状态**：活动文档名 + 选中零部件名；`IZPart/IZAssembly::GetExternallyLinkedInfo` 判外部链接，失败显示「(虚拟)」。
- **重命名**：写属性（`IZCustomPropMgr::SetValueByName`）→ `IZElement::set_Name`（vtable 槽 10）→ `IZDoc::Save`（槽 9）→ CAXA 2027 依选项把本地 ics 改名。
- **工程图同步**：`IZBaseApp::OpenFile2(exb)` 打开同名 exb；“是否更新外链文件名”确认框由 PopupAutoYes 代点「是」；随后投 `CMD:CLOSEDOC|<文件名>` 给 CRX，CRX 先 CX_save 再 WM_CLOSE 关图。
- **等待信号**：CRX 日志中的 `REQ.ack | seq=N`（请求已被消费）、`close-done`、`commandEnded | CX_open_Impl`；等待一律 `WaitUntil` + `Application.DoEvents()` 轮询，绝不阻塞 STA 主线程。
- **焦点回归**：`Process.GetCurrentProcess().MainWindowHandle` + SetForegroundWindow（DLL 宿主即 IronCAD.exe）；仅 `IsIconic` 时才 `SW_RESTORE`（见教训14）。
- **重新生成（已于 2026-10-25 移除，勿再加回）**：曾用 `IZPart::Regenerate`（槽 18）/`IZAssembly::RegenerateParts`（槽 17）/`IZSceneDoc::RegenerateParts`（槽 21）刷新设计树名称，但装配体下极慢，用户要求去掉（里程碑16）。现在流程末尾只 `WaitUntil` 等回到设计环境，设计树旧名由用户手工点一次「重新生成」。
- **窗口前置**：主界面「窗口前置」按钮切换 `TopMost`，解决在 3D 环境选零部件时本窗口被压到后面。
- **交互约束**：虚拟零部件禁止重命名（提示先另存为外部 ics）；新零件名自动填入原零件名，同名 ics 已存在时按钮禁用并提示；原/新名输入框显式接管 `Ctrl+C`/`Ctrl+V`（`ProcessCmdKey`）。

## 4. 陷阱与教训（务必保留）
1. **PowerShell 脚本必须存 UTF-8 带 BOM**：PS 5.1 对无 BOM 的 UTF-8 按 ANSI 解码，中文字符串/here-string 直接被解析器打乱，报 `using System;` 一类莫名其妙的语法错误。
2. **`"$var中文.txt"` 变量名会吞掉中文**：PowerShell 变量名允许 CJK，`$addinName图号分离规则.txt` 整体被当作变量名 → 展开为空。必须写 `${addinName}图号分离规则.txt`。
3. **多插件共存铁律**：DLL 内嵌清单不能带 `interop.*` 的 `clrClass/clrSurrogate`（同名 progid 不同 CLSID → SxS 激活上下文合并失败，CAXA 起不来）。`strip-manifest.ps1` 只剥 interop，保留自己的 Addin clrClass。
4. **vtable 槽号需按元数据顺序核对**：DispId 相同的属性 get/set 各占一个槽（如 IZElement get_Name=9、set_Name=10）。本次已用 `interop.ICApiIronCAD.dll` 反射核对：IZPart::GetExternallyLinkedInfo=17、IZAssembly=21、IZDoc::Save=9。
5. **代点用 PostMessage 而非 SendMessage**：点「是」后 CAXA 常紧接着弹下一个文件对话框，同步 SendMessage 会卡在模态循环里，导致本线程再也点不到后续弹窗。
6. **本进程 MessageBox 会被自己的代点器点掉**：提示框必须放在 `PopupAutoYes.Enabled=false` 且稍等 300ms 之后再弹。
7. **图纸进程是 CAXADraft.exe**：exb 的「是否保存修改」弹窗不在 IronCAD.exe 里，代点器必须扫白名单进程（IronCAD + CAXADraft）。
8. **CRX 冷启动会删请求文件**：投递 PING 后若请求文件消失却无 ack，说明 CRX 刚 init 丢弃了它，需补投（已实现于 `OpenAndCloseDrawing`）。
9. **CAXA 运行中覆盖 DLL 可能被锁**：deploy 会明确报错并提示退出 CAXA 后重试。
10. **代点器绝不能扫进程主窗口**（trace 实测根因）：CAXA 主窗口标题形如 `CAXA 3D 实体设计2027 - [设计7.ics*]`，里面同样有 Edit、ComboBox 和名为「打开」的工具栏按钮。旧代码把所有可见窗口都当候选，于是把新 ics 路径填进主窗口的编辑框并点了主窗口的「打开」，连锁引出「选择设计元素库文件(*.icc)」→「查找外链文件」→ 真正的“是否更新外链文件名”确认框反而要人工点。修复：只处理 class==`#32770` 且标题不含 ` - [` 的窗口；填路径还要求同时存在文件名 Edit 与“文件类型”下拉。
11. **「静默换源」探针无法在插件流程外复现（2026-10-24 实测否决）**：手工把 .ics 文件改名后，CAXA 打开该零件因旧路径不存在而判定为无外链（显示为虚拟零部件），`GetExternallyLinkedInfo` 直接失败，独立「静默探针」按钮无从触发。`IZDrawingDoc`/`IZTDLinkedScene::ChangeSource` 只在「零件已指向新 ics（外链有效）而 exb 仍指向旧 ics」这一时间窗口内有意义 —— 该窗口只存在于插件重命名流程的步骤 2~3（`set_Name`+`Save`）之后、步骤 4（打开 exb）之前。故探针按钮方案已整体回退，后续若要验证只能内联进主流程。
12. **exb 变长改写必须同步修正流内绝对偏移（2026-10-24 对照组实测破案）**：`*Blk_fffffffb` 解压数据中 `"MeasureUnitType\0"`(UTF-16) 后 +36 字节处有一个 uint32 绝对偏移（基址=流起点），指向路径记录之后的图形数据结构。只改路径+双长度前缀不改它 → CAXA 打开图纸静默丢弃视图块，显示空文件。定位方法：空转组（仅重压缩）+等长改写组均正常，锁定「变长平移」层；再用插件真实改名产物（对照组）逐字节 diff，全流仅此 1 字节差异。修正后产物与 CAXA 官方保存结果**逐字节 0 差异**。
13. **OLE2 结构化存储 COM 互操作三坑**：① STATSTG 结构体数组封送必崩（0xE0434352 且异常文本都打不出），须 IntPtr + 手工偏移解析（x64: pwcsName@0/type@8/cbSize@16/共80字节）；② 子存储枚举器不立即 ReleaseComObject 会导致同名元素二次打开 0x80030005 ACCESSDENIED——先收集完再下钻；③ 流/子存储打开共享模式用 STGM_SHARE_EXCLUSIVE，DENY_WRITE 会 0x80030001。
14. **SW_RESTORE 会把最大化窗口打回普通窗口（2026-10-25 用户实测"全屏偶变窗口"破案）**：`ShowWindow(h, SW_RESTORE)` 语义是"还原最小化**或最大化**的窗口到原始大小"。焦点回归时无条件调用 → 只要 CAXA 主窗口处于最大化，流程结束就被强制还原成窗口化（非最大化时无视觉效果，故表现为"偶尔"）。修复：`if (IsIconic(h)) ShowWindow(h, SW_RESTORE);` 后再 `SetForegroundWindow`。
15. **同一行控件不要混用"跟随缩放"与"固定位置"**：规则下拉框用 `Top|Left|Right` 会随窗口拉宽吃掉整行，把它旁边的按钮挤出可视区（用户反馈"看不到按钮、框太长"）。定级方案：主输入控件固定宽度且只锚 `Top|Left`，紧随其后的按钮同样 `Top|Left`；需要贴右的按钮才用 `Top|Right`，且两者间距 >=15px。
16. **3D 侧注册表选项不控制 Draft 侧弹窗（2026-10-25 选项级静默排查定案）**：「当前有3D视图数据源更新，要更新对应所有视图吗?」由 **CAXA Draft** 弹出（文本资源在 `bin\CAXADraft\Bin\zh-CN\ResApp.dll`），而 `HKCU\Software\CAXA\CAXA 3D 29.0\Technical Drawing` 下的 `TDViewOutOfSyncDlg`、Draft 模板 `SysConfig_ID_DFT_TMPL.xml` 的 `DisShow3DDlg` 改为 0/-1 后**弹窗照旧**。教训：静默化排查第一步应先用二进制搜弹窗文本定位「弹窗归属模块」，再找该模块的配置；不要先在猜测的选项树上试值。3D 选项真实存储键名是单层 `CAXA 3D 29.0`（`CAXA 3D\29.0` 不存在）；`.ucx` 实为 ZIP，内含 `IRONCAD_Option_Config.xml`。
17. **exb 内的链接路径必须按文件名匹配，不能按全路径匹配**：同一图纸的 exb 里常存**历史绝对路径**（如 `G:\吴庶凌\...\零件.ics`），而 `IZDoc` 解析出的 `LinkedFile` 是**当前机器路径**（如 `C:\Users\strong\Desktop\...\零件.ics`），两者目录完全不同。按全路径比较会导致 0 命中 → 直改静默失败并回退老流程。判定口径：`Path.GetFileName` 后忽略大小写比较（教训来自里程碑14）。
18. **多视图 exb 的偏移修正必须做「位置映射」，不能用单 delta 近似（2026-10-25 直改打开后图形消失的根因之一）**：单个 `*Blk` 流里若有 N 条链接记录（本案例 6 条），每条各自平移；顺序是「按记录降序改写」，于是锚点（`MeasureUnitType\0`+36 处的 uint32 绝对偏移）自身位置也随之前的平移移动。旧实现用「原位置 + 一个总 delta」→ 字段写回位置错位、字段值也没按各区段累计位移换算。正确做法：`mapPos(x) = x + Σ(PathStart<k 的记录的 delta)`。
20. **大图纸 exb 的 `*Blk` 流是「分段 zlib 块容器」，绝不能当单段处理（2026-10-25「改名后 exb 打不开」根因）**：容器头 20 字节后是第 0 段 zlib，**之后每段是 `[uint32 压缩长][zlib]`**；每段解压后固定为块大小（`header[4]`，通常 262144），**有效数据长度由 `header[8]` 声明**，最后一块尾部为零填充。旧实现只解压第 0 段并整体重压回写 → 27 段的壳体图纸只剩 1 段 → CAXA 直接打不开（本案例 `*Blk_fffffffb` raw=1345616、声明 6876908、27 段）。
    正确做法：① 解压所有段后拼接，再按 `header[8]` 截断；② 回写时按 `header[4]` 分块、末块补零到块大小，头部写真实长度；③ 段数保持 27→27（不会因路径变长凭空多段）。
    验证：改写后 `*Blk` 仅 +672 字节（7 条记录各 +96），与 7 条路径长度差完全吻合；记录区外差异仅 16 字节，全部是各记录的 pathLen/reclen 字段。
21. **`MeasureUnitType` 锚点偏移修正要带值域校验**：大图纸流里同标记后面那个 uint32 可能不是偏移（本案例 5 处均为 3260625295，远超数据长度）。条件必须同时满足「值 > 首记录位置」且「值 < 数据长度」才修正，否则会把无关字段写坏。
22. **「重新生成零部件」在大装配下极慢，已从流程移除**：改名流程末尾不再调用 CAXA 重新生成（用户裁定），设计树名称若未刷新由用户手工执行一次。步骤日志同步由 7 步改为 6 步。
23. **大图纸（多段容器）直改必须回退老流程（2026-10-25 壳体定案）**：`*Blk` 解压后 >262144 字节时会被切成多段（壳体 27 段），段间含绝对偏移；CAXA 官方保存时还会重写 `*GBlock`/`*SpaceStream`/`*StyleStream`/`*DictionaryStream` 及 `*Blk` 尾部明细表（+23783 字节，远超路径增量）。**仅靠改链接记录无法完整复刻官方保存**。规则：`Patch` 预检任一流段数>1 → 返回 -1 → 主流程回退老流程。单段小图纸（折弯板）直改有效。
24. **`-12` 字段对父级记录不能平移（2026-10-25 折弯板/壳体官方对照定论）**：标准视图记录的 `-12` 是 reclen（小值，`old+delta`）；父级记录的 `-12` 是大整数（1.6 亿量级，官方保持不变）。判定：`if (oldLenField > 0 && oldLenField < 100000)` 才平移。折弯板验证产出 v@-12 与官方逐值一致。
25. **exb 格式逆向知识已归档（功能已剔除，勿再实现）**：教训12、17、18、19、20、21、23、24 以及里程碑8/13~17 记录的 exb 直改细节，**全部只作取证资料保留**。2026-10-25 用户裁定剔除该功能（里程碑18），生产代码不再含 `ExbLinkPatcher`/`ExbLinkPatch`。若将来再有人提出「不开图改 exb 链接」，先看教训23：大图纸（多段容器）CAXA 保存时会重写尾部明细表/BOM（+23783 字节），**纯记录级改写永远无法与官方产物一致**，这条路对本企业图纸不成立。

19. **exb 链接记录有两种头部布局，不能一律按 `PathStart-12` 改 reclen（2026-10-25 图形全空的真凶）**：逐字节比对「原始 exb」与「CAXA 官方改名产物」后发现：
    - 标准视图记录（本案例第 3~6 条）：`[12B 固定签名 0CDE794A…][reclen][tag=100][pathLen][路径]`，`reclen = pathLen + 46`。改 `PathStart-12` 正确。
    - 父级记录（本案例第 1~2 条）：`[16B GUID][1B][uint32][pathLen][路径]`，`PathStart-12` 落在 **GUID 中间**。按旧逻辑写入会改掉 GUID 高 2 字节 → CAXA 无法把视图块对应到模型 → **图纸能打开但一点图形都没有**（区别于「完全空文件」：空文件是长度前缀写错）。
    修正：新增 `IsStandardRecordHeader()`，仅当 `PathStart-12` 的值满足 `pathLen ≤ v ≤ pathLen+60` 时才当 reclen 改写。验证：改写后 GUID 段与原文逐字节一致，非记录区差异仅剩各记录的 pathLen/reclen 字段（13 字节，全部为预期）。
    附带发现：父级记录头部那个 uint32 字段（官方产物由 220169 变为 150537）与路径无关，暂不改写。

## 5. 里程碑记录表
| 序号 | 时间 | 类型 | 内容 |
| --- | --- | --- | --- |
| 1 | 2026-10-24 14:0x | 功能搭建 | 完成 Add-in 骨架、COM 底层（选中/外链/属性/改名/保存）、CRX 桥、deploy/restore/CRX 注册脚本，编译并部署到 2027 |
| 2 | 2026-10-24 15:5x | 坑点修复 | 双弹窗冲突：新增 icc 元素库浏览框识别 + 自动「取消」；新增虚拟零部件拦截、新名自动填原名、重名禁用按钮、Ctrl+C/V 显式接管 |
| 3 | 2026-10-24 16:2x | 坑点修复（定案） | trace 定位真根因：代点器把 CAXA 主窗口当对话框误填误点。改为只扫 `#32770` 且排除 ` - [` 主窗口、填路径需 Edit+文件类型下拉 → **代点法实测成功**，全流程无需人工点弹窗 |
| 4 | 2026-10-24 16:2x | 体验优化 | 新增「窗口前置」按钮（TopMost 开关）；成功后自动清空新名框，避免误报重名 |
| 5 | 2026-10-24 17:0x | 交付完善 | 三个 2027 人工配置文件（ucx/属性模板/BOM）随编译与部署输出；使用说明改写为完整版（含配置文件导入、前提、流程、规则、排障、卸载） |
| 6 | 2026-10-24 17:5x | 功能增强 | 改名流程收尾新增“重新生成零部件”（IZPart::Regenerate / IZAssembly::RegenerateParts，回退 IZSceneDoc::RegenerateParts），自动刷新设计树名称，无需再人工点重新生成；流程日志改为 7 步 |
| 7 | 2026-10-24 17:5x | 坑点修复 | trace 实测：`IZPart::Regenerate` 返回 hr=0 但设计树节点名不刷新——Regenerate 只重算几何，不刷浏览器缓存。人工“重新生成”命令额外触发 DocModifyNotify ×N + 视图重绘。修复：Regenerate 成功后补调 `IZSceneDoc::Update(36)` + `UpdateGraphics(83)` + `UpdateBrowser(84)`，静默 API 刷新设计树。同时 caxa2d配置.xml 加入编译输出与部署清单 |
| 8 | 2026-10-24 21:0x | 关键技术突破 | **exb 图纸外链路径外部直改完全打通**。格式破解：exb=OLE2 复合文档，链接路径在 `*BlockStg/*Blk_fffffffb`（20字节容器头[00 DE E3 3D]+zlib），路径记录=12字节固定签名+reclen+0x64标签+路径长+UTF-16 路径；变长改写须同步 reclen、pathlen 及 `MeasureUnitType` 锚点绝对偏移（教训12）。产出独立验证工具 ExbLinkPatch.exe（scan/patch/roundtrip 三命令，自动备份+读回校验）。二分实验（空转/等长/变长）+ 对照组（插件真实改名产物）逐字节验证：v2 改写结果与 CAXA 官方保存**0 差异**，用户实测打开正常、不弹「是否更新外链文件名」。CAXA 改名仅动 Blk+图纸标题，其余 15 流原样。意义：图纸链接更新可不开图、不弹窗、不走 CRX 保存，一次文件操作完成 |
| 9 | 2026-10-25 12:0x | 坑点修复+功能增强 | 修复"执行后 CAXA 主窗口偶尔从全屏变窗口"：根因 `RestoreDesignFocus` 无条件 `SW_RESTORE`（教训14），改为仅 `IsIconic` 时还原。新增「图号分离」开关按钮（规则下拉框右侧，仿窗口前置蓝/灰态）：默认开启；关闭后跳过规则段数校验与 SC 属性回填（步骤1/7），仅做改名+保存+图纸同步；规则下拉随开关联动禁用 |
| 10 | 2026-10-25 11:3x | UI修复+交付完善 | 「图号分离」按钮不可见修复：下拉框 500→380、去掉 Right 锚（教训15），按钮紧随其后左锚定（498,133）。`caxa3d2027关键设置\` 整目录加入 csproj `CopyToOutputDirectory`；deploy.ps1 增加 3.2 段整目录部署；使用说明补充该目录说明。另修复 deploy.ps1 被编辑器剥掉的 UTF-8 BOM（对应教训1）。**2026-10-25 用户已实测通过**：按钮紧邻下拉框右侧可见，开关默认开启 |
| 11 | 2026-10-25 12:1x | 交付策略调整 | **配置/截图归集进 `caxa3d2027关键设置\`，只打包不进安装目录**：用户把 4 个配置文件移入该目录；csproj 移除 4 个单独条目（目录 glob 已覆盖），deploy.ps1 3.1 清单移除 4 文件、删除 3.2 目录部署段；已清理 CAXA 2027\bin 中旧副本（4 文件+目录）。修复 deploy.ps1 双 BOM（TrimStart(0xFEFF) 后按带 BOM UTF-8 重写）。使用说明第一节改为"文件在 caxa3d2027关键设置\ 内" |
| 12 | 2026-10-25 13:0x | 优化探索（中止） | **「视图静默更新」选项级路线排查并中止**（详见第 8 节）。实测否决两个候选：3D 注册表 `TDViewOutOfSyncDlg`、Draft 模板 `DisShow3DDlg`；二进制取证定位弹窗归属 CAXA Draft `ResApp.dll`。用户裁定 A（COM `IZDrawingDoc.UpdateAll` 直调）永不可行，C 路线中止，B（CRX 图纸环境刷新视图）留作后续。已完整还原注册表与 Draft 配置，删除临时备份 |
| 13 | 2026-10-25 13:3x | 功能增强 | **新增「启用/关闭exb直改」开关**（默认**关闭**，蓝色/灰色态同图号分离按钮）：开启时改名后**不打开图纸环境**，直接由内联 `ExbLinkPatcher` 改写 exb 外链路径（等价里程碑8 验证过的工具，变长同步修正绝对偏移）；exb 内 3D 视图数据不刷新，首次打开图纸时按提示手动更新。关闭时走老流程（打开 exb → 代点 → CRX 保存关图）。直改失败（文件被占用/未找到旧链接）自动回退老流程。旧「图号分离」按钮改名为「启用/关闭图号分离功能」；规则下拉框 380→330 为两个开关按钮腾位。使用说明同步更新（第 5、6 条）。已编译并部署 |
| 14 | 2026-10-25 14:5x | 坑点修复 | **直改首次实测失败根因：exb 内链接路径与 API 返回路径目录不一致**。案例：exb 存历史路径 `G:\吴庶凌\...\SC08020102.01-03 折弯板.ics`，而 `IZDoc` 解析出的 `LinkedFile` 为 `C:\Users\strong\Desktop\SC08020103.00 P80油槽\...\SC08020102.01-03 折弯板.ics`；旧逻辑按**全路径**比较 → 未命中任何记录 → `n=0` → 自动回退老流程（trace 里出现 CRX CLOSEDOC 即此症状）。改为**按文件名（忽略大小写）匹配**（教训17），复测同一份 exb：6 处记录全部改写成功。同时按用户要求把「exb直改」默认值改为**关闭** |
| 15 | 2026-10-25 15:2x | 坑点修复 | **直改后图纸能开但看不到图形**：根因为多视图 exb（单流 6 条链接记录）时偏移修正用了「单 delta + 原位置」近似（教训18），同时旧逻辑对所有记录都改 `PathStart-12` 当 reclen，而第 1~2 条是「16B GUID」父级记录，被改坏 GUID（教训19）。修复：①位置映射 `mapPos`；②新增 `IsStandardRecordHeader()` 守卫。验证：改写后 GUID 逐字节一致、非记录区仅 13 字节预期差异；补丁耗时已加 `Diagnostics.Log` 计时。已编译部署 |
| 16 | 2026-10-25 16:3x | 坑点修复 + 流程精简 | **「改名后 exb 打不开」根因=大图纸流是分段块容器**（教训20）：壳体 exb 的 `*Blk` 有 27 段，旧实现只解压第 0 段 → 内容丢失 → 打不开。改为全段解压拼接→按声明长度截断→回写时分块补零（27→27 段）。验证：`*Blk` 仅 +672 字节（7 记录×96），声明 6876908→6877580，记录区外差异仅 16 字节（均为 pathLen/reclen）。另按用户要求**移除流程末尾的「重新生成零部件」**（装配体下极慢），日志 7 步改 6 步，使用说明同步。已生成 `Desktop\对比\直改验证D-壳体.exb` 待实测；本轮部署因 CAXA 运行中锁 DLL 未完成 |
| 17 | 2026-10-25 18:3x | 坑点修复（定案） | **大图纸直改失败最终定案：多段容器无法仅靠改链接记录复刻官方保存**。对照「原始壳体」与「CAXA 官方改名产物」逐流 diff：官方不只有改 `*Blk`(+24912)，还改 `*GBlock`(+180)、`*SpaceStream`(+6044/186→187条)、`*StyleStream`(-4)、`*DictionaryStream`(+2)；`*Blk` 记录间区域仅 +707 字节，而**末尾区域 +23783 字节**（CAXA 保存时重写尾部明细表/BOM/图纸属性），远超路径增量 7×46=322。另确认 `-12` 字段规则因文件而异：折弯板官方对父级记录也按 delta 平移，壳体官方对父级记录保持不变 → 改为「仅小值（<100000，即 reclen）才平移」。尝试后直改产物仍无法与官方一致，为大图纸不可行的实证 |
| 18 | 2026-10-25 18:5x | 功能剔除（用户裁定） | **彻底剔除「exb 直改」功能**：删除 `ExbLinkPatcher.cs`（内联直改器）与 `ExbLinkPatch/`（独立诊断工具、`OLE2` COM 互操作全套）；`MainForm` 移除 `_exbPatchEnabled` 字段、`_btnExbPatch` 按钮、`ToggleExbPatch()`/`ApplyExbPatchVisual()`、面板注册、点击绑定、以及流程 4/6 的整段直改分支与 `patchedExb` 收尾提示；步骤 4/6 统一为「打开工程图 → `OpenAndCloseDrawing`」。使用说明删除两处直改描述，改为单一流程。UI 恢复为「规则下拉 + 图号分离开关」一行。已编译（0 错误）并部署。**结论：图纸链接更新只保留老流程一条路径**，exb 格式知识（OLE2/分段容器/记录头/偏移）仅作为取证记录保留在本总结与全局规则中，不再有生产代码引用 |
| 19 | 2026-10-31 20:0x | 功能合并 | **合并独立工具 `caxa3dsizeboxread`**：新增 `SizeBoxTool.cs`（包围盒读取逻辑原样迁入），主界面「钣金件设计环境设默认」正下方新增「读取最小包围盒」按钮（214,252,200），结果输出到现有日志区；长流程运行期间按钮禁用。详见第 11 节 |
| 20 | 2026-10-31 20:3x | 功能替换 | **按钮替换为「钣金件尺寸填写」**（用户裁定）：该位置改为合并独立工具 `caxa3dsheetsizewrite` 的核心流程（`SheetSizeTool.cs` + 规则 `caxa3dsheetsizewriteaddfix.txt`），只读按钮与 `ReadBoundingBoxes` 一并移除；`SizeBoxTool.cs` 保留为内部依赖。详见第 11 节 |
| 21 | 2026-10-31 21:0x | 代码精简（用户裁定） | 依 trace 实证删除从未触发的兜底：`GetExternallyLinkedInfo` 异常不再按虚拟件放过（校验弹错）；板厚只认 `GetStockProperties`（删 `Thickness` 回退与量级纠偏）；`GetFlatPart` 异常不再吞；`SetValueByName` 失败不再回退 `AddCustomProp`（直接抛错）；`Doc.Save` 不再 try/catch 吞错。**同时删除材料来源字段 `MaterialSourceField` 及「读取属性 `<材料>板T=板厚`」整条路径**（规则只取前 2 项，材料统一由 `GetMaterialNameProperty` 提供），消除 trace 中 3 次 `读取属性「<材料>板T=板厚」失败：Unspecified error.` |

## 6. 交付状态
- 已完成编译并部署到 `C:\Program Files\CAXA\CAXA 3D\2027`（DLL/PDB、规则/说明 txt、CRX 注册脚本；`IronCad.Addin.config`、`IronCad.exe.manifest`、注册表 Applications 键、`CxAutorun.cx` 均登记，仅追加本插件条目）。4 个配置文件与 `caxa3d2027关键设置\` 截图目录**只随编译输出打包，不进 CAXA 安装目录**（里程碑11）。
- 已验证：强名称有效、清单仅含自身 clrClass、程序集在 .NET Framework x64 可加载、CRX 导出 `crxrxEntryPoint`。
- **已实测通过（2026-10-25）**：「图号分离」开关按钮紧邻规则下拉框右侧、默认开启可见；CAXA 主窗口最大化执行流程后不再被还原成普通窗口（焦点回归仅 `IsIconic` 时 `SW_RESTORE`）。
- **2026-10-25 已剔除「exb 直改」功能（里程碑18）**：该开关、`ExbLinkPatcher.cs`、独立诊断工具 `ExbLinkPatch/` 全部删除，改名流程统一走「打开 exb → 代点 → 保存关图」。原因：大图纸多段容器无法靠改链接记录复刻 CAXA 官方保存结果（壳体实测失败，里程碑17）。**当前交付版只有一条图纸更新路径，行为确定、无分支**。
- **已实测通过（2026-10-24）**：真实零部件“我的_100x20_Q2212135A_GB123 → 我的_100x20_Q235A_GB123”，属性回填、本地 ics 改名、exb 弹窗代点「是」、保存关图、焦点回归全部自动完成，**无需人工点击任何弹窗**。

## 7. 使用与排障速查
运行前提（CAXA 2027 选项）：开启“同名 ics/exb 联动”，旧图需手工转存一次。

1. 启动 CAXA 3D 2027 → 打开含 ics/exb 的文件夹工程。
2. 点功能区「带图改名及图号分离」→ 需要边选零件边看界面时先点「窗口前置」。
3. 在 3D 环境选中零部件（虚拟件会被拦截）→ 新零件名自动填入原名 → 改成新名 → 选图号分离规则。
4. 点「重命名」：右侧提示“不存在工程图/零件不存在”为绿色通路；红字“零件名已存在”时按钮不可点。
5. 日志窗口依次出现 1/6…6/6；失败看 `%TEMP%\caxa3dcodenameextraction_trace.log`。
   - 若无「是」被代点：核对 trace 里该窗口的 class 与标题是否被 `#32770`/` - [` 规则排除（必要时放宽）。
   - 若 CRX 未就绪：点「注册CRX」（自动提权）→ 重启 CAXA。
6. 卸载：CAXA bin 目录下运行 `卸载.bat`（restore.ps1，正则手术式清理，不影响其它插件）。

## 8. 优化方向探索记录（2026-10-25，未实施 / 已中止）

### 8.1 目标
两个静默化诉求：
1. CAXA 3D 内修改零件名**全程静默**（不弹任何框）；
2. 更新工程图参考模型链接**静默替换**（不开图、不代点、不人工更新视图）。

### 8.2 已落地部分
诉求 2 曾用**外部直改 exb** 方案实现（里程碑8/13），但大图纸无法复刻官方保存结果（里程碑17），**已于里程碑18 整体剔除**。当前交付版对诉求 2 采用「打开 exb → 代点 → 保存关图」老流程，链接弹窗由 PopupAutoYes 代点消除人工操作。剩余缺口：图纸打开时 Draft 仍弹「当前有3D视图数据源更新，要更新对应所有视图吗?」，需人工点「是」或依赖 PopupAutoYes 代点。

### 8.3 三条候选路线与结论
| 路线 | 做法 | 结论 |
| --- | --- | --- |
| A. COM 直调 | `OpenFile2(exb)` 后 QI `IZDrawingDoc` → `UpdateAll()`（已反射确认该接口确有 `UpdateAll()`、`GetLinkedScenes/Count/At`） | **用户裁定永不可行，放弃**，不再尝试 |
| B. CRX 桥命令 | 图纸环境内执行「刷新视图」命令后 `CX_save` + `WM_CLOSE`，复用现有 `CX_DFT_ViewSave` 链路 | **可行，留作后备**（本轮未实施） |
| C. 选项级抑制 | 找「自动更新视图、不询问」开关，让弹窗根本不产生 | **实测无效，已中止** |

### 8.4 C 路线取证过程（关键证据，避免重复排查）
1. **弹窗归属**：二进制搜 UTF-16 文本「当前有3D视图数据源更新」命中 `C:\Program Files\CAXA\CAXA 3D\2027\bin\CAXADraft\Bin\zh-CN\ResApp.dll` → 弹窗由 **CAXA Draft** 弹出，非 3D 侧。
2. **3D 选项真实存储**：`HKCU\Software\CAXA\CAXA 3D 29.0\...`（**单层键名**，`CAXA 3D\29.0` 不存在）；`.ucx` 是 ZIP，内含 `IRONCAD_Option_Config.xml`，仅供人工导入参考。候选键：`TDViewOutOfSyncDlg`、`DrawDrawingSceneOutOfSynch`、`ShowLinkNotFoundDialog`。
3. **实测 1（无效）**：`TDViewOutOfSyncDlg` 1→0，重启并打开图纸，弹窗照旧；且该值在开图后被回写为 1 → 非注册表直读。
4. **实测 2（无效）**：Draft 模板 `%LOCALAPPDATA%\CAXA\CAXA CAD CXIC-INT 2027 (x64)\27.0\zh-CN\Config\SysConfig_ID_DFT_TMPL.xml` 中 `DisShow3DDlg` 0→-1（备份后改），重启开图，弹窗照旧。该文件由 `KnlXmlParser.dll` / `DftConfig.dll` 解析。
5. **弹窗特征**：标准 `#32770`，按钮文本 `是(&Y)` / `否(&N)`，静态文本即上述提示 → **与现有 PopupAutoYes 的 `#32770` + 标题规则完全匹配，代点路径无需任何改动**。

### 8.5 环境还原记录
中止后已完整还原：注册表 `TDViewOutOfSyncDlg=1`、`DrawDrawingSceneOutOfSynch=1`、`ShowLinkNotFoundDialog=1`（reg 备份导入）；`SysConfig_ID_DFT_TMPL.xml` 由 `.bak20261025` 还原（`DisShow3DDlg=0`、`UpdateView=-1`）；临时 reg/xml 备份文件全部删除；CAXA 进程已关闭。

### 8.6 若日后重启该方向
- 直接走 **B（CRX）**：在图纸环境内调用刷新视图命令，再接 `CX_save`，属于铁律内方案，风险可控。
- **不要再投入 C**：3D 注册表键不控制 Draft 弹窗；Draft 侧配置键已试过两个均无效，继续试值性价比极低（教训16）。
- 若只是想「用户手动打开图纸时不被打扰」，维持现状即可：PopupAutoYes 已能代点「是」，代价是仍会闪一下弹窗。

## 9. 并入 caxa3dmytoolsaddin 集合体（2026-10-27）

### 9.1 入口改造
- 删除原独立 Add-in 入口 `Addin.cs`（带 `[ComVisible(true)]`、`[Guid]`/`[ProgId]`），新建 `Plugin.cs` 实现 `IPlugin`：
  `Initialize(IPluginHost)` 保存 `AddinSite` 并预建隐藏主窗口；`Activate()` 显示；`Shutdown()` 释放。
- 不再自行注册 Ribbon/ControlBar 按钮，功能区只保留 Host 的「我的工具箱」；本插件在工具箱内以「带图改名及图号分离」按钮出现（集合体铁律 1/4）。
- 主窗口默认 `TopMost=true`；隐藏“连接状态”标签，只保留“当前设计环境.ics---零部件名（外链为虚拟）”格式（集合体铁律 2/3）。
- 程序集级 `ComVisible(false)`；插件 DLL 不内嵌 SxS 清单、不做 COM 注册。

### 9.2 图纸环境 CRX 桥部署
- 源工程：`plugins\caxa3dcodenameextraction\caxa3dcodenameextraction-crx\`（MSBuild/v143 编译）。
- 产物 `caxa3dcodenameextraction.crx` 随插件输出；`deploy.ps1` 拷到 `<CAXA>\bin\CAXADraft\bin\`，并幂等登记 `bin\CAXADraft\Modules\CxAutorun.cx`；`restore.ps1` 反向清理。
- **缺失该 .crx 的典型现象**：插件日志显示“已投递 CMD:CLOSEDOC”，但图纸一直不关、等待超时——请求文件没人消费，等于命令被静默丢弃。

### 9.3 旧独立登记自动撤销
- deploy 的 legacy 清单包含本插件原独立 Add-in 的 CLSID `{3B7D9E21-6C4A-4F2B-B8D5-7A1C9E3F5A62}`；安装时自动移除其 config/manifest/注册表登记，避免与集合体重复加载。

### 9.4 共享属性表复核（2026-10-29）
- `WriteFirstElementCustomProps` 只写共享表，与 `caxa3dAttributeManageraddin` / `caxa3dsetMaterial` 的 `SC图号/SC名称/SC规格/SC材料` 语义一致，未改动。

## 10. 2026-10-28 现场问题：带图改名在个别机器卡 17 秒（「查找外链文件」）

### 现象
测试机（同时装了 2024 和 2027，账户 strong）执行「带图改名及图号分离」时，`3/6 保存零部件 ics` 这一步会停住，屏幕上一直挂着 CAXA 自己的「查找外链文件」对话框（`更新参考` 为空，`文件位置` = 项目目录，`外链文件路径` = 改名前的旧 ics）。本机同样流程只停顿 1.9 秒，测试机约 17–18 秒，两台机器步骤完全一致。

### 定位过程（可复用）
1. **用双端日志时间戳算停顿归属**：插件 `trace.log` 里 `PopupAutoYes: 点击「确定」@「查找外链文件」` 与紧随其后的 `SaveActiveDoc hr=` 的时间差就是卡顿时长（本机 1.92 s，测试机 17.15 s / 18.06 s）。
2. **看线程号**：卡顿窗口内 `tid 1`（UI 线程）没有任何输出，`tid 3`（PopupAutoYes 后台线程）仍在工作 → 阻塞发生在同步的 `IZDoc::Save` 调用内部，不是插件的等待循环。
3. **排除插件自身 IO**：全插件搜不到 `Directory.GetFiles / EnumerateFiles`；TEMP 下只写 4 个固定文件名（request / crx.log / marker / trace），不产生任何 `.TMP`。
4. **资源监视器按 `IRONCAD.exe` 过滤**：看到它访问 `%TEMP%\~DF*.TMP`，但实测只有 16 个，且磁盘吞吐 91 KB/s、最长活动时间 1% → 既不是文件数量问题，也不是吞吐问题。16 个文件对上 17 s，即约 1 s/文件，属于“等待型延迟”。
5. **纯手动复现**：不用插件、完全手工改名并保存，同样延迟 → 判定为 CAXA 2027 在该机器上的行为。

### 结论
- 「查找外链文件」是 CAXA 自己的外链引用更新：改元素名后保存，旧文件名失效，CAXA 在 `IZDoc::Save` 内同步搜索并改写未打开文档里的引用，UI 线程被占住，窗口看起来“卡死”。插件的职责只是等这个同步调用返回并记录耗时，能做的只有把提示写清楚。
- 耗时取决于目录内待更新文档数量、磁盘/网络位置、杀毒实时扫描等环境因素。
- **与同时安装 2024+2027 无直接关系**：本机只装 2027 也会弹同样两个框（只是 1.9 s）；CAXA 注册表按版本分树（2027 = `CAXA 3D 29.0`，2024 = 26.0），也没有共享的外链搜索路径键。多版本最多是间接放大（目录里混有 2024 格式文档时，2027 更新引用要顺带升级格式）。
- 「每文件约 1 秒」的来源尚未闭环，剩余候选（需在该机取证）：杀毒实时防护对每次文件打开加延迟；`%TEMP%` / 桌面被重定向到网络或同步盘；CAXA 在等不可达主机的网络超时。

### 待办（环境侧，不写进插件）
- 该机加杀毒排除项（`IRONCAD.exe`、`%TEMP%`、项目目录、`.ics/.exb`）前后各跑一次，对比耗时。
- Procmon 复核时**同时打开网络事件**（TCP/UDP/SMB/DNS），确认 17 秒里是否有连接超时。
- 检查 `Desktop` / `%TEMP%` 是否存在 reparse point 或 UNC 重定向。
- 把项目目录复制到只装 2027 的机器上跑同一操作，验证版本无关性。

> 归因方法已沉淀为集合体铁律 10「卡顿先归因，再改代码」：① 用日志时间戳 + 线程号判断阻塞在谁的调用里；② 数据必须自洽（16 × ~1 s ≈ 17 s）；③ 磁盘占用极低却有长等待 = 等待型延迟；④ 用纯手动流程作最终判据；⑤ 环境问题不写进插件代码。

### 同期小改动
- 工具箱显示名与 `AssemblyTitle` 去掉「（子插件）」后缀（`Plugin.cs` DisplayName 与同名 `.csproj`）。


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
