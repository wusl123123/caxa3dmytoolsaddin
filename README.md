# caxa3dmytoolsaddin

CAXA 3D 内置插件集合体（Add-in Host）与统一工具箱。CAXA 3D 中只登记**一个** Add-in，主程序通过契约接口反射加载 `plugins/` 下的多个独立子插件，功能区只保留一个「我的工具箱」按钮。

- 运行环境：CAXA 3D 2024 及以上 / .NET Framework 4.8 / x64
- 语言与界面：C# + WinForms
- 主程序：`src/Host`，唯一带内嵌 SxS 清单的程序集
- 子插件：`plugins/*`，各自保留独立解决方案，DLL 不内嵌任何清单

---

## 一、为什么必须是集合体

每个独立的 CAXA 3D .NET Add-in 都会内嵌 `interop.ICApiIronCAD` 类型并各自带一份 SxS 清单。`mt.exe` 会为同名 ProgId 生成不同的 CLSID，同一激活上下文合并失败，结果是 **CAXA 3D 无法启动**。

集合体从根上解决这个问题：

1. 只有主程序 `caxa3dmytoolsaddin.dll` 是 Add-in，内嵌且仅内嵌一条 `clrClass caxa3dmytoolsaddin.Addin`；
2. 子插件直接引用契约程序集，不内嵌清单、不注册 COM、不注册 Ribbon；
3. 新增插件只需把编译产物放进 `src/plugins/<插件名>/`，主程序无需改动。

---

## 二、插件清单

| 插件 Id | 界面名称 | 功能概述 |
| --- | --- | --- |
| `Caxa3dcudeaddin` | 立方体工具 | 原点建块、vtable 槽位调用、子进程兜底 |
| `caxa3dAttributeManageraddin` | 自定义属性管理器 | 基于 xlsx 属性表批量读写自定义属性，共享表精确匹配 |
| `caxa3dcodenameextraction` | 带图改名及图号分离 | 图纸环境 CRX 桥、图号分离、批量改名、弹窗代点 |
| `caxa3dsetMaterial` | 材料工具 | 材料 / 密度 / 颜色批量设置，色卡与材料资料库 |
| `caxa3dTreeBom` | 树形BOM | 按装层级构建 BOM 并导出 Excel，合并规则可配 |
| `caxa3didcskill` | 后台进程清理 | 识别并清理无窗口的 IRONCAD 残留后台进程 |
| `caxa3dminitool` | 小工具合集 | 曲线、元素、特征、内核等调试类小工具 |
| `caxa3dsheetmaster` | 折弯管理大师 | 钣金折弯数据管理、K 因子计算 |
| `caxa3dexbtest` | 图纸环境矩形测试 | 图纸环境 CRX 桥接模块接入样例 |

---

## 三、目录结构

```
caxa3dmytoolsaddin/
├── Lib/                           ICApiAddin.snk + interop.ICApiIronCAD.dll
├── build.ps1                      一键编译：契约 -> 子插件 -> 主程序，并汇总到 src/plugins
├── deploy.ps1  / 安装.bat         一键部署（自动提权）
├── restore.ps1 / 卸载.bat         一键卸载（手术式清理，可重复执行）
├── package.ps1 / 打包.bat         无源码交付打包 -> dist/caxa3dmytoolsaddin-<版本>.zip
├── src/
│   ├── Contracts/                 契约类库（不引用任何 CAXA 类型）
│   ├── Host/                      主程序 caxa3dmytoolsaddin
│   └── plugins/                   插件编译产物汇总目录（主程序运行时扫描，构建生成）
└── plugins/                       各插件独立解决方案（可单独打开维护）
    ├── Caxa3dcudeaddin/
    ├── caxa3dAttributeManageraddin/
    ├── caxa3dcodenameextraction/
    ├── caxa3dsetMaterial/
    ├── caxa3dTreeBom/
    ├── caxa3didcskill/
    ├── caxa3dminitool/
    ├── caxa3dsheetmaster/         含 CRX 桥子工程（C++）
    └── caxa3dexbtest/             含 CRX 桥子工程（C++）
```

运行期布局：

```
C:\Program Files\CAXA\CAXA 3D\<年份>\bin\
├── caxa3dmytoolsaddin.dll         主程序（内嵌 SxS 清单，强名称签名）
├── Caxa3dMyTools.Contracts.dll    契约
└── caxa3dmytoolsaddin\plugins\<插件名>\<插件名>.dll
```

---

## 四、环境要求

