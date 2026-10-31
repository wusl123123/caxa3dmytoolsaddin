# caxa3dminitool 项目总结

- 中文名称：CAXA3D小工具合集
- 交付形态：`caxa3dminitool.dll`（.NET Framework 4.8 / x64 / 强命名 / 内嵌 SxS 清单） + `安装.bat` / `卸载.bat`
- 目标版本：CAXA 3D 2027+（部署脚本会自动扫描 `C:\Program Files\CAXA\CAXA 3D\*` 下所有已装版本，当前本机 2024 / 2027 均已可部署）
- 工作目录：`C:\code\ICAPI\caxa3dminitool`
- 参考工程：`C:\code\ICAPI\caxa3dsetMaterial`（链接 CAXA 与界面、注册/卸载骨架）

## 功能清单（第 1 次迭代）

| 控件/按钮 | 行为 |
|---|---|
| 功能区按钮「小工具合集」 | 注册 `ZControlBar` + `ZRibbonBar`，点击才创建窗口（不在 CAXA 启动时弹窗） |
| 主窗口「窗口前置」 | TopMost 开关，默认锁定最上层，蓝色高亮表示已锁定 |
| 主窗口「增料除料切换」 | 取当前选中特征 → `IZFeature.OperationType` 在 `Z_UNITE(1)` / `Z_SUBTRACT(2)` 之间互换 → `IZPart.Update()` 重建 → **回读 OperationType 判定成功** |
| 选中状态条 | 实时（2s 定时）显示 `当前设计环境.ics---零部件名（虚拟 / 外部链接）`，零件附带「内核：ACIS/Parasolid」，特征则附「特征：名称、类型、增料/除料」 |
| 日志区 | 保存日志 / 清理日志；同时写 `%TEMP%\caxa3dminitool_trace.log`（进程启动时清空） |

## 功能清单（第 2 次迭代：内核切换）

| 控件/按钮 | 行为 |
|---|---|
| 「切换Parasolid内核」/「切换ACIS内核」 | 按 `caxa3dsetMaterial` 的装配体穿透规则抓叶子零件 → 逐个 `IZPart.KernelType = target` → 全部写完再 `IZSceneDoc.Update()` → 对每个已改零件**回读 KernelType 判定** |
| 界面形态（第 3 次迭代） | 只留按钮：窗口前置 + 三个功能按钮平铺（无分组框、无状态标签、无进度条、无日志区、无停止键）；结果全部写文件日志 `%TEMP%\caxa3dminitool_trace.log` |
| 计时器分片 | 定时器每 tick 处理 25 个零件，界面保持可响应；关窗即置停止标志，不强行保存、不强关图纸 |

穿透规则（与 caxa3dsetMaterial 同口径）：
- 装配体判定：`elem is IZAssembly / IZAssemblyProperty` 或 `elem.Type.ToString()` 含 `Assembly`；
- 装配体 → `GetChildrenZArray()` 递归；
- 叶子 → 直接收，`HashSet<IZElement>` 跨节点去重；
- 无选中 → `IZSceneDoc.GetChildElements()` 穿透整棵树；
- 轻量化零件写前 `GetBody(true)` 强制加载（对应内置工具的「加载所有零件」）。

## 功能清单（第 3 次迭代：元素工具三连）

