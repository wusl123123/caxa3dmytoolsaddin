# caxa3dCXDFTViewUpdate（刷新图纸视图）项目总结

> 来源：从独立 Add-in `C:\code\ICAPI\caxa3dCXDFTViewUpdate` 迁入集合体，作为第 11 个子插件，
> 归属主工具箱「1. 常用插件」列（`PluginCategory.BuiltIn`）。
> 批处理业务逻辑与界面保持原样，只做「独立 Add-in → 集合体子插件」的形态改造。

## 一、版本与工程

| 项目 | 值 |
| --- | --- |
| 插件 Id | `caxa3dCXDFTViewUpdate`（== 暂存目录名 == 程序集名，PluginManager 硬性要求） |
| 界面名称 | 刷新图纸视图 |
| 分类 | `PluginCategory.BuiltIn`（主工具箱「1. 常用插件」） |
| 入口类 | `Plugin.cs` → `ViewUpdatePlugin : IPlugin`，程序集级 `[assembly: ComVisible(false)]` |
| 工程 | `plugins\caxa3dCXDFTViewUpdate\caxa3dCXDFTViewUpdate.csproj` |
| 目标框架 / 平台 | net48 / x64（`Library`），`UseWindowsForms`，C# 10 |
| 强名称 | `Lib\ICApiAddin.snk`，`sn -vf` 有效，token `56647e1eee90f6d4`（与 Host 及全部兄弟插件同一密钥） |
| 依赖 | `..\..\src\Contracts\Contracts.csproj`、`Lib\interop.ICApiIronCAD.dll`（`EmbedInteropTypes=True`，md5 `29a23396bfbe966ba92cb4598f8c38f3` 与仓库 `Lib\` 同一份） |
| 已删除 | `Addin.cs`（CLSID `{C9F5A7B3-6D4E-4F8A-AB1C-3E4D5F6A7B8C}`、ProgId、Ribbon 注册）、自带 `deploy.ps1`/`restore.ps1`/`strip-manifest.ps1`/`一键编译部署.bat`/`安装.bat`/`卸载.bat`、`poc\`、`项目总结-2026-10-22.md`/`-23.md` 历史文档 |
| 保留模块 | `MainForm.cs`、`CrxBridge.cs`、`PopupAutoYes.cs`、`ForegroundGuard.cs`、`Diagnostics.cs`、`caxa3dCXDFTViewUpdate-crx\`（C++ 源码 + 预编译 `.crx` 127488 B） |
| 运行期资源 | `register-crx.ps1`、`unregister-crx.ps1`、`caxa3dCXDFTViewUpdate.crx`（csproj `CopyToOutputDirectory=PreserveNewest`，`.crx` 用 `Link` 平铺到输出根） |

命名空间本来就是 `caxa3dCXDFTViewUpdate`，与插件 Id 一致，迁入时未改任何命名空间。

## 二、里程碑记录表

| 时间 | 内容 |
| --- | --- |
| 2026-10-22~25 | （原独立项目）POC 17 轮攻关定下 OpenFile2 + CRX 桥链路；SendKeys 方案在主项目失败并回滚，实证「addin 进程内发按键」结构性不可行 |
| 2026-11-01 | 迁入集合体：入口换 `IPlugin`、去 Ribbon/COM/清单；CRX 注册从「在进程内直接写 Program Files」改为调用随包 `register-crx.ps1`（自提权）；`build.ps1` 加第 11 项（`Extra=.crx/.ps1`），`deploy.ps1` 的 CRX 步骤由单插件硬编码改为 `$crxItems` 列表，`restore.ps1` 三处登记（legacy 数组 / `$crxNames` / 残留检查） |
| 2026-11-01 | 离线取证全部通过：插件 0 warning 0 error、无内嵌清单、`sn -vf` 有效、反射冒烟 `Id/DisplayName/Category` 正确、槽位检查与基线一致（174/OK 124/MISMATCH 3）、假根（3098/3099）安装+卸载幂等且零残留。证据归档 `C:\code\ICAPI\_probe-tools\out\caxa3dCXDFTViewUpdate\` |

## 三、CRX 桥契约（与兄弟插件 `caxa3dcodenameextraction` 同构、文件互不共用）

| 方向 | 载体 | 路径 |
| --- | --- | --- |
| C# → CRX | 请求文件（UTF-8 无 BOM，单行 `CMD:XXX#seq=N`） | `%TEMP%\caxa3d_viewupdate_request.txt` |
| CRX → C# | 追加式日志（CRX 端 300ms 定时器 + `CRxEditorReactor` 回调） | `%TEMP%\caxa3d_viewupdate_crx.log` |
| 心跳/就绪 | 标记文件 | `%TEMP%\caxa3d_viewupdate_crx.marker` |

