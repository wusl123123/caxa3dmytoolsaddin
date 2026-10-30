# caxa3dsetMaterial — 项目总结

> 中文名：CAXA 3D 材料工具。目标：把自备材料信息注入 CAXA 3D 选中零件（含虚拟件），不依赖官方库、可跨机分发。
> 环境：.NET Framework 4.8 / x64 / CAXA 3D 2027（IronCAD 29.0 内核）/ C# + WinForm，纯设计环境，不用 CRX。

## 里程碑记录表

| 序号 | 日期 | 阶段 | 结论/产出 | 关键证据 |
|---|---|---|---|---|
| 1 | 2026-10-28 | 阶段一：材料库文件↔材料页面对应关系分析 | 完成 XML 结构逆向，确认材料页面完全由 `materials\Materials\*.xml` 驱动 | 见下文章节一；材料页面.PNG 树=国标材料→铁→软铁(SN) 与 `GB Materials.xml` 顺序一致 |
| 2 | 2026-10-28 | 阶段一：进入材料页面 API 查找 | 无公开 ICAPI 可打开材料页；对话框归属 `3isd31.dll`（语言资源 `3isdLg.dll` 中"编辑材质库"），命令枚举 `eZCommand` 无材料项 | ICAPIIRONCADLib.txt 全文检索 Material 仅得 6 个接口；`eZCommand` 仅 13 个值 |
| 3 | 2026-10-28 | 阶段一：注入路径查找 | 属性注入：`IZPartProperty.SetMaterialName` + `MassDensity`；外观注入：`IZSurfaceFinish`/`IZBodyFace.SetSurfaceFinish`/`IZShaderMgr`；待实测 | 见章节三 |
| 4 | 2026-10-28 | 阶段一：国标材料设置方法 | 归纳出可复用的材料 XML 书写规范（UTF-16LE BOM、属性键→显示名→单位、shader/texture 相对路径） | 见章节四 |
| 5 | 2026-10-28 | 阶段二：探针 Add-in 交付 | 可运行 DLL + 安装/卸载 bat；功能区「材料工具」按钮；窗口前置、读取选中件、写入材料名+密度并回读校验、扫描材料库、保存文档 | 编译 0 错误；DLL ComVisible=False、强名称有效、内嵌 SxS 清单 345 B |
| 6 | 2026-10-28 | 阶段二：接口签名实证（反射 interop） | `IZPartProperty` 由 `IZElement` 直接 QI 获得；所有出参为 `out`；`IZSceneDoc.SelectionMgr` 返回 `ZSelectionMgr`（实现 `IZSelectionMgr`）；`IZSceneDoc.Update()` 可刷新 | 见章节六；官方 C++ 样例 `IZPartPropertyPtr spiProp = spiElem;` 同源佐证 |
| 7 | 2026-10-28 | 阶段二：踩坑修复（Add-in 静默不加载） | 现象：加载列表可见、勾选后无功能区按钮、`%TEMP%` 无 trace 日志。根因：Addin 类被标 `ComVisible(false)` → `mt.exe -managedassemblyname` 不生成 `<clrClass>` → CAXA 按 siteclsid 的 SxS COM 激活失败，`InitSelf` 永不执行。修复：**仅 Addin 入口类标 `[ComVisible(true)]`**，程序集其余类型仍 false | 内嵌清单从 345 B（仅 `<file>`）变为 527 B（含 `<clrClass clsid="{C4B2A6E7-...}" progid="caxa3dsetMaterial.Addin">`）；参考插件清单 3363 B 同样含 clrClass |
| 8 | 2026-10-28 | 阶段三：收敛为三项（材料/密度/颜色）+ 自定义属性回填 | 界面"材料名"改"材料"；写入时材料名同步落 `材料`、`SC材料` 两个自定义属性；新增颜色注入（取色器 + RRGGBB 输入），三项均回读校验 | 名称+密度实测通过（用户日志 11:51）；`CustomPropMgr` 为索引属性须用 `GetCustomPropManager(0)`；`IZSurfaceFinish.Color` 为 0xRRGGBB |
| 9 | 2026-10-28 | 阶段三修正：颜色编码 + SC材料归属 | ①颜色实测是 **COLORREF 0xBBGGRR**，不是文档写的 0xRRGGBB——三组对照全反；②`SC材料` 未出现在 CAXA 属性界面，因它属**共享表**，而 `材料` 属零件表；③新增「随机颜色」按钮，多选时逐件随机上色并回读 | 见 4.2 实证表 |
| 10 | 2026-10-28 | 对齐参考插件 caxa3dAttributeManageraddin 的属性写法 | ①共享表不再硬编码 selector，改按 `IsSharedMgr()` 依次试 0/1 动态定位；②写入优先 `SetValueByName`（覆盖模板已定义属性），失败才 `AddCustomProp` 新建；③`材料`→零件表、`SC材料`→共享表，另一表若已有同名属性同步覆盖 | 参考插件 `GetSharedCustomPropMgr` / `TrySetValueByName` 同构；`SC图号/SC名称/SC规格/SC材料` 在参考项目里全部走共享表 |
| 11 | 2026-10-28 | **设定正式版 v1.0**：窗口前置 + 按钮拆分 + 材料资料库 | ①「窗口前置」改为 **TopMost 开关**（默认置顶，蓝底高亮；对齐 caxa3dmytoolsaddin 的场景，`SetForegroundWindow` 跨进程抢焦点不可靠）；②按钮拆为「写入材料和颜色」「单独写入材料」「单独写入颜色」「随机颜色」；③打开窗口即读取表格清单可边选边注入；④新增 `自定义材料.xml`（260 条：8 大类，材料名/密度/颜色全命中），界面 DataGridView 直接编辑并「保存资料库」写回 `%APPDATA%\caxa3dsetMaterial\自定义材料.xml` | 资料库由 `materials/Materials/*.xml` 的 physicalproperties/DENS + `Graphics/Materials/**` 着色器 SolidColor RGB×255 生成；部署时种子不覆盖用户已编辑库 |
| 12 | 2026-10-28 | v1.1：颜色读取诊断 + 分类标签页 | ①`GetColor` 增 `out detail`，日志打印取色路径与原始值（`IZPart 原始=0x00BBGGRR`），不再静默返回 `<无>`；②表格改为 **TabControl：常用 / 全部 / 各分类**（按条数降序，标题带条数）；③新增「加入常用」按钮，`favorite="1"` 写回 XML；④分类页/常用页是同一批 MaterialItem 的对象快照，改单元格即改主对象，仅「全部」页可增删行 | `IZPart.SurfaceFinish` / `IZFacetPart.SurfaceFinish` 均返回 **ZSurfaceFinish（IZSurfaceFinish 的 coclass）**，读值需 `as IZSurfaceFinish` 再取 `Color` |
| 13 | 2026-10-28 | v1.2：删着色器字段 + 所有标签页可增行 | ①`自定义材料.xml` 与模型彻底去掉 `shader` 字段（着色器对注入无用，只留分类/材料/密度/颜色/常用）；②根本原因：分类页/常用页原先绑定快照 `BindingList`，`AddNew` 只进快照不进主库 → 无法保存。改为 `MaterialViewList`（继承 BindingList，持有主库引用并订阅 `ListChanged`）：插入时按本页语义补默认值（分类页补 Category、常用页补 Favorite=true）并同步 Add 进主库，删除同理回写主库 | 保存前自动清理「只点出空行、未填材料名」的占位行，避免空条目写进 XML |
| 14 | 2026-10-28 | v1.3：材料页「属性」表探源 | ①反射 `IZPartProperty` 全成员：**只有 MaterialName / MassDensity / 体积/质量等，没有 EX/NUXY/GXY/ALPX/KX/C/强度** → 这 8 项 API 既读不到也写不了；②资料库扩展到 **638 条**（Standard 260 + GB 378 去重 8），带 model/desc/ex/nuxy/gxy/alpx/kx/c/sigxt/sigyld/sigxc（590 条有 EX）；③界面右侧加只读「材料属性」面板，选中表格行即镜像该组数值；④`读取选中件材料` 用 `GetMaterialName` 回查资料库，命中即打印该材料的属性表值 | 用户目录无自定义材质库文件（`%APPDATA%\CAXA\CAXA 3D\29.0` 下无 material*.xml）→ **自定义材料随 .ics 文档存储**，所以属性表只能靠"库内定义 + 按名应用"，不能靠 API 逐项写 |
| 15 | 2026-10-28 | v1.4：自定义库路径探明（3iAdvFeature.dll 硬编码）+ **色卡功能** | ①注册表无材质库清单；官方库 4 个文件名写死在 `bin\3iAdvFeature.dll`，安装目录塞新文件不被加载；用户库固定目录 `Custom Material\<分类>\<子分类>\<材料名>.XML`（UTF-8 无 BOM、单 `<material>` 根、`<ModeType>` 子节点）；②用户铁律：投放必须严格按此两级目录+双文件（材料+Shader）模板，否则有 bug；③材料探索按用户要求**暂停**，界面保持；④新增「色卡」按钮 → ColorCardForm：参照 SolidWorks 宏 `颜色参考\usfSetColor.frm` 与 `2222.PNG`，RAL（208 色）/ GSB05-1426-2001（83 色）两个标准下拉 + RGB 输入 + 预览 + 「应用颜色」对选中件逐个 `ApplyColorOnly` 回读校验，成功后回写主窗体颜色框 | 色卡数据从 frm（GBK 编码）正则提取为 `色卡.xml`（291 条，DLL 同目录部署，只读）；SW 宏 ComboBox1=RAL、ComboBox2=GSB、TextBox1/2/3=R/G/B、Label4=预览、CommandButton1=应用 |
| 16 | 2026-10-28 | 色卡补齐：RAL 经典系列 208 → **216** | 以 wandlogo.com 的 RAL Classic 216 色官方清单（德文名+HEX）比对，缺 8 色已补：2017 RAL橙色(FC5500)、3028 纯红(CC2C24)、6035 珍珠绿(194D25)、6036 珍珠蛋白石绿(04574B)、6037 纯绿(2A7E3E)、6038 荧光绿(00A64F)、6039 纤维绿(3C9E4E)、9012 洁净室白(FFFDE6)；校验 216/216 无缺失无多出，总计 299 条（RAL 216 + GSB 83）；色卡.xml 为只读数据文件，改后**无需重启 CAXA/重装插件**，重开色卡窗体即生效 | 旧 SW 宏的 208 色是 RAL 2012 年前旧清单；6035-6039/9012 为后期新增，2017 是 RAL 企业色 |
| 17 | 2026-10-28 | v1.5：设计树穿透 + 随机颜色按钮拆分 | ①**随机(选中)**：零件直接上色、装配体穿透其所有叶子；**全部随机颜色**：穿透整个设计树（无需选中）；②实现路径：直接调 `IZAssembly.GetChildrenZArray()` + `IZElement.GetChildrenZArray()` 递归穿透（每层判断 `IsAssembly`，装配体递归、零件直接加）；③实测 20 层嵌套装配体、**865 个叶子零件全穿透**；④新增 `GetLeafElementsFromNodes(nodes)` 支持从任意选中节点集穿透、自动去重 | 实测日志：`GetSceneChildren: 1 个子元素` → `GetAssemblyLeafParts: 865 个叶子` → `随机上色通过 865` |
| 18 | 2026-10-28 | v1.5 清理：日志降噪 + 界面布局 + **GetAllLeafParts 陷阱实证** | ①`IZAssembly.GetAllLeafParts(eZPartType, Int32)` 返回 **int[]**（元素 Id，SAFEARRAY 封送为 VT_I4），无 managed 映射可用——**废弃**；②`IZSceneDoc.GetChildElements()` 返回 `object[]`，**可用**（之前反射 RCW 只看到 `ToString()`，证实 COM RCW 反射拿不到接口方法）；③逐零件 SetMaterialName/MassDensity/颜色写入/自定义属性日志从 `Log`（界面可见）降到 `Trace`（仅文件），汇总行保留在界面；④状态反馈从 Overlap Label 改窗口标题栏 `BaseTitle — 写入完成：通过 3 / 未通过 0` | 反射 `interop.ICApiIronCAD.dll` 真实 managed 签名：`Object GetChildElements()` / `ZArray GetChildrenZArray()` / `Object GetAllLeafParts(eZPartType, Int32)`；`GetAllLeafParts` 在真实环境返回 int[]，放弃 |
| 19 | 2026-10-28 | v1.6：材料属性回填规则可编辑 + **脚本重复 BOM 踩坑** | ①新增 `BackfillRule`：规则文件 `caxa3dsetMaterialaddfix.txt` 只取双引号内字段，`#` 整行注释跳过、`""` 不算字段、`Ordinal` 严格区分大小写；规则为空不兜底、不跨表、不组合；用户副本放 `%APPDATA%\caxa3dsetMaterial\`，DLL 同目录只做播种模板；②界面在「色卡」左侧新增「材料属性回填规则」按钮，用系统默认编辑器打开规则；③`deploy.ps1` 曾累积 **6 个连续 UTF-8 BOM**（`EF BB BF` 重复），PS 5.1 报 `术语 '﻿#' 不是…`（脚本仍继续执行）；已规范为单 BOM，`[scriptblock]::Create` 在 PS 5.1 解析通过 | 字节级校验：`[IO.File]::ReadAllBytes` 读首字节，循环计数连续 BOM 段；修复前后对比 6→1、4→1；`powershell.exe -NoProfile -Command` 逐个解析通过 |
| 20 | 2026-10-29 | **统一共享表 + 材料随零件下发** | ①**BackfillRule.IsSharedField 删除**：规则里所有字段统一写共享属性表，不再按 `SC` 前缀分流；`TryGetPropStrict` 同样只查共享表；②`MainForm._chkOnly.Checked = false`：默认"仅此零件使用"不勾选，`SetMaterialName(name, false)` 使材料随零件定义下发到全部链接实例；③删除 `GetPartPropMgr` 内部辅助方法 | 回读校验日志标注统一改为 `(共享表)`；DLL 内嵌 manifest 含 clrClass + sn.exe 重签通过；编译 0 错误 0 警告 |
| 21 | 2026-10-29 | **部署脚本彻底重写（对齐 caxa3dminitool 模板）** | 根因链：①旧 `deploy.ps1` 有 **7 个连续 UTF-8 BOM**（比 #19 的 6 个更严重），PS 5.1 首行直接崩溃；②**缺失第 4 登记点**：`HKCU\SOFTWARE\CAXA\CAXA 3D\<ver>\Applications\<addin名>`（CAXA 实际靠此注册表键判断 Add-in 是否加载，缺了导致"加载列表可见但 InitSelf 永不执行"）；③硬编码 `$caxaBin = '...\2027\bin'`，机器有 2024+2027 两个版本只部署一个；④restore 用 `*.bak` 整份回滚，config/manifest 是所有插件共享文件，会抹掉其他插件的登记。修复：**deploy.ps1 + restore.ps1 完全重写**，对齐 caxa3dminitool 成熟模板——自动发现所有 CAXA 版本目录、remove→add 两步清理、DLL 版本/PKT 从编译产物反射读取、补全注册表 Applications 键、restore 手术式清理（XML 节点移除 + 正则兜底，不回滚 .bak）、提权前复制到 `C:\ProgramData\` 带版本号隔离、部署后逐版本回读验证 4 个登记点 | 字节级修复：`EF BB BF × 7` → `EF BB BF × 1`（首字节即 `70 61 72` = `par`）；PS5.1 AST Parser 解析通过；部署后 CAXA 2024+2027 全部 4 个登记点 True：DLL=True config=True manifest=True registry=True；restore 后 residual check 两个版本均 clean |

## 一、材料库文件与材料页面的关系

### 1. 文件位置与编码

| 文件 | 大小 | 根节点 | 分类数 | 材料数 |
|---|---|---|---|---|
| `GB Materials.xml` | 762,858 B | `<MaterialLib Type="GB Materials" Version="1">` | 16 | 378 |
| `Standard materials.xml` | 531,536 B | `<MaterialLib Type="Standard Materials" Version="1">` | 15 | 260 |
| `Sustainability extras material.xml` | 11,216 B | `<MaterialLib Type="Sustainability extras" Version="1">` | 3 | 8 |

- 安装目录：`C:\Program Files\CAXA\CAXA 3D\2027\AppData\zh-cn\materials\Materials\`（本项目已整目录拷贝到 `materials\`）。
- 编码：**UTF-16LE + BOM**（`FF FE`），声明 `encoding="UTF-16"` / `utf-16`。自造库必须保持同样编码，否则极易解析失败。
- 数值允许普通十进制与小写 e 记数（`0.200E+12`、`0.160E-4`、`1.2e+11`）。

### 2. 树结构与材料页面的对应

材料页左侧树的根节点 = **MaterialLib**（显示名"国标材料"，与 `Type` 值对应，由程序本地化），下一级 = `classification`（如"铁""合金钢"），叶子 = `material`。截图 `信息收集\材料页面.PNG` 中 `国标材料 → 铁 → 软铁(SN)` 的顺序与 `GB Materials.xml` 完全一致。

```
<MaterialLib Type="..." Version="1">
  <classification name="铁">
    <material name="软铁(SN)" GUID="..." description="" propertysource="">
      <physicalproperties Type="eLinear_elastic_isotropic">
        <EX displayname="弹性模量" value="120000000000.000000"/>
        ...
      </physicalproperties>
      <shaders>
        <shader path="\metal\iron\cast iron.xml"/>          <!-- 基路径：materials\Graphics\Materials -->
        <swtexture path="\images\textures\metal\cast\cast_rough.jpg"/> <!-- 基路径：materials -->
        <swatchcolor RGB="0.439,0.412,0.412">
          <ColorOptical Ambient="1" Transparency="0" Diffuse="1" Specularity="1" Shininess="0.31" Emission="0"/>
        </swatchcolor>
      </shaders>
    </material>
  </classification>