| 控件/按钮 | 行为 |
|---|---|
| 「选中取消压缩」 | 按**切换内核的装配体穿透规则**抓零部件（无选中则穿透整棵树）→ 逐个判 `GetStateStatus(Z_SUPPRESSED)` → `SetSuppressState(false)` → `IZSceneDoc.Update()` → **回读 Z_SUPPRESSED 判定** |
| 「更新包围盒」 | 选中元素（无选中则全树）逐个 `as IZSizeBox` → 检查 `PermitResetSizeBox` → `Reset(false)` → `IZSceneDoc.Update()`；回读 `Length/Width/Height` 判定 |
| 「全选内部链接零件」 | 种子=选中节点（无选中则全树）→ `IZSceneElement.HasInternalLinks(out bool)` + `GetInternallyLinkedElements()` 收集 → 清空选区后逐个加入；回读选区计数判定 |
| 日志区（第 4 次迭代） | 参照 caxa3dsetMaterial：底部 Dock=Bottom 只读 TextBox（Consolas 9），`Diagnostics.UiLogSink = AppendLog`，所有步骤/判定实时可见，同时写 trace 文件 |
| 「导入3D曲线坐标数据」（第 5 次迭代） | 选 txt → 逐行解析 X Y Z（空格/逗号/分号/制表符分隔，`#` `//` 注释，单位毫米）→ `IZSceneDoc.CreateWire()` → `IZWire.CreateInterpolationSpline(double[] 平铺XYZ, dir4, dir4, 0)`（官方样例 SelectionTool.cs:486-490 的写法）；毫米按 ×0.001 换算内部单位；失败回退 `CreateLines`；判定=曲线数增加 + `GetInterpolationSplinePointsCount` 回读 |
| 坐标文件示例 | `坐标数据示例.txt`（项目根目录）：25 点螺旋线，附格式说明注释 |
| 「移除透明度」 | 已按用户要求移除（按钮、处理逻辑、`ElementsTool.RemoveTransparency` 全部删除；API 映射仍保留在 `icm\Catalogs\ICM Utils-API映射.md` U10 供以后复用） |
| 「锚点移动到重心」（第 6 次迭代，原名「三维球移至重心」） | 选中零件（或装配体）→ `IZPartProperty.GetMassProperties(density=1.0, false, 1e-4, out acc, out mass, out center, out inertia)` 取重心（失败自动改 density=0 用零件自身密度；装配体走 `IZAssemblyProperty`）→ `IZSceneElement` 移锚（⚠零件**不实现** `IZEDOSiteElement`，见 M14）。移锚两策略：A `AnchorTransform` 平移 += Rᵀ·(重心−锚)（几何不动）；B `PositionTransform` 平移 = 重心。判定=**锚父系位置≈重心（≤0.05mm）且 `GetTransformToGlobal` 平移变化≈0（≤0.1mm）**；B 若把几何搬走立即回滚 `PositionTransform`。**2026-10-31 按用户要求去掉 `ActivateTriball` 三维球复位，只做重心计算 + 移锚**（按钮文案同步改为「锚点移动到重心」）。**同日整体移除**（按钮 + `MoveTriBallToCog` + `AnchorTool.cs`）；**同日 10:57 再从源工程移植新版「锚点移至重心」**：双写补偿方案（`posM.t := 重心`、`anchorM.t += Δ`，两项位移在实测模型下严格抵消），不再有三维球复位，方法名 `MoveAnchorToCog`、按钮字段 `_btnAnchorToCog`，失败回滚两矩阵并回读确认 |
| 「获取点坐标」 | 拾取顶点在选择集中返回的是**零件**而非顶点对象；正确链路=`IZSelectionMgr.GetSelectedVerticesCount(elem)`→`GetSelectedVertices(elem)`（顶点ID数组）→`IZPart.GetBody(true)`→`body as IZBodyVertex`→`GetPosition(vid)`（体局部坐标米）→`IZSceneElement.GetTransformToGlobal` 矩阵合成全局。首个成功点全局坐标复制到剪贴板 |
| 「将零部件添加到BOM」（**开发暂停**） | `IZPart/IZAssembly.IncludedInBOM = true` + 回读确认。⚠实测（2026-10-31）：put 调用成功但值**静默不生效**，三通道回读（属性/Update后/GetBOMSetting 返回值）全为 false，已回滚至简单版并暂停，见 M15。「输出范围」单选对应隐藏成员 `SetPartLinkAllIns(bool)`（文档 0 收录，ildasm 挖出，DispId 42~46） |

ICM 原条目（同一 ProgID `IPROShapeUtils.ShapeUtils`，本机未注册，只能走 API）：

