# caxa3dmytoolsaddin 项目总结（集合体）

> 定位：CAXA 3D 内置 Add-in「集合体 + 统一工具箱」。CAXA 只登记一个 Add-in，主程序反射加载 `plugins/` 下各子插件，功能区只保留一个「我的工具箱」按钮。
> 维护约定：完善子项目后，相关总结直接写入对应子项目总结；本文件只记录集合体架构、部署与跨插件铁律。
> 建档 2026-10-22；最近整理 2026-10-30。

## 一、目标

- CAXA 只登记一个内置 Add-in：`caxa3dmytoolsaddin`。
- 主程序不提供业务按钮，只负责反射加载子插件；子插件保留自己的解决方案，统一由主工具窗口入口。
- 子插件源码集中在 `plugins/`，编译产物汇总到 `src/plugins/`，交付一键安装/卸载。

## 二、架构与目录

```
caxa3dmytoolsaddin/
├── Lib/                       ICApiAddin.snk + interop.ICApiIronCAD.dll
├── build.ps1                  一键编译 + 汇总插件输出
├── deploy.ps1 / 安装.bat      一键部署（自动提权）
├── restore.ps1 / 卸载.bat     一键卸载（手术式清理）
├── package.ps1 / 打包.bat     无源码交付打包
├── src/
│   ├── Contracts/             契约类库（不引用 CAXA 类型）
│   ├── Host/                  caxa3dmytoolsaddin 主程序
│   └── plugins/               编译产物汇总（主程序运行时扫描）
└── plugins/
    ├── Caxa3dcudeaddin/                  立方体工具
    ├── caxa3dAttributeManageraddin/      自定义属性管理器
    ├── caxa3dcodenameextraction/         带图改名（含 CRX 桥子工程）
    ├── caxa3didcskill/                   后台进程清理
    ├── caxa3dsetMaterial/                材料工具
    └── caxa3dTreeBom/                    树形BOM
```

运行期布局：

```
C:\Program Files\CAXA\CAXA 3D\2027\bin\
├── caxa3dmytoolsaddin.dll (+ .pdb)
├── Caxa3dMyTools.Contracts.dll
└── caxa3dmytoolsaddin\plugins\<插件名>\<插件名>.dll
```

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
- 主窗口顶部只有「窗口前置」开关（默认开启）；中间 `FlowLayoutPanel` 从 `PluginManager.Plugins` 动态生成子插件按钮；不显示连接状态。
- 子插件实现 `Activate()`，由主窗口按钮调用 `Show()+Activate()`。

### 3.5 子插件迁入模式（统一清单）

1. 删除原 `Addin.cs` / `Program.cs` 入口，改用 `Plugin.cs`（或同名文件内新类）实现 `IPlugin`。
2. 删除 Ribbon/ControlBar 注册、`Icons`、`ConvertImage.cs`、`stdole` 引用。
3. 资源路径改为 `IPluginHost.PluginDirectory` 或 `Assembly.GetExecutingAssembly().Location`；不用 `AppDomain.BaseDirectory`（= CAXA bin）。
4. 保留原长任务执行框架与 COM 调用上下文，不做后台线程化“优化”。
5. 删除原独立 `安装.bat` / 卸载脚本，避免把无内嵌清单的 DLL 登记进 config/manifest。
6. 构建/打包/部署登记：`build.ps1` 加插件与 `Extra` 扩展名、`package.ps1` 白名单补扩展名、`deploy.ps1` legacy 清单补旧 DLL/目录/CLSID。

### 3.6 为什么必须是集合体

每个 .NET Add-in 都内嵌 interop 类型并各自带 SxS 清单；`mt.exe` 会为同名 progid 生成不同 CLSID，同一激活上下文合并失败 → CAXA 无法启动。集合体从根上解决：只有主程序是 Add-in，插件 DLL 不内嵌任何清单。

## 四、集合体铁律（1–14）