</MaterialLib>
```

### 3. 属性键 ↔ 页面字段 ↔ 单位（实测映射）

| XML 元素 | 页面显示名（各库写法略有差异） | 单位 | 备注 |
|---|---|---|---|
| `DENS` | 密度 / 质量密度 / Mass density | kg/m³ | 直接决定零件重量 |
| `EX` | 弹性模量 / X 弹性模量 | N/m² | |
| `NUXY` | 泊松比 / XY 泊松比 | — | |
| `GXY` | 抗剪模量 | N/m² | |
| `ALPX` | 热膨胀系数 / 热扩张系数 | /K | |
| `KX` | 导热率 / 热导率 | W/(m·K) | |
| `C` | 比热 | J/(kg·K) | |
| `SIGXT` | 抗拉强度 / 张力强度 / Tensile strength | N/m² | |
| `SIGYLD` | 屈服强度 / 屈服力 | N/m² | |
| `SIGXC` | 抗压强度 / 压缩强度 | N/m² | 仅标准库有 |
| `RK` | 硬化因子（0=同向性，1=运动性） | — | 部分材料有 |
| `BETA` / `GAMMA` / `VISC` | 容积热膨胀系数 / 比热比率 / 动态粘度 | — | 仅标准库少量材料 |
| `材料模型名称` | — | — | 纯文本元素（无 displayname），少见 |

`physicalproperties` 的 `Type` 固定为 `eLinear_elastic_isotropic`（线性弹性各向同性），页面"模型类型"下拉即显示该项。

### 4. 数据质量（影响注入时的名称定位）

- 638 条材料、636 个唯一 GUID（2 个 GUID 重复）。
- **8 组材料名跨库/库内重名**：`Q235-A`、`Q235-B`、`Q235-A (F)`、`Q195`、`Q215`、`2A90 (GB)`、`7A10 (GB)`、`K409#` 各出现 2 次 → `SetMaterialName` 按名称定位时存在歧义，工具内应优先用 GUID 或"名称+分类"定位，并提示重名。
- 所有材料都有 `shaders`、`GUID`、`propertysource`（空串）；`description` 基本为空。
- 外观（颜色/光亮度/透明度/凸痕/反射/贴图/散射 7 个页签）由 `shaders` 决定：`shader path` 指向 `materials\Graphics\Materials\<材质族>\<具体>.xml`，`swtexture` 指向 `materials\images\textures\...`，`swatchcolor` 是缩略图/未渲染时的色块。
- GB 库 shader 复用度很高：193 条用 `polished steel`、120 条用 `polished aluminum`、27 条用 `cast iron`、16 条用 `satin finish stainless steel`。

