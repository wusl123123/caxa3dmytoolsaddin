# caxa3dsheetmaster-控件树与API记录

## 三、已核对的 API 清单（2027 探针 + interop 反射）

### 会话与场景
| 接口.成员 | 槽位 | 签名 |
| --- | --- | --- |
| `IZBaseApp::get_ActiveDoc` | 11 | `int(IntPtr, out IntPtr)` |
| `IZDoc::Save` | 9 | `int(IntPtr)` |
| `IZDoc::get_Name` | 11 | `int(IntPtr, out IntPtr BSTR)` |
| `IZSceneDoc::RegenerateParts` | 21 | `int(IntPtr, VARIANT_BOOL)` |
| `IZSceneDoc::get_SelectionMgr` | 27 | `int(IntPtr, out IntPtr)` |
| `IZSceneDoc::Update` | 36 | `int(IntPtr)` |
| `IZSceneDoc::GetTopElement` | 58 | `int(IntPtr, out IntPtr)` |
| `IZSceneDoc::UpdateGraphics` / `UpdateBrowser` | 83 / 84 | `int(IntPtr, UInt32)` |
| `IZSelectionMgr::GetSelectedElementsZArray` | 19 | `int(IntPtr, out IntPtr)` |
| `IZSelectionMgr::AddElementToSelection` / `RemoveAllFromSelection` | 20 / 22 | `int(IntPtr, IZElement*, VARIANT_BOOL)` / `int(IntPtr)` |
| `IZSceneDoc::get_ConfigurationMgr` | 40 | `int(IntPtr, out IntPtr)` → QI `IZConfigurationMgr` |
| `IZArray::get_Count` / `Get` | 7 / 9 | `int(IntPtr, out int)` / `int(IntPtr, int, out IntPtr)` |

### 元素与钣金
| 接口.成员 | 槽位 | 说明 |
| --- | --- | --- |
| `IZElement::get_Type` / `get_Id` / `get_Name` | 7 / 8 / 9 | `eZElementType`：SHEETMETAL_PART=20、SHEETMETAL_BEND=22、PUNCHBEND=49、SKETCHBEND=50 |
| `IZElement::GetChildrenCount` / `GetFirstChild` / `GetNextChild` | 15 / 19 / 20 | 设计树遍历三件套 |
| `IZPart::GetExternallyLinkedInfo` / `IZAssembly::GetExternallyLinkedInfo` | 17 / 21 | `(VARIANT_BOOL* linked, BSTR* path)` |
| `IZSheetMetalPart::get_Thickness` / `get/set_KFactor` | 10 / 17 / 18 | 厚度单位米；K 因子无量纲 |
| `IZSheetMetalPart::GetBendLineIDs` | 25 | `int(IntPtr, VARIANT* pVarOut)` —— **返回 VARIANT**，可能是 `uint[]`/`int[]`/单值，需统一转换 |
| `IZSheetMetalPart::GetBendLineInfoByID` | 26 | `int(IntPtr, uint Id, IZMathPoint**, IZMathPoint**, double* radius, double* angle, int* dir)`；半径米、角度弧度、`eZSMBendDirection`：UP=0 / DOWN=1 |
| `IZSheetMetalPart::GetBendLineSource` | 29 | `int(IntPtr, uint Id, uint* pCrvID, IZElement**)` —— 返回折弯来源元素，QI `IZSheetMetalBend` 后可读写系数 |
| `IZSheetMetalBend` 见上表 | 15–47 | 全部 `ezBendAllowanceSrc` / Boolean / Double |
| 探针专用只读 | 15 / 19 / 21 / 23 / 27 | SM：`Thickness2`、`MinBendRadius`、`UseDIN6935`、`ThicknessRefSide`、`CustomUnfoldLengthValue` |
| `IZSheetMetalBend::set_InsideRadius` | 48 | `int(IntPtr, double meters)`；与读取槽 47 配对使用（内半径列可直接编辑） |
| 探针专用 Bend 只读 | 7 / 9 / 21 / 36 / 38 | `FirstBendLength`、`SecondBendLength`、`CustomUnfoldLengthValue`、`UseCustomUnfoldLengthOfPart`、`UpdateBehavior` |
| `IZConfigurationMgr::get_DefaultConfiguration` / `IZConfiguration::get_Name` / `Activate` | 12 / 7 / 15 | `Activate(VARIANT_BOOL bUpdateAllConfigsFirst)`，用于 `Update` 后切回折叠配置 |
| 「使用零件的最小折弯半径 / 使用零件的折弯扣除」 | 类型库与运行期都没有 | 反射 `interop.ICApiIronCAD.dll` 确认 `IZSheetMetalBend` 无此成员；再用 `IDispatch::GetIDsOfNames` 试探 11 个候选名（`UsePartMinBendRadius`/`UsePartBendDeduction`/…）**全部未命中** ⇒ 该勾选不可读写。表格改为纯数据判定：扣除值 == 零件继承值 → 是，否则 → 否；类型不是折弯扣除 → —（不适用） |

IID：`IZSheetMetalPart=05667879-65CD-4417-AFB8-16E09A669D06`、`IZSheetMetalBend=F1100C88-8FBE-4ED2-AD8D-343D9D23B2C9`（其余同 `caxa3dsheetunfoldnameupdate` 已实证表）。

### 槽位调用防护
所有 vtable 直调前用 `VirtualQuery` 校验函数指针落在 `MEM_COMMIT + PAGE_EXECUTE*` 内存，避开短 vtable / 代理接口导致的 AV 崩溃；失败仅记日志并返回失败码（不抛异常打断遍历）。

---

