# AGENTS.md — caxa3dmytoolsaddin

CAXA 3D **集合体 Add-in**：CAXA 里只登记一个 `caxa3dmytoolsaddin`，主程序反射加载 `plugins/` 下的独立子插件，功能区只保留一个「我的工具箱」按钮。
.NET Framework 4.8 / x64 / WinForms / C#，全部代码由 AI 编程工具制作（WorkBuddy、Trae、Qoder 免费积分）。

本文件是**动手前必读**：权威口径、构建命令、不可违背的契约、验证清单。发现本文件与规则文件冲突，以规则文件为准并回来修正本文件。

---

## 1. 权威文件与读取顺序

| 顺序 | 文件 | 用途 |
| --- | --- | --- |
| 1 | `C:\code\ICAPI\caxa3d-csharp-二次开发规则.txt`（**v3.5**） | 唯一权威。冲突以它为准 |
| 2 | `caxa3dmytoolsaddin-项目总结.md` | 集合体架构、部署、跨插件铁律、里程碑 |
| 3 | `plugins/<插件名>/<插件名>-项目总结.md` | 该子项目的实现细节与踩坑，改哪个插件先读哪个 |
| 4 | `C:\code\ICAPI\_probe-tools\README.md` + `out\<项目名>\` | 取证工具与证据归档 |
| 5 | 本文件 | 执行层索引 |

**证据优先级：运行日志 > 类型库/反射（`out\tlb\<年份>\members.csv`）> 官方 Samples > 文档 > 猜测。**
只写规则写明或本机实测过的结论；都没有就先做探针，不要直接进生产代码。

技能：本项目默认走 `caxa3d-csharp-addin`（`C:\Users\Administrator\.qoder-cn\skills\caxa3d-csharp-addin`），细则在其 `references/`。

---

## 2. 构建 / 部署 / 打包（只有这四个入口）

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1     # 契约 -> 独立工具 EXE -> 11 个子插件 -> 主程序 -> 汇总 src\plugins
安装.bat                                                # 部署（自动提权，扫描本机所有 CAXA 3D 版本）
卸载.bat                                                # 手术式移除四个登记点，不还原 .bak
打包.bat                                                # dist\caxa3dmytoolsaddin-<版本>.zip（无源码）
```

- 产物路径：Host `src\Host\bin\x64\Release\net48\caxa3dmytoolsaddin.dll`；插件 `plugins\<插件>\bin\x64\Release\net48` → 汇总到 `src\plugins\<插件Id>\`。
- 必须带 `-p:Platform=x64`（`build.ps1` 已带）；`net48` 不许改。
- 不要手工 `dotnet publish`，不要绕过 `build.ps1` 的暂存步骤（插件目录里混入契约 DLL 会导致插件被判「未实现契约」）。
- 交付给用户时**给完整文件夹路径**（`C:\code\...`）+ 具体命令，**不打包 zip**（`打包.bat` 的产物仅供无源码交付场景）。

修改后必跑：

```powershell
powershell -ExecutionPolicy Bypass -File C:\code\ICAPI\_probe-tools\caxa-slot-check.ps1
```

退出码非 0 不算完成（`MISMATCH`/`NOTFOUND` 为致命；`UNMAPPED` 建议补 `// IZXxx::Method` 注释）。当前已知基线：默认扫描根 107 条 / 3 处 MISMATCH 全在 `plugins\caxa3dcodenameextraction\SheetMetalOps.cs` 的死常量上，不要「顺手」把它们复制到新代码。

---

## 3. 目录与运行期布局

```
caxa3dmytoolsaddin/
├── Lib/                  ICApiAddin.snk（不在仓库）+ interop.ICApiIronCAD.dll
├── src/Contracts/        IPlugin / IPluginHost 契约（不引用任何 CAXA 类型）
├── src/Host/             主程序（唯一带内嵌 SxS 清单的程序集）
├── src/plugins/          插件编译产物汇总（构建生成，勿手改）
├── plugins/<插件>/        各插件独立解决方案（11 个，可单独打开）
└── tools/caxa3didcskill/  独立 EXE 源码（进程清理，规则 16.6 要求进程外，不由 Host 加载）
```

```
C:\Program Files\CAXA\CAXA 3D\<年份>\bin\
├── caxa3dmytoolsaddin.dll          主程序
├── Caxa3dMyTools.Contracts.dll     契约（只此一份）
└── caxa3dmytoolsaddin\plugins\<插件Id>\<插件Id>.dll
```

