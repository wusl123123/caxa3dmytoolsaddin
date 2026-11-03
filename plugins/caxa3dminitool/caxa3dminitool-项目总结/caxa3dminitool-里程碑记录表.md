# caxa3dminitool-里程碑记录表

## 里程碑记录表

| # | 阶段 | 结论 / 踩坑 | 处理方式 |
|---|---|---|---|
| M1 | 需求解析 | 需求文档把 `IZRayTestResult.HitFeature` 列为首选取特征方式，但用户口径是「选中特征后点按钮」 | 以 `IZSceneDoc.SelectionMgr` 选中集为主通道，`HitFeature` 不用；不引入点击拾取交互 |
| M2 | 工程骨架 | 必须 `net48` + `x64` + `ICApiAddin.snk` 强命名；无 VS 的机器需要 `Microsoft.NETFramework.ReferenceAssemblies` 才能 `dotnet build` | csproj 固化 3 项 + PackageReference |
| M3 | 免注册 COM | Release 后必须走「mt.exe 生成清单 → 剥离 interop clrClass/clrSurrogate → mt.exe 嵌入 ID=2 → sn.exe 重签」四步；`mt.exe` 报 810100b3 警告（嵌入清单破坏强名）是预期的，随后 `sn.exe -R` 即修复 | csproj `EmbedSxsManifestAndResign` Target 固化四步（`AfterTargets=Build`，Release 生效） |
| M4 | 清单核验 | 抽取内嵌清单确认 `<clrClass>` 仅 1 条（自身 Addin），无 `interop.` 条目；`assemblyIdentity` name/version/publicKeyToken=56647e1eee90f6d4/amd64 正确 | `mt.exe -inputresource:...;#2` 抽检 |
| M5 | 特征定位兜底 | 特征树元素 QI 到 `IZFeature` 万一失败，不能直接报「不支持」 | 兜底走 `IZPartFeatureMgr.GetFeaturesCount()/GetFeatureAt(i)`，按「类型 + 名称」匹配同名特征 |
| M6 | 脚本编码 | bat 里调用的是 PowerShell 5.1；中文脚本无 BOM 会乱码/解析错 | 三个 ps1 统一 UTF-8 BOM（已用 `Parser::ParseFile` 校验 0 语法错误）；bat 首行 `chcp 65001` |
| M7 | 卸载安全 | 多个 Add-in 共用 `IronCad.Addin.config` 与 `IronCad.exe.manifest`，不能整份还原 `.bak` | 改造为「手术式移除」：只删本插件的 `AddIn` 节点 / `dependency` 节点 / 注册表键，可重复执行，末段做残留检查 |
| M8 | 内核切换取证 | `ICM Tools.icc` 里的「内核切换」条目是 IronCAD 老目录遗留（COMProgID `IPROKernelMgr.KernelMgr` / 方法 `OnDragDropStartKernelMgr`，该 CLSID 本机未注册，无法实例化） | 查 `IronCad.Addin.config`：真正实现是内置 Add-in `SwitchKernel.dll`（CLSID `{E5F45419-8678-4E0F-BC11-597B6EBFC6EC}`，system=true） |
| M9 | 内核切换命令不可投递 | `SwitchKernel.All.PK/.AK`、`SwitchKernel.Sel.ACIS`、`SwitchKernel.Selected.PK` 等只存在于 `SwitchKernel.dll` 的运行时命令名，`cmd_index`（源自 Commands.xml）里没有 | 按规则 9 不做 `sendStringToExecute`，直接走 `IZPart.KernelType` 写入 + 回读 |
| M10 | 内核批量处理不卡界面 | 大量零件逐个 `Update` 会长时间冻结 UI，且停止键失效 | 定时器分片（tick 处理 25 个，只写属性不做重建），全部写完只调一次 `IZSceneDoc.Update()`；停止=置标志，当前批结束后收尾 |
| M11 | 安装脚本崩溃（2026-10-29） | `C:\Program Files\CAXA\CAXA 3D` 下存在 `Mechanical` 目录且带 `bin`，被 `Get-ChildItem ... \bin 存在` 误判成版本目录；随后 `'{0}.0' -f ([int]$year - 1998)` 对非数字目录名 `[int]` 转换抛异常 → 提权窗口瞬间关闭，用户只看到「报错」 | 版本发现改为「`^\d{4}$` 目录名 + `bin` + `bin\IronCad.exe` 三者同时成立」；版本号改为读 `IronCad.exe` 的 `ProductVersion`（实测 2024→26.0、2027→29.0），失败回退年份推算；`deploy.ps1` 加全局 `trap` + `Start-Transcript` 到 `%TEMP%\caxa3dminitool_install.log`，绝不再静默退出 |
| M12 | ps1 多重 BOM 导致整个脚本失效 | `deploy.ps1` / `restore.ps1` / `strip-manifest.ps1` 行首有 **6 个** U+FEFF（`ef bb bf` 重复），PS 5.1 把 `param(` 解析成命令名 `?????param` → `Test-Path : 参数 Path 为空`、`New-Object 找不到构造函数`、`InvokeMethodOnNull` 一连串报错（构建日志里可见），`strip-manifest.ps1` 实际从未生效 | 三个脚本统一 `TrimStart([char]0xFEFF)` 后重写为**单个** UTF-8 BOM；重建后 `dotnet build` 的 PS 报错消失；`dotnet` 构建日志里出现 `InvokeMethodOnNull`/`CannotFindAppropriateCtor` 就说明 ps1 编码又坏了 |
| M13 | 文档 C# 段 ≠ interop 真实签名（2026-10-30） | ICAPIHELP 写的 `ref` 在 tlbimp 生成的 interop 里多数是 `out`：`GetMassProperties`、`GetTranslation`、`GetValue` 全要 `out`（`GetValue` 更是直接返回值 `double GetValue(row,col)`）；`SetAnchorTransform` / `SetPositionTransform` 的形参是 **coclass `ZMathMatrix`** 而不是 `IZMathMatrix`（文档只写 IZMathMatrix）；`ActivateTriball` 第二参数是 `ZArray` 不是 `IZArray`；`IsTriballActive()` 无参、返回 bool | 按编译器报错逐条校正；写回接口时一律 `mat as ZMathMatrix` 再传、判空报错。**教训：抄文档签名必编译验证，`IZxxx` 与 `Zxxx` 不可混用** |
| M14 | `IZEDOSiteElement` 是死路；文档 [out] 单参数访问器多为返回值形式（2026-10-31） | 实测零件 QI `IZEDOSiteElement` 返回 null（该接口只服务 EDO 外部对象），场景元素（零件/装配）必须用 `IZSceneElement`（成员一一对应）。拾取顶点选择集返回的是零件不是顶点。另外 `GetPosition`/`GetBody`/`GetSelectedVertices*` 这些文档标 [out] 单参数的，interop 里全是**返回值形式** | 锚点工具整体换 `IZSceneElement`；顶点走 `GetSelectedVertices→GetBody→IZBodyVertex.GetPosition(vid)` 链路。**教训：EDO 接口≠场景接口；先想 ZSceneElement 再试别的** |
| M15 | `IncludedInBOM` put 静默无效——BOM 按钮开发暂停（2026-10-31） | `IZPart/IZAssembly.IncludedInBOM = true` 调用成功（无异常）但值不生效；三通道回读（属性 / `scene.Update()` 后 / `GetBOMSetting` 返回值）全为 false，写前写后件号/描述均空。已排除 vtable 错位（tlbimp 重生成 2027 interop 对比：IZPart 前 57 槽位一致，新增成员全在末尾）。怀疑方向：写入需 undo 事务/写到共享表拥有者/2027 运行时改走其它通道 | 降级处理：回滚至简单版（写+回读，失败如实报告），暂停开发。后续可试：`IZBaseApp.Export3DBOMSettingData/Import3DBOMSettingData`（XML 导入导出 BOM 设置）、先在 UI 手动勾选后用 GetBOMSetting 对比快照定位真实存储位置 |

