# caxa3dsetMaterial-踩坑与教训

## 铁律清单

1. **自定义材料投放目录**（固定两级目录+双文件）：
   ```
   %LOCALAPPDATA%\CAXA\CAXA 3D\29.0\zh-CN\materials\Materials\Custom Material\<分类>\<子分类>\<材料名>.XML
   %LOCALAPPDATA%\CAXA\CAXA 3D\29.0\zh-CN\materials\Graphics\Materials\Custom\<分类>\<子分类>\<材料名> Shader.XML
   ```
   编码 UTF-8 无 BOM，根元素单个 `<material>`，`<ModeType Type="...">` 子节点。
2. **Addin 入口类必须 `[ComVisible(true)]`**（程序集级仍 false），否则 `mt.exe` 不生成 `<clrClass>`，CAXA SxS COM 激活失败。
3. **颜色 API 是 COLORREF 0xBBGGRR**（不是 RRGGBB），界面用 RRGGBB → 写 API 前转 BGR。
4. **COM RCW 永远别反射**：直接用 managed 接口方法。
5. **SAFEARRAY 封送有歧义时用 ZArray**（CAXA 自己的 managed 包装，不受元素类型影响）。
6. **官方材料库 4 个文件名硬编码在 `3iAdvFeature.dll`**，安装目录塞新文件不被加载。
7. **成功判定只看业务副作用**（回读值、密度×体积、颜色），不看 API 返回值。
8. **Add-in 部署四登记点缺一不可**（2026-10-29 实证）：
   ① DLL/PDB → `<CAXA>\<year>\bin\`
   ② `IronCad.Addin.config` → `<AddIn inprocserver="dll名" siteclsid="CLSID" system="true">`
   ③ `IronCad.exe.manifest` → `<dependency><dependentAssembly><assemblyIdentity name=版本=PKT=amd64>`
   ④ **注册表** `HKCU\SOFTWARE\CAXA\CAXA 3D\<verTag>\Applications\<addin名>`（CAXA 实际靠此键判断加载开关，缺了"加载列表可见但 InitSelf 永不执行"）
   版本号换算：2024→26.0、2027→29.0（年份 - 1998）。
9. **deploy.ps1 铁律**：
   - PS5.1 只能识别**单个 UTF-8 BOM**（`EF BB BF` × 1），多 BOM 首行解析崩溃；补 BOM 前先剥离已有 BOM，Write 时只写一个
   - DLL 版本号 / publicKeyToken 从 `[Reflection.AssemblyName]::GetAssemblyName(dllPath)` 反射读取，**不要硬编码**（重签后 PKT 不变但版本会变）
   - 提权前先复制到 `C:\ProgramData\<addin名>\deploy-<ver>.ps1`，通过 `-WorkRoot` 传源目录（沙箱内临时文件对提权进程不可见）
   - CAXA 进程在运行时拒绝部署（DLL 被锁）
   - deploy 用 remove→add 两步清理（先删旧条目再写新的），避免重复登记
   - restore 手术式清理（XML 节点移除 + 正则兜底），**不整份回滚 .bak**（config/manifest 是共享文件）
   - 自动发现版本目录：`Get-ChildItem "C:\Program Files\CAXA\CAXA 3D" -Directory | Where { Test-Path "$($_.FullName)\bin" }`，逐个部署
10. **写自定义属性统一走共享表**（2026-10-29 项目约定）：规则文件里所有字段（`材料`、`SC材料`、未来新增的）统一写入 `GetSharedPropMgr` 返回的共享表，不再按 `SC` 前缀分流；默认"仅此零件使用"不勾选，`SetMaterialName(name, false)` 使材料随零件定义下发到全部链接实例。

## 2026-10-29 自定义属性与材料统一落共享表（详细记录）

> 对应里程碑 #20。

### 背景与问题
原逻辑按字段前缀分流：`SC*` 写共享表，其余（含 `材料`）写零件表；`SetMaterialName` 受「仅此零件使用」复选框控制，且该框**默认勾选**。结果：链接实例（外链零件）看不到 `材料` 等属性，材料也只落在当前零件上。

### 本次改动

| 文件 | 改动 |
| --- | --- |
| `MaterialProbe.cs` | `WriteMaterialProps` 去掉前缀分流，**所有字段只写共享表**；`TryGetPropStrict` 只读共享表、不兜底零件表，`shared` 恒为 1；删除不再使用的 `GetPartPropMgr` |
| `BackfillRule.cs` | 删除 `IsSharedField`（不再按 `SC` 前缀判定归属），注释改为「全部字段统一写共享表」 |
| `MainForm.cs` | 「仅此零件使用」默认 `Checked=false` → `SetMaterialName(..., onlyThisPart=false)`，材料随零件定义下发到所有链接实例 |

- 编译：`dotnet build plugins\caxa3dsetMaterial -c Release -p:Platform=x64` → 0 警告 0 错误；产物 65024 字节，已同步到 `src\plugins\caxa3dsetMaterial\caxa3dsetMaterial.dll`。
- 兼容性提示：改造前写在零件表的旧数据，改造后回读显示 `<无>`，需重新写一次。

### 三插件复核结论

| 插件 | 写入 | 读取 | 结论 |
| --- | --- | --- | --- |
| `caxa3dAttributeManageraddin` | 共享 `GetCustomPropMgr(selector)+IsSharedMgr` | 共享 | 一致，未改 |
| `caxa3dcodenameextraction` | 共享（`WriteFirstElementCustomProps`） | 共享 | 一致，未改 |
| `caxa3dsetMaterial` | 共享（本次改造） | 共享 | 已对齐 |

三者的 `TargetFields` 都以 `SC图号/SC名称/SC规格/SC材料` 为核心，均落共享表。

### 共性风险（未改，待决策）
`GetSharedCustomPropMgr` 在 selector 0/1 都未能通过 `IsSharedMgr` 确认时，会回退使用第一个管理器，仅记 Trace 日志。极端情况下可能写进零件表。改成「取不到共享表即失败」可严格保证语义，但会使依赖该回退的机器报错，需用户拍板。

### 新经验
- **同一套属性语义要在插件族内对齐。** 三个插件共用 `IZCustomPropMgr` 共享表定位逻辑，任何「按前缀分流」都会制造跨插件行为不一致。
- **验证回读必须与写入同表。** 写共享却读零件表（或反之）会稳定报「未通过」，排查时应先核对写表与读表是否一致。
- **材料归属由 `SetMaterialName(onlyThisPart)` 决定，不是由自定义属性决定。** 自定义属性只是文本回填，真正让链接实例改材料必须传 `false`。

### 待实机确认
1. 选一个外链零件的两个实例，只对其中一个执行「写入材料和颜色」；两个实例的自定义属性与材料应同时变化。
2. 未勾选「仅此零件使用」时，日志中的 `仅此零件=False`、回读 `backOnly=False` 应成立。
3. 若旧零件回读 `<无>`，重写一次后应变为 `(共享)`。