| ICM 名称 | ICM 方法 | API 映射 |
|---|---|---|
| 查找压缩元素 | `OnDragDropManageSuppressedParts` | `IZElement.GetStateStatus(Z_SUPPRESSED)`(槽17) / `SetSuppressState`(槽16) / `IZUtility.SetSuppressStateMultiple`(槽16) |
| 更新包围盒 | `OnDragDropResetSizeBox` | `IZSizeBox.Reset(bool)`(槽30) + `PermitResetSizeBox`(槽26)，元素 QI 得 IZSizeBox |
| 全选内部链接零件 | `OnDragDropSelectLinkedElements` | `IZSceneElement.HasInternalLinks`(槽23) / `GetInternallyLinkedElements` / `IZSelectionMgr.AddElementToSelection`(槽20) |

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

## 技术突破点 / 新验证的 API 用途

- `IZFeature.OperationType` 读写可用：`get_`/`put_OperationType`，`Z_UNITE=1`、`Z_SUBTRACT=2`（与需求文档实测值一致）；切换后必须 `IZPart.Update()` 才反映到几何。
- `IZElement.GetOwnerPart()` 返回 `IZElement`，QI 到 `IZPart` 即可拿到所属零件，用于批量重建（同一零件只 Update 一次）。
- `IZPart.GetExternallyLinkedInfo(out bool)` 返回外链路径，可用于选中状态里区分「虚拟 / 外部链接」。
- `IZSceneDoc.SelectionMgr.GetSelectedElementsZArray()` 在部分上下文取不到真实选中项，`GetTrueSelectedElements()` 作为兜底（两者都做过空集判断）。
- 特征类型白名单（`eZElementType`）覆盖拉伸/旋转/扫描/放样/抽壳/圆角/倒角/布尔/曲面/钣金；不在白名单的元素直接提示「不是可切换的特征」，避免无意义写入。
- 内核 API：`IZPart.KernelType`（槽 get=11 / put=12，`eZKernelType`：`NOKERNEL=0, ACIS=1, PARASOLID=2, LITE=3`）；新建零件默认内核走 `IZBaseAppOptions.Parts` → `IZPartsOptions.DefaultKernelType`（get=11 / put=12）。官方样例 `ModelingHelperSupport.cpp::ConvertElementKernel` 即 `put_KernelType` + 递归装配 + 最后 `IZSceneDoc.Update()`。
- CAXA 内核切换 UI 的实际承载者是内置 Add-in `SwitchKernel.dll`（`IronCad.Addin.config` 里 `system=true`，HKCU `Applications\Switch Kernel`），其命令名（`SwitchKernel.All.PK/.AK`、`SwitchKernel.Sel.ACIS`、`SwitchKernel.Selected.PK`、`SwitchKernel.Suppress.*`、`SwitchKernel.loadAllPart`）只存在于 DLL 字符串里，命令表查不到 → 只能走 API。

## 部署说明

1. 双击 `C:\code\ICAPI\caxa3dminitool\bin\x64\Release\net48\安装.bat`（会自动 UAC 提权，脚本会自我复制到 `%ProgramData%\caxa3dminitool` 再运行）。
2. 启动 CAXA 3D，在功能区看到「小工具合集」按钮；点开窗口后布局：顶部「窗口前置」+ 选中状态条，中部「增料除料切换」，下部日志。
3. 日志：`%TEMP%\caxa3dminitool_trace.log`。
4. 卸载双击 `卸载.bat`，会删除 DLL/PDB、配置节点、清单依赖与注册表键，并打印残留检查。

## 已否决清单

- （暂无）

## 待验证 / 下一步

- 首次实机验证点：选中拉伸特征后点「增料除料切换」，看日志是否出现「重建后回读一致」，并观察模型几何是否真的发生变化。
- 若 `IZFeature` QI 在实机上失败，日志会留下 `FindFeatureByElement` 的 trace，可按 trace 结果决定是否改用 `IZRayTestResult.HitFeature` 通道。