1. **Host 拥有唯一 Ribbon 按钮。** 子插件不得自行注册 Ribbon/ControlBar，否则破坏「功能区只有一个我的工具箱」。
2. **子插件窗口默认 `TopMost=true`。** 用户在 CAXA 内需边选零部件边看窗口；按钮提供开关。
3. **不显示连接状态。** 主窗口与子插件窗口都不显示 CAXA 连接状态；选中项显示格式：`当前设计环境.ics---零部件名`（无外链显示“(虚拟)”）。
4. **子插件实现 `Activate()` 而非 `CreateRibbonButton()`。** `Initialize` 只做资源准备，`Activate` 负责 `Show()+Activate()`。
5. **迁入必须删除原 `Addin.cs` / `Program.cs`。** 残留 `[ComVisible(true)]`、`[Guid]`/`[ProgId]` 会污染 Host 清单或造成 COM 注册冲突。
6. **资源文件用 `PluginDirectory` 或 `Assembly.Location` 解析。** 不用 `AppDomain.BaseDirectory`（CAXA bin）。
7. **mt.exe 清单必须用 `local-name()` 查询。** `$m.assembly.$tag` 在 manifest 带 xmlns 时全部返回空，会导致 interop 的 `clrClass/clrSurrogate` 静默残留 → SxS 合并失败、CAXA 起不来。
8. **部署后必须抽出 Host DLL 的内嵌清单验证。** `mt.exe -inputresource:host.dll;#2 -out:probe.xml`，确认 clrClass 只有 `caxa3dmytoolsaddin.Addin`；bin 下的 `.dll.manifest` 不能作为最终状态依据。
9. **`.ps1` 必须恰好一个 UTF-8 BOM。** 无 BOM → PS5.1 按 GBK 解码吞引号；多 BOM → 注释块/`param` 失效（`The term '<#' is not recognized`）。入口脚本无法自校正，打包/交付前做字节级检查；`package.ps1` 的归一化清单必须包含 `build.ps1`。
10. **卡顿先归因，再改代码。** 用日志时间戳 + 线程号判断阻塞在谁的调用里；数据必须自洽；磁盘占用低却长等待 = 等待型延迟；用纯手动流程作最终判据；环境问题不写进插件。
11. **新增目录库 = 文件 + config + 注册表登记（或放到注册表已指向的绝对路径），三者缺一不可。** 只加 `.icc.config` 一律视为未注册。
12. **接手外部 .icc 先解析 MemberData。** 含 `IPRO*` / 第三方程序集类型的条目，先确认程序集在盘、类型可加载，再谈目录显示。
13. **CAXA COM 对象只能在 UI 主线程访问，严禁 `Task.Run` / `BackgroundWorker`。** 长任务留在 UI 线程，用限流 40ms 的 `Application.DoEvents()`（`PumpUi`）保持响应 + `CancellationTokenSource` + 停止按钮。
14. **迁入集合体后禁止“顺手优化”独立 Add-in 的线程模型。** 可改资源路径、入口契约、删 Ribbon；不改长任务执行框架与 COM 调用上下文。

> 铁律 10 的实例见《caxa3dcodenameextraction-项目总结》第 10 节；铁律 13/14 的实例与正反代码见《caxa3dTreeBom-项目总结》第十节。

## 五、部署、登记与打包

### 5.1 四个登记点（缺一即静默不加载）

1. DLL/PDB → `<CAXA>\<年份>\bin\`
2. `IronCad.Addin.config` → `<AddIn inprocserver="dll名" siteclsid="CLSID" system="true">`
3. `IronCad.exe.manifest` → `<dependency><dependentAssembly><assemblyIdentity name=版本=PKT=amd64>`
4. 注册表 → `HKCU\SOFTWARE\CAXA\CAXA 3D\<verTag>\Applications\<addin名>`

- 版本号换算：`年份 - 1998`（2024→26.0，2027→29.0）。
- 版本、publicKeyToken 从编译出的 DLL 反射读取，不写死。
- 无内嵌清单的 DLL 不登记 manifest dependency，并反向清理历史遗留依赖。
- `IronCad.Addin.config` 根节点无命名空间时，`CreateElement(name, "")` 会抛异常导致登记静默失败；按命名空间是否为空选择创建方式。

### 5.2 deploy / restore

- 自动发现所有 CAXA 版本目录（`Get-ChildItem ...\CAXA 3D -Directory` 且存在 `bin`）。
- 提权前先把脚本复制到 `C:\ProgramData\caxa3dmytoolsaddin\`（真实文件系统），用 `-WorkRoot` 传原业务目录。
- 只对“干净状态”做一次覆盖式 `*.bak` 备份；当前文件已含本程序登记时跳过。
- **restore 永不整份回滚 `.bak`**：config/manifest 是所有插件共享文件，回滚会抹掉其他插件登记。改为手术式移除（`local-name()` XML 精确删除 + 无条件正则兜底），可重复执行。
- 删除操作全部 try/catch；CAXA 锁 DLL 时提示退出后重试，最后统一做残留校验。
- 旧独立插件自动撤销：config 条目、manifest 依赖、同名 DLL、按旧 CLSID 匹配的 Applications 键（含中文显示名键）。
- BOM 数量自检（必须为 1）：`$b=[IO.File]::ReadAllBytes($p); $n=0; while(($n+2) -lt $b.Length -and $b[$n] -eq 0xEF){$n+=3}; $n/3`。
- 实测（2026-10-22）：部署后 config 20 条（19 原生 + 1 本程序）/ manifest 依赖 24 条；卸载后回到 19/23，注册表、bin、插件目录零残留。

### 5.3 编译后处理

- Host：`mt.exe -managedassemblyname` → `strip-manifest.ps1` 剥离 `interop.*` 的 `clrClass/clrSurrogate` → `mt -outputresource:#2` 嵌入 → `sn -R Lib\ICApiAddin.snk` 重签。
- 子插件：**不做** mt/sn 后处理，DLL 不内嵌任何 SxS 清单。
- 校验：publicKeyToken = `56647e1eee90f6d4`；内嵌清单仅 1 条自身 `clrClass`。

