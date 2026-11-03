# caxa3dcodenameextraction-交付状态与验证结果

## 6. 交付状态
- 已完成编译并部署到 `C:\Program Files\CAXA\CAXA 3D\2027`（DLL/PDB、规则/说明 txt、CRX 注册脚本；`IronCad.Addin.config`、`IronCad.exe.manifest`、注册表 Applications 键、`CxAutorun.cx` 均登记，仅追加本插件条目）。4 个配置文件与 `caxa3d2027关键设置\` 截图目录**只随编译输出打包，不进 CAXA 安装目录**（里程碑11）。
- 已验证：强名称有效、清单仅含自身 clrClass、程序集在 .NET Framework x64 可加载、CRX 导出 `crxrxEntryPoint`。
- **已实测通过（2026-10-25）**：「图号分离」开关按钮紧邻规则下拉框右侧、默认开启可见；CAXA 主窗口最大化执行流程后不再被还原成普通窗口（焦点回归仅 `IsIconic` 时 `SW_RESTORE`）。
- **2026-10-25 已剔除「exb 直改」功能（里程碑18）**：该开关、`ExbLinkPatcher.cs`、独立诊断工具 `ExbLinkPatch/` 全部删除，改名流程统一走「打开 exb → 代点 → 保存关图」。原因：大图纸多段容器无法靠改链接记录复刻 CAXA 官方保存结果（壳体实测失败，里程碑17）。**当前交付版只有一条图纸更新路径，行为确定、无分支**。
- **已实测通过（2026-10-24）**：真实零部件“我的_100x20_Q2212135A_GB123 → 我的_100x20_Q235A_GB123”，属性回填、本地 ics 改名、exb 弹窗代点「是」、保存关图、焦点回归全部自动完成，**无需人工点击任何弹窗**。

## 6.1 2026-11-03 全量对齐 `caxa3ddiffrename` + 功能区重构（里程碑22）

### 编译与静态校验（已通过）
| 项 | 结果 |
| --- | --- |
| 插件 DLL | `caxa3dcodenameextraction.dll` **251904 B**，`dotnet build -c Release -p:Platform=x64` 0 error / 0 warning |
| CRX | `caxa3dcodenameextraction.crx` **205824 B**，SHA256 `442828DFADCE78C7970E2A4F46F3496E884527DDEB75CCDD300640E2935759CF` |
| CRX 关键串（ASCII 通道） | `closeraw-dispatched` / `closedoc-rejected` / `name-mismatch` / `skip-mainframe` / `skip-not-view` 各 1 次；`fallback-all` **0 次** |
| `caxa-slot-check.ps1` | 全库 186 条：OK 139 / MISMATCH **0** / NOTFOUND 0 / UNMAPPED 47（警告）/ CONFLICT 0 → **PASS**。基线里的 3 条 MISMATCH（`SheetMetalOps.cs` 死常量）随本次同步一起消失 |
| 暂存目录 | `src\plugins\caxa3dcodenameextraction\` 含 dll/pdb/crx/**caxa3d图纸环境预热.exb**/3 个 txt/ini/exe/2 个 ps1（`.exb` 是本轮给 `build.ps1` 的 `Extra` 补的） |

### 尚未实测（必须在真机上点过才算可用）
1. 窗口实际排布：已用离屏宿主 `DrawToBitmap` 截图确认（客户区 1000×620，与参考工程截图逐行一致），**CAXA 内未截图**。
2. 「图纸环境预热」：本机走 `caxa3d图纸环境预热.exb` 单一入口，成功后 `CLOSERAW` 关掉空图纸。
3. 「替换零部件 / 更新工程图链接文件 / 带图独立三步链」：逻辑与 caxa3ddiffrename v20 同源，
   但**本插件的 DLL 与 CRX 组合**是第一次跑，面板代点、`MFCGridCtrlEx` 网格行、`CX_DFT_ChangeLinks` 派发都要复验。
4. 「零部件重新生成」：改名为已落盘后刷新设计树的出口。


## 9. 并入 caxa3dmytoolsaddin 集合体（2026-10-27）

### 9.1 入口改造
- 删除原独立 Add-in 入口 `Addin.cs`（带 `[ComVisible(true)]`、`[Guid]`/`[ProgId]`），新建 `Plugin.cs` 实现 `IPlugin`：
  `Initialize(IPluginHost)` 保存 `AddinSite` 并预建隐藏主窗口；`Activate()` 显示；`Shutdown()` 释放。
- 不再自行注册 Ribbon/ControlBar 按钮，功能区只保留 Host 的「我的工具箱」；本插件在工具箱内以「带图改名及图号分离」按钮出现（集合体铁律 1/4）。
- 主窗口默认 `TopMost=true`；隐藏“连接状态”标签，只保留“当前设计环境.ics---零部件名（外链为虚拟）”格式（集合体铁律 2/3）。
- 程序集级 `ComVisible(false)`；插件 DLL 不内嵌 SxS 清单、不做 COM 注册。

### 9.2 图纸环境 CRX 桥部署
- 源工程：`plugins\caxa3dcodenameextraction\caxa3dcodenameextraction-crx\`（MSBuild/v143 编译）。
- 产物 `caxa3dcodenameextraction.crx` 随插件输出；`deploy.ps1` 拷到 `<CAXA>\bin\CAXADraft\bin\`，并幂等登记 `bin\CAXADraft\Modules\CxAutorun.cx`；`restore.ps1` 反向清理。
- **缺失该 .crx 的典型现象**：插件日志显示“已投递 CMD:CLOSEDOC”，但图纸一直不关、等待超时——请求文件没人消费，等于命令被静默丢弃。

### 9.3 旧独立登记自动撤销
- deploy 的 legacy 清单包含本插件原独立 Add-in 的 CLSID `{3B7D9E21-6C4A-4F2B-B8D5-7A1C9E3F5A62}`；安装时自动移除其 config/manifest/注册表登记，避免与集合体重复加载。

### 9.4 共享属性表复核（2026-10-29）
- `WriteFirstElementCustomProps` 只写共享表，与 `caxa3dAttributeManageraddin` / `caxa3dsetMaterial` 的 `SC图号/SC名称/SC规格/SC材料` 语义一致，未改动。

