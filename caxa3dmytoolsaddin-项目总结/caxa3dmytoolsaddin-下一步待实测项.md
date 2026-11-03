# caxa3dmytoolsaddin-下一步待实测项

## 九、待验证 / 待办（集合体级）

1. 启动 CAXA 3D 2027，确认功能区只有一个「我的工具箱」按钮，主窗口四列表格列出 12 个插件且均可打开（常用 6 / 独立 1（后台进程清理）/ 系统工具 1（设置备份）/ 测试 4）。其中独立列的入口只在真机上验证过「启动器能拉起 EXE + 不重复启动」，完整链路（CAXA 内点击 → 独立进程弹窗）待在装机器上确认。
2. 装机验证：安装 → 12 个插件 DLL 均被加载（各自 trace 有 `Initialize`）→ 卸载后 config/manifest/注册表/插件目录/CAXADraft\bin 两个 .crx 零残留。
3. `caxa3dCXDFTViewUpdate.crx` 需真机确认：随包落进 `CAXADraft\Bin` 并被 `CxAutorun.cx` 拉起，`CMD:PING` 有 `REQ.ack`，CLOSEDOC 链路能改 .exb mtime（原独立项目自述该链路稳定性待复验）。
4. 操作 API 抓取器需真机确认：`caxa3dapicapture.crx` 能进 `CAXADraft\Bin` 并被 `CxAutorun.cx` 加载，图纸环境事件能写入 `%TEMP%\caxa3d_crx_events.log`。
5. 新增子插件时只改 `build.ps1` 清单、`package.ps1` 白名单、`deploy.ps1` legacy 清单三处，主程序无需改动；插件分类只需实现 `IPlugin.Category`。
6. 各子插件自身的待实测项见对应子项目总结。

