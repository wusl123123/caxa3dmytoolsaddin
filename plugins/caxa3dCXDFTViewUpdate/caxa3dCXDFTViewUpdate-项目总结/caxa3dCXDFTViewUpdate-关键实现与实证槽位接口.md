# caxa3dCXDFTViewUpdate-关键实现与实证槽位接口

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