### 5.4 无源码打包

`package.ps1`（或 `打包.bat`）产出 `dist\caxa3dmytoolsaddin-<版本>.zip`，仅含运行期文件：

```
caxa3dmytoolsaddin-<版本>/
├── caxa3dmytoolsaddin.dll
├── Caxa3dMyTools.Contracts.dll
├── plugins/<插件名>/<插件名>.dll（含 xlsx/xml/txt 等运行期资源，不含 pdb）
├── deploy.ps1 / restore.ps1
├── 安装.bat / 卸载.bat
└── README.txt
```

- 扩展名白名单需随插件新增资源同步扩充（如 `.xml`）；发现 `.cs/.csproj/.sln/.snk/.resx/.props/.targets` 立即中止。
- 新增插件只需编译后放入 `src\plugins\<插件名>\` 再重新打包，无需改主程序。

## 六、里程碑（集合体级）

| 时间 | 里程碑 |
| --- | --- |
| 2026-10-22 | 集合体首版：Host + 契约 + 2 个子插件；只有主程序带 SxS 清单，插件 DLL 无清单 |
| 2026-10-22 晚 | 部署/卸载脚本重构：单 BOM、干净备份、正则兜底、try/catch、ProgramData 提权；实测部署 20/24、卸载回 19/23、零残留 |
| 2026-10-27 | 统一工具箱：Host 唯一 Ribbon + `MainToolboxForm`；4 个子插件迁入并改 `IPlugin.Activate()` |
| 2026-10-27 | 修 `mt.exe` xmlns 导致 `strip-manifest.ps1` 静默失效（SxS 启动崩溃） |
| 2026-10-28 | 打包脚本累积 6 个 BOM 失效；BOM 归一化范围扩到 `build.ps1` / `package.ps1` |
| 2026-10-29 | 并入材料工具、树形BOM（6 子插件），补 legacy 撤销与 `.xml` 白名单 |
| 2026-10-29 | 跨插件约定：自定义属性统一写共享表；CAXA COM 必须 STA UI 线程 |
| 2026-10-29 | ICM Utils.icc 调查：结论“不可用”；icc 解析工具固化到 `_probe-tools` |

### 验证结果

- 2026-10-22：Host 强名称有效，内嵌清单含且仅含 `clrClass caxa3dmytoolsaddin.Addin`；两个插件 DLL 无内嵌清单；反射确认均为 `IPlugin` 实现，`ZAddinSite` 类型等价为 true。
- 2026-10-29：6 个插件工程 + Host 全部编译通过（0 错误）；两个新插件 DLL 各恰好暴露 1 个 `IPlugin`；`MainForm(null)` 冒烟构造成功，种子资源播种正常。

## 七、子项目索引

| 子项目 | 总结 | 当前重点 |
| --- | --- | --- |
| Caxa3dcudeaddin | [Caxa3dcudeaddin-项目总结](plugins/Caxa3dcudeaddin/Caxa3dcudeaddin-项目总结.md) | 原点建块、vtable 槽位、子进程兜底 |
| caxa3dAttributeManageraddin | [caxa3dAttributeManageraddin-项目总结](plugins/caxa3dAttributeManageraddin/caxa3dAttributeManageraddin-项目总结.md) | xlsx 属性表、共享表精确匹配 |
| caxa3dcodenameextraction | [caxa3dcodenameextraction-项目总结](plugins/caxa3dcodenameextraction/caxa3dcodenameextraction-项目总结.md) | 带图改名、CRX 桥、17s 卡顿归因 |
| caxa3didcskill | [caxa3didcskill-项目总结](plugins/caxa3didcskill/caxa3didcskill-项目总结.md) | 后台进程识别与保护 |
| caxa3dsetMaterial | [caxa3dsetMaterial-项目总结](plugins/caxa3dsetMaterial/caxa3dsetMaterial-项目总结.md) | 材料/密度/颜色、共享表、色卡 |
| caxa3dTreeBom | [caxa3dTreeBom-项目总结](plugins/caxa3dTreeBom/caxa3dTreeBom-项目总结.md) | 树形BOM、STA 线程模型、界面布局 |

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

## 九、待验证 / 待办（集合体级）

1. 启动 CAXA 3D 2027，确认功能区只有一个「我的工具箱」按钮，主窗口列出 6 个子插件按钮且均可打开。
2. 装机验证：安装 → 6 个插件 DLL 均被加载（各自 trace 有 `InitSelf/Initialize`）→ 卸载后 config/manifest/注册表/插件目录零残留。
3. 新增子插件时只改 `build.ps1` 清单、`package.ps1` 白名单、`deploy.ps1` legacy 清单三处，主程序无需改动。
4. 各子插件自身的待实测项见对应子项目总结。
