# caxa3dAttributeManageraddin-已否决路线与探索记录

## 十三、空转兜底清理（2026-10-31）

依据 `caxa3dAttributeManageraddin_trace.log`（13:32:22–13:33:52，60+ 次探测）实测：

- 全程无 `QI(IZBaseApp) E_NOINTERFACE`，IDispatch `ZIronCADApp` 兜底从未触发；
- `GetSelectedElementsZArray` 全部 `hr=0x0`（Count 0/1/17），SAFEARRAY 回退从未进入；
- `GetCustomPropManager(selector=1)` + `IsSharedMgr=-1` 每次成功，旧属性 getter 与 firstFallback 未触发；
- 2024/2027 两套已部署插件目录均自带 `caxa3dManagermodle.xlsx`，`AppDomain.BaseDirectory` 回退无意义且违反 AGENTS 规则 8。

删除内容：

1. `CaxaAutomation.cs`：`GetZAppObject`、`IRawDispatch`、`DispidZIronCADApp`、`HR_NOINTERFACE`；
2. `GetSelectedElementViaSafeArray`、`FnGetVariantResult`、`SafeArrayGetLBound/UBound/Element`、`SlotSelMgrGetSelectedElements`；
3. `GetSharedCustomPropMgr` 的 `firstFallback`：未确认共享的管理器不再返回，取不到直接返回 Zero 由调用方报错；
4. `MainForm.GetDefaultTablePath` 的 `AppDomain.BaseDirectory` 回退：插件目录未知时返回 null，调用方已判空。

保留：规则 14.4 空值 `RemoveByName`、规则 14.5 旧 `get_CustomPropMgr(vbShared=TRUE)` 回退（本次未触发，但跨安装兼容需要）。

验证：`dotnet build -c Release -p:Platform=x64` 0 错误 0 警告；`caxa-slot-check.ps1` 本插件 14 条槽位全部 OK，仓库致命项仍为既有 3 条（`caxa3dcodenameextraction\SheetMetalOps.cs` 死常量），未新增。待真机复测读取/写回/空值写入。


