# caxa3dmytoolsaddin-关键实现与实证槽位接口

## 三、关键实现

### 3.1 契约

`Contracts/IPlugin.cs`：

- `IPlugin.Id / DisplayName / Initialize(IPluginHost) / Activate() / Shutdown()`
- `IPluginHost.AddinSite / PluginDirectory / Log()`
- 契约里不出现 `ZAddinSite` 等 CAXA 类型；Host 与插件只靠这个程序集耦合。

### 3.2 主程序

`src/Host/Addin.cs`：

- 只实现 `IZAddinServer.InitSelf / DeInitSelf`，不注册业务按钮。
- 入口类 `Addin` 必须标 `[ComVisible(true)]` + `[Guid]`/`[ProgId]`；Host 内其余公开类型（`PluginManager`、`MainToolboxForm` 等）标 `[ComVisible(false)]`。入口类漏标 → 内嵌清单无 `<clrClass>` → SxS 激活失败 → `InitSelf` 永不执行、无 trace。
- `InitSelf` → `PluginManager.LoadAll()`；`DeInitSelf` → 逆序 `Shutdown()`。
- 单个插件加载失败只写日志，不影响其他插件与 CAXA 启动。

### 3.3 插件加载

- .NET Framework 4.8 没有 `AssemblyLoadContext`：用 `Assembly.LoadFrom` + 全局 `AssemblyResolve`，每个插件目录登记为探测根。
- 契约程序集**强制返回 Host 已加载的那一份**，防止同一 `IPlugin` 出现两份类型（否则 `IsAssignableFrom=false`，插件被当成“未实现契约”）；插件目录里不放契约 DLL。
- 插件目录逐级向上查找：`<bin>\caxa3dmytoolsaddin\plugins`、`plugins`、各级祖先的 `src\plugins`，不写死绝对路径。
- 类型等价：Host 与插件各自内嵌 `interop.ICApiIronCAD.ZAddinSite`，是不同的 `Type`，但 `IsEquivalentTo` 为 true（都有 `TypeIdentifier`），`host.AddinSite as ZAddinSite` 可正常转换。

### 3.4 统一工具箱