| 项目 | 要求 |
| --- | --- |
| CAXA 3D | 2024 及以上（版本号换算：`年份 - 1998`，如 2027 -> 29.0） |
| 运行时 | .NET Framework 4.8 |
| 平台 | x64（`PlatformTarget` 必须为 x64） |
| 构建 | .NET SDK（`dotnet build`）+ Windows SDK 的 `mt.exe` + `sn.exe` |
| 权限 | 部署 / 卸载需管理员权限（脚本会自动提权） |

### 关于强名称私钥

`Lib/ICApiAddin.snk` 是强名称私钥，**不在本仓库中**。主程序与各子插件均为 `SignAssembly=true` 且指向该密钥，缺失会导致编译失败。

使用前请自备同名密钥并放到以下位置（内容需一致）：

```
Lib\ICApiAddin.snk
plugins\<每个插件>\Lib\ICApiAddin.snk
```

若改用自有密钥，须保证部署后 DLL 的 publicKeyToken 与 `IronCad.exe.manifest` 中的依赖声明一致，否则 CAXA 加载时 SxS 合并失败。仓库当前约定值为 `56647e1eee90f6d4`。

---

## 五、快速开始

### 1. 编译

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

流程：契约 -> 9 个子插件 -> 主程序 -> 清理并重建 `src/plugins/` 暂存目录。

编译后处理（自动）：主程序剥离 interop 的 `clrClass/clrSurrogate`、重新嵌入清单、`sn -R` 重签；子插件**不做**任何清单处理。

### 2. 安装

```powershell
安装.bat
```

双击即可，脚本会先复制自身到 `C:\ProgramData\caxa3dmytoolsaddin\` 再提权执行，自动扫描本机所有 CAXA 3D 版本目录并完成四项登记。

### 3. 卸载

```powershell
卸载.bat
```

手术式移除（`local-name()` 精确 XML 删除 + 正则兜底），只删本程序的登记，不整份回滚 `.bak`，因为 `IronCad.Addin.config` / `IronCad.exe.manifest` 是所有插件共享的文件。

### 4. 打包交付

```powershell
打包.bat
```

产出 `dist\caxa3dmytoolsaddin-<版本>.zip`，只含运行期文件，不含 `.cs/.csproj/.sln/.snk/Lib`。白名单命中源码类扩展名会立即中止。

---

## 六、插件加载机制

.NET Framework 4.8 没有 `AssemblyLoadContext`，采用：

- `Assembly.LoadFrom` + 全局 `AssemblyResolve`，每个插件目录登记为探测根；
- **契约程序集强制返回 Host 已加载的那一份**，否则同一 `IPlugin` 会出现两份类型，`IsAssignableFrom` 为 false，插件被判为「未实现契约」；因此插件目录内不放契约 DLL；
- 插件目录逐级向上查找：`<bin>\caxa3dmytoolsaddin\plugins`、`plugins`、各级祖先的 `src\plugins`，不写死绝对路径；
- 类型等价：Host 与插件各自内嵌 `interop.ICApiIronCAD.ZAddinSite`，是不同的 `Type`，但 `IsEquivalentTo` 为 true，`host.AddinSite as ZAddinSite` 可正常转换；
- 单个插件加载失败只写日志，不影响其他插件与 CAXA 启动。

契约定义（`src/Contracts/IPlugin.cs`）：

```csharp
public interface IPlugin
{
    string Id { get; }
    string DisplayName { get; }
    void Initialize(IPluginHost host);   // 只做资源准备
    void Activate();                     // 负责 Show() + Activate()
    void Shutdown();
}

