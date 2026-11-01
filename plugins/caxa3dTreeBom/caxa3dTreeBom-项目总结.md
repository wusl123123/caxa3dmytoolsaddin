# caxa3dTreeBom 项目总结

> CAXA 3D（IronCAD）树形结构 BOM 工具 · 首次建档：2026-10-28
> 参考产品：`C:\code\SwTreeBom`（界面与其 Form1 完全对齐）

## 一、项目定位

在 CAXA 3D 场景中选中装配体（可多选），递归遍历整棵装配结构，生成带层级编码的树形 BOM：
`1 / 1.1 / 1.1.1 …`，并回填自定义属性、材料、数量/总数量、同名工程图标记、缩略图，最终写出 xlsx。

- 交付形态：`caxa3dTreeBom.dll`（x64 / net48 / 强签名）+ `安装.bat` / `卸载.bat`
- 入口：场景环境功能区按钮「树形BOM」
- 默认表头模板：`caxa3dTreeBomxls.xlsx`
  `层级 | 是否有工程图 | 缩略图 | SC图号 | SC名称 | SC规格 | SC材料 | SC备注 | 数量 | 总数量`
- 默认输出路径：`%APPDATA%\caxa3dTreeBom\caxa3dTreeBomxls.xlsx`（CAXA bin 目录不可写，不能放程序目录）

## 二、交付物清单

| 路径 | 说明 |
| --- | --- |
| `bin\x64\Release\net48\caxa3dTreeBom.dll` | 编译产物（已内嵌 SxS 清单 + 重签名） |
| `bin\x64\Release\net48\安装.bat` / `卸载.bat` | 一键部署 / 还原（自动 UAC 提权） |
| `deploy.ps1` / `restore.ps1` | 部署脚本主体（兼容 PowerShell 5.1 与 7） |
| `caxa3dTreeBomxls.xlsx` | 默认表头模板（部署时播种到 `%APPDATA%`） |
| `%TEMP%\caxa3dTreeBom_trace.log` | 运行时调试日志（每次 CAXA 启动自动清空；TEMP 不可写时自动降级到 `%APPDATA%\caxa3dTreeBom\trace.log` 或 DLL 目录，启动时在日志区回显实际路径） |

## 三、代码结构

| 文件 | 职责 |
| --- | --- |
| `Addin.cs` | `IZAddinServer` 入口；`CreateCommandHandler` + `AddControlBar` + `RibbonBar.AddButton` 注册按钮；窗口单例管理 |
| `MainForm.cs` | 与 SwTreeBom 的 Form1 同构：连接状态区 / 表格区 / 日志区；额外「停止」按钮 + 进度文字 |
| `CaxaService.cs` | CAXA API 封装：场景/选中/子元素/自定义属性/材料/BOM 设置/外链/工程图/缩略图 + IDispatch 延迟绑定 |
| `BomBuilder.cs` | 递归遍历与层级编码、同层同名合并计数、总数量汇总、属性回读、缩略图回填 |
| `BomModels.cs` | `BomRow`（Code/TreeText/Level/FilePath/FileName/ElementName/Quantity/TotalQuantity/Properties…） |
| `XlsxTableService.cs` | 零依赖 xlsx 读写（`System.IO.Compression` + `System.Xml.Linq`），支持内嵌 PNG 缩略图 |
| `Diagnostics.cs` | 文件日志 + UI 日志队列（后台线程只入队，UI 定时消费） |
| `ConvertImage.cs` | `Image → IPictureDisp`（AxHost 技法，按钮图标） |

## 四、关键技术结论（本机实证）

1. **装配遍历**：`IZAssembly.GetChildrenZArray()`（元素侧 `IZElement.GetChildrenZArray()` 兜底）逐层递归，
   零件/装配体/钣金/面片/线框/轮廓用 `IZElement.Type` 判定，子件用 `IncludedInBOM` 过滤。
