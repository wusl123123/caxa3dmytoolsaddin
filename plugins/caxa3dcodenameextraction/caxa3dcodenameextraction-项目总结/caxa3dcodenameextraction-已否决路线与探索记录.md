# caxa3dcodenameextraction-已否决路线与探索记录

## 8. 优化方向探索记录（2026-10-25，未实施 / 已中止）

### 8.1 目标
两个静默化诉求：
1. CAXA 3D 内修改零件名**全程静默**（不弹任何框）；
2. 更新工程图参考模型链接**静默替换**（不开图、不代点、不人工更新视图）。

### 8.2 已落地部分
诉求 2 曾用**外部直改 exb** 方案实现（里程碑8/13），但大图纸无法复刻官方保存结果（里程碑17），**已于里程碑18 整体剔除**。当前交付版对诉求 2 采用「打开 exb → 代点 → 保存关图」老流程，链接弹窗由 PopupAutoYes 代点消除人工操作。剩余缺口：图纸打开时 Draft 仍弹「当前有3D视图数据源更新，要更新对应所有视图吗?」，需人工点「是」或依赖 PopupAutoYes 代点。

### 8.3 三条候选路线与结论
| 路线 | 做法 | 结论 |
| --- | --- | --- |
| A. COM 直调 | `OpenFile2(exb)` 后 QI `IZDrawingDoc` → `UpdateAll()`（已反射确认该接口确有 `UpdateAll()`、`GetLinkedScenes/Count/At`） | **用户裁定永不可行，放弃**，不再尝试 |
| B. CRX 桥命令 | 图纸环境内执行「刷新视图」命令后 `CX_save` + `WM_CLOSE`，复用现有 `CX_DFT_ViewSave` 链路 | **可行，留作后备**（本轮未实施） |
| C. 选项级抑制 | 找「自动更新视图、不询问」开关，让弹窗根本不产生 | **实测无效，已中止** |

### 8.4 C 路线取证过程（关键证据，避免重复排查）
1. **弹窗归属**：二进制搜 UTF-16 文本「当前有3D视图数据源更新」命中 `C:\Program Files\CAXA\CAXA 3D\2027\bin\CAXADraft\Bin\zh-CN\ResApp.dll` → 弹窗由 **CAXA Draft** 弹出，非 3D 侧。
2. **3D 选项真实存储**：`HKCU\Software\CAXA\CAXA 3D 29.0\...`（**单层键名**，`CAXA 3D\29.0` 不存在）；`.ucx` 是 ZIP，内含 `IRONCAD_Option_Config.xml`，仅供人工导入参考。候选键：`TDViewOutOfSyncDlg`、`DrawDrawingSceneOutOfSynch`、`ShowLinkNotFoundDialog`。
3. **实测 1（无效）**：`TDViewOutOfSyncDlg` 1→0，重启并打开图纸，弹窗照旧；且该值在开图后被回写为 1 → 非注册表直读。
4. **实测 2（无效）**：Draft 模板 `%LOCALAPPDATA%\CAXA\CAXA CAD CXIC-INT 2027 (x64)\27.0\zh-CN\Config\SysConfig_ID_DFT_TMPL.xml` 中 `DisShow3DDlg` 0→-1（备份后改），重启开图，弹窗照旧。该文件由 `KnlXmlParser.dll` / `DftConfig.dll` 解析。
5. **弹窗特征**：标准 `#32770`，按钮文本 `是(&Y)` / `否(&N)`，静态文本即上述提示 → **与现有 PopupAutoYes 的 `#32770` + 标题规则完全匹配，代点路径无需任何改动**。

### 8.5 环境还原记录
中止后已完整还原：注册表 `TDViewOutOfSyncDlg=1`、`DrawDrawingSceneOutOfSynch=1`、`ShowLinkNotFoundDialog=1`（reg 备份导入）；`SysConfig_ID_DFT_TMPL.xml` 由 `.bak20261025` 还原（`DisShow3DDlg=0`、`UpdateView=-1`）；临时 reg/xml 备份文件全部删除；CAXA 进程已关闭。

### 8.6 若日后重启该方向
- 直接走 **B（CRX）**：在图纸环境内调用刷新视图命令，再接 `CX_save`，属于铁律内方案，风险可控。
- **不要再投入 C**：3D 注册表键不控制 Draft 弹窗；Draft 侧配置键已试过两个均无效，继续试值性价比极低（教训16）。
- 若只是想「用户手动打开图纸时不被打扰」，维持现状即可：PopupAutoYes 已能代点「是」，代价是仍会闪一下弹窗。