请求必须带 `#seq=N`，C# 端靠 `REQ.ack | seq=N` 判定「这条请求真的被 CRX 取走了」——
CRX 源码注释里留过实证：CLOSEDOC 与兜底 CLOSE 同一毫秒写入时，单槽请求文件被后者覆盖，光看文件内容会误判。

CRX 侧可用命令：`CMD:PING` / `CMD:SAVE` / `CMD:CLOSEDOC|<文件名>` / `CMD:CLOSE` / `CMD:REFRESH_VIEWS` / `CMD:EXEC|<命令串>` / `CMD:DUMPWND`。
C# 侧权威信号：`REQ.ack`、`close-done`、`commandEnded | CX_DFT_ViewClose`、`commandEnded | CX_open_Impl`、`MOD.state | modified=1/0`。

注册方式：`.crx` 拷进 `<CAXA bin>\CAXADraft\bin`，并在 `CAXADraft\Modules\CxAutorun.cx` 里幂等追加
`  <File Template="ID_DFT_TMPL"><绝对路径></File>`。**只按文件名增删自己的行**，共享清单里其它插件条目绝不动。

## 四、批处理流水线（`MainForm.ProcessOneFile`，逐文件）

```
1  CMD:PING                        测 CRX 是否在图纸进程里活着
2  _app.OpenFile2(path,…)          E_FAIL 但实际打开（已知现象，忽略异常）
3  等 REQ.ack（≤120s，超时重发）    CRX 就绪
4  PopupAutoYes 代点「数据源更新→是」CAXA 自己跑视图更新
5  等就绪三信号（≤30s）：文档窗口出现 + commandEnded|CX_open_Impl + ModalCount==0
6  CMD:CLOSEDOC|<文件名>           让 CRX 走原生「保存并关闭」命令链
7  等 close-done / CX_DFT_ViewClose / 窗口消失（≤60s）；超时降级 CMD:CLOSE（≤20s）
8  WaitStampChange 比对文件 mtime（≤1500ms）—— 唯一能证明「.exb 真被写过的」信号
```

超时常量：`OpenReadyTimeoutMs=30000`、`CloseTimeoutMs=60000`、`StampWaitMs=1500`、CRX 就绪 `120000`、兜底关闭 `20000`。
`DocWindowExists` 用 `EnumWindows(主框架) → MDIClient → EnumChildWindows` 找类名含 `AfxFrameOrView` 的子窗口；只枚举顶层窗口曾永远 False。
`ForegroundGuard` 后台化的目标进程是图纸进程 `CDRAFT_M_IC`（不是 IronCAD）。

## 五、界面结构

`选择文件夹 / 开始运行 / 停止 / 清空日志 / 保存日志 / 注册CRX / 卸载CRX` + 复选框「后台化（不抢前台）」（默认勾选）；
ListView 五列 = 序号 / 文件 / 状态 / 耗时 / 信息；底部状态栏。
「选择文件夹」只列清单（含子目录 exb），「开始运行」才动 COM —— 两步分离，批处理期间「停止」置 `_stopRequested`，
**不强存、不关图**，`FormClosing` 必置停止标志并关掉 `PopupAutoYes` / `ForegroundGuard`。
「注册CRX/卸载CRX」不在本进程里写 Program Files，而是 `FindResource` 定位随包脚本后用 `Start-Process powershell -Verb RunAs` 拉起（脚本自身也会再提权一次）。

## 六、踩坑与结论（继承自原项目，均为实证）