2. **选中元素**：`IZSceneDoc.SelectionMgr` → `SelectionsAvailable()` → `GetSelectedElementsZArray()`；
   非零部件类型（面/边/特征）直接过滤。选中为空时退回场景顶层。
3. **自定义属性**：`IZElement.GetCustomPropManager(selector)`，`selector=0` 零件级、`1` 共享（文档级）；
   用 `IsSharedMgr()` 定位。**`SC*` 系列属性（SC图号/SC名称/SC材料…）在共享表**，
   读取策略：按原名判定主表 → 另一张表兜底 → 去掉 `SC` 前缀再试 → 专用兜底（名称/材料/图号/备注）。
4. **材料**：先读 `GetMaterialNameProperty` 指向的属性值，再退回 `IZPartProperty/IZAssemblyProperty.GetMaterialName`。
5. **外链文件**：`IZPart/IZAssembly.GetExternallyLinkedInfo(out bool)`；虚拟件返回空 → 用元素名作为分组键。
6. **工程图判定**：同名 `.exb` 存在即 `★`（CAXA 3D 图纸环境格式；SolidWorks 侧为 `.slddrw`）。
7. **缩略图**：`IZBaseApp::FilePreviewImage(BSTR) → IPictureDisp**` 在 interop 中**未声明**，
   用 `IDispatch::GetIDsOfNames + Invoke(DISPATCH_PROPERTYGET)` 延迟绑定读取；
   失败回退 `IZBaseApp.WritePreviewToFile(src, out)`，解析 PNG/BMP/JPG/GIF，裸 DIB 补 14 字节 BMP 文件头。
   预览原始数据前 32 字节写入 trace，便于后续按实测调整。
8. **构建后处理不可省**：`mt.exe -managedassemblyname` → `mt.exe -outputresource:#2` → `sn.exe -R`。
   mt 嵌入清单会使强名称失效，不重签则 CAXA 拒绝加载。
9. **部署登记**：`IronCad.Addin.config` 的 `<AddIn>` + `IronCad.exe.manifest` 的 `<dependency>`
   （`name/version/publicKeyToken/processorArchitecture=amd64` 必须与内嵌清单逐字一致）。
10. **成功判定**：只认文件侧副作用——文件存在、字节数 > 0、xlsx 包可解析、表头回读列数一致；
    不使用「命令已执行」「编辑器空闲」之类推断。

## 五、里程碑记录表

