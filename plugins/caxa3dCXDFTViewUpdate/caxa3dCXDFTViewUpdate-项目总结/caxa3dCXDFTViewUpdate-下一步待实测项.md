# caxa3dCXDFTViewUpdate-下一步待实测项

## 七、未验证项（下次实测必查）

1. CAXA 3D 2027 真机启动后，Host 反射加载本插件（`%TEMP%\caxa3dCXDFTViewUpdate_trace.log` 有 `Initialize`），点「1. 常用插件」里的行能开窗口。
2. `安装.bat` 后 `<2027 bin>\CAXADraft\bin\caxa3dCXDFTViewUpdate.crx` 就位且 `CxAutorun.cx` 有登记行；CAXA 启动后 CRX 的 `REQ.ack` 能回来。
   本机 `HKCU\SOFTWARE\CAXA\CAXA 3D 29.0\Applications\caxa3dCXDFTViewUpdate` 是**独立版残留**，下次安装由 `deploy.ps1` legacy 清单撤销。
3. 整条 OpenFile2 → 代点 → CLOSEDOC → mtime 变化 链路：原独立项目自述 CLOSEDOC「稳定性待验证」，本次移植未改业务逻辑，**必须在真机重跑一批才能宣称可用**。
4. 「后台化（不抢前台）」在集合体形态下的实际观感；关掉勾选后是否回到改造前行为。
5. 「注册CRX/卸载CRX」按钮在 CAXA 里点击时 UAC 弹窗与脚本窗口交互是否正常（脚本自带 `Read-Host` 收尾）。