现有插件 Id（11 个）：`Caxa3dcudeaddin`、`caxa3dAttributeManageraddin`、`caxa3dcodenameextraction`、`caxa3dsetMaterial`、`caxa3dTreeBom`、`caxa3dminitool`、`caxa3dsheetmaster`、`caxa3dexbtest`、`caxa3dapigetaddin`、`caxa3didcskill`、`caxa3dCXDFTViewUpdate`。

主工具箱窗口按 `IPlugin.Category` 分三列表格：`1. 常用插件`（`BuiltIn`）/ `2. 独立插件`（`Independent`）/ `3. 测试插件`（`Test`）。当前常用列（6）：`caxa3dcodenameextraction`、`caxa3dsetMaterial`、`caxa3dTreeBom`、`caxa3dminitool`、`caxa3dsheetmaster`、`caxa3dCXDFTViewUpdate`；测试列（4）：`Caxa3dcudeaddin`、`caxa3dAttributeManageraddin`、`caxa3dexbtest`、`caxa3dapigetaddin`；独立列（1）：`caxa3didcskill`（后台进程清理）。

独立工具接入方式（唯一范式）：`plugins\caxa3didcskill\` 是**启动器 DLL**（`Category=Independent`，不引用 interop、不建窗口、不实现任何业务逻辑，`Activate()` 只做「同目录 EXE 存在→单实例前台化/启动」），真正干活的是 `tools\caxa3didcskill\caxa3didcskill.exe`。两者随包部署到同一目录 `<CAXA bin>\caxa3dmytoolsaddin\plugins\caxa3didcskill\`。禁止把进程清理逻辑写回进程内插件（规则 16.6）。

---

## 4. 不可违背的契约

1. **一个 Add-in**：只有 `caxa3dmytoolsaddin.dll` 内嵌 `<clrClass>`，且**只允许一条** `caxa3dmytoolsaddin.Addin`。子插件内嵌任何清单 → 宿主激活上下文合并失败 → CAXA 启动即崩。
2. **一个 Ribbon 按钮**：子插件不得注册 Ribbon / ControlBar / COM。新增界面只能进主工具箱窗口。
3. **`[ComVisible]` 铁律（本仓库是「程序集级 true」变体）**：Host 的 `Addin.cs` 顶部 `[assembly: ComVisible(true)]`，所以 **Host 里每个 `public` 类型都必须逐个标 `[ComVisible(false)]`**（`MainToolboxForm`、`PluginManager` 已标，新增 public 类必须补），否则会被 `mt.exe` 写进清单；能不改 public 就改 `internal`。子插件程序集级 `[assembly: ComVisible(false)]`，且不注册任何 COM。漏标 → 清单多出 `clrClass` 或没有自身入口 → SxS 合并失败、`InitSelf` 永不执行且无日志。
4. **契约单实例**：插件目录不得出现 `Caxa3dMyTools.Contracts.dll`；`AssemblyResolve` 必须把契约请求强制回落到 Host 那份，否则同一 `IPlugin` 两份类型、`IsAssignableFrom` 为 false。
5. **四个登记点缺一即静默不加载**：bin 文件、`Config\IronCad.Addin.config`、`IronCad.exe.manifest`、`HKCU\SOFTWARE\CAXA\CAXA 3D <verTag>\Applications\<addin名>`。
6. **强名称一致**：Host 与全部插件同一密钥，`publicKeyToken = 56647e1eee90f6d4`；清单依赖声明的版本与 token 从 DLL 反射读取，禁止写死。
7. **`Addin.cs` CLSID 固定为 `{6F1A9C24-3B57-4E8D-9A02-5C7D1E8B4F36}`**（`deploy.ps1:50`）。新插件禁止自建 Addin 类，禁止复用本 GUID。
8. **资源路径**：用 `IPluginHost.PluginDirectory` 或 `Assembly.GetExecutingAssembly().Location`，**不用** `AppDomain.BaseDirectory`（= CAXA bin）。
9. **`.ps1` 恰好一个 UTF-8 BOM**，`.bat` 纯 ASCII + `chcp 65001`；改完用 `Parser::ParseFile` 或 skill 的 `scripts/fix-ps1-bom.ps1` 校验。
10. **XML 清单/配置查询必须用 `local-name()`**，否则带 xmlns 时静默返回空。

---

## 5. COM / ICAPI 调用规则

- **槽 = 声明序号 + 7**（IUnknown 3 + IDispatch 4）。禁止 `DispId + 6/+7` 推导：属性 get/set 共用 DispId 却各占一槽。
- 槽位唯一来源：`_probe-tools\out\tlb\2027\members.csv`（`SlotEstimate`），改代码后跑 `caxa-slot-check.ps1`。
- 新接口先锚定已知好成员（`IZDoc.Save=9`、`IZBaseApp.get_ActiveDoc=11`、`IZElement.set_Name=10`），再扩展；一次选错偏移就是进程崩溃。
- ≥2 个 out 参数 → 走 RCW（`Marshal.GetObjectForIUnknown` + `as IZxxx`）；单 IntPtr 直调才用槽位。2027 进程内 vtable 直调可用，跨进程不行。
- **判成功只看权威副作用信号**：`hr=0` 只代表调用成功。写入类必须 `IZPart.Update`（槽 7）后再回读比对；K 因子这类不可回读的，按规则第 32 节 B/C 的判据写日志说明。
- **类型库有 ≠ 运行时可用**：`E_NOTIMPL(0x80004001)`、`TYPE_E_ELEMENTNOTFOUND(0x8002801D)`、`E_FAIL(0x80004005)`、`E_INVALIDARG(0x80070057)` 分别对应「未实现 / 无 ITypeInfo / 上下文不对 / 参数非法」，按规则第 32 节 F 处置，不要重试。
- 单位：长度 = **米**、角度 = **弧度**、板料属性（`GetStockProperties`）= **毫米**；界面显示 mm 需 ×1000。
- COM 对象**只在 UI 主线程**访问，严禁 `Task.Run` / `BackgroundWorker`；长任务用 `Application.DoEvents()` 泵 + `CancellationTokenSource`。
- 长等待链必须有「停止」按钮 + 进程级互斥；停止时**不强行保存、不关图**。`FormClosing` 必置停止标志。

---

## 6. 新增 / 迁入子插件

1. `plugins/<插件>/` 保留独立解决方案，入口改为 `Plugin.cs` 实现 `IPlugin`。
2. 删除原 `Addin.cs` / `Program.cs`（残留 `[ComVisible(true)]`、`[Guid]`、`[ProgId]` 会污染 Host 清单）。
3. 删除 Ribbon / ControlBar 注册、`Icons`、`ConvertImage.cs`、`stdole` 引用；图标参数传 `null, null`。
4. `build.ps1` 的 `$plugins` 加一项；需要额外资源时补 `Extra` 扩展名；产物不在本工程输出目录的（如独立 EXE）用 `ExtraSrc` 指明源路径，暂存缺失会直接抛错。
5. `package.ps1` 白名单补扩展名（`.exe` 已在白名单内）；若来自独立 Add-in，`deploy.ps1` 的 legacy 清单补旧 DLL / 目录 / CLSID；独立 EXE 类工具还要在 `package.ps1` 缺件校验、`deploy.ps1` 的 `[3c/5]` 落位校验、`restore.ps1` 的清理与残留校验三处同步登记。
6. 首次即建 `plugins/<插件>/<插件>-项目总结.md`。
7. 主程序不改。

`IPlugin` 契约：`Id` / `DisplayName` / `Initialize(IPluginHost)`（只做资源准备）/ `Activate()`（负责 `Show()` + `Activate()`）/ `Shutdown()`。

---

## 7. 日志与取证

- 主程序：`%TEMP%\caxa3dmytoolsaddin_trace.log`（每次启动清空）。
- 子插件：`%TEMP%\<插件Id>_trace.log`（9 个业务插件各自一份，命名即插件 Id；`caxa3didcskill` 启动器不建自己的日志文件，只写 Host 主日志）。
- 独立工具：`%TEMP%\caxa3didcskill_trace.log`（`tools\caxa3didcskill`）。
- 关键判据必须写**文件日志**；`AddLog` 类只进 UI 的步骤日志不算证据。
- 专项证据统一归档 `C:\code\ICAPI\_probe-tools\out\<项目名>\`（`probe_*.txt` / `trace_*.log` / `user_*.txt`），**不要散在桌面或 `%TEMP%`**；归档最小集 = 探针报告 + 全量 trace + 被质疑那批 DLL 指纹（大小/mtime/SHA256）。
- 用户报「数据抓错了」时排查顺序：①trace 里有 `set_*` 痕迹 → ②CAXA 里实际加载的 DLL 指纹 → ③最后才 diff 源码（规则坑 114）。

---

## 8. 交付前验证（缺一即上线必炸）

1. `mt.exe -inputresource:...dll;#2 -out:probe.xml` 抽内嵌清单：`clrClass` 恰好 1 条且为自身 Addin，`clrSurrogate`/interop 残留 = 0。
2. `sn -vf` 通过；`publicKeyToken = 56647e1eee90f6d4`。
3. 全部 `.ps1` 单 BOM，`Parser::ParseFile` 0 错误。
4. `caxa-slot-check.ps1` 无新增致命项。当前基线：默认扫描根 107 条 = OK 104 + MISMATCH 3（`plugins\caxa3dcodenameextraction\SheetMetalOps.cs` 第 66/68/69 行三个无调用点的死常量，值 9/31/45 应为 8/28/42）。**本次改动若让致命项 > 3，即未通过**；顺手改成正确值或删除也算合格。
5. 编译 0 error 0 warning（`dotnet build -c Release -p:Platform=x64`）。