| 序号 | 时间 | 事件 | 结果 |
| --- | --- | --- | --- |
| 1 | 2026-10-28 | 需求解析 + 复用资产调研（setMaterial / AttributeManager / apigetaddin） | 确认遍历、属性、选中三条主链路均有实证代码可抄 |
| 2 | 2026-10-28 | 反射 interop 程序集 | `FilePreviewImage` 未声明 → 必须 IDispatch 延迟绑定；`WritePreviewToFile(string,string)` 存在 |
| 3 | 2026-10-28 | 读取模板表头 | 实际 10 列：层级/是否有工程图/缩略图/SC图号/SC名称/SC规格/SC材料/SC备注/数量/总数量 |
| 4 | 2026-10-28 | 首次编译 | 通过；mt 警告「嵌入清单使强名称失效」由 `sn -R` 解决，`sn -v` 校验有效 |
| 5 | 2026-10-28 | xlsx 零依赖冒烟测试 | 生成 5032 字节；10 部件齐全；缩略图 PNG 嵌入；XML 转义正确；表头回读一致 |
| 6 | 2026-10-28 | Excel COM 复核 | `usedRange=4x10`、`shapes=1`、`总装 & <A>` 正确还原、`总数量` 数字为数值型 |
| 7 | 2026-10-28 | 界面照搬 SwTreeBom | Form1 同构重建（连接区/表格区/日志区/右键菜单/插入删除列/缩略图自绘/日志拖拽） |
| 8 | 2026-10-28 | 部署到 CAXA 3D 2027 | DLL/PDB 拷贝成功，两个配置文件条目登记成功，DLL 强名称有效 |
| 9 | 2026-10-28 | 后台启动 CAXA 验证加载 | 进程内无任何托管 Add-in 加载（其它三个已验证插件同样未加载）→ 后台启动不具备代表性，需用户前台启动验证 |
| 10 | 2026-10-28 | 前台用户实测日志 | Add-in 成功加载并注册「树形BOM」按钮；`FilePreviewImage` 的 GetIDsOfNames 全部失败（该属性不存在），`WritePreviewToFile` 输出标准 BMP（`42 4D`）128×128，全部解码成功 |
| 11 | 2026-10-28 | 修复 SC备注 混淆填充 | 根因：`ReadRowProperties` 对表头先「去 SC 前缀」再走"备注→BOM 描述"兜底，`SC备注` 无该属性时被 `GetBOMSetting` 的 desc 填充。改为 SC* 表头只认精确同名属性（对齐 caxa3dAttributeManageraddin 的 `TargetFields` 语义），不兜底 |
| 12 | 2026-10-28 | 界面对齐参考项目 | 「定位文件」按钮 90×28、Margin(6,0,6,0)，右侧面板基准 500，并按文件名 `PreferredWidth` 动态放宽以保证完整显示；新增「窗口前置」按钮（默认 `TopMost=true`，点击切换，蓝/灰两态） |
| 13 | 2026-10-28 | 连接区显示选中零部件 | 标签改为「文件名.ics----零件名」（`FileLabelText`），2s 轮询同时比较文档路径与选中元素名，换选即刷新 |
| 14 | 2026-10-28 | 附加合并规则（caxa3dtreebomaddfix.txt） | 顶部新增「附件合并规则」按钮：开启后以规则文件字段全部一致为签名合并总数量（区分大小写、缺项即不合并、禁止兜底），签名首行显示合并值、其余行 `---`；未形成完整签名的行仍按文件名规则 |
| 15 | 2026-10-28 | 规则按钮改为「打开规则文件编辑」 | 点击打开 `%APPDATA%\caxa3dTreeBom\caxa3dtreebomaddfix.txt`（notepad），保存关闭后自动重读规则并补读属性、重算总数量；新增 `MergeRule`：只抓引号内字段、`#` 开头说明行整行隔离（说明里的 `""` 不会被当成规则）；安装脚本只播种一次、不覆盖用户修改 |
| 16 | 2026-10-28 | 第一行装配体工程图判定 | 层级 1（第一行装配体）在「同级同名 .exb」之外，追加查同名 ics 文件夹内的同名 .exb（`<基名>\<基名>.exb`、`<基名>.ics\<基名>.exb`），命中即判定有工程图；其它层级维持原规则 |
| 17 | 2026-10-29 | 现场机器「不是3D场景文档 + 场景顶层0个零件」排障与修复 | 根因：长任务用 `Task.Run` 在后台 MTA 线程调用 CAXA COM（`ActiveDoc` 返回 null），连接区（UI 线程轮询）却正常显示零部件，形成"能选中却遍历不到"的假象。改为 `RunOnUiThread` + 40ms 限流 `Application.DoEvents` 消息泵（进度可见、停止可点），所有 `IZBaseApp/IZSceneDoc/IZElement` 调用回归 STA 主线程；日志增加 TEMP→APPDATA→DLL 目录三级降级并回显路径；新增 `DescribeActiveDoc()` 文档探针（`IZDoc.Type` 的 `Z_SCENE/Z_DRAWING`、文档名、可转接口、打开文档数） |

## 六、踩坑与规避

1. **后台启动 CAXA 无法验证 Add-in 加载**：命令行 `Start-Process IronCad.exe` 起来的进程里，
   连已长期使用的 `caxa3dsetMaterial` 等托管插件也未加载；说明加载时机依赖前台交互/环境初始化，
   不能据此判定本插件失败。验证必须以用户桌面正常启动为准。
