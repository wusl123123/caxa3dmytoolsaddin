# caxa3dAttributeManageraddin-关键实现与实证槽位接口

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

## 十二、共享属性表复核（2026-10-29）

| 插件 | 写入 | 读取 | 结论 |
| --- | --- | --- | --- |
| `caxa3dAttributeManageraddin` | 共享 `GetCustomPropMgr(selector) + IsSharedMgr` | 共享 | 一致，未改 |

- `TargetFields` 以 `SC图号 / SC名称 / SC规格 / SC材料` 为核心，全部落共享表；与 `caxa3dcodenameextraction`、`caxa3dsetMaterial` 对齐。
- 跨插件共性风险（取不到共享表时会回退第一个管理器）见《caxa3dsetMaterial-项目总结》第十二节。