- Host 注册唯一 Ribbon 按钮「我的工具箱」→ `MainToolboxForm`。
- 主窗口顶部只有「窗口前置」开关（默认开启）；主体是 `DataGridView` 四列表格（`1. 常用插件` / `2. 独立插件` / `3. 系统工具` / `4. 测试插件`），按 `IPlugin.Category` 分列，不显示连接状态。
- 表格硬约束：`WrapMode=False`、10F 固定字体、行高 36/表头 38、`AutoSizeColumnsMode=Fill` + 逐列实测 `MinimumWidth`，窗口 `MinimumSize` 由四列最小宽之和推导 → 长名称不折行、不缩字号、不被遮挡。
- 空分类显示「（暂无插件）」占位；未知 `Category` 归入常用列，避免插件从界面消失。
- 子插件实现 `Activate()`，由主窗口单元格单击（或 Enter/Space）调用 `Show()+Activate()`。
- `2. 独立插件` 列现有唯一入口 `caxa3didcskill`（显示名「后台进程清理」）：该插件 DLL 只是**启动器**，不实现清理逻辑、不引用 ICAPI、不建窗口；`Activate()` 找到同目录的 `caxa3didcskill.exe`，已有实例则 `ShowWindow+SetForegroundWindow` 前台化（工具带定时清理，多开会并发清理同一批进程），否则 `Process.Start` 拉起。进程外执行是规则 16.6 的硬要求。
- `3. 系统工具` 列现有 `caxa3dsetbackup`（显示名「设置备份」）：原独立 Add-in 只移植内置插件内核（`MainForm` / `BackupEngine` / `CaxaAutomation` / `Diagnostics`），备份/还原为纯文件 + 注册表操作、不触碰 ICAPI COM，Ribbon 入口改为工具箱单元格；原 `exe\` 独立 EXE 子工程不迁入。

### 3.5 子插件迁入模式（统一清单）

1. 删除原 `Addin.cs` / `Program.cs` 入口，改用 `Plugin.cs`（或同名文件内新类）实现 `IPlugin`。
2. 删除 Ribbon/ControlBar 注册、`Icons`、`ConvertImage.cs`、`stdole` 引用。
3. 资源路径改为 `IPluginHost.PluginDirectory` 或 `Assembly.GetExecutingAssembly().Location`；不用 `AppDomain.BaseDirectory`（= CAXA bin）。
4. 保留原长任务执行框架与 COM 调用上下文，不做后台线程化“优化”。
5. 删除原独立 `安装.bat` / 卸载脚本，避免把无内嵌清单的 DLL 登记进 config/manifest。
6. 构建/打包/部署登记：`build.ps1` 加插件与 `Extra` 扩展名、`package.ps1` 白名单补扩展名、`deploy.ps1` legacy 清单补旧 DLL/目录/CLSID。

### 3.6 为什么必须是集合体

每个 .NET Add-in 都内嵌 interop 类型并各自带 SxS 清单；`mt.exe` 会为同名 progid 生成不同 CLSID，同一激活上下文合并失败 → CAXA 无法启动。集合体从根上解决：只有主程序是 Add-in，插件 DLL 不内嵌任何清单。

## 八、专题：ICM Utils.icc 在 2027 不可用（结论摘要）

`icm\Catalogs\ICM Utils.icc` 在 CAXA 3D 2024 可用、2027 不可用。按“复制 `.icc` + 同名 `.icc.config`”路线试过原位置与 `AppData\zh-cn\Catalogs\Scene\` 两处，**均失败**。

实测结论：

1. **文件本体**：OLE 复合文档，`Contents` 声明 29 条、解析 29 条；29 条均无 Brep/HSF，全部是“工具动作”。
2. **条目结构**：每条只有 `MemberData` 流，格式为 `DWORD 1 + BSTR 类型名 + BSTR 方法名`，靠反射调用外部程序集，不是内置命令。
3. **处理器缺失**：涉及 `IPROShapeUtils / IPROSuppressManager / IPROExportUnfold / IPROWire / IPROActiveManager / IPROStatistics`；HKCR 无对应 ProgID，`Program Files` 全盘无 `IPRO*.dll`，2024/2027 安装目录也无类型字符串。
4. **`.icc.config` 不是注册入口**：只提供 `CatalogSet + FileName + DisplayName`；2024/2027 都是 38 个 `.icc` : 38 个 `.icc.config` 一一对应，仅新增 config 不会让目录出现。
5. **真正加载清单在注册表**：2027 的 `HKLM\SOFTWARE\CAXA\New User Settings\CAXA 3D 29.0\LangDependent\zh-cn\Scene Catalogs` 共 19 条，`Cat0014..Cat0019` 指向本机不存在的 `...\Mechanical\Catalogs\...`（悬空路径）。
6. **2024 侧同样没有 ICM 组件**；“2024 能用”应来自另装的 IronCAD Mechanical（ICM）组件，或当时只验证了标签页可见性。

已否决：仅添加 `.icc.config`；把 `ICM Utils.icc` 当作自带几何/命令的独立库（见铁律 11/12）。

待验证：

1. 在 `C:\Program Files\CAXA\CAXA 3D\Mechanical\Catalogs\` 建目录并放入 `.icc + .icc.config`（注册表已指向，无需改键）。
2. 仍不出现，则在 29.0 的 `Scene Catalogs` 追加 `Cat0020` 并把 `Count` 改为 `0x14`（改前导出备份）。
3. 可执行性唯一判据：对应 `IPRO*.dll` 能加载、图标出现且点击有业务副作用。

工具与归档：

- 共享解析工具：`C:\code\ICAPI\_probe-tools\caxa-icc-dump.ps1` + `src\IccProbe.cs`（C# 5、零依赖、`StgOpenStorageEx` 只读解析，产物 `out\icc_dump_*.csv`）；全库 10 个文件 232 条 PASS。
- 解析要点：`Contents` 条目尾部长度随 schema 变（schema>=6 为 22B，<=5 为 18B）；子流打开用 `STGM_SHARE_EXCLUSIVE`；独占期间禁止二次打开同名子存储。
- 映射归档：`C:\code\ICAPI\ICM Utils-API映射.md`、`C:\code\ICAPI\ICM Tools-API映射.md`。