2. **CAXA 程序目录不可写**：默认模板和导出默认路径一律放 `%APPDATA%\caxa3dTreeBom`，
   模板从程序目录播种（安装脚本也播种），避免「保存表格」直接失败。
3. **备份策略**：仅当配置文件**不含本插件条目**时才备份，避免把已注入条目的「脏文件」当成原始备份；
   还原时再叠加正则兜底移除，保证卸载干净。
4. **日志跨线程**：后台线程只 `Enqueue`，UI `Timer` 每 250ms 批量刷入 `RichTextBox`，
   避免高频 `BeginInvoke` 拖死界面（大装配遍历时节点数可达数千）。
5. **缩略图缓存**：按 PNG 路径缓存 `Image`，切换表格/重新生成时统一 `Dispose`，防止 GDI 句柄泄漏。
6. **元素失效**：属性回读时若 `IZElement` 已失效（场景重开/元素删除），保留原值并只告警一次，
   不因单行失败中断整批。
7. **同名件合并**：同层按「外链文件 + 元素名称」合并；总数量按「文件名（虚拟件用元素名）」汇总，
   仅首次出现的行填数值，其余填 `---`（与 SwTreeBom 语义一致）。
8. **SC 列禁止兜底（实证）**：`SC*` 表头必须只认**精确同名**的自定义属性。曾经实现的
   「去 SC 前缀 → 名称/材料/图号/备注 兜底」会让 `SC备注` 在没有该属性时被 `GetBOMSetting`
   的描述（desc）填充，造成"SC备注被填备注信息"的混淆。参考项目
   `caxa3dAttributeManageraddin` 的 `TargetFields` 只有 `SC图号/SC名称/SC规格/SC材料`，
   且逐字精确读取、不做任何回退。结论：SC 列读不到就留空。
9. **脚本重复 BOM**：`deploy.ps1/restore.ps1` 曾被补写 UTF-8 BOM 两次（`EF BB BF EF BB BF`），
   Windows PowerShell 5.1 会把它当成脚本首行的非法符号（`术语 '﻿#' 不是…`）并报错（脚本仍能继续执行）。
   规则：给脚本补 BOM 前先剥离已有 BOM，最终只保留一个；本轮已修复项目根与 `bin\...\net48` 下四份脚本。
10. **规则文件说明与规则必须隔离（实证）**：说明行 `#规则说明,抓取""之间的字段#` 本身含两个双引号，
    若按"抓所有引号内文本"解析会生成一个空字段并破坏比对。`MergeRule.Load` 的处理是：
    先按行判断首字符 `#` → 整行跳过；只对非说明行提取 `"..."`，空字段丢弃。
11. **第一行装配体的工程图不在 ics 同级**：顶层装配体保存时工程图常放在与 ics 同名的文件夹里
    （`<目录>\<基名>\<基名>.exb`），只查同级同名 .exb 会误判"无工程图"，须追加同名文件夹查找。
12. **CAXA COM 对象只能在宿主 STA 主线程调用（实证）**：`Task.Run` 的后台线程里调
    `IZBaseApp.ActiveDoc` 会拿到 null（不抛异常也不崩），于是 `SceneDoc == null`，
    日志表现为「当前活动文档不是 3D 场景文档」紧接「场景顶层 0 个零部件」；
    同一进程里 UI 线程的 2s 轮询却能正常读到文件名和选中零部件，极易误判为"环境不对"。
    解决：长任务改 `RunOnUiThread` 同步跑在 UI 线程，进度回调里 `Application.DoEvents()`
    （40ms 限流）维持进度可见与停止按钮可点；不要再用后台线程碰任何 CAXA API。
13. **现场机器整份日志写不出来**：日志固定写 `%TEMP%` 时，有的机器 TEMP 被重定向/无写权限，
    `File.AppendAllText` 抛异常被静默 catch，现场就"无法生成 log"。改为三级降级
    （%TEMP% → `%APPDATA%\caxa3dTreeBom` → DLL 目录），并在窗口启动时把实际日志路径打进日志区。

