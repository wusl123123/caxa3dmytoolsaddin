# caxa3dCXDFTViewUpdate-踩坑与教训

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

