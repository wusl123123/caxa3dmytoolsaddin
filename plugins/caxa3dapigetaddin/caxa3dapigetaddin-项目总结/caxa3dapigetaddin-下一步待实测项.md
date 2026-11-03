# caxa3dapigetaddin-下一步待实测项

## 七、未验证项（下次实测必查）

1. 真机：主工具箱「3. 测试插件」列出现「操作 API 抓取器」，单击能打开且「窗口前置」可用。
2. 真机：`OperationEvents / HandleEvents / DrawEvents / 智能装配 / 机构` 五类事件能持续产生记录（这是迁入进程内的根本目的）。
3. 真机：点击「注册CRX模块」后 `.crx` 落到 `CAXADraft\Bin`、`CxAutorun.cx` 有登记行，重启 CAXA 后 `%TEMP%\caxa3d_crx_events.log` 有内容。
4. 真机：「卸载CRX模块」后 CAXA 3D 仍能正常启动（历史上卸载流程曾导致无法启动，需重点回归）。
5. `%TEMP%\caxa3dapigetaddin_trace.log` 随 Host 卸载/重装正确轮转，无句柄残留。

