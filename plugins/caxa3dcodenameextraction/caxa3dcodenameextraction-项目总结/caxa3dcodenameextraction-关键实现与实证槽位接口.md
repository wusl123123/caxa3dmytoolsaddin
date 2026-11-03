# caxa3dcodenameextraction-关键实现与实证槽位接口

## 3. 关键实现要点
- **连接状态**：活动文档名 + 选中零部件名；`IZPart/IZAssembly::GetExternallyLinkedInfo` 判外部链接，失败显示「(虚拟)」。
- **重命名**：写属性（`IZCustomPropMgr::SetValueByName`）→ `IZElement::set_Name`（vtable 槽 10）→ `IZDoc::Save`（槽 9）→ CAXA 2027 依选项把本地 ics 改名。
- **工程图同步**：`IZBaseApp::OpenFile2(exb)` 打开同名 exb；“是否更新外链文件名”确认框由 PopupAutoYes 代点「是」；随后投 `CMD:CLOSEDOC|<文件名>` 给 CRX，CRX 先 CX_save 再 WM_CLOSE 关图。
- **等待信号**：CRX 日志中的 `REQ.ack | seq=N`（请求已被消费）、`close-done`、`commandEnded | CX_open_Impl`；等待一律 `WaitUntil` + `Application.DoEvents()` 轮询，绝不阻塞 STA 主线程。
- **焦点回归**：`Process.GetCurrentProcess().MainWindowHandle` + SetForegroundWindow（DLL 宿主即 IronCAD.exe）；仅 `IsIconic` 时才 `SW_RESTORE`（见教训14）。
- **重新生成（已于 2026-10-25 移除，勿再加回）**：曾用 `IZPart::Regenerate`（槽 18）/`IZAssembly::RegenerateParts`（槽 17）/`IZSceneDoc::RegenerateParts`（槽 21）刷新设计树名称，但装配体下极慢，用户要求去掉（里程碑16）。现在流程末尾只 `WaitUntil` 等回到设计环境，设计树旧名由用户手工点一次「重新生成」。
- **窗口前置**：主界面「窗口前置」按钮切换 `TopMost`，解决在 3D 环境选零部件时本窗口被压到后面。
- **交互约束**：虚拟零部件禁止重命名（提示先另存为外部 ics）；新零件名自动填入原零件名，同名 ics 已存在时按钮禁用并提示；原/新名输入框显式接管 `Ctrl+C`/`Ctrl+V`（`ProcessCmdKey`）。