public interface IPluginHost
{
    object AddinSite { get; }
    string PluginDirectory { get; }
    void Log(string message);
}
```

契约内不出现 `ZAddinSite` 等 CAXA 类型，Host 与插件仅靠此程序集耦合。

---

## 七、新增子插件

1. 在 `plugins/` 下保留独立解决方案，入口改用 `Plugin.cs` 实现 `IPlugin`；
2. 删除原 `Addin.cs` / `Program.cs` 入口（残留 `[ComVisible(true)]`、`[Guid]`、`[ProgId]` 会污染 Host 清单）；
3. 删除 Ribbon / ControlBar 注册、`Icons`、`ConvertImage.cs`、`stdole` 引用；
4. 资源路径改用 `IPluginHost.PluginDirectory` 或 `Assembly.GetExecutingAssembly().Location`，**不用** `AppDomain.BaseDirectory`（= CAXA bin）；
5. 在 `build.ps1` 的 `$plugins` 清单加一项，需要额外资源时补 `Extra` 扩展名；
6. 在 `package.ps1` 白名单补扩展名、在 `deploy.ps1` 的 legacy 清单补旧 DLL / 目录 / CLSID（若从独立 Add-in 迁入）；
7. 主程序无需改动。

---

## 八、部署的四个登记点

缺任何一个都会静默不加载：

1. `caxa3dmytoolsaddin.dll`、`Caxa3dMyTools.Contracts.dll`、插件目录 -> `C:\Program Files\CAXA\CAXA 3D\<年份>\bin\`
2. `IronCad.Addin.config` -> `<AddIn inprocserver="dll名" siteclsid="CLSID" system="true">`
3. `IronCad.exe.manifest` -> `<dependency><dependentAssembly><assemblyIdentity name 版本 publicKeyToken processorArchitecture=amd64>`
4. 注册表 -> `HKCU\SOFTWARE\CAXA\CAXA 3D\<verTag>\Applications\<addin名>`

版本号与 publicKeyToken 均从编译出的 DLL 反射读取，不写死。

---

## 九、开发约定（摘自项目铁律）

| 编号 | 约定 |
| --- | --- |
| 1 | Host 拥有唯一 Ribbon 按钮，子插件不得自行注册 Ribbon/ControlBar |
| 2 | 子插件窗口默认 `TopMost=true`，用户需在 CAXA 内边选零部件边看窗口 |
| 3 | 主窗口与子插件窗口均不显示 CAXA 连接状态 |
| 4 | 子插件实现 `Activate()`，`Initialize` 只做资源准备 |
| 5 | 迁入必须删除原 `Addin.cs` / `Program.cs` |
| 6 | 资源文件用 `PluginDirectory` 或 `Assembly.Location` 解析 |
| 7 | `mt.exe` 清单查询必须用 `local-name()`，否则带 xmlns 时静默返回空 |
| 8 | 部署后必须抽取 Host DLL 的内嵌清单验证 clrClass 数量为 1 |
| 9 | `.ps1` 必须恰好一个 UTF-8 BOM：无 BOM 被 GBK 解码吞引号，多 BOM 使注释块失效 |
| 10 | 卡顿先归因再改代码：日志时间戳 + 线程号，数据必须自洽 |
| 11 | 新增目录库 = 文件 + config + 注册表登记，三者缺一不可 |
| 12 | 接手外部 `.icc` 先解析 `MemberData`，确认程序集与类型可加载 |
| 13 | CAXA COM 对象只能在 UI 主线程访问，严禁 `Task.Run` / `BackgroundWorker`；长任务用限流 `Application.DoEvents()` + `CancellationTokenSource` |
| 14 | 迁入后禁止「顺手优化」原独立 Add-in 的线程模型 |

跨插件约定：自定义属性统一写入共享属性表；CAXA COM 调用一律 STA UI 线程。

---

## 十、常见问题

**功能区没有「我的工具箱」按钮**
按顺序排查：四个登记点是否齐全 -> Host DLL 的内嵌清单是否只有一条 `clrClass` -> 注册表 `Applications` 键是否写入。

**按钮出现但子插件空白**
子插件目录未被扫描到，或插件 DLL 被判为「未实现契约」（插件目录里混入了 `Caxa3dMyTools.Contracts.dll`）。查看 `Diagnostics` 日志。

**CAXA 3D 启动即崩**
多半是 SxS 清单合并失败：子插件内嵌了清单，或 Host 清单里残留 interop 的 `clrClass/clrSurrogate`。用 `mt.exe -inputresource:host.dll;#2 -out:probe.xml` 抽取实际内嵌清单确认。

**安装脚本报语法错误 / 中文乱码**
`.ps1` 的 BOM 数量不为 1。执行 `$b=[IO.File]::ReadAllBytes($p); $n=0; while(($n+2) -lt $b.Length -and $b[$n] -eq 0xEF){$n+=3}; $n/3` 检查，结果必须为 1。

**界面卡死**
CAXA COM 被放到后台线程，或长任务缺少 `Application.DoEvents()` 泵。参考铁律 13。

**后台 IRONCAD 进程抢连接**
CAXA 常驻若干无窗口后台进程，与前台进程同名。连接前清空同名无窗口进程再抓取，见「后台进程清理」插件。

---

## 十一、相关文档

| 文件 | 内容 |
| --- | --- |
| `caxa3dmytoolsaddin-项目总结.md` | 集合体架构、部署与跨插件铁律、里程碑 |
| `caxa3dmytoolsaddin-需求文件.txt` | 原始需求与架构约定 |
| `plugins/<插件名>/<插件名>-项目总结.md` | 各子项目的实现细节、踩坑记录与验证结果 |

---

## 十二、许可

本项目采用 [GNU General Public License v3.0](LICENSE)。