## 二、进入材料页面的 API 情况

- **没有公开 API**：全量检索 ICAPIIRONCADLib / ICAPIHELP，与材料相关的只有 `IZPartProperty.GetMaterialName/SetMaterialName`、`IZAssemblyProperty.GetMaterialName/SetMaterialName`、`IZElement.GetMaterialNameProperty`，全部是读写名称，**不含打开对话框**。
- **没有命令枚举**：`IZBaseApp.RunCommand(eZCommand)` 的枚举只有 13 个值（缩放、隐藏、ghost、Excel 更新等），无材料项。
- 对话框归属已定位：右键菜单项"**编辑材质库...**"的字符串存在于 `bin\zh-cn\3isdLg.dll`（场景文档模块 `3isd31.dll` 的语言资源），"编辑材料"同时出现在 `3icmLg/3iuiaLg/3iAdvFeatureLg` 等资源里。
- 结论：程序化"进入材料页面"只能走 **UI 自动化**——在结构树/场景中右键选中零件，点击上下文菜单"编辑材质库..."（可复用 `caxa3dcodenameextraction` 的弹窗自动点击与焦点回归代码）。设计环境的右键菜单是原生 MFC 菜单，无法用 CRX 命令表投递。

## 三、注入路径（候选，含实测状态）

| 路径 | API | 说明 | 实测状态 |
|---|---|---|---|
| 材质名称注入 | `IZPartProperty.SetMaterialName(string name, bool useByOnlyThisPart)` | 给零件写材料名；`true`=仅此零件使用（类似页面提示的"拷贝到自定义库"） | 待实测 |
| 材质名称读取 | `IZPartProperty.GetMaterialName(ref string, ref bool)`；`IZElement.GetMaterialNameProperty(ref string)` | 读取现有材料名 | 待实测 |
| 密度注入 | `IZPartProperty.MassDensity`（get/put, double） | 直接设定密度，决定 `CalculatedMass` | 待实测（单位/是否随材料联动） |
| 质量模式 | `IZPartProperty.MassType`（`Z_MT_Calculated=0` / `Z_MT_Specified=1`）、`SpecifiedMass`、`CalculatedMass` | 计算重量 or 指定重量 | 待实测 |
| 重量校验 | `IZPartProperty.GetMassProperties(density, useLocalCoord, accuracy, ...)` | 用指定密度算体积/质量/惯性矩，可作"副作用判据" | 待实测 |
| 外观注入 | `IZPart.SurfaceFinish`（get）、`IZBodyFace.GetSurfaceFinish/SetSurfaceFinish`、`IZBaseApp.ShaderMgr`→`IZShaderMgr.AddShader/FindShader/GetShader` | 表面外观（颜色/反射/贴图等） | 待实测 |
| 材质库扩展 | 向 `AppData\zh-cn\materials\Materials\` 增放自定义 `*.xml`（同 schema） | 让自备材料出现在材料页并在 `SetMaterialName` 时可按名命中 | 待实测（程序未在任何 dll 中出现该路径常量，推测为目录枚举加载） |

补充事实：
- 页面提示"默认材质库中的材质不能被编辑，必须先拷贝到自定义库"，且用户已知"自定义材料修改后无法导出" → 自定义材料随文档（.ics）走，不能跨机复制；**跨机分发只能靠自造 XML 库文件**，这也印证了"材质库扩展"路线的价值。
- 注册表中材料库条目仅有一条：`Cat0007 = Scene\Materials.icc`；磁盘上并不存在 `Materials.icc`（由程序按需生成），因此 XML 目录不是通过配置注册的。

## 四、国标材料的设置方法（可复用规范）

1. **造库**：复制 `GB Materials.xml` 为骨架，只保留需要的 `<classification>`/`<material>`，`MaterialLib Type` 改为自己的库名（决定材料页根节点显示名）。
2. **每个材料必填**：`name`（唯一，建议"牌号 (GB)"）、`GUID`（新生成，不可与官方重复）、`description`、`propertysource`（可空串）；`physicalproperties Type="eLinear_elastic_isotropic"`。
3. **物理量按 SI 单位填裸值**，至少 `DENS/EX/NUXY/GXY/ALPX/KX/C/SIGXT/SIGYLD`；`displayname` 可沿用官方写法（"密度""弹性模量"…），程序按元素名 `DENS` 等识别，不依赖 displayname。
4. **外观三件套**：`shader path` 选 `Graphics\Materials` 下已有外观（钢/铝/铁等），`swtexture` 选 `images\textures\...`，`swatchcolor RGB` + `ColorOptical`（Ambient/Diffuse/Specularity/Shininess）给缩略图色块；三项可省略 `swtexture`，但 `shader` + `swatchcolor` 建议保留。
5. **保存为 UTF-16LE + BOM**，根节点 `Version="1"`。
6. 参考实现要点：GB 库把国标号写进分类名（如"碳素结构钢 GB/T 700-2006""不锈钢 GB/T 20878-2024"），材料名带 `(GB)` 后缀；同一牌号在不同标准下会重复（Q235-A 等），造库时要靠 GUID 去重。

## 五、下一步实证清单（未验证，勿当结论）

1. 向 `materials\Materials\` 放入自造 XML 后，材料页是否自动出现新库（目录枚举假设）。
2. `SetMaterialName("自定义名", true/false)` 在名称不存在于任何库时的行为（报错？自动建同零件材质？密度取默认？）。
3. `MassDensity` 的单位与写入后 `CalculatedMass`/`GetMassProperties` 的业务侧变化（作为成功判据，不看 API 返回值）。
4. 保存 .ics → 关闭 → 重开后材料名/密度是否持久化（虚拟件同样验证）。
5. 装配体层级注入：`IZAssemblyProperty.SetMaterialName` 与零件级的关系（子件是否继承）。
6. 材料名写入后，工程图 BOM/标题栏"材料"栏是否能读到（用 `IZElement.GetMaterialNameProperty` 交叉验证）。

## 六、探针 Add-in（已交付，2026-10-28）

### 1. 产物与用法

| 产物 | 说明 |
|---|---|
| `bin\x64\Release\net48\caxa3dsetMaterial.dll` | 主程序集（x64 / net48 / 强名称 / 内嵌 SxS 清单） |
| `安装.bat` → `deploy.ps1` | 提权部署到 CAXA bin，登记 `IronCad.Addin.config` + `IronCad.exe.manifest` |
| `卸载.bat` → `restore.ps1` | 备份还原 + 正则兜底移除条目，最后校验残留 |

用法：双击 `安装.bat` → 启动 CAXA 3D 2027 → 场景功能区「材料工具」→ 在 CAXA 中选中零件 → 「窗口前置」回 CAXA 确认 → 「读取选中件材料」→ 填材料名/密度 → 「写入选中件」。

### 2. 界面（参照 caxa3dtitleupdate 风格）

窗口前置 / 读取选中件材料 / 材料名 / 密度(kg/m³) / 仅此零件使用 / 写入选中件 / 扫描材料库 / 保存当前文档 + 日志区（同时写 `%TEMP%\caxa3dsetMaterial_trace.log`）。

### 3. 接口签名实证（反射 `interop.ICApiIronCAD.dll`，优先于文档）

| 用途 | 实际 C# 签名 | 关键点 |
|---|---|---|
| 取选中元素 | `IZSceneDoc.SelectionMgr`（返回 `ZSelectionMgr`，可隐式转 `IZSelectionMgr`）→ `GetSelectedElementsZArray()`（返回 `ZArray`） | 比 `GetSelectedElements()`（VARIANT）更好用 |
| 遍历数组 | `IZArray.Count(out int)`、`IZArray.Get(int, out object)` | **不是** `property Count` |
| 设计树根子元素 | `IZSceneDoc.GetChildElements()` → `Object`（实际 `object[]`） | **可用**；不要用 `GetFirstChild()/GetNextChild()` |
| 取装配体子元素 | `IZAssembly.GetChildrenZArray()` → `ZArray` | **可用**（递归穿透核心）；C# 侧 `IZAssembly` 继承 `IZElement`，二者都有此方法 |
| IZElement 子元素 | `IZElement.GetChildrenZArray()` → `ZArray` | **可用**；ZArray 用 `Get(i, out var)` 解包不受 SAFEARRAY 元素类型影响 |
| ~~取叶子零件~~ | ~~`IZAssembly.GetAllLeafParts(eZPartType, Int32)`~~ | **废弃**：managed 签名返回 `Object`，运行时封送成 **`int[]`（元素 Id）**，无法映射回 IZElement；C++ 文档写 `VARIANT*` 是 out 参数，实际 managed 签名完全不同 |
| 类型判断 | `IZElement.Type` → `eZElementType`（枚举，转 `int` 比较） | `Z_ELEMENT_ASSEMBLY = 2`；枚举名含 `Assembly` 即装配体系 |
| 取材料属性 | `IZPartProperty p = elem as IZPartProperty;`（QI） | 官方 C++ 样例 `IZPartPropertyPtr spiProp = spiElem;` 同源 |
| 读材料名 | `GetMaterialName(out string, out bool)` | 出参是 `out`，不是文档写的 `ref` |
| 写材料名 | `SetMaterialName(string, bool)` | 密度写入必须放在名称之后 |
| 密度/质量 | `MassDensity`（读/写 double）、`MassType`（`eZMassType`）、`CalculatedMass`（只读）、`SpecifiedMass`（读/写） | |
| 体积 | `double GetVolume(double accuracy, out double achieved)` | 返回体积（m³） |
| 用户属性 | `GetUserProperty(string)` 返回 string；`SetUserProperty(string,string)` | 反射显示的最终形态 |
| 自定义属性 | `IZElement.GetCustomPropManager(int selector)` → `IZCustomPropMgr`；写 `AddCustomProp(name, object, replace)`；读 `GetCustomPropAsString(name, out string, out bool found)`；`IsSharedMgr()` 可自检 | **selector：0=零件级，1=共享（文档级）**。CAXA 属性界面里的 `SC材料`/`SC图号` 等 SC* 属性都在**共享表**，只写零件表则界面看不到（本机 `get_CustomPropMgr` 封送描述有误，必须走 GetCustomPropManager，否则 RPC_E_FAULT） |
| 颜色 | `IZPart.SurfaceFinish.Color`（`IZSurfaceFinish.Color`，int，**COLORREF 0xBBGGRR，不是 0xRRGGBB**） | 实测：写 0xFF0000→蓝、写 0x0000FF→红、写 0xFFFF00→青；界面输入 RRGGBB 必须转成 BGR 再写。`IZFacetPart.SurfaceFinish` 同理；设完调 `IZPart.Update()` |
| 刷新 | `IZSceneDoc.Update()` | 写完后调用，刷新浏览器/图形 |

### 4. 成功判定（只看业务副作用）

1. 回读 `GetMaterialName` 与期望名比较（忽略大小写/首尾空格）；
2. 回读 `onlyThisPart` 标志；
3. 回读 `MassDensity` 与期望值（相对误差 1e-6）；
4. `密度 × GetVolume()` 与 `CalculatedMass` 比较（1% 容差；不等时提示"质量模式可能是指定重量"）；
5. 回读自定义属性 `材料` / `SC材料`（`GetCustomPropAsString` 的 `found` 必须为 true 且值一致）；
6. 回读 `IZPart.SurfaceFinish.Color` 与期望 0xRRGGBB 一致。

### 4.1 实测结果（2026-10-28，用户机器 CAXA 29.0.0.20519）

- 名称 + 密度：**通过**（`Q235A` / 7000 / 仅此零件=True，回读一致，`密度×体积==CalculatedMass`）。
- `SetMaterialName` 传库中不存在的名称 `Q235A`（库中是 `Q235-A`）也成功，且不改变密度（7900→7000 是自己写的密度）→ 名称定位失败不会自动套用库内物理量，需自己写密度。
- `IZDoc.Save()` 对新文档（无文件名）返回 **E_FAIL**：未保存过的文档不能直接 Save，需先另存为。

### 4.2 颜色编码实证（三组对照，2026-10-28）

| 界面输入(RRGGBB) | 写入的 API 值 | 模型实际显示 | 结论 |
|---|---|---|---|
| FF0000（红） | 0x00FF0000 | 蓝 | API 按 COLORREF 解释，高低位反了 |
| 0000FF（蓝） | 0x000000FF | 红 | 同上 |
| FFFF00（黄） | 0x00FFFF00 | 青 | 同上 |

→ 处理：`RgbToApi(rgb) = (B<<16)|(G<<8)|R`，`ApiToRgb` 反向转换；界面与日志始终用 RRGGBB，只有写 API / 比对时才转换。

### 4.3 SC材料归属实证

- `SC材料` 在 `材料` 之后写入零件表（selector=0）：API 回读成功，但 CAXA 属性界面看不到。
- 参考 [caxa3dAttributeManageraddin] 的实证：`SC图号/SC名称/SC规格/SC材料` 全部在 **共享管理器**（`IsSharedMgr()` 为真那张），写入用 `SetValueByName`。
- 修正后的写法（与参考插件同构）：
  1. `GetSharedPropMgr`：selector 依次试 0、1，返回第一个 `IsSharedMgr()==true` 的管理器；
  2. `GetPartPropMgr`：同理返回第一个 `IsSharedMgr()==false` 的管理器；
  3. 写入优先级 `SetValueByName` → 失败再 `AddCustomProp(name, value, true)`；
  4. `材料`→零件表，`SC材料`→共享表；另一张表若已存在同名属性也同步覆盖；
  5. 回读时两表都查，日志标注 `<共享表>` / `<零件表>` 来源。


### 5. 工程要点（已验证）

- **Addin 入口类必须 `[ComVisible(true)]`**（程序集级仍 `ComVisible(false)`）：CAXA 用 `IronCad.exe.manifest` 里的 `<dependency>` + DLL 内嵌清单里的 `<clrClass clsid/progid>` 做 SxS COM 激活；类不可见 → mt 不生成 clrClass → Add-in 出现在加载列表但 `InitSelf` 永不执行，**无任何报错**。判定手法：看 `%TEMP%` 是否有 trace 日志，并用 `mt.exe -inputresource:dll;#2` 导出清单查 `<clrClass>`。
- SDK 工程默认**不写** `ComVisible` 属性 → .NET Framework 默认 `true`（参考插件 `caxa3dapigetaddin` 即如此）；本项目用 `AssemblyInfo.cs` 显式 `[assembly: ComVisible(false)]`，只放行 Addin 入口类，既满足"不污染 COM 清单"又能被激活。
- 源码含中文必须 `<CodePage>65001</CodePage>`，否则无 BOM 文件按 ANSI 编译会乱码。
- Release 后处理三步不能省：`mt.exe -managedassemblyname` → `mt.exe -outputresource:;#2` → `sn.exe -R` 重签。
- 部署脚本已内置两道防呆：① `IRONCAD` 进程在运行则拒绝部署（DLL 被占用）；② 内嵌清单不含本插件 CLSID 的 `<clrClass>` 则拒绝部署。
- PowerShell 脚本必须 UTF-8 **带 BOM**（PS 5.1 靠 BOM 识别 UTF-8），但 `.bat` **不能带 BOM**（否则 cmd 解析首行出错）。
- 图标用内嵌 BMP + `ConvertImage : AxHost` 转 `IPictureDisp`，MemoryStream 需静态保活。
- 字符串含中文的资源 dll 定位：`bin\zh-cn\3isdLg.dll`（场景）等。

