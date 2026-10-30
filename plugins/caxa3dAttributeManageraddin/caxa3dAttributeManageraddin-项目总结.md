# 项目总结 - 2026-10-22

## 一、项目目标

把原跨进程独立工具 `D:\code\ICAPI\addout\caxa3dAttributeManager`（CAXA 3D 自定义属性管理器）
改制成 **CAXA 3D 内置 .NET Add-in 插件**，项目名 `caxa3dAttributeManageraddin`：

- CAXA 3D 2027 启动后自动加载，场景功能区出现按钮「**自定义属性管理器**」；
- 点击按钮打开原有工具窗口，直接在进程内读写 CAXA 3D 模型的自定义属性；
- 保留原工具全部功能：xlsx 属性表加载/编辑/保存、读取选中零件/装配体的
  `SC图号 / SC名称 / SC规格 / SC材料`、回填属性、行列增删、日志保存。

参考项目：`D:\code\ICAPI\caxa3dapigetaddin`（Add-in 注册与部署）
避坑参考：`D:\code\ICAPI\Caxa3dcudeaddin`（SxS 清单、强名称、InitSelf 预创建句柄等）
开发规则：`D:\code\ICAPI\caxa3d-csharp-二次开发规则.txt`

## 二、技术方案

### 1. Add-in 入口与功能区按钮

- `Addin.cs`：`IZAddinServer` 实现，`[Guid("82B0A784-9822-4009-B83B-4AD63D6D2A88")]`，
  `ProgId = caxa3dAttributeManageraddin.Addin`；
- `InitSelf(ZAddinSite)` → `CreateCommandHandler` + `ZControlBar` + `ZRibbonBar.AddButton`
  注册「自定义属性管理器」按钮，并在此阶段 `new MainForm(...)`（构造里 `CreateHandle()`）；
- `Button_OnClick` 显示/激活窗口，`Button_OnUpdate` 保持按钮可用；
- 图标内嵌资源 `Icons\c1_small.bmp` / `c1_large.bmp`，`MemoryStream` 保活。

### 2. 进程内会话（核心改造）

原工具通过 COM 连接外部 CAXA 进程；插件内改为 **直接从 Add-in 站点取应用对象**：

- `CaxaAutomation.ConnectFromSite(IZBaseApp siteApp)`：`Marshal.GetIUnknownForObject` +
  QI `IID_IZBaseApp{A5B9AF7D-228B-4ED1-BA42-D51318F7EA20}`，失败再回退 IDispatch；
- `CaxaSession` 只保存接口指针，`Dispose()` 只释放指针，**不 `ReleaseComObject`**（对象归 CAXA 所有）；
- 无跨进程 Ping/ROT 清理、无进程重启后重连逻辑。

### 3. Z 系列接口 vtable 直调

CAXA 的 Z 接口是「单 IDispatch 多表面」：QI 任意接口都成功，但 IDispatch 只认默认接口。
因此全部关键调用都保存原始接口指针后 **vtable 直调**：

| 接口 | 槽位 |
| --- | --- |
| IZBaseApp.ActiveDoc | 11 |
| IZDoc.get_Name | 11 |
| IZSceneDoc.get_SelectionMgr | 27 |
| IZSelectionMgr.GetSelectedElementsZArray / GetSelectedElements / SelectionsAvailable | 19 / 10 / 17 |
| IZArray.Count / Get | 7 / 9 |
| IZElement.get_Type / get_Name / get_CustomPropMgr / GetCustomPropManager | 7 / 9 / 24 / 42 |
| IZCustomPropMgr.IsSharedMgr / GetValueByName / SetValueByName / RemoveByName | 7 / 9 / 10 / 13 |

`LooksCallable` 做槽位防护；空值写入回退 `RemoveByName`。

### 4. SxS / 强名称 / 部署

- 免注册 COM：`clrClass` + `IronCad.Addin.config` + `IronCad.exe.manifest`；
- 编译后处理 Target `EmbedSxsManifestAndResign`：
  `mt -managedassemblyname` → `strip-manifest.ps1` 剥离 `interop.*` 的 `clrClass/clrSurrogate`
  → `mt -outputresource:...;#2` 嵌入 → `sn -R` 用 `Lib\ICApiAddin.snk` 重签；