## 七、待实测项（需在桌面启动的 CAXA 3D 中验证）

| 项 | 验证方法 | 预期 |
| --- | --- | --- |
| 按钮出现 | 场景环境功能区 | 出现「树形BOM」按钮 |
| 遍历正确性 | 选中总装 → 生成BOM | 行数与设计树一致，编码 1/1.1/1.1.1 |
| SC 属性读取 | SC图号/SC名称等列 | 共享表属性有值 |
| 缩略图 | 抓取缩略图 | 表格出现预览图，日志无失败；trace 中记录预览文件头 |
| 保存 | 保存表格 → 用 Excel 打开 | 10 列齐全、缩略图嵌入、数量为数值 |
| 工程图标记 | 存在同名 `.exb` 的零件 | `是否有工程图` = ★ |

> 若缩略图两路都失败，先看 `%TEMP%\caxa3dTreeBom_trace.log` 里的
> `预览文件头:` 行（WritePreviewToFile 输出格式）与 `FilePreviewImage 失败:` 行，
> 再决定补齐解码分支还是改用视图截图方案。

## 八、已否决清单

（暂无）

## 九、并入 caxa3dmytoolsaddin 集合体（2026-10-29）

### 9.1 入口与资源
- 删除原 `Addin.cs`，新建 `Plugin.cs → TreeBomPlugin : IPlugin`：`Initialize` 预建窗口 / `Activate` 显示 / `Shutdown` 释放；不注册 Ribbon（集合体铁律 1/4）。
- 删除 `Icons\*.bmp`、`ConvertImage.cs`、`stdole` 引用（只服务 Ribbon 图标）。
- 种子资源改为 `Path.GetDirectoryName(typeof(X).Assembly.Location)` 优先、`AppContext.BaseDirectory` 兜底：
  - `caxa3dTreeBomxls.xlsx`（MainForm 播种默认表头）；
  - `caxa3dtreebomaddfix.txt`（`MergeRule.EnsureFile`）。
- 用户数据目录保持不变：`%APPDATA%\caxa3dTreeBom`；老用户规则文件/表格继续生效。

### 9.2 构建 / 打包 / 部署登记
- `build.ps1` 的 `$plugins` 清单增加本插件，声明 `Extra=@('.txt')`。
- `package.ps1` 扩展名白名单同步补充（同批加入 `.xml`，本插件自身无 xml 资源）。
- `deploy.ps1` 的 legacy 清单补原独立 Add-in 的 dll 名、目录名、CLSID `{2F6C8B41-...}`，安装时撤销独立登记。
- 冒烟：无 CAXA 上下文构造 `MainForm(null)`（1400×1050），种子资源成功播种到 `%APPDATA%`。

## 十、2026-10-29 其他机器无法生成 BOM（COM STA 线程模型被 Task.Run 破坏）—— 详细记录

> 对应里程碑 17、踩坑 12。集合体层规则见铁律 13/14。

### 现象
树形BOM 插件在**本机**（开发者机器）能正常生成 BOM、读属性、抓缩略图，但**部署到其他机器**后，点击「生成BOM」无反应或日志显示“场景顶层 0 个零部件”、“当前活动文档不是 3D 场景文档”。同一台机器同一流程，本机 OK、他人机器直接失效。

### 根因
把独立 Add-in 迁入集合体时，把所有长任务的执行框架从 **`RunOnUiThread`（UI 线程同步）** 改成了 **`RunAsync` + `Task.Run`（后台线程）**。CAXA 的 COM 对象（`IZBaseApp / IZSceneDoc / IZSelectionMgr` 等）是 **STA（Single Threaded Apartment）** 模型，只能在创建它们的线程（UI 主线程）访问。把 RCW 放到 `Task.Run` 里跨公寓访问，**其他机器 COM 严格模式下** `_app.ActiveDoc` 会返回 null、`scene.SelectionMgr` 直接抛异常、装配遍历空列表。

