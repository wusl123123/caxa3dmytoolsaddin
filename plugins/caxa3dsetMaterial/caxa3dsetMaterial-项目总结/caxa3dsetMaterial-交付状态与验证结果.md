# caxa3dsetMaterial-交付状态与验证结果

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

