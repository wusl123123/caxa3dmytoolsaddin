# caxa3didcskill-交付状态与验证结果

## 5. 验证（2026-10-31）

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

1. 编译：0 error；暂存目录 `src\plugins\caxa3didcskill\` = `caxa3didcskill.dll` + `caxa3didcskill.exe` + pdb；DLL token `56647e1eee90f6d4`。
2. 假宿主冒烟（临时 net48 控制台直接引用启动器 DLL，`IPluginHost` 用桩实现）：
   - `Id=caxa3didcskill`、`DisplayName=后台进程清理`、`Category=Independent` → 归类列判定为「2. 独立插件」；
   - `Initialize` 打印 `独立工具已就位：…\caxa3didcskill.exe`；
   - 先手动起一个实例再 `Activate()`：进程数 1 → 1（未重复启动，走前台化分支）；
   - 结束全部实例后 `Activate()`：进程数 0 → 1（成功拉起 EXE）；退出码 0。
3. 槽位检查 `caxa-slot-check.ps1`：174 / OK 124 / MISMATCH 3 / NOTFOUND 0 / UNMAPPED 47 —— 与基线一致（本插件不调用任何 ICAPI）。
4. 打包：zip 内含 `plugins\caxa3didcskill\caxa3didcskill.dll` + `caxa3didcskill.exe`；缺件校验两行均通过。
5. 安装/卸载实跑（`CAXA3D_ROOT` 指向临时模拟根，版本目录名取 `3099` → verTag `1101.0`，隔离真实注册表）：安装后 `plugins\caxa3didcskill\` 内 DLL+EXE 就位、bin 根无散放 EXE、`HKCU\...\CAXA 3D 1101.0\Applications\caxa3dmytoolsaddin` 已建；卸载后目录/EXE/注册表/进程零残留，末行「校验通过」。烟测现场（临时目录 + 1101.0 注册表键）已清理。