1. **SendKeys 在 addin 进程内结构性不可行**：addin 跑在 IronCAD 主线程，`SendWait` = 主线程等自己处理按键 = 死锁；
   改 `keybd_event` 异步注入后，消息被自己的 Form WndProc 拦掉，到不了 CAXA 的 Ctrl+S 处理器。已列为死路，不再尝试。
2. **远程 COM 调 `Save/SaveAs/Close` 全是假成功**：`hr=0` 但只写 `xxx.exb.ics`（不是 .exb）、DocCount 不归 0。
   写入/关闭必须走 CRX 里的原生命令链，判据只看 `close-done` + 文件 mtime 变化。
3. **`OpenFile2` 返回 E_FAIL 却真的打开了**（DocCount+1、CDRAFT 起来）—— 这个异常必须忽略，别当失败重试。
4. **双进程**：C# 侧在 IronCAD.exe，exb 图纸在 CAXADraft / `CDRAFT_M_IC.exe`；「是否保存修改」弹窗弹在图纸进程里，
   所以 `PopupAutoYes` 的白名单必须同时含 `CAXADraft`、`IronCAD`，只扫本进程永远点不到。
5. **CRX RibbonBar/ToolBar 按钮 = CDRAFT 闪退**（死路）；CRX 只能做「隐藏窗口 + 定时器轮询文件桥」。
6. **CoCreateInstance 冷启动 IRONCAD** 完全无窗口、CDRAFT 起不来（死路）。
7. 与兄弟插件共存注意：两个插件各自的 CRX 模块会在同一图纸进程里同时轮询，请求/日志文件是分开的（`caxa3d_viewupdate_*` vs `caxa3dcodenameextraction_*`），互不覆盖；
   但 `PopupAutoYes` 是每插件一个后台线程、都点白名单弹窗，**不要同时跑两个批处理**（会互相抢点）。

## 七、未验证项（下次实测必查）

1. CAXA 3D 2027 真机启动后，Host 反射加载本插件（`%TEMP%\caxa3dCXDFTViewUpdate_trace.log` 有 `Initialize`），点「1. 常用插件」里的行能开窗口。
2. `安装.bat` 后 `<2027 bin>\CAXADraft\bin\caxa3dCXDFTViewUpdate.crx` 就位且 `CxAutorun.cx` 有登记行；CAXA 启动后 CRX 的 `REQ.ack` 能回来。
   本机 `HKCU\SOFTWARE\CAXA\CAXA 3D 29.0\Applications\caxa3dCXDFTViewUpdate` 是**独立版残留**，下次安装由 `deploy.ps1` legacy 清单撤销。
3. 整条 OpenFile2 → 代点 → CLOSEDOC → mtime 变化 链路：原独立项目自述 CLOSEDOC「稳定性待验证」，本次移植未改业务逻辑，**必须在真机重跑一批才能宣称可用**。
4. 「后台化（不抢前台）」在集合体形态下的实际观感；关掉勾选后是否回到改造前行为。
5. 「注册CRX/卸载CRX」按钮在 CAXA 里点击时 UAC 弹窗与脚本窗口交互是否正常（脚本自带 `Read-Host` 收尾）。

## 八、常用命令

```powershell
# 仓库根：全量编译（含本插件）
powershell -ExecutionPolicy Bypass -File C:\code\ICAPI\caxa3dmytoolsaddin\build.ps1

# 只编译本插件
dotnet build C:\code\ICAPI\caxa3dmytoolsaddin\plugins\caxa3dCXDFTViewUpdate\caxa3dCXDFTViewUpdate.csproj -c Release -p:Platform=x64

# CRX 单独注册/卸载（不进 CAXA 也能做）
powershell -ExecutionPolicy Bypass -File <插件目录>\register-crx.ps1
powershell -ExecutionPolicy Bypass -File <插件目录>\unregister-crx.ps1
```

CRX C++ 工程 `caxa3dCXDFTViewUpdate-crx\caxa3dCXDFTViewUpdate.vcxproj` 需 VS/C++ 工具链单独编译；
仓库里带的是预编译产物（127488 B）。改 `.cpp` 后必须重编并把新 `.crx` 放回 `caxa3dCXDFTViewUpdate-crx\bin\`，`build.ps1` 才会带进暂存目录。
