# caxa3dsheetmaster-踩坑与教训

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