### 6. 材料页右下「属性」表：来源与可注入性（v1.3 实证）

**结论：这 8 项物理量没有 API 通道，只能由"材料库定义 + 按名应用"带出来。**

- 反射 `IZPartProperty` 全成员（2026-10-28）：`GetMaterialName / SetMaterialName / GetVolume / GetCalculatedMass / MassDensity / MaterialInLib` 等，**无 EX、NUXY、GXY、ALPX、KX、C、SIGXT/SIGYLD/SIGXC 任何读写器**。
- CAXA 3D 29.0 用户目录（`%APPDATA%\CAXA\CAXA 3D\29.0`）下**没有** 自定义材质库 xml → 文档里"自定义材料"随 `.ics` 存储，不能跨机，与项目说明第 6 行一致。
- 因此该表的唯一来源是 `Materials\*.xml` 的 `material/physicalproperties` 节点（`Type` → 模型类型；`DENS` → 密度；`EX/NUXY/GXY/ALPX/KX/C/SIGXT/SIGYLD/SIGXC` → 其余 9 行）。
- 本版做法：把 638 条材料的完整物理量收进资料库，右侧面板只读镜像 + 读取选中件时按材料名回查并打印，解决"看得到、对得上"的问题。

**优化方向（按可行性排序）**

| 方案 | 做法 | 优点 | 风险/前置 |
|---|---|---|---|
| A. 自造材料库 XML（推荐） | 按第 4 章规范生成 `<MaterialLib>` 的 UTF-16LE XML，投放进 `…\AppData\zh-cn\materials\Materials\`，材料名唯一，写入时 `SetMaterialName(名称)` | 属性表 10 项全部由 CAXA 自动回填，跨机一致，可批量 | 需写 Program Files（管理员）；**未实测**该目录是否被枚举加载、是否需重启；名称冲突需避开官方 638 个名字 |
| B. 只写 MassDensity（当前） | 现有 `MassDensity` + 材料名 + 自定义属性 | 已实测通过、零风险 | 属性表其余 9 项不变；材料名不在库时 CAXA 回退默认值 |
| C. 文档内自定义材料（UI 自动化） | 驱动"编辑材质库/新建材料"对话框填 10 项 | 不污染系统目录 | MFC 控件树未测绘，模态框多、易随版本变；违反"只走命令表内命令"风险高 |

**下一步最小验证（建议）**：~~复制 `GB Materials.xml` 为 `ZG Test.xml` 放入安装目录~~（已证伪，见 6.1），改为投放到用户级自定义库。

#### 6.1 材料库清单路径：硬编码 + 用户目录（2026-10-28 探明）

**注册表里没有任何材质库路径**：全量扫 `HKCU\Software\CAXA`、`HKLM\SOFTWARE\CAXA[\WOW6432Node]`，无 `material` 相关键值（仅有 `HKCU\...\CAXA 3D 29.0\Applications\<addin名>` 这类插件登记）。

**清单写死在 `bin\3iAdvFeature.dll`**（偏移约 1.55M，UTF-16 字符串表）：

| 类型 | 路径（字面量） |
|---|---|
| 官方库（4 个，安装目录，**新增文件不被加载**） | `\materials\Materials\GB Materials.xml`、`Standard materials.xml`、`Sustainability extras material.xml`、`DIN Materials.xml` |
| 用户自定义库（目录，**重启即扫**） | `\CAXA\` → `\Materials\Custom Material` |
| 自定义着色器根 | `\Custom\`（=`Graphics\Materials\Custom`） |
| 其它字面量 | `\Materials`、`Custom Material`、`Images\`、`images\`、`ReflectEnv`、`cutAway` |

用户自定义库实测绝对路径（无管员权限，LocalAppData）：

```
%LOCALAPPDATA%\CAXA\CAXA 3D\29.0\zh-CN\materials\Materials\Custom Material\<分类>\<子分类>\<材料名>.XML
%LOCALAPPDATA%\CAXA\CAXA 3D\29.0\zh-CN\materials\Graphics\Materials\Custom\<分类>\<子分类>\<材料名> Shader.XML
```

**用户强调（2026-10-28，铁律）**：投放自定义材料时文件夹层级必须**严格**按上面两条模板，即固定两段式：
`Materials\Custom Material\<分类>\<子分类>\<材料名>.XML` 与 `Graphics\Materials\Custom\<分类>\<子分类>\<材料名> Shader.XML`——
`<分类>`/`<子分类>` 两级目录、`<材料名>.XML` / `<材料名> Shader.XML` 两个文件名、编码格式（UTF-8 无 BOM）、单 `<material>` 根元素、`<ModeType Type=...>` 子节点写法，全部照抄用户机器上的真实样例 `自定义材料\个人常用\Q235A.XML`，**否则有 bug**（层级错了 CAXA 扫不到或归类错乱）。

用户机器上已有真实样例 `…\Custom Material\自定义材料\个人常用\Q235A.XML`（851 B）+ `…\Custom\自定义材料\个人常用\Q235A Shader.XML`（532 B）。

**单个自定义材料文件的格式（关键，与官方库不同）**：

- 编码 **UTF-8 无 BOM**（官方库是 UTF-16LE+BOM）；
- 根元素就是单个 `<material Version="1" Name="Q235A" GUID="{...}" description="" propertysource="">`，**没有 `<MaterialLib>` 外层、没有 `classification`**——分类靠**目录层级**表达（`自定义材料\个人常用`）；
- 物理属性节点是 `<physicalproperties><ModeType Type="eLinear_elastic_isotropic"/>…`：注意模型类型是子节点 `<ModeType Type=...>`，不是官方库那种 `physicalproperties Type=...` 属性；
- 子项：`EX/NUXY/GXY/ALPX/DENS/KX/C/SIGXT/SIGXC/SIGYLD/MDAMP`，每个都是 `<X displayname="X弹性模量" value="208000000000"/>`；
- 着色器：`<shaders><shader path="\Custom\<分类>\<子分类>\<材料名> Shader.XML"/></shaders>`，路径相对 `Graphics\Materials`；
- 着色器文件格式：`<materialshader Version="1">` + `<Color><SolidColor RGB="0.501961,1.000000,0.000000"/></Color>` + `<Finish>` / `<Transparency>` / `<Bumps>` / `<Reflection>` / `<Decal>` / `<Emission>`，RGB 为 **0~1 浮点**。

**这也解释了用户最初的问题**："自定义材料修改后无法导出"——它就是 LocalAppData 下**一材料一文件**散落存放，跨机需整目录拷贝。

**已投放的验证件**（`zgtlib.ps1`，无需管理员）：

| 产物 | 内容 |
|---|---|
| `…\Custom Material\ZG测试\ZG_TEST_Q235.XML` | 861 B，UTF-8 无 BOM，EX=2.06e11、NUXY=0.3、DENS=7850、SIGYLD=235e6 |
| `…\Graphics\Materials\Custom\ZG测试\ZG_TEST_Q235 Shader.XML` | 520 B，深灰 RGB=0.4,0.4,0.4 |
| `测试材料库-安装.bat` / `测试材料库-移除.bat` | 幂等投放/清理，CAXA 运行时拒绝投放 |

**判定**：重启 CAXA → 零件右键 → 材料 → 左侧树是否出现 **自定义材料 → ZG测试 → ZG_TEST_Q235**，选中后右下属性表是否显示 7850 / 2.06e11 / 0.3。

**方案 A 成立后的量产形态**：把 `自定义材料.xml` 638 条 → 每条生成一个 `<材料名>.XML`（分类→目录），安装脚本一键投放/卸载，插件写入时 `SetMaterialName("ZG_xxx")` 命中即可让属性表 10 项自动回填，真正实现跨机一致的材料注入。


## 已否决清单

- ~~Addin 入口类使用 `ComVisible(false)`~~：导致 clrClass 缺失、Add-in 静默不加载（2026-10-28 实证）。永久改为入口类 `ComVisible(true)` + 程序集级 `false`。
- ~~COM RCW 对象用 `GetType().GetMethods()` 看接口方法~~：RCW 返回 `__ComObject`，反射只看到基类方法（ToString/GetType 等），接口方法全部不可见。**直接用 managed 接口方法**（编译期就有），永远别对 RCW 用反射。
- ~~`IZAssembly.GetAllLeafParts(eZPartType, Int32)` 取叶子零件~~：managed 签名返回 `Object`，运行时封送成 **`int[]`（元素 Id）**，无 SAFEARRAY VT_VARIANT 映射，废弃。改用 `IZAssembly.GetChildrenZArray()` + `IZElement.GetChildrenZArray()` 递归穿透，ZArray 用 `Get(i, out var)` 解包不受 SAFEARRAY 类型影响。
- ~~restore.ps1 用 `*.bak` 整份覆盖回滚 config/manifest~~：这两个文件是所有 Add-in 共享的，整份回滚会抹掉其他插件的登记。永久改为"手术式"XML 节点移除 + 正则兜底。

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

## API 速查（源码索引）

- `ICAPIIRONCADLib.txt`：`IZPartProperty` 接口总表（含 GetMaterialName / SetMaterialName / MassDensity / MassType / GetMassProperties）
- `ICAPIIRONCADLib.txt`：`IZElement.GetMaterialNameProperty`
- `ICAPIIRONCADLib.txt`：`IZSurfaceFinish`、`IZBodyFace.SetSurfaceFinish`、`IZShaderMgr`、`IZBaseApp.ShaderMgr`
- `ICAPIIRONCADLib.txt`：`eZCommand`（13 个值，无材料命令）

## 并入 caxa3dmytoolsaddin 集合体（2026-10-29）

### 入口改造
- 删除原独立 Add-in `Addin.cs`，新建 `Plugin.cs → MaterialPlugin : IPlugin`：`Initialize` 预建窗口 / `Activate` 显示 / `Shutdown` 释放；不注册 Ribbon（集合体铁律 1/4）。
- 删除 `Icons\*.bmp`、`ConvertImage.cs`、`stdole` 引用（只服务 Ribbon 图标，迁入后无运行期依赖）。
- 工具箱显示名「材料工具」；程序集级 `ComVisible(false)`，插件 DLL 不内嵌 SxS 清单。

### 资源路径
- 合并后 `AppContext.BaseDirectory` 指向 CAXA bin，种子资源改为
  `Path.GetDirectoryName(typeof(X).Assembly.Location)` 优先、`AppContext.BaseDirectory` 兜底：
  - `色卡.xml`（ColorCardForm 原本已是 DLL 相对路径）；
  - `自定义材料.xml`（`MaterialDb.SeedPath`）。
- 用户数据目录保持不变：`%APPDATA%\caxa3dsetMaterial`；老用户资料库继续生效，不迁移、不覆盖。

### 构建 / 打包 / 部署登记
- `build.ps1` 的 `$plugins` 清单增加本插件，声明 `Extra=@('.xml')`。
- `package.ps1` 扩展名白名单补 `.xml`（否则 zip 会静默丢掉色卡与资料库种子）。
- `deploy.ps1` 的 `$legacyDlls / $legacyNames / $legacyClsids` 补原独立 Add-in 的 dll 名、目录名、CLSID `{C4B2A6E7-...}`，安装时自动撤销独立登记，避免按钮重复。
- 冒烟：无 CAXA 上下文用 `Activator` 构造 `MainForm(null)`（940×700），种子资源成功播种到 `%APPDATA%`。

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
