# caxa3dsheetmaster-项目定位与需求

> 原总结标题：caxa3dsheetmaster（CAXA 3D 折弯管理大师）项目总结


> 目标：在 CAXA 3D 设计环境内统一设置钣金 **K 因子** 与 **折弯扣除**，界面参照 `C:\code\csharp-solidworks折弯管理器` 复刻，API 全部换成 CAXA 3D 的 ICAPI。
> 形态：内置 Add-in（免注册 COM clrClass，无 CRX），.NET Framework 4.8 / x64，交付 DLL + 一键安装/卸载脚本。

---

## 一、版本与工程

| 项 | 值 |
| --- | --- |
| 目标框架 | net48 / x64（CAXA 3D 为 64 位进程） |
| 工程 | 必须 `-p:Platform=x64`，输出 `bin\x64\Release\net48` |
| Add-in CLSID | `{7A3F9C21-5B4E-4D86-9E0A-3C1B7D5F2E48}`（Addin.cs / 清单 / deploy.ps1 三处一致） |
| 强名称 | `..\Samples\C#\References\ICApiAddin.snk`，嵌清单后必须 `sn -R` 重签 |
| 日志 | `%TEMP%\caxa3dsheetmaster_trace.log`（每次 CAXA 启动时清空） |

编译后处理（csproj `EmbedSxsManifestAndResign`）：`mt -managedassemblyname` → `strip-manifest.ps1` → `mt -outputresource:...;#2` → `sn -R`。
`strip-manifest.ps1` 用于剥离 mt 为内嵌 interop 生成的 `clrClass`/`clrSurrogate`（多插件共存铁律）；本项目因 `EmbedInteropTypes=True` 未产出 interop 条目，实测 removed=0 —— **脚本保留**，换 interop 引用方式时仍有保护作用。

### 交付物（`bin\x64\Release\net48`）

`caxa3dsheetmaster.dll` / `.pdb` / `caxa3dsheetmaster.dll.manifest` / `安装.bat` / `卸载.bat` / `deploy.ps1` / `restore.ps1` / `strip-manifest.ps1` / `caxa3dsheetmaster使用说明.txt`

---

## 四、功能行为定义

1. **统一修改 → K 因子**：对每个钣金件写零件级 `KFactor`（回读 `get_KFactor` 校验）；折弯逐个设 `BendAllowanceType=Kfactor` + 写 `CustomKFactorValue`。
2. **统一修改 → 折弯扣除**：所有折弯 `BendAllowanceType=BendDeduction` + `BendDeductionValue = 输入mm / 1000`。
3. **单行「应用」→ K 因子**：只对该折弯写 `BendAllowanceType=Kfactor` + `CustomKFactorValue`（不影响同零件其它折弯）。
4. **单行「应用」→ 折弯扣除**：同 2，仅该折弯。
5. **成功判定**：每次 set 后先 `IZPart::Update` 提交，再 get 回读比对（扣除容差 1e-7 m），全部一致才计成功；K 值因 API 不可回读，判定条件为「set 全部 hr=0 且 type==1」，日志明确标注「K 值未回读」。
6. **折弯扣除写不进去的已知原因**：折弯若「使用零件的折弯扣除」=是（扣除值等于零件继承值），`set_BendDeductionValue` 返回 hr=0 但回读不变（实测 6mm 写 5mm 无效）。此时报错文案要求先在 CAXA 折弯对话框取消该勾选；表格「使用零件折弯扣除」列即为该状态的展示（是/否/—）。
7. 每个零件写完后调用 `IZPart::Regenerate`，让展开长度等几何结果刷新。
8. 统一修改完成后自动**重新扫描**整表，表格「当前类型 / 当前数值」显示落盘后的真实值。
9. **表格列序（13 列，必须整体同步）**：0序号 / 1折弯特征名称 / 2系统名称 / 3折弯角度 / 4板材厚度 / 5内半径 / 6使用零件折弯扣除 / 7折弯系数类型 / 8折弯系数数值 / 9新类型 / 10新数值 / 11修改 / 12结果。
10. **「使用零件折弯扣除」列**：只读展示；`是`=扣除值等于零件继承值（CAXA 忽略单独写入），`否`=本折弯独立值可单独改，`—`=系数类型不是折弯扣除（不适用）。
11. 「保存图纸」= `IZDoc.Save`，hr≠0 视为未落盘并报错；「重新生成」= `RegenerateParts(VARIANT_TRUE)`，单次 COM 调用不可中断，执行前弹确认。

---

