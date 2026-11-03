# caxa3dmytoolsaddin-里程碑记录表

## 六、里程碑（集合体级）

| 时间 | 里程碑 |
| --- | --- |
| 2026-10-22 | 集合体首版：Host + 契约 + 2 个子插件；只有主程序带 SxS 清单，插件 DLL 无清单 |
| 2026-10-22 晚 | 部署/卸载脚本重构：单 BOM、干净备份、正则兜底、try/catch、ProgramData 提权；实测部署 20/24、卸载回 19/23、零残留 |
| 2026-10-27 | 统一工具箱：Host 唯一 Ribbon + `MainToolboxForm`；4 个子插件迁入并改 `IPlugin.Activate()` |
| 2026-10-27 | 修 `mt.exe` xmlns 导致 `strip-manifest.ps1` 静默失效（SxS 启动崩溃） |
| 2026-10-28 | 打包脚本累积 6 个 BOM 失效；BOM 归一化范围扩到 `build.ps1` / `package.ps1` |
| 2026-10-29 | 并入材料工具、树形BOM（6 子插件），补 legacy 撤销与 `.xml` 白名单 |
| 2026-10-29 | 跨插件约定：自定义属性统一写共享表；CAXA COM 必须 STA UI 线程 |
| 2026-10-29 | ICM Utils.icc 调查：结论“不可用”；icc 解析工具固化到 `_probe-tools` |
| 2026-10-31 | 主工具箱改三列表格（常用/独立/测试，`IPlugin.Category` 归类）；`caxa3didcskill` 迁出为独立 EXE；并入操作 API 抓取器（第 9 个子插件，含 CRX 桥） |
| 2026-10-31 | 「2. 独立插件」列真正有内容：新增启动器插件 `plugins\caxa3didcskill`（`Category=Independent`，只负责拉起进程外 EXE）；`build/deploy/restore/package` 四个脚本同步管理该独立工具（暂存、落位校验、卸载清理、打包缺件校验） |
| 2026-10-31 | `caxa3dminitool` 界面改为与主工具箱同款四列表格（列=分类、单元格=工具、单击执行、悬停说明）；deploy/restore 安装位置改为多来源实际探测（`CAXA3D_ROOT` / 注册表 InstallDir / 卸载表 / 各固定盘），不再假设 C 盘 |
| 2026-10-31 | 修复探测回归：根去重与版本去重共用 `$seen` 导致本机 2024 被漏装（只剩 2027）。改为 `$seenRoot`/`$seenVer` 两张表 + 版本目录直入列，restore 同步补「发现版本目录」输出；真机实测 2024(26.0)/2027(29.0) 双版本安装与卸载全部通过 |
| 2026-11-02 | 修复第三方机器「装错位置」：`CAXA ICS VIEWER\2027\bin` 被误认成 CAXA 3D。安装位置校验由「有 `<年份>\bin`」改为 `Test-CaxaVersionDir`（年份 + bin + `Config\IronCad.Addin.config`），部署校验只遍历真正装成功的版本、`Select-String` 前加 `Test-Path`，空版本列表返回真空数组；deploy/restore 同步，版本号 2026-11-02.1 || 2026-11-01 | 并入「刷新图纸视图」（第 11 个子插件，`Category=BuiltIn`，落「1. 常用插件」列）；`deploy.ps1` 的 CRX 落位步骤由「只认 caxa3dcodenameextraction.crx」改为 `$crxItems` 列表逐个处理，`build.ps1`/`restore.ps1`/`package.ps1` 同步登记第二个 CRX 插件 |
| 2026-11-02 | 工具箱扩为四列：新增 `3. 系统工具`（`SystemTool`），原 `3. 测试插件` 顺延为 `4. 测试插件`；原独立 Add-in `caxa3dsetbackup` 仅移植内置插件内核并入该列（第 12 个子插件），不移植其 `exe\` 独立 EXE 子工程 |

### 验证结果

- 2026-10-22：Host 强名称有效，内嵌清单含且仅含 `clrClass caxa3dmytoolsaddin.Addin`；两个插件 DLL 无内嵌清单；反射确认均为 `IPlugin` 实现，`ZAddinSite` 类型等价为 true。
- 2026-10-29：6 个插件工程 + Host 全部编译通过（0 错误）；两个新插件 DLL 各恰好暴露 1 个 `IPlugin`；`MainForm(null)` 冒烟构造成功，种子资源播种正常。
- 2026-10-31：`build.ps1` 全量 10 个子插件编译通过（0 错误）；`caxa3dapigetaddin` 暂存目录含 DLL + `caxa3dapicapture.crx`；Host 清单仍恰好 1 条 `clrClass caxa3dmytoolsaddin.Addin`、0 条 `clrSurrogate`，token `56647e1eee90f6d4`；插件 DLL 无内嵌清单；槽位检查 174 / OK 124 / MISMATCH 3 / NOTFOUND 0 / UNMAPPED 47（基线未变）；三列表格离线截图实测无折行、无遮挡。
- 2026-10-31（独立插件列）：启动器 DLL token = `56647e1eee90f6d4`；离线假宿主冒烟：`Id=caxa3didcskill`、`DisplayName=后台进程清理`、`Category=Independent`（落在「2. 独立插件」列），已有实例时 `Activate()` 不重复启动、无实例时能拉起 EXE，退出码 0；打包 zip 内含 `plugins\caxa3didcskill\caxa3didcskill.dll` + `caxa3didcskill.exe`；deploy/restore 在模拟 CAXA 根（`CAXA3D_ROOT` 指向临时目录，verTag 1101.0 隔离）实跑：安装后独立插件目录 DLL+EXE 就位、bin 根无散放 EXE、注册表登记成功，卸载后目录/EXE/注册表/进程零残留，校验通过。四个脚本单 BOM、`Parser::ParseFile` 0 错误。
- 2026-10-31（minitool 表格 + 安装位置探测）：`caxa3dminitool` 0 warning 0 error；离屏截图实测四列表格列头/单元格无折行无遮挡、空单元格浅灰留白、4 列 3 行 10 个工具分布 3+3+2+2。~~安装位置探测在本机只读实跑：正确发现 2027 并过滤已卸载 2024 的残留条目~~ ← **此结论有误，2024 是本机真实安装，被探测逻辑漏掉**，见下条修正。
- 2026-11-01（刷新图纸视图迁入）：`build.ps1` 全量 0 错误，本插件单独编译 0 warning 0 error；插件 DLL `mt.exe` 抽 #2 资源=「找不到指定的资源类型」（无内嵌清单，与既有子插件一致），`sn -vf` 有效、token `56647e1eee90f6d4`；反射冒烟 = 恰好 1 个 `IPlugin`、`Id=caxa3dCXDFTViewUpdate`、`Category=BuiltIn`、文件名==Id；槽位检查 174/OK 124/MISMATCH 3/NOTFOUND 0/UNMAPPED 47（与 10-31 基线逐位一致，本插件全走 RCW 未新增槽位）；假根模拟（3098/3099 → verTag 1100.0/1101.0，预置外来 AddIn/dependency/CxAutorun 条目）安装两次幂等、卸载零残留且外来条目原样保留；证据 `C:\code\ICAPI\_probe-tools\out\caxa3dCXDFTViewUpdate\`。
- 2026-11-02（安装位置误判修复）：模拟根 A（`CAXA ICS VIEWER\2027` 只有 `bin` + 真 3D `3098`/`3099` 齐全）→ 只认 3098、3099；模拟「只有 Viewer」（根指 VIEWER 目录、或直给 `VIEWER\2027`）→ 0 命中，脚本打印「未在本机找到 CAXA 3D 安装目录…」并退出码 1（不再半装半崩）；真机只读探测仍为 `2024, 2027`，并正确跳过 `3D NATIVE TRANSLATOR\2027/2024`（`bin.64` 布局、无 Config）；模拟根端到端 deploy（3098/3099 → 1100.0/1101.0）两版本 `[1/5]`→`[5/5]` 全部走完，校验行 `DLL/config/manifest/registry/独立插件/无散放EXE` 全 True（`CRX=False` 属假根没有 `CAXADraft\bin`），restore 后「校验通过」；HKCU `1100.0`/`1101.0` 与模拟根已清理，真实 `26.0`/`29.0` 登记未被触碰；deploy/restore `Parser::ParseFile` 0 错误、单 BOM。- 2026-10-31（双版本修正）：取证 `HKLM\SOFTWARE\CAXA\CAXA 3D\26.0\InstallDir = ...\CAXA 3D\2024\`、`29.0 = ...\2027\`，两目录 `bin\IronCad.exe`、`Config\IronCad.Addin.config`、`bin\IronCad.exe.manifest` 均齐全，且 2024 的 `Applications\caxa3dmytoolsaddin` 早在旧版脚本中就存在。修复后探测输出 `发现版本目录：2024, 2027`；双版本模拟根（3098/3099→verTag 1100.0/1101.0，隔离真实 26.0/29.0）安装+卸载全部通过、零残留、真实键未被触碰；**真机实跑**：`发现版本目录：2024, 2027`，两版本各自 `DLL/config/manifest/registry/CRX/CxAutorun/独立插件/无散放EXE` 全 True，落位 `caxa3dmytoolsaddin.dll` 25600B + 10 个插件 + `caxa3didcskill.exe`，注册表 `26.0`/`29.0` 双登记；四脚本 `Parser::ParseFile` 0 错误、单 BOM。
- 2026-11-02（系统工具列 + caxa3dsetbackup 迁入）：`build.ps1` 全量 12 个子插件编译通过（0 错误）；`caxa3dsetbackup.dll` 与 Host 的 `sn -vf` 均有效、token 均为 `56647e1eee90f6d4`；Host 内嵌清单仍恰好 1 条 `clrClass caxa3dmytoolsaddin.Addin`；插件 DLL `mt.exe` 抽 #2 资源为空（无内嵌清单）；暂存目录仅 `caxa3dsetbackup.dll + .pdb`，未混入契约 DLL。