**本机能跑纯属时序巧合**：.NET 的 RCW 代理在某些时机可以容忍跨线程访问（.NET Framework 4.8 的 COM 互操作对 STA 违规不是 100% 强制阻断），加上本地缓存/时序幸运，让这个致命 bug 在本机被掩盖了。

### 修复内容

| 文件 | 改前 | 改后 |
| --- | --- | --- |
| `CaxaService.cs` `SceneDoc` | `_app.ActiveDoc as IZSceneDoc` 单步 return | 完整版：含 `NoteDocState` 去重 trace、`DescribeActiveDoc()` 诊断方法、异常时记 trace |
| `MainForm.cs` 执行框架 | `RunAsync(string, Func<CancellationToken, Task>)` → `Task.Run(async ...)` | `RunOnUiThread(string, Action<CancellationToken>)` → 直接 `work(token)` |
| `MainForm.cs` 进度维持 | `BeginInvoke` 跨线程更新 `lblTableFile` | `PostStatus` + `PumpUi()`（限流 40ms 的 `Application.DoEvents()`），UI 线程同步更新 |
| `btnGenerate_Click` | `async void` + `await RunAsync` + `BeginInvoke` 包 UI 更新 | `void` + `RunOnUiThread` + PumpUi + 直接 UI 更新 |
| `btnReadProps_Click` | 同上 | 同上 |
| `btnThumb_Click` | 同上 | 同上 |
| `ApplyMergeRulesAsync` | `async Task` + `RunAsync` | 改名 `ApplyMergeRules`（同步）+ `RunOnUiThread` |
| BOM 生成诊断 | 无 | 日志输出 `🔎 DescribeActiveDoc()`（文档类型/名称/可转接口/打开数） |

### 反面 / 正面教材

```csharp
// ❌ 危险：跨线程访问 STA COM 对象
await RunAsync("生成BOM", delegate(CancellationToken token) {
    // 这里 _svc.SceneDoc / GetSelectedElements 会取到 null 或抛异常
    var roots = _svc.GetSelectedElements(out err);
    ...
    BeginInvoke(new Action(delegate { DisplayTableData(); }));  // UI 还要回切
    return Task.FromResult(true);
});
```

```csharp
// ✅ 安全：留在 UI 线程同步执行
RunOnUiThread("生成BOM", delegate(CancellationToken token) {
    var roots = _svc.GetSelectedElements(out err);   // STA 合法
    ...
    DisplayTableData();   // UI 线程直接调用
    PumpUi();             // 保持可响应 + 停止按钮可点击
});
```

### 待实机确认
1. 在之前“无法生成 BOM”的那台机器上部署修复后的 DLL，验证：
   - 点击「生成BOM」后日志显示 `🎯 遍历起点：场景顶层 N 个零部件`（N > 0）；
   - 表格区出现树形结构（层级编码 1 / 1.1 / 1.1.1 …）；
   - 缩略图能正常抓取、属性能正常读取。
2. 在本机同样部署，确认无回归（已编译通过，0 错误 0 警告）。

## 十一、2026-10-29 界面优化：附加合并规则与顶部布局

1. 顶部功能按钮由「附件合并规则」更名为「附加合并规则」（日志、进度任务名、注释同步改名，概念统一为“附加”）。
2. 「表格文件」地址与抓取/生成时的跳动进度文字（`lblTableFile`）从「树形BOM表格」组框标题行上移到顶部按钮行，紧随「附加合并规则」右侧。
   实现：顶部条由 `Panel` 绝对定位改为 3 列 `TableLayoutPanel`（100 窗口前置 / 132 附加合并规则 / 100% 文字，`AutoSize=false + AutoEllipsis`，悬停看全路径）；表格标题行缩为单列只放按钮组，高度 38 不变。
3. 验证：`DrawToBitmap` 渲染顶部条，按钮与文字同排不重叠；最小宽度 1200 下文字右侧省略；`build.ps1` + `package.ps1` 0 错误并重新出包。
