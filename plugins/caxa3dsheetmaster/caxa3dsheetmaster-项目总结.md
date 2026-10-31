# caxa3dsheetmaster（CAXA 3D 折弯管理大师）项目总结

> 目标：在 CAXA 3D 设计环境内统一设置钣金 **K 因子** 与 **折弯扣除**，界面参照 `C:\code\csharp-solidworks折弯管理器` 复刻，API 全部换成 CAXA 3D 的 ICAPI。
> 形态：内置 Add-in（免注册 COM clrClass，无 CRX），.NET Framework 4.8 / x64，交付 DLL + 一键安装/卸载脚本。

---

## 一、版本与工程

| 项 | 值 |
| --- | --- |
| 目标框架 | net48 / x64（CAXA 3D 为 64 位进程） |
| 工程 | 必须 `-p:Platform=x64`，输出 `bin\x64\Release\net48` |
| Add-in CLSID | `{7A3F9C21-5B4E-4D86-9E0A-3C1B7D5F2E48}`（Addin.cs / 清单 / deploy.ps1 三处一致） |
| 强名称 | `..\Samples\C#\References\ICApiAddin.snk`，嵌清单后必须 `sn -R` 重签 |
| 日志 | `%TEMP%\caxa3dsheetmaster_trace.log`（每次 CAXA 启动时清空） |

编译后处理（csproj `EmbedSxsManifestAndResign`）：`mt -managedassemblyname` → `strip-manifest.ps1` → `mt -outputresource:...;#2` → `sn -R`。
`strip-manifest.ps1` 用于剥离 mt 为内嵌 interop 生成的 `clrClass`/`clrSurrogate`（多插件共存铁律）；本项目因 `EmbedInteropTypes=True` 未产出 interop 条目，实测 removed=0 —— **脚本保留**，换 interop 引用方式时仍有保护作用。

### 交付物（`bin\x64\Release\net48`）

`caxa3dsheetmaster.dll` / `.pdb` / `caxa3dsheetmaster.dll.manifest` / `安装.bat` / `卸载.bat` / `deploy.ps1` / `restore.ps1` / `strip-manifest.ps1` / `caxa3dsheetmaster使用说明.txt`

---

## 二、里程碑记录表