**未在真机跑过就不能宣称"可用"**；只能说明「已编译通过、待实测」，并在项目总结第 7 节标注待实测项。

---

## 9. 文档与总结维护

- 项目级：`caxa3dmytoolsaddin-项目总结.md` 只写集合体架构、部署与跨插件铁律。
- 子项目级：`plugins/<插件>/<插件>-项目总结.md` 写实现细节、踩坑、里程碑；**完善子项目后，相关总结直接写入对应子项目总结**，不再往总总结里堆。
- 每次功能迭代记里程碑（日期 + 改了什么 + 实证结论）；每 5 次迭代追加一次完整整理；积分 < 30 强制生成/更新总结。
- 跨项目通用的新坑与新实证回流规则文件：**追加条目、不重排既有节号**（规则文件维护约定）。
- 规则/总结里的数字（槽位、CLSID、计数）必须是查得到来源的实测值，禁止估算填写。

---

## 10. 明确禁止（历史上的崩进程/白干清单）

- 对 CAXA 顶层 MDI 主框架投 `WM_CLOSE`（标题天然含文档名，会整个退出）。
- 主线程 `WaitForExit` / `ReadToEnd`（CAXA 假死）。
- 在非 UI 线程创建/操作窗口或 COM 对象（`CreateHandle`、`Show` 都必须在 CAXA 的 STA UI 线程）。
  本 Host 的窗口是**点击时惰性创建并复用**（`_toolboxForm == null || IsDisposed` 才 `new`），这是既有可用设计；单插件独立模板才在 `InitSelf` 线程预创建。
