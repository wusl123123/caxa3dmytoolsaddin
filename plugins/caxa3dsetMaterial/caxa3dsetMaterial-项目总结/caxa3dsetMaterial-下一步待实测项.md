# caxa3dsetMaterial-下一步待实测项

## 五、下一步实证清单（未验证，勿当结论）

1. 向 `materials\Materials\` 放入自造 XML 后，材料页是否自动出现新库（目录枚举假设）。
2. `SetMaterialName("自定义名", true/false)` 在名称不存在于任何库时的行为（报错？自动建同零件材质？密度取默认？）。
3. `MassDensity` 的单位与写入后 `CalculatedMass`/`GetMassProperties` 的业务侧变化（作为成功判据，不看 API 返回值）。
4. 保存 .ics → 关闭 → 重开后材料名/密度是否持久化（虚拟件同样验证）。
5. 装配体层级注入：`IZAssemblyProperty.SetMaterialName` 与零件级的关系（子件是否继承）。
6. 材料名写入后，工程图 BOM/标题栏"材料"栏是否能读到（用 `IZElement.GetMaterialNameProperty` 交叉验证）。