| # | 日期 | 事件 | 关键内容 | 实证结论 / 踩坑 |
| --- | --- | --- | --- | --- |
| 1 | 2026-10-29 | 需求对齐 | 纯设计环境（不用 CRX）；参考 SW 折弯管理器；界面含「连接状态 + 文件」状态区 | 需求文件 `caxa3dsetMaterial-项目需求.txt`（实际项目名 caxa3dsheetmaster，中文名"折弯管理大师"） |
| 2 | 2026-10-29 | API 勘察 | 反射 `interop.ICApiIronCAD.dll` + 2027 探针 `members.csv`/`enums.csv` 交叉核对 | `IZSheetMetalPart`：`get_Thickness`(10, Double)、`get/set_KFactor`(17/18)；`IZSheetMetalBend`：`BendAllowanceType`(15/16, ezBendAllowanceSrc=Int32)、`CustomKFactorUsed`(17/18, Boolean)、`CustomKFactorValue`(19/20, Double)、`BendDeductionValue`(23/24, Double)、`BendAngle`(25)、`InsideRadius`(47)。枚举实测值：Explicit=0 / Kfactor=1 / BendDeduction=2 |
| 3 | 2026-10-29 | 单位口径确认 | 长度 = **米**，角度 = 弧度 | 复用 `caxa3dsheetunfoldnameupdate` 真机日志：2mm 板读出 `Thickness=0.002` ⇒ 界面 mm 需 ×1000；`Thickness2` 才是可写板厚，本项目只读 `Thickness` 显示 |
| 4 | 2026-10-29 | 工程骨架 | `Diagnostics.cs`（文件+UI 双通道、`Suppress` 降噪）、`CaxaSession`、原始 vtable 直调封装、`Addin.cs`（按钮注册）、`AssemblyInfo.cs`、csproj 四步后处理 | 程序集级 `[ComVisible(false)]` + Addin 类 `[ComVisible(true)]`，清单只含 1 个 clrClass，强名称 `sn -vf` 有效 |
| 5 | 2026-10-29 | 核心逻辑 | 设计树递归扫描（`GetTopElement`→`GetFirstChild`→`GetNextChild`）、按「钣金件栈」归属折弯、写入后**即时回读校验** | 展开态零件名以「展开」开头，自动跳过（否则会把展开件当成另一个钣金件重复计数） |
| 6 | 2026-10-29 | 界面 | 连接状态 + 文件（`docName---零部件名`，含 `（外部链接）/（虚拟）` 标注）、表格 12 列、统一修改、单行「应用」、保存、重新生成、停止、进度条、日志区 | 长链路全部走 `PumpProgress`：刷新状态文字 + `DoEvents` + 停止检查；停止抛 `OperationStoppedException`，**不保存文件、不关图纸** |
| 7 | 2026-10-29 | 编译验证 | `dotnet build -c Release -p:Platform=x64` 0 error 0 warning；内嵌清单核对 | 清单内 clrClass=`caxa3dsheetmaster.Addin` CLSID 与 Guid 属性一致；`ct` 无 interp 条目 |
| 8 | 2026-10-29 | 部署脚本 | deploy/restore 完全对齐已验证模板（`caxa3dsetMaterial`）：自动发现所有年份版本、4 个登记点、手术式清理、不提权即自提权、BOM 单份 | 字节级校验 `EF BB BF ×1`；PS AST `Parser::ParseFile` 0 错误 |
| 9 | 2026-10-29 | 真机首轮反馈 | 窗口/连接/文档名/选中零件均正常；`get_Thickness=0.003`（3mm）、`get_KFactor=0.325` 读取成功 ⇒ `IZSheetMetalPart` QI 与槽位正确 | trace 日志暴露致命问题：**设计树里钣金件下只有 STOCK(21)→PROFILE(19)，没有任何 22/49/50 折弯元素** ⇒ 旧「遍历设计树折弯元素」方案必然得 0 条，折弯列表为空 |
| 10 | 2026-10-29 | 折弯枚举改道（突破点） | 改为 `IZSheetMetalPart::GetBendLineIDs()` → `GetBendLineInfoByID()`（半径/角度/方向）→ `GetBendLineSource()`（`crvId` + 来源元素）→ QI `IZSheetMetalBend` 读写系数 | 槽位：GetBendLineIDs=25（返回 **VARIANT**）、GetBendLineInfoByID=26（6 个 out 参数）、GetBendLineSource=29（`UInt32* pCrvID` + `IZElement**`）。设计树折弯元素仅作兜底，且折弯线数 >0 时不再重复采集 |
| 11 | 2026-10-29 | 探针功能 | 新增 `Probe.cs`（partial）：全量 dump 文档/选中/设计树 type-id-name、每个钣金件 `Thickness/Thickness2/KFactor/MinBendRadius/UseDIN6935/ThicknessRefSide/CustomUnfoldLengthValue`、每条折弯线几何+来源+全部 `IZSheetMetalBend` 属性，输出到 `桌面\caxa3dsheetmaster_probe_<时间>.txt` | 探针走独立只读槽位（7/9/15/19/21/23/27 + 36/38），与业务写入链路解耦，便于后续扩展 API 勘察 |
| 12 | 2026-10-29 | 界面调整 | 状态行改 `设计环境：<ics>---<零件名>（虚拟）[钣金零件]`；第二行改扫描汇总；列名对齐 SW 参考（折弯特征名称/基体名称/折弯角度/基体厚度/折弯半径/折弯系数类型/折弯系数数值/…/修改）；新增「探针」按钮 | 编译 0 error，四步后处理（mt→strip→mt→sn）全部通过 |
| 13 | 2026-10-29 | 探针首轮实测（16:57:30 报告） | 确认 `GetBendLineIDs` 在**折叠件**上 `hr=0x80004005 vt=VT_EMPTY`；设计树仅 4 个节点：`根 → 钣金件(20) → 板料(21)`，以及**与零件同级的 `斜接法兰1`（type=0, id=5）**；零件属性：Thickness 3mm、MinBendRadius 3mm、KFactor 0.325、UseDIN6935=False、ThicknessRefSide=1、CustomUnfoldLength=0.1mm | 结论：折弯必须「先 GetFlatPart 取展开件」＋「场景级遍历靠 GetOwnerPart 归属」，两条路同时实现 |
| 14 | 2026-10-29 | 折弯抓取二次改道 | 扫描/写入新增：`GetFlatPart`（槽 12）优先取展开件枚举折弯线；`CollectSceneBends` / `WriteSceneBends` 场景级补扫补写（`GetOwnerPart` 槽 29 归属）；`TryAcquireBendObject` 用 QI 而非 `get_Type` 判定折弯对象；单条修改按「折弯 Id + 属主零件」双重匹配 | 编译 0 error，四步后处理通过 |
| 15 | 2026-10-29 | 探针 v2 | 新增：`IsFolded`(9)、`UnfoldConfiguration`(11)、`GetFlatPart`(12) 展开件折弯线、`GetSiblingPart`(13)、`GetStockProperties`(14)、`ChildrenCount`(15)，**逐节点 QI 矩阵**（IZPart/IZAssembly/IZSheetMetalPart/Stock/Bend/PunchBend/SketchBend + ownerId/owner），以及每个节点的**自定义属性全量导出**（`IZCustomPropMgr`） | 用于最终确认「斜接法兰1 到底实现了哪些接口」「折弯扣除存在哪里」 |
| 16 | 2026-10-29 | 表格数据打通 | `GetFlatPart`→`GetBendLineIDs`→`GetBendLineSource` 链路实测成功，01-13 零件折弯行完整（类型=折弯扣除、值 6.000mm、角度 90°、半径 3mm、来源 type=22） | 折弯线只来自「展开件」，折叠件直接枚举必失败 |
| 17 | 2026-10-29 | 探针闪退修复 + K 写入修正 | ①探针 `GetStockProperties` 由裸槽位直调改为 RCW（`Marshal.GetObjectForIUnknown` +7 个 out 参数）；`GetFlatPart`/`GetSiblingPart` 同步走 RCW；②K 模式不再写 `CustomKFactorUsed`，只写 `BendAllowanceType=Kfactor` + `CustomKFactorValue` | 编译 0 error，四步后处理通过 |
| 18 | 2026-10-29 | 折弯采集改道（弃用折弯线） | 用户实测折弯线（展开件）几何/角度不准确 ⇒ 采集与写入**全部改为主零件（非「展开」件）的折弯特征元素**；删除 `GetFlatPart→GetBendLineIDs→GetBendLineSource` 业务链路 | 编译通过，但**表格回归为空**：实测该模型设计树只有 4 个节点，主零件下没有任何折弯元素 ⇒ 纯树遍历必然 0 条 |
| 19 | 2026-10-29 | 折弯采集最终方案（折弯线仅作索引） | ①板厚与 K 因子改为从**板料属性** `GetStockProperties` 读取（牌号/gauge/厚/K/minR），读不到才回退零件属性；②折弯行只认**「钣金折弯特征」type=22** 且实现 `IZSheetMetalBend` 的元素（压弯 49/草图折弯 50 一律丢弃）；③展开件折弯线**只作定位索引**，几何/角度/半径/系数全部取自折弯特征本身（`BendAngle`/`InsideRadius`/`BendAllowanceType`）；④按来源元素 Id **去重**（多条折弯线可能指向同一折弯特征）；⑤单行修改按「零件 + 折弯线索引」定位折弯特征 | 编译 0 error；表格恢复出数，且板厚/K 与板料表一致（27.钢 gauge=1 → 3mm、K=0.325） |
| 20 | 2026-10-29 | 界面精简 + 折弯改名 + 折弯扣除修复 | ①删除「基体名称」列，列索引整体前移；②「折弯特征名称」列改为可编辑，采集时按「名称+序号」显示（如 `斜接法兰1`→`斜接法兰1`/`斜接法兰11`），点「修改」时把该列文本写入折弯元素 `put_Name` 并回读回填（QI `IZElement` 后写 slot 10），API 拒绝则回显示原名；③折弯扣除写入改为三级兜底：`type→value` → `value→type` → 清 `CustomKFactorUsed` 后重写；④写入前按 `D ≤ 2(R+T) − θR` 校验扣除上限，超限直接给出允许最大值，不再盲写被系统拒绝 | 编译 0 error，强签名有效 |
| 21 | 2026-10-29 | 特征管理器通道（表格空表的根因修复） | 用户指出数据应取自「钣金参数」对话框（零件级板厚/折弯半径/K系数 + 折弯列表）。实测设计树遍历与折弯线来源都拿不到折弯特征 ⇒ 新增 `IZPartFeatureMgr`（IID `26D70B9B-291B-4727-9DA1-E2D7EEC1FA27`，`GetFeaturesCount` slot 7、`GetFeatureAt` slot 9）枚举零件全部特征，只保留 QI 到 `IZSheetMetalBend` 的折弯特征；特征名经 QI `IZElement` 取 `get_Name`（无 IZElement 时用 `IZFeature::get_FeatureIndex` slot 8 兜底）。折弯线索引降级为兜底通道 | 编译 0 error，强签名有效 |
| 22 | 2026-10-29 | 表格命名与改名 | 「折弯特征名称」= 特征真名；同一零件内重名时才追加序号（两遍枚举统计，避免每次重新加载名字越加越长）；点「修改」把单元格文本 `put_Name` 到折弯特征并回读回填 | 同前 |
| 23 | 2026-10-29 | 写入落盘根因修复（关键突破） | 日志实证：折弯特征上 `set_BendAllowanceType`/`set_BendDeductionValue` 全部 `hr=0`，但回读 13 条里忽新忽旧（0.0008 / 0.006 交替）⇒ 特征上的 set_* 只是内存待提交状态，**必须 `IZPart::Update`（slot 7）后才落盘**，几何结果还要 `IZPart::Regenerate`（slot 18）。已在每次写入后 Update、每零件收尾 Regenerate。另：`get_CustomKFactorValue` 在特征对象上恒返回 E_FAIL，K 值无法回读 ⇒ K 模式改用「set hr=0 + type==1」判定并在日志标注 | 编译 0 error，强签名有效 |
| 24 | 2026-10-29 | 界面精简第二轮 | 「折弯特征名称」直取 API 原名（不追加序号、不提供改名回填）；新增只读「系统名称」列（`IZElement::get_Name` 槽 25）；列序：序号 / 折弯特征名称 / 系统名称 / 角度 / 厚度 / 半径 / 当前类型 / 当前数值 / 新类型 / 新数值 / 修改 / 结果 | 同前 |
| 26 | 2026-10-30 | 采集口径改回「设计树」（13→10 修根因）+ 去掉 Unfold | 用户反馈「设计树只有 10 个折弯却抓到 13 个，且点修改会把焦点跳到展开零件」。日志证实：`SM.Unfold hr=0` 后 `GetBendLineIDs` 仍 `0x80004005`，`SM.Refold` 返回 `0x80070057` 失败 ⇒ 展开通道既没用又副作用大（展开件被激活、焦点跳走、还原失败）。特征管理器的 27 个特征里 13 个 QI 到 `IZSheetMetalBend`，多出的 3 个是设计树上不显示的内部折弯。改法：**采集/写入一律走设计树零件子树的 type=22 节点（10 条，与用户所见一致）**，特征管理器只在一个折弯都没采到时兜底；彻底删除 `ForEachBendFeatureByFlat`（Unfold/Refold）与 `ForEachBendFeature` | 编译 0 error，强签名有效 |
| 27 | 2026-10-30 | 13 vs 10 计数修正 + 单条写入改特征管理器 + 去焦点跳转 | 探针确认设计树 API 视图只有 5 个节点（根/零件/板料/斜接法兰），折弯节点不在其中，只能靠特征管理器。特征管理器 13 个折弯全部 90°/R3/K0.45（几何无差异），改用 `IZElement::get_ShowInBrowser`（槽 30）过滤「设计树上不可见」的内部折弯；单条写入新增「特征管理器按序号定位」通道（BendId=特征序号）；**删除写入后的 `IZPart::Regenerate` 与 `RefreshSceneUi`**，避免把激活对象/焦点切到展开件 | 编译 0 error，强签名有效，clrClass=1 |
| 28 | 2026-10-30 | 去兜底（单通道收敛）+ 单条修改修复 | ①采集/写入**只剩零件特征管理器一条通道**：删除 `CollectSceneBends`（场景补扫）、`WriteSceneBends`（场景补写）、`ReadBendRow`、`ApplyCtx` 的设计树折弯写入分支与 `WrittenBendIds`/`Parts` 归集；`ScanBends` 去掉 `skippedBends` 出参；②单条修改**删除「设计树按折弯 Id 定位」通道**（折弯不在 API 视图里，且 BendId 实为特征管理器序号，树通道纯浪费遍历），改由特征管理器按序号唯一定位；③「修改」按钮点击判定 `e.ColumnIndex != 9` 改为 `!= 10`（新增「系统名称」列后列索引后移，旧判断导致点了完全无反应） | 编译 0 error；`sn -vf` 有效；清单仅 1 个 clrClass（`caxa3dsheetmaster.Addin`） |
| 29 | 2026-10-30 | 新增两个勾选列 + 列名对齐 CAXA 对话框 | ①新增「使用最小折弯半径」列（原「折弯半径」右侧）与「使用零件折弯扣除」列（原「折弯系数类型」左侧）；②列名对齐 UI：`基体厚度(mm)`→`板材厚度(mm)`、`折弯半径(mm)`→`内半径(mm)`；③列索引整体重排为 0序号…6最小折弯半径 7零件折弯扣除 8当前类型 9当前数值 10新类型 11新数值 12修改 13结果，`GridCellContentClick`/`ApplyRow`/`FillGrid` 全部同步；④两个勾选在 interop 类型库里**不存在**（`IZSheetMetalBend` 反射无此成员），故先按候选属性名走 `IDispatch::GetIDsOfNames+Invoke` 运行期探测，探不到再用数据推断：内半径==零件最小折弯半径(槽19)→是；同零件全部折弯扣除值一致→是；推断值显示带「(推断)」后缀 | 编译 0 error；强签名有效；推断口径写入日志 |
| 30 | 2026-10-30 | 列精简 + 「使用零件折弯扣除」语义 | ①删除「使用最小折弯半径」列（属性根本不存在，且 IDispatch 名称探测 6 个候选全部未命中）；删除整套 `TryReadComBoolByNames`/候选名/推断标记代码；②「使用零件折弯扣除」改为纯数据判定：类型≠折弯扣除→`—`（不适用）；扣除值==零件继承值→`是`；≠→`否`；零件继承值取该零件第一条折弯扣除行的值，采集时写日志说明；③单条写入后用回读值刷新该行三列（勾选/当前类型/当前数值）；④折弯扣除写不进去时的报错补齐可行动原因："该折弯正在使用零件继承的折弯扣除，需在 CAXA 折弯对话框取消「使用零件的折弯扣除」后才能单独修改"；列序变为 13 列 | 编译 0 error；`sn -vf` 有效；清单 1 个 clrClass |
| 31 | 待实测 | 写入验证 | 待用户安装后确认：13 列表格、勾选列 是/否 与 CAXA 对话框一致、单条 K 修改生效；折弯扣除「是」行按提示在 CAXA 里取消勾选后能改 | 未实测前不得宣称"写入成功" |
| 32 | 2026-10-30 | UI 三项 + 探针扩展 + 多配置恢复 | ①「统一设置」数值框加宽到 110 并支持点击/聚焦全选直接键入；`DataGridView.EditMode = EditOnEnter`，「新数值」单击即进入编辑；②表头 `ColumnHeadersHeight=46` + `WrapMode=True` + 居中，折行文字不再被截断；③「窗口前置」默认开启（`TopMost=true`），置顶时蓝底白字高亮，切换写日志（对齐 `caxa3dmytoolsaddin` 工具箱）；④探针在每个折弯特征追加 `DumpDispatchMembers`（ITypeInfo 枚举真实成员）与 `DumpCustomProps`（折弯元素自定义属性表），用于探索「使用零件折弯扣除」真实开关；⑤`IZPart::Update` 后会切到「展开」多配置，`PartUpdate` 提交后立即 `ActivateDefaultConfiguration`，写入结束 `RestoreFoldState`（恢复默认配置 + `RemoveAllFromSelection`/`AddElementToSelection` 选回与 ics 同名的折叠件） | 编译 0 error；`sn -vf` 有效；清单 1 个 clrClass；后处理链路修复（见踩坑 38） |
| 33 | 2026-10-30 | 14 列 + 两列可编辑勾选 + 列宽自适应 | ①新增「使用零件K系数」列（位于「使用零件的折弯扣除」右侧）：`CustomKFactorUsed=false` 即「是」，非 K 因子类型显示「—」并锁格；②「使用零件折弯扣除」更名「使用零件的折弯扣除」，两列都改为 `DataGridViewComboBoxColumn`（是/否/—），用户改完即写：K 列经 `set_CustomKFactorUsed`（是=0 / 否=-1，先写自定义K值），扣除列「是」写回零件继承值、「否」要求「新数值」与继承值不同；写入后回读刷回表格，不弹窗打断；③列宽改为按表头/单元格文本实测（`AutoFitColumnWidths`，表头最长 200px 折两行、内容优先、单列上限 260px），超宽由水平滚动条接管 | 编译 0 error；`sn -vf` 有效；清单 clrClass=1 |
| 34 | 2026-10-30 | 探针复核结论（083730 报告） | 每个折弯特征打出的 `[Dispatch 成员清单]` 全部是 `GetTypeInfo 失败 hr=0x8002801D`（TYPE_E_ELEMENTNOTFOUND ⇒ `GetTypeInfoCount()=0`，该 dual 接口不提供类型信息，无法枚举类型库外成员）；`[自定义属性]` 全部 `hr=0x80004005（无）`，折弯元素没有任何自定义属性。⇒ 「使用零件的折弯扣除」不是隐藏属性、也不是可枚举的 Dispatch 成员，只能靠「扣除值 == 零件继承值」间接表达。写入逻辑不再盲目重试，结果列如实提示「值已同步，勾选待在CAXA确认」 | 探针报告 `caxa3dsheetmaster_probe_20261030_083730.txt` |
| 35 | 2026-10-30 | 15 列（新增最小折弯半径列）+ 表头自适应行高 | ①「使用零件的最小折弯半径」列插到「内半径」左侧：`IZSheetMetalPart::get_MinBendRadius(槽 19)` 读零件值（米→mm，本机实测 0.003=3mm），与本折弯 `get_InsideRadius` 比较，相等→「是」、不等→「否」、零件值未知→「—」；「是」时该行内半径锁死变灰，「否」时内半径可直接输入，改完走 `set_InsideRadius(槽 48)` + `PartUpdate` + 回读校验；②列宽按单元格内容实测，新增 `AutoFitHeaderHeight()`：按每列宽度用 `TextRenderer.MeasureText(WordBreak)` 算表头折行后高度，取最大值 +12px（30~96px 夹紧），表头不再被截断 | 编译 0 error；`sn -vf` 有效；清单 clrClass=1 |
| 36 | 2026-10-30 | 数据源头核对 + 新版部署（用户误报"抓取数据出错"事件） | 用户反馈"抓取数据出错"，要求参考旧版本修复数据源头。全链路 diff 发现：①旧版(桌面) DLL 102400 bytes(08:29)，新版(工作目录)刚编译 110592 bytes(09:48)，CAXA 里跑的是旧版；②`CaxaAutomation.cs` 核心扫描链 `WalkTree` → `ForEachBendFeatureByFeatureMgr` → `FillBendProps` 新旧版本**一字不差**，`Probe.cs`/`Addin.cs`/`Diagnostics.cs` 完全一致，仅 `MainForm.cs` 有 411 行差异（加列/勾选列/内半径编辑）和写入部分（新增 `WriteKFlag`/`WriteInsideRadius`/`ApplyOneBend`）；③诊断日志实证：旧版 DLL 09:37 运行时**完整抓到 12 个折弯**，全部 type=2(折弯扣除)、deduction=0.006(6mm)；④**新版 DLL 部署后**，trace log(09:50) 初始扫描仍是 type=2 deduction=0.006，但用户 09:51 点了「统一修改→折弯扣除 0.0061」全部 hr=0 但回读不变（因为使用零件继承值），紧接着 09:52 点了「统一修改→K 因子 0.273」**全部成功生效**！10 个折弯被切成 type=1 CustomK=true；⑤新版探针(09:52) 全部读到 type=1 K=0.273——**这才是用户感知"抓取数据出错"的真正原因：数据是被写入操作改了，不是扫描链坏了** | 编译 0 error；`sn -vf` 有效；清单 clrClass=1；证据归档于 `_probe-tools\out\sheetmaster\`：`probe_20261030_095246_Kfactor_after_write.txt`（8856B）、`trace_20261030_Kfactor_write_chain.log`（62767B，955 行含完整写入链路） |
| 37 | 2026-10-30 | 最小折弯半径下拉 + 载入限制 + 严格折弯过滤 | ①「使用零件的最小折弯半径」列改为 是/否 下拉（`ApplyMinRadiusFlag`）：是→`set_InsideRadius`=零件 `MinBendRadius`(槽19) 回读校验；否→仅解锁内半径列（类型库无 `UsePartMinRadius` setter）；②`ReloadAll` 加 `ProbeSessionInfo.Type==20` 判定（虚拟件同属 type=20 自然放行），空选中/非钣金件清表；③内半径可编辑条件收敛为「UsePartMinRadius=否 且 IsCustomK=true」；④删除 `ForEachBendFeatureByFeatureMgr` 里 `useVisibleOnly` 兜底，严格只入 `bendVisible==true` | 编译 0 error；0 条 Error/Warning（VS Code 诊断） |
| 38 | 2026-10-30 | 统一 K=0.273 + 下拉去「—」+ 自动刷新 + K 因子计算器 v1 | ①`_numGlobalValue` 默认 0.273（`BA=π(R+KT)`、X=2T ⇒ K≈0.273）；②`AddFlagColumn` 下拉只留 是/否，`FlagText(-1)` 兜底也返回「否」；③`RefreshConnectionInfo` 用 `_lastDocName`/`_lastPartName` 跟踪：换设计环境/换钣金件自动 `ReloadAll`，选中零件内部特征（type=19/21 等）保持表格不动；④新增 `KFactorCalcForm`（VB6 公式移植：`K=(4R+4T-2X)/(π·T)-R/T`） | 编译 0 error |
| 39 | 2026-10-30 | K 因子计算器崩溃 + 删折弯扣除列 + 删探针按钮 | ①`IndexOutOfRangeException` 根因：`BuildUi()` 里先 `ValueChanged` 绑定、后创建行控件数组 ⇒ 用内部类 `Row` 封装 8 行控件再绑 lambda；②表格 15→14 列（删 Cells[7]「使用零件的折弯扣除」），全文件 Cells 索引同步 -1，`ApplyFlag` 删除扣除分支；③探针按钮字段/创建/SetBusy 三处删除，`RunProbe()` 与 `Probe.cs` 保留 | 编译 0 error |
| 40 | 2026-10-30 | 全表可编辑 + 数值框去箭头 + 部署脚本双 Bug | ①Cells[8-9] 列定义 `readOnly=false`，`FillGrid` 只锁 0-4/12/13 及 `UsePartMinRadius=是` 时的 Cells[6]；②抽 `CaxaAutomation.RemoveUpDownArrows(NumericUpDown)`（反射把内部 `upDownButtons` 设 `Visible=false/Width=0`）；③**脚本 Bug A**：deploy/restore 开头有 6 个连续 UTF-8 BOM 块（18 字节），PS5.1 解析第二个 BOM 即崩 ⇒ 清洗为单 BOM；**Bug B**：`Get-ChildItem $caxaRoot -Directory` 未过滤非年份目录，`Mechanical` 目录也有 bin 被当版本号，`[int]"Mechanical"` 抛错 ⇒ 加 `-match '^\d{4}$'` | `dotnet build` 0 error；`restore.ps1` exit code 0，`Mechanical` 红色报错消失 |
| 41 | 2026-10-30 | 项目整体回退老版本 + 仅 UI 调整 | 用 `caxa3dsheetmaster-out` 备份覆盖主项目源码，在其上只做：新增 K 因子计算器（纯计算、不写任何 CAXA 数据）、统一修改数值框去箭头、下拉仅 是/否、删除「使用零件的折弯扣除」列、删除探针按钮 UI；扫描/写入/回读链路保持老版本原样 | 编译 0 error，产物 112,640 B |
| 42 | 2026-10-30 | 严格载入条件 + 折弯计数 13 vs 10 的最终修复 | ①新增 `CanLoadBends`：`type==20` + 非外链（`ExternallyLinked==false`）+ 零件名不含「展开」+ **零件名==ics 文件名**（`DocBaseName` 去目录与后缀、忽略大小写）；`RefreshConnectionInfo` 每秒比对 `_lastDocName`/`_lastPartName`，变化且合规即自动 `ReloadAll`；②计数误抓：场景级 `IZSceneDoc::RegenerateParts`（槽 21）实测 `hr=0x80004001(E_NOTIMPL)` **不可用**，`UpdateBrowser`+重读同样无效 ⇒ 改为 `ScanCtx.OnExit` 内**逐零件** `IZPart::Update`（槽 7）+ `Sleep(30)` 后再枚举特征，`ScanBends` 收尾补 `RestoreFoldState` 恢复折叠态；③**严禁折弯线兜底**：整体删除 `AcquireBendByLineId`/`AcquireFlatPartSm`/`ReadBendLineIds`/`ConvertBendLineIds`/`TryToUInt32`/`ReadBendLineGeometry`/`DumpBendLine`/`FnGetBendLineSource`/`FnGetBendLineInfo` 及 `SlotSmGetBendLine*` 常量与全部探针输出 | 编译 0 error；产物 109,056 B；`grep 折弯线\|BendLine` 仅剩注释与历史方法名，业务判断链路 100% 只剩 `get_ShowInBrowser`（槽 30） |

---

## 三、已核对的 API 清单（2027 探针 + interop 反射）

### 会话与场景
| 接口.成员 | 槽位 | 签名 |
| --- | --- | --- |
| `IZBaseApp::get_ActiveDoc` | 11 | `int(IntPtr, out IntPtr)` |
| `IZDoc::Save` | 9 | `int(IntPtr)` |
| `IZDoc::get_Name` | 11 | `int(IntPtr, out IntPtr BSTR)` |
| `IZSceneDoc::RegenerateParts` | 21 | `int(IntPtr, VARIANT_BOOL)` |
| `IZSceneDoc::get_SelectionMgr` | 27 | `int(IntPtr, out IntPtr)` |
| `IZSceneDoc::Update` | 36 | `int(IntPtr)` |
| `IZSceneDoc::GetTopElement` | 58 | `int(IntPtr, out IntPtr)` |
| `IZSceneDoc::UpdateGraphics` / `UpdateBrowser` | 83 / 84 | `int(IntPtr, UInt32)` |
| `IZSelectionMgr::GetSelectedElementsZArray` | 19 | `int(IntPtr, out IntPtr)` |
| `IZSelectionMgr::AddElementToSelection` / `RemoveAllFromSelection` | 20 / 22 | `int(IntPtr, IZElement*, VARIANT_BOOL)` / `int(IntPtr)` |
| `IZSceneDoc::get_ConfigurationMgr` | 40 | `int(IntPtr, out IntPtr)` → QI `IZConfigurationMgr` |
| `IZArray::get_Count` / `Get` | 7 / 9 | `int(IntPtr, out int)` / `int(IntPtr, int, out IntPtr)` |

### 元素与钣金
| 接口.成员 | 槽位 | 说明 |
| --- | --- | --- |
| `IZElement::get_Type` / `get_Id` / `get_Name` | 7 / 8 / 9 | `eZElementType`：SHEETMETAL_PART=20、SHEETMETAL_BEND=22、PUNCHBEND=49、SKETCHBEND=50 |
| `IZElement::GetChildrenCount` / `GetFirstChild` / `GetNextChild` | 15 / 19 / 20 | 设计树遍历三件套 |
| `IZPart::GetExternallyLinkedInfo` / `IZAssembly::GetExternallyLinkedInfo` | 17 / 21 | `(VARIANT_BOOL* linked, BSTR* path)` |
| `IZSheetMetalPart::get_Thickness` / `get/set_KFactor` | 10 / 17 / 18 | 厚度单位米；K 因子无量纲 |
| `IZSheetMetalPart::GetBendLineIDs` | 25 | `int(IntPtr, VARIANT* pVarOut)` —— **返回 VARIANT**，可能是 `uint[]`/`int[]`/单值，需统一转换 |
| `IZSheetMetalPart::GetBendLineInfoByID` | 26 | `int(IntPtr, uint Id, IZMathPoint**, IZMathPoint**, double* radius, double* angle, int* dir)`；半径米、角度弧度、`eZSMBendDirection`：UP=0 / DOWN=1 |
| `IZSheetMetalPart::GetBendLineSource` | 29 | `int(IntPtr, uint Id, uint* pCrvID, IZElement**)` —— 返回折弯来源元素，QI `IZSheetMetalBend` 后可读写系数 |
| `IZSheetMetalBend` 见上表 | 15–47 | 全部 `ezBendAllowanceSrc` / Boolean / Double |
| 探针专用只读 | 15 / 19 / 21 / 23 / 27 | SM：`Thickness2`、`MinBendRadius`、`UseDIN6935`、`ThicknessRefSide`、`CustomUnfoldLengthValue` |
| `IZSheetMetalBend::set_InsideRadius` | 48 | `int(IntPtr, double meters)`；与读取槽 47 配对使用（内半径列可直接编辑） |
| 探针专用 Bend 只读 | 7 / 9 / 21 / 36 / 38 | `FirstBendLength`、`SecondBendLength`、`CustomUnfoldLengthValue`、`UseCustomUnfoldLengthOfPart`、`UpdateBehavior` |
| `IZConfigurationMgr::get_DefaultConfiguration` / `IZConfiguration::get_Name` / `Activate` | 12 / 7 / 15 | `Activate(VARIANT_BOOL bUpdateAllConfigsFirst)`，用于 `Update` 后切回折叠配置 |
| 「使用零件的最小折弯半径 / 使用零件的折弯扣除」 | 类型库与运行期都没有 | 反射 `interop.ICApiIronCAD.dll` 确认 `IZSheetMetalBend` 无此成员；再用 `IDispatch::GetIDsOfNames` 试探 11 个候选名（`UsePartMinBendRadius`/`UsePartBendDeduction`/…）**全部未命中** ⇒ 该勾选不可读写。表格改为纯数据判定：扣除值 == 零件继承值 → 是，否则 → 否；类型不是折弯扣除 → —（不适用） |

IID：`IZSheetMetalPart=05667879-65CD-4417-AFB8-16E09A669D06`、`IZSheetMetalBend=F1100C88-8FBE-4ED2-AD8D-343D9D23B2C9`（其余同 `caxa3dsheetunfoldnameupdate` 已实证表）。

### 槽位调用防护
所有 vtable 直调前用 `VirtualQuery` 校验函数指针落在 `MEM_COMMIT + PAGE_EXECUTE*` 内存，避开短 vtable / 代理接口导致的 AV 崩溃；失败仅记日志并返回失败码（不抛异常打断遍历）。

---

## 四、功能行为定义

1. **统一修改 → K 因子**：对每个钣金件写零件级 `KFactor`（回读 `get_KFactor` 校验）；折弯逐个设 `BendAllowanceType=Kfactor` + 写 `CustomKFactorValue`。
2. **统一修改 → 折弯扣除**：所有折弯 `BendAllowanceType=BendDeduction` + `BendDeductionValue = 输入mm / 1000`。
3. **单行「应用」→ K 因子**：只对该折弯写 `BendAllowanceType=Kfactor` + `CustomKFactorValue`（不影响同零件其它折弯）。
4. **单行「应用」→ 折弯扣除**：同 2，仅该折弯。
5. **成功判定**：每次 set 后先 `IZPart::Update` 提交，再 get 回读比对（扣除容差 1e-7 m），全部一致才计成功；K 值因 API 不可回读，判定条件为「set 全部 hr=0 且 type==1」，日志明确标注「K 值未回读」。
6. **折弯扣除写不进去的已知原因**：折弯若「使用零件的折弯扣除」=是（扣除值等于零件继承值），`set_BendDeductionValue` 返回 hr=0 但回读不变（实测 6mm 写 5mm 无效）。此时报错文案要求先在 CAXA 折弯对话框取消该勾选；表格「使用零件折弯扣除」列即为该状态的展示（是/否/—）。
7. 每个零件写完后调用 `IZPart::Regenerate`，让展开长度等几何结果刷新。
8. 统一修改完成后自动**重新扫描**整表，表格「当前类型 / 当前数值」显示落盘后的真实值。
9. **表格列序（13 列，必须整体同步）**：0序号 / 1折弯特征名称 / 2系统名称 / 3折弯角度 / 4板材厚度 / 5内半径 / 6使用零件折弯扣除 / 7折弯系数类型 / 8折弯系数数值 / 9新类型 / 10新数值 / 11修改 / 12结果。
10. **「使用零件折弯扣除」列**：只读展示；`是`=扣除值等于零件继承值（CAXA 忽略单独写入），`否`=本折弯独立值可单独改，`—`=系数类型不是折弯扣除（不适用）。
11. 「保存图纸」= `IZDoc.Save`，hr≠0 视为未落盘并报错；「重新生成」= `RegenerateParts(VARIANT_TRUE)`，单次 COM 调用不可中断，执行前弹确认。

---

## 五、界面结构（复刻 SW 折弯管理器并适配 CAXA）

```
┌ 状态区：设计环境：xxx.ics---零件名（虚拟）[钣金零件]      [刷新状态] [窗口前置] ┐
│         折弯列表：钣金件 N 个 / 折弯 M 条                                     │
├ 数据区：[统一设置▼][数值][统一修改][重新加载][保存图纸][重新生成][探针][停止] ● 状态 ┤
│ 序号│折弯特征名称(可改名)│折弯角度(°)│基体厚度(mm)│折弯半径(mm)│折弯系数类型│折弯系数数值│新类型▼│新数值│修改│结果 │
├ 日志区（可保存/清理）                                                          ┘
```

- 列：序号 / 折弯特征名称（**可编辑**）/ 折弯角度 90.00 / 基体厚度 3.00 / 折弯半径 3.00 / 折弯系数类型 K因子 / 折弯系数数值 0.2730 / 新类型 / 新数值 / 修改 / 结果。
- 「折弯特征名称」采集时为「元素名+序号」，用户可直接改；点「修改」后把该文本 `put_Name` 写到折弯元素（QI `IZElement`）并回读回填，写入被拒绝则自动回显示 API 里的真实名称。
- 不可写行（来源元素不支持 `IZSheetMetalBend`）整行灰底、新类型/新数值/修改按钮禁用，结果列写明原因。
- 定时器 1s 刷新连接与文件行；连接失败 5 秒内不重复重试，避免日志刷屏（`Diagnostics.Suppress` 同时抑制文件日志）。
- 「窗口前置」= TopMost 开关（跨进程 `SetForegroundWindow` 不可靠，沿用已验证做法）。
- 长任务结束/异常后把前台还给 CAXA 主窗口（`Process.MainWindowHandle`），不 SetFocus 子窗，避免焦点风暴。

---

## 六、踩坑与结论

1. **`createCommandHandler` 图标必须能引用 stdole**：即使传 `null`，编译也要求 `stdole` 引用（`IPictureDisp` 出现在签名里）。图标仍传 null —— 2027 对自定义图标渲染苛刻（沿用 `caxa3dsheetunfoldnameupdate` 实测口径）。
2. **`Thickness2` 与 `Thickness` 区别**：10 号为只读 `Thickness`（Double，米），15/16 为可写 `Thickness2`。本项目只读显示板厚，不写板厚，避免改板厚导致几何重算。
3. **枚举底层是 Int32**：`ezBendAllowanceSrc` 必须按 4 字节参数传，用 `short` 会破坏栈/寄存器约定。
4. **Boolean 属性是 VARIANT_BOOL（2 字节）**：`CustomKFactorUsed` 读写都用 `short`，写 `-1`=true、`0`=false。
5. **展开态零件**：名以「展开」开头，必须跳过，否则同一块板被统计两次且展开件无折弯特征。
6. **`strip-manifest` 本项目 removed=0**：`EmbedInteropTypes=True` 时 mt 不会为 interop 生成 clrClass；保留脚本用于防御。
7. **`GetBendLineIDs` 在折叠件上直接返回 E_FAIL（0x80004005，vt=VT_EMPTY）**：探针实测。折弯线只存在于「展开件」上 ⇒ 必须先 `IZSheetMetalPart::GetFlatPart`（槽 12）取展开件，再对展开件枚举折弯线。没有展开件时该接口无解。
8. **折弯特征不在钣金件子树里**：实测设计树形如 `type=0 根 → type=20 钣金件 → type=21 板料`，而「斜接法兰1」是 **与零件同级的 `type=0` 节点（id=5）**。所以「按零件子树找折弯」永远找不到，必须场景级遍历 + `IZElement::GetOwnerPart`（槽 29）反查归属。
9. **同一零件的折弯既可能来自折弯线也可能来自折弯特征元素**：两条路都实现，靠 `BendRowInfo.BendLineId`（≠0 折弯线 / =0 特征元素）区分，写入时分别走 `GetBendLineSource` 与直接 QI，并用 `PartCtx.BendLines > 0` 去重。
10. **ICAPI 类型库与运行时实现可能不一致**：`type=0` 的元素也可能在运行时支持 `IZSheetMetalBend`。故扫描不信任 `get_Type`，一律用 QI 结果判定（探针逐节点打印 QI 矩阵）。
11. **部署脚本 BOM 只允许 1 份**：多份连续 BOM 会让 PS 5.1 首行解析失败（前项目曾出现 7 份）；本次字节级校验 3 个脚本均 `EF BB BF ×1`，PS7 AST 解析 0 错误。
12. **免注册 COM 三处 CLSID 必须一致**：Addin.cs `[Guid]`、内嵌清单 `clrClass clsid`、deploy.ps1 `$clsid`。实测清单内 clrClass 已正确生成。
13. **进程内会话禁止 `ReleaseComObject`**：只 `Marshal.Release` 经 QI 得到的接口指针；CAXA 自己的 RCW 不释放。
14. **`GetBendLineIDs` 返回的是 VARIANT**：不能按 `IZArray*` 接。必须传 `VARIANT*`、`VariantClear` 收尾，并把 `uint[]/int[]/IZArray/单值` 四种形态统一转换（`ConvertBendLineIds`）。
15. **`GetBendLineInfoByID` 的 out 指针必须 Release**：两个 `IZMathPoint*` 是引用计数对象，漏 Release 会持续泄漏；本项目在 `finally` 外的统一位置释放。
16. **折弯参数一律以「钣金折弯特征」的 `IZSheetMetalBend` 为准**：`BendAngle`/`InsideRadius` 取自折弯特征元素；`GetBendLineInfoByID` 的半径/角度经用户实测不准确，仅用于探针诊断，不参与业务显示。
17. **探针必须与写入链路隔离**：探针只调只读槽位，不改任何属性，避免"探针一跑就把模型改了"。
18. **多 out 参数的接口绝对不能裸槽位直调（闪退根因）**：`IZSheetMetalPart::GetStockProperties` 有 7 个 out（BSTR×2 + DOUBLE×4 + LONG×1），按「单 `double*`」接会破坏栈 → 进程直接消失，无任何日志。凡 out 参数 ≥2 的接口一律走 RCW：`Marshal.GetObjectForIUnknown(p)` + `as IZSheetMetalPart` 后调 `GetStockProperties(out ...)`。探针崩点即此（日志停在上一行 `GetUnfoldConfiguration`）。
19. **`put_CustomKFactorUsed` 实测不可用**：`set_CustomKFactorUsed(0)` 返回 `0x80070057`（E_INVALIDARG），且 `type=Kfactor` 时 `get_CustomKFactorUsed` 自动变成 `-1`（true）。若先写 type 再写 used=false，会把折弯从「折弯扣除 6mm」改成「自定义K = 0」，属于**破坏数据**。K 模式正确写法：`set_BendAllowanceType(1)` → `set_CustomKFactorValue(v)` → 回读 `type==1 && k≈v`，不再碰 `CustomKFactorUsed`。
20. **板料属性单位是 mm，零件属性单位是 m**：`GetStockProperties` 返回的 厚=3.0 / K=0.325 与「27.钢 gauge=1」板料表一致（3mm）；而 `get_Thickness`=0.003（米）。两者不可混用，本项目以板料属性为准，并用「与零件厚度差一个数量级」做单位兜底。
21. **设计树里没有折弯元素（本模型实测 4 节点）**：root(0) → 钣金件(20) → 板料(21) → PROFILE(19)，另有与零件同级的 `斜接法兰1`（type=0、QI 只支持 IZElement）。因此**放弃纯树遍历**，折弯只能靠展开件折弯线作索引反查 `IZSheetMetalBend` 元素；且必须**按来源元素 Id 去重**（13 条折弯线不一定对应 13 个折弯特征）。
22. **只认 type=22「钣金折弯特征」**：压弯(49)/草图折弯(50) 不提供 K 系数与折弯扣除的完整语义，一律不进表；这也是「数据不准确」的主要来源之一。
23. **折弯扣除有物理上限 `D ≤ 2(R+T) − θR`**：实测「K因子统一修改成功，但再改折弯扣除失败」——超限值会被系统静默拒绝。写入前必须先算上限并拦截，日志/弹窗给出该折弯允许的最大扣除值。
24. **从 K 切到折弯扣除时写入顺序很关键**：`set_BendAllowanceType(2)` → `set_BendDeductionValue` 可能不生效，需反向写（先值后类型）。已实现两级兜底，每级都 `Update` 后回读 `type==2 && deduction≈value` 判定。
25. **特征上的 `set_*` 不 Commit 就是假成功（本项目最大坑）**：13 条折弯全部 `hr=0`，但回读在 `0.0008`（新值）与 `0.006`（旧值）之间随机交替。根因是 `IZSheetMetalBend` 的 setter 只改特征的待提交参数，**必须调 `IZPart::Update`（slot 7）提交**；要刷新展开长度还要 `IZPart::Regenerate`（slot 18）。这是「K 改不动、扣除改不动」的唯一根因。
26. **`get_CustomKFactorValue` 在特征对象上恒 E_FAIL（0x80004005）**：探针与写入日志一致，K 值无法回读；K 模式只能靠 `set hr=0 + type==1` 判定，并把「K 值未回读」写进日志，不能声称回读成功。
27. **折弯特征全部同名「折弯」**：不能用名称区分，必须靠 `BendId`（`IZFeature::FeatureIndex` 兜底）或折弯线 Id 定位；界面也不再给名称追加序号。
28. **`SM.Unfold` 有毒，永久禁用**：实测 `Unfold hr=0` 后 `GetBendLineIDs` 依旧 `0x80004005`，`Refold` 直接 `0x80070057` 失败 ⇒ 零件停在展开态、激活对象变成展开件、用户点「修改」时焦点跳走。展开通道已彻底删除，不要再尝试。
29. **特征管理器 ≠ 设计树，但它是折弯的唯一来源**：`IZPartFeatureMgr` 会返回设计树上不显示的内部折弯（本模型 27 特征 / 13 个折弯，UI 树只有 10 个）⇒ 采集/写入一律走特征管理器，并用 `IZElement::get_ShowInBrowser`（槽 30）过滤掉不可见项，结果与 UI 树一致（10 条）。设计树/场景级遍历已删除，不再作兜底。
30. **设计树遍历不要加类型过滤**：业务扫描与探针过去只放行 `type 0/1/2/20`，导致设计树「看起来只有 4 个节点」，误判「折弯不在树里」。去掉过滤后 `21/22/49/50` 等节点都在树里。
31. **API 设计树视图 ≠ UI 设计树**：去掉过滤后 API 树仍只有 5 个节点，UI 上却能显示 10 个「折弯」⇒ UI 的折弯节点来自特征管理器，`IZElement::GetFirstChild/GetNextChild` 拿不到。折弯枚举必须走 `IZPartFeatureMgr`。
32. **`IZPart::Regenerate` 会抢焦点**：写入后调用 Regenerate/`RefreshSceneUi` 会让 CAXA 把激活对象切到停在那儿的那一方（本例是停在展开态的零件），用户点「修改」就看到焦点跳到展开件。写入只需 `IZPart::Update`，几何刷新交给用户点「重新生成」。
33. **加列/减列后必须同步所有硬编码列索引**：新增「系统名称」列后，「修改」按钮列从索引 9 变成 10，而 `GridCellContentClick` 仍判 `e.ColumnIndex != 9` ⇒ 用户点「修改」毫无反应（表现为"无法单独修改"）。当前为 13 列：点击判定 11、新类型 9、新数值 10、结果 12、勾选 6、当前类型 7、当前数值 8。
34. **`BendRowInfo.BendId` 在特征管理器通道里是「特征序号(1-based)」而非元素 Id**：因此单条修改不能拿它在设计树里找折弯元素（树里没有折弯，且数值语义不同），必须回到同一零件的特征管理器按序号定位，保证与采集口径完全一致。
35. **UI 上的勾选项 ≠ 类型库成员（实测两路都不可得）**：CAXA 折弯对话框的「使用零件的最小折弯半径」「使用零件的折弯扣除」在 `IZSheetMetalBend` 里没有对应属性（interop 反射确认），运行期用 `IDispatch::GetIDsOfNames` 试探 11 个候选名也**全部未命中**（2026-10-30 探针 073648 报告）。结论：该勾选不可读写 ⇒ 表格只能用数据表达状态（扣除值 == 零件继承值 → 是；否则 → 否；非折弯扣除 → —），不要再去猜属性名。
36. **「使用零件的折弯扣除」=是 时，单条写扣除必然无效**：实测 `set_BendDeductionValue(0.005) hr=0` + `IZPart.Update hr=0`，但回读仍是 0.006（零件继承值），换「先写值再写类型」顺序同样无效。只能提示用户在 CAXA 折弯对话框里取消该勾选后再改。别把这种 hr=0 当成写入成功（必须回读判定）。
37. **`IZPart::Update` 会把钣金件切到「展开」多配置（本轮实测）**：Update 之后回读值来自展开态（看不到刚写的值），用户也被留在展开件上。修法：①`PartUpdate` 里提交后立即 `IZConfigurationMgr.get_DefaultConfiguration(12)` → `IZConfiguration.Activate(15)` 切回默认（折叠）配置，再做回读；②写入收尾 `RestoreFoldState` 再恢复一次配置，并用 `IZSelectionMgr.RemoveAllFromSelection(22)` + `AddElementToSelection(20)` 把选中放回与 ics 同名的折叠件。**注意 WalkTree 递归返回会 Release 子节点**，回调里要 QI 拿一份自己的引用，否则选择时指针已失效。
38. **`strip-manifest.ps1` 首行 6 个 BOM 会让 `param` 静默失效**：`param(...)` 解析失败 ⇒ `$ManifestPath` 为 null ⇒ `Test-Path` 报错、`removed 0 interop entries from`（路径为空），实际剥离没执行，嵌入的可能是上一次的旧清单。修法：剥离已有 BOM 后写回单 BOM，脚本注释改纯 ASCII（已修复，本次后处理输出正常）。
39. **类型库全量核对：`IZSheetMetalBend` 没有「使用零件折弯扣除」开关**：2027/2024 的 `members.csv` 里 `Deduction` 只有 `get/set_BendDeductionValue`，`IZSheetMetalPart`/`IZSheetMetalStock`/`IZSheetMetalPunchBend`/`IZSheetMetalSketchBend` 也没有任何 UsePart 布尔（K 因子那边倒是有现成的 `get/set_CustomKFactorUsed`）。
40. **ICAPI 的 `IZ*` 接口没有类型信息，`ITypeInfo` 枚举这条线走不通（实测 083730）**：对 `IZSheetMetalBend` 调 `GetTypeInfo(0)` 一律返回 `hr=0x8002801D`（TYPE_E_ELEMENTNOTFOUND，因为 `GetTypeInfoCount()=0`），说明它们虽是 dual 接口但不带 ITypeInfo，**任何"枚举类型库外隐藏成员"的尝试都会失败**；折弯元素的 `[自定义属性]` 也是 `0x80004005（无）`。要继续找隐藏开关只能走 `IDispatch::GetIDsOfNames` 猜名或控件树取证，不要再花时间在 ITypeInfo 上。另：`DumpDispatchMembers` 保留为兜底探针，输出失败不影响其他段。
41. **"抓取数据出错"不等于扫描链坏了——先排除"用户写过把数据改了"**：本轮用户报"抓取数据出错"，全链路 diff 发现扫描链代码一字不差，最后 trace log 实证：用户自己先点了统一修改→K 因子，把 10 个折弯从 type=2 切成了 type=1，然后回来又报"抓错了"。排查顺序应该是：①先看 trace log 里有没有 set_* 痕迹；②确认 CAXA 里跑的 DLL 版本（文件大小/mtime）；③再 diff 源码。不要一上来就怀疑"数据源头被破坏"，**先排除写操作副作用**。
42. **`set_CustomKFactorUsed(-1)` 会无条件把折弯切成 K 因子模式**：不管之前是什么类型，只要执行 `set_BendAllowanceType(1)` + `set_CustomKFactorValue(v)` + `set_CustomKFactorUsed(-1)`，折弯就永久变成 K 因子。而切回去（从 K 因子改回折弯扣除）因为"使用零件继承值"会被卡，**K 因子方向的写入是单向不可逆的**。统一修改/K 因子按钮必须加确认对话框，防止误操作把用户数据"锁死"到 K 因子模式。
43. **外链零件首次进入时 `get_ShowInBrowser` 一律返回 true ⇒ 内部隐藏折弯被误入表；唯一解是「抓取前对该零件单独 `IZPart.Update`」**：现象是装配内点选外链钣金件，表格显示 13 条（含 3 条内部折弯），**手动点开设计树**或**手动执行一次重新生成**后再加载就返回正确的 10 条。根因是设计树浏览器节点未构建，`IZElement::get_ShowInBrowser`（槽 30）在节点未构建时给默认 true。**实测结论**：场景级 `IZSceneDoc::RegenerateParts`（槽 21）在本机返回 `hr=0x80004001(E_NOTIMPL)`，**不可用**；`UpdateBrowser`（槽 84）单独调用 + 连续重读**同样无效**（仍全 true）。**最终修法**：在 `ScanCtx.OnExit` 里对每个目标钣金件先 `IZPart.Update`（槽 7，`PartUpdate` 工具方法）重建，`Sleep(30)` 后再枚举折弯特征 —— 等价于 UI 手动「重新生成该零件」，实测可正确剔除内部折弯。注意 `IZPart.Update` 会把零件切到展开配置，扫描结束必须 `RestoreFoldState` 恢复折叠态。
44. **【已否决·永久封存】用「折弯线来源集合」判断内部折弯不可行**：曾尝试 `GetFlatPart`（槽 12）→ `GetBendLineIDs`（槽 25）→ `GetBendLineSource`（槽 29）反查「有折弯线的折弯特征」以剔除内部折弯。实测：折弯线 Id 只能在**展开件**上取到，而 `GetBendLineSource` 在展开件上全部 `hr=0x80004005(E_FAIL)`；改到折叠件上调用后来源集合 = 13（与特征数相同），说明**内部折弯同样参与展开、同样有折弯线**，该判据天然无区分力。用户已明确要求**严禁再用折弯线做兜底判断**，本项目已把折弯线相关方法（`AcquireBendByLineId` / `AcquireFlatPartSm` / `ReadBendLineIds` / `DumpBendLine`）与探针输出全部删除，此路线永久封存。
45. **PowerShell 脚本开头出现多个连续 UTF-8 BOM 会导致 PS5.1 整体解析失败（静默崩）**：现象是双击 `安装.bat` 后无任何输出、脚本不执行。根因是 deploy.ps1 / restore.ps1 开头各有 6 个连续 BOM 块（18 字节），PS5.1 吃掉第一个 BOM 后把第二个 0xEF 当非法字符直接抛 `ParserError`，而 bat 里的 `-ErrorAction` 掩盖了输出。排查手法：`[System.IO.File]::ReadAllBytes(path)[0..8]` 看前 9 字节是否 `EF BB BF EF BB BF ...`；修复只留一组。**每次用 PowerShell 写脚本后必须做这一步字节校验**。
46. **`Get-ChildItem $caxaRoot -Directory` 不能只按「有没有 bin 子目录」筛选版本**：CAXA 安装根目录下除 `2024/2027` 还有 `Mechanical` 等目录也带 bin，被当成年份后 `[int]$_.Name` 抛 `Cannot convert value "Mechanical" to type System.Int32`（红色报错但不中断）。必须加 `Where-Object { $_.Name -match '^\d{4}$' }` 双重过滤。
47. **WinForms `NumericUpDown.ValueChanged` 在构建期就会触发，不能在字段数组初始化前绑事件**：`KFactorCalcForm.BuildUi()` 里先 `_thickness[i].ValueChanged += OnThicknessChanged`、后 `new NumericUpDown[]` ⇒ 设默认值时回调访问空数组，抛 `IndexOutOfRangeException`。修法是先建控件（封装成 `Row` 对象）再绑 lambda，或绑定时判断数组长度。同类风险适用于所有「先绑事件后填值」的 WinForms 构建代码。

## 七、未验证项（下次实测必查）

1. `set_KFactor` / `set_BendDeductionValue` 是否真正改变展开长度（用展开件长度或展开态几何做权威副作用验证）。
2. 修改后是否需要 `RegenerateParts` 才能刷新几何/展开图（表格回读只证明属性写了，不证明几何更新）。
3. `BendAngle` / `InsideRadius` 的米/弧度换算是否与实际一致（当前仅作展示）。
4. 外链件（装配内真实 `.ics`）修改后，是否需要在链接件文档内保存才能持久化。
5. **逐零件 `IZPart.Update` 重建后 `get_ShowInBrowser` 是否稳定区分内部折弯**（本轮最终改法，待真机确认；若仍为 13 条，下一轮改 Win32 枚举设计树控件强制展开）。
6. `IZElement.GetStateStatus`（槽 17，`Z_HIDDEN=2`）是否可作内部折弯的第二判据（本轮未启用，`LooksCallable` 会拦）。