- `restore` 整体还原 `.bak`（`IronCad.Addin.config` / `IronCad.exe.manifest` 是所有插件共享的）。
- 用 `DispId+N` 推导槽位；用 `ITypeInfo` 枚举 `IZ*` 接口成员（`GetTypeInfoCount()=0`，恒 `0x8002801D`）。
- 把契约 DLL 拷进插件目录；让子插件内嵌 SxS 清单；给子插件注册 Ribbon/COM。
- 依赖「命令名」执行内部命令（`Commands.xml` 有登记 ≠ CRX 命令表里有）。
- 已否决路线见规则第 23 节（64 条）与第 32 节 E，**永久不再尝试**。
- 安装/卸载里把「安装根」和「版本目录」放进同一张去重表。注册表 `InstallDir` 指向的是版本目录本身（`<根>\<年份>\`），共用 `$seen` 会让该版本先被记成"已处理的根"，随后扫真根时被跳过 → **整版漏装**（本机曾因此只装 2027、丢掉真实存在的 2024）。必须 `$seenRoot` / `$seenVer` 分离。
- 装/卸脚本验证只跑单版本或只看"程序没报错"。本机同时装了 2024(26.0) 与 2027(29.0)，**必须两版本都验证**（发现列表、落位、注册表、卸载残留），且模拟根烟测要用 3098/3099 这类假年份隔离真实 `26.0`/`29.0` 键。