- `deploy.ps1`：多版本扫描 `%ProgramFiles%\CAXA\CAXA 3D\<年份>`、自动提权、备份、
  拷贝 DLL/PDB/模板 xlsx、登记 config/manifest、写 HKCU/HKLM `Applications` 键；
- `restore.ps1`：还原备份/手术式移除条目、删注册表键，保留用户数据；
- `安装.bat` / `卸载.bat` 为双击入口。

### 5. 用户数据目录

CAXA 装在 `Program Files`，bin 不可写。首次运行把模板
`caxa3dManagermodle.xlsx` 复制到
`%LocalAppData%\caxa3dAttributeManageraddin\`，之后读写该副本。

### 6. xlsx 读写

沿用原项目 `XlsxLite.cs`（ZipArchive + XML，inlineStr/sharedStrings），零第三方依赖。

## 三、目录结构

```
caxa3dAttributeManageraddin/
├─ Addin.cs                     Add-in 入口 + 功能区按钮
├─ CaxaAutomation.cs            进程内会话 + vtable 直调读写
├─ MainForm.cs / .Designer.cs   工具主窗口（属性表 UI）
├─ Diagnostics.cs               trace 日志（%TEMP%）
├─ ConvertImage.cs              Image → IPictureDisp（按钮图标）
├─ XlsxLite.cs                  xlsx 零依赖读写
├─ caxa3dAttributeManageraddin.csproj
├─ deploy.ps1 / restore.ps1 / strip-manifest.ps1
├─ 安装.bat / 卸载.bat
├─ Icons\  Lib\（ICApiAddin.snk、interop.ICApiIronCAD.dll）
└─ caxa3dManagermodle.xlsx     默认表格模板
```

## 四、踩坑与修复

1. **PowerShell 中文乱码 / Unexpected token**
   Write 工具默认 UTF-8 无 BOM，PowerShell 5.1 按 ANSI 解析 → 所有 `.ps1` 改存 UTF-8 **with BOM**。
2. **`-CaxaRoot` 传错层级**：要传父目录 `C:\Program Files\CAXA\CAXA 3D`，不是 `...\2027`。
3. **Add-in 未加载（无 trace 日志）**：根因是 `IronCad.Addin.config` 的
   `<displayname><default>` 必须与 `HKCU\...\Applications\<键名>` 一致。
   修正为 `default=caxa3dAttributeManageraddin`、`zh-cn=自定义属性管理器` 后加载成功。
4. **点击按钮前死锁风险**：窗口句柄必须在 `InitSelf` 线程预创建（`new MainForm` 构造里
   `CreateHandle()`），不能等 OnClick 回调再创建，否则与 CAXA 主线程互等。
5. **窗口超出 1366×768 屏幕**：`ClientSize` 由 1264×1040 改为 1264×700。
6. **DataTable 插列重名异常**：`InsertColumnAt` 使用唯一列名 `Col_{idx}_{Guid:N}`。
7. **插入行越界**：`InsertRowAt` 增加 `rowIdx >= Rows.Count` 时 `Rows.Add` 分支。
8. **无活动文档时刷新报红**：改为提示「当前没有活动文档，打开设计场景后再刷新」。

## 五、验证结果

- 构建：`dotnet build -c Release -p:Platform=x64` → 0 错误；
- 部署：`deploy.ps1 -CaxaRoot 'C:\Program Files\CAXA\CAXA 3D'` → 部署完成；
- CAXA 3D 2027 启动后 trace 日志：
  ```
  13:42:47.891 InitSelf: addin loaded by CAXA
  13:42:48.396 MainForm constructed
  ```
  → 插件被 CAXA 正常加载，主窗口已预创建（隐藏）。
- 早前带自动显示验证时已确认进程内会话建立成功：
  `session accepted (in-process)`、`ActiveDoc hr=0x0`。

## 六、待办 / 后续

- [ ] 在 CAXA 中打开含零件/装配体的场景，选中对象后点「自定义属性管理器」，
      验证 `SC图号/SC名称/SC规格/SC材料` 的读取与回填（端到端）；
- [ ] 如需，补充多选批量写入、属性名校验提示。

## 七、优化（第二轮）

1. **按钮“口”乱码**：根因是图标位图尺寸不符（原为 16×15 / 26×25），CAXA 在
   文字前画占位方块。改为标准 16×16 / 32×32 位图后按钮显示正常图标。
2. **连接区**：删除「连接状态: 已连接/未连接」标签；文件标签移至「定位文件」按钮
   左侧，用 `TextRenderer.MeasureText` 按实际文本长度设置宽度，过长时以按钮为界省略。
3. **API 抓取简化**：
   - 删除 `SelectionsAvailable` 诊断调用（语义随版本相反，无实际用途）；
   - `GetSelectedElementsZArray` 为主路径，成功即返回，SAFEARRAY 仅在 ZArray 不可用/失败时回退；
   - 定时器改为一次 `ProbeSessionInfo` 探测，失败即判会话失效，去掉重复 `Ping`。
4. **日志**：`Diagnostics.Trace` 只写文件不进 UI；UI 日志去掉 `[tid x/bg]`，仅保留时间与消息。

## 八、优化（第三轮）

1. **按钮“口”乱码（彻底方案）**：
   - 彻底移除图标：`CreateCommandHandler` 图标参数改回 `null, null`，使用 CAXA 默认图标；
   - 删除 `ConvertImage.cs`、`Icons/` 目录、csproj 里的 EmbeddedResource 条目；
   - 删除 `Addin.cs` 里的 `LoadIcon`、`IconStreams` 及相关 using。
   - 根因分析：CAXA 2027 对 `IPictureDisp` 类型的位图渲染要求苛刻（尺寸/格式/分辨率稍有不符即画占位方块），
     直接传 null 后走内置图标无此问题。

2. **严格按属性名精确匹配（大小写敏感）**：
   - 原逻辑自动为"图号"拼出候选"SC图号"，导致表头"图号"列错误读取"SC图号"属性值；
   - 已删除所有 SC 前缀自动回退逻辑（`RefreshPropertyValues` 与 `ResolveTargetPropName`）；
   - 表头写什么就查什么：表头是 "SC图号" → 读 CAXA 里的 "SC图号" 属性；表头是 "图号" → 读 CAXA 里的 "图号" 属性，
     两者独立，互不干扰。CAXA 自定义属性本身区分大小写，C# `Dictionary<string,T>` 默认为 Ordinal 比较，完全匹配。

## 九、卸载链路修复（第四轮）

**现象**：卸载提示"完成"却零效果 / 还原后 CAXA 无法启动。

**根因链与修复**（deploy.ps1 / restore.ps1）：

1. **BOM 丢失 = 静默崩溃秒退**：bat 调用的 PS5.1 对无 BOM UTF-8 按 GBK 解码，
   中文后引号/换行被吞 → 解析出错静默退出。本次修复过程中再次复现
   （编辑工具保存 .ps1 会丢 BOM），已固化流程：每次编辑后用
   `[IO.File]::WriteAllText` + `UTF8Encoding($true)` 重写单一 BOM，
   并用 PS5.1 `Parser::ParseFile` 校验 0 错误。
2. **脏备份**：原 deploy 无条件备份，部署后再装所拍备份含本插件条目；
   restore"最新优先"取备份还原 → 残留指向已删 DLL 的条目 → SxS 解析失败。
   修复：deploy 只做**干净备份**（配置/清单无本插件条目时才备份，重装不覆盖）。
3. **无条件兜底清理**：restore 备份还原后**一律**再执行手术式清理
   （XML 移除 → 解析失败降级正则文本移除），脏备份也无法留下残留。
4. **try/catch 全覆盖**：DLL/PDB 删除、注册表删除、去只读、备份还原、XML 保存
   均有 catch，CAXA 进程锁文件不再中断整个卸载流程，重跑即可补删。
5. **内嵌清单检测**：deploy 新增 `Test-EmbeddedManifest`（kernel32
   `FindResourceW` 查 RT_MANIFEST ID=1/2），无内嵌清单的 DLL 不登记
   manifest dependency，并反向清理历史遗留 dependency。

**附带发现的 PS 语法坑**：XmlNodeList 空集合布尔判定为真（须 `Count -gt 0`）；
.NET 方法调用参数内不能直接内嵌 PS 命令（须 `$()` 子表达式）。

**验证**：PS5.1 解析 0 错误 + 单一 BOM；临时目录模拟 15 项断言全部通过
（脏备份还原后零残留、损坏 XML 正则降级、干净文件零改动、内嵌清单检测真假两例、
无备份直接清理）。脚本已同步 bin 输出目录。

## 十、SC 前缀自动回退残留修复（2026-10-23）

**现象**：插件依然将 "SC图号" 内容填写到 "图号" 列。

**根因**：之前只删了两处 SC 回退逻辑，漏了 MainForm.cs 里三处关键残留——
RefreshPropertyValues 的 candidates 列表自动拼 SC 前缀、
刷新循环内的 scName fallback 查询、
ResolveTargetPropName 的 fallback 也拼 SC 前缀。
三者配合导致：表头 "图号" → 插件偷偷回退查 "SC图号" 拿到值 → resolvedPropNames
缓存成 "SC图号" → 后续写回也用 "SC图号"。

**修复**（MainForm.cs 三处，严格精确匹配）：
1. `candidates` 列表改为直接 `new List<string>(propertyNames.Values)`，不再自动拼 SC 前缀
2. 删除 scName 构造和 fallback 查询块，`resolvedPropNames[colIdx] = header` 始终存表头原名
3. `ResolveTargetPropName` fallback 从 `header.StartsWith("SC") ? header : "SC" + header` 改为直接 `return header`

**验证**：dotnet build Release x64 0 错误，DLL 已部署到 CAXA 3D 2027 bin 目录。

## 十一、并入 caxa3dmytoolsaddin 集合体（2026-10-27）

- 入口类由 `IZAddinServer` 改为 `AttributeManagerPlugin : IPlugin`（仍位于 `Addin.cs`）：
  `Initialize(IPluginHost)` 保存 `AddinSite` 与 `PluginDirectory`、预建隐藏窗口；`Activate()` 显示；`Shutdown()` 释放。
- 删除 Ribbon 注册（`CreateCommandHandler` + `ControlBar` + `RibbonBar.AddButton`）及图标资源；功能区只保留 Host 的「我的工具箱」（集合体铁律 1/4）。
- 删除 `[Guid]` / `[ProgId]` / 类级 `[ComVisible(true)]`；程序集级 `ComVisible(false)`，插件 DLL 不内嵌 SxS 清单。
- 默认表格 `caxa3dManagermodle.xlsx` 改为优先用 `IPluginHost.PluginDirectory` 定位（合并后 `AppDomain.BaseDirectory` 是 CAXA bin）；首次运行仍复制到 `%LocalAppData%\caxa3dAttributeManageraddin\`。
- 主窗口默认 `TopMost=true`，顶部「窗口前置」蓝/灰态开关；隐藏“连接状态”显示（集合体铁律 2/3）。

## 十二、共享属性表复核（2026-10-29）

| 插件 | 写入 | 读取 | 结论 |
| --- | --- | --- | --- |
| `caxa3dAttributeManageraddin` | 共享 `GetCustomPropMgr(selector) + IsSharedMgr` | 共享 | 一致，未改 |

- `TargetFields` 以 `SC图号 / SC名称 / SC规格 / SC材料` 为核心，全部落共享表；与 `caxa3dcodenameextraction`、`caxa3dsetMaterial` 对齐。
- 跨插件共性风险（取不到共享表时会回退第一个管理器）见《caxa3dsetMaterial-项目总结》第十二节。

