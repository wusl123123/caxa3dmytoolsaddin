# caxa3dsheetmaster-下一步待实测项

## 七、未验证项（下次实测必查）

1. `set_KFactor` / `set_BendDeductionValue` 是否真正改变展开长度（用展开件长度或展开态几何做权威副作用验证）。
2. 修改后是否需要 `RegenerateParts` 才能刷新几何/展开图（表格回读只证明属性写了，不证明几何更新）。
3. `BendAngle` / `InsideRadius` 的米/弧度换算是否与实际一致（当前仅作展示）。
4. 外链件（装配内真实 `.ics`）修改后，是否需要在链接件文档内保存才能持久化。
5. **逐零件 `IZPart.Update` 重建后 `get_ShowInBrowser` 是否稳定区分内部折弯**（本轮最终改法，待真机确认；若仍为 13 条，下一轮改 Win32 枚举设计树控件强制展开）。
6. `IZElement.GetStateStatus`（槽 17，`Z_HIDDEN=2`）是否可作内部折弯的第二判据（本轮未启用，`LooksCallable` 会拦）。

