# caxa3dmytoolsaddin-交付状态与验证结果

## 五、部署、登记与打包

### 5.1 四个登记点（缺一即静默不加载）

1. DLL/PDB → `<CAXA>\<年份>\bin\`
2. `IronCad.Addin.config` → `<AddIn inprocserver="dll名" siteclsid="CLSID" system="true">`
3. `IronCad.exe.manifest` → `<dependency><dependentAssembly><assemblyIdentity name=版本=PKT=amd64>`
4. 注册表 → `HKCU\SOFTWARE\CAXA\CAXA 3D\<verTag>\Applications\<addin名>`

- 版本号换算：`年份 - 1998`（2024→26.0，2027→29.0）。
- 版本、publicKeyToken 从编译出的 DLL 反射读取，不写死。
- 无内嵌清单的 DLL 不登记 manifest dependency，并反向清理历史遗留依赖。
- `IronCad.Addin.config` 根节点无命名空间时，`CreateElement(name, "")` 会抛异常导致登记静默失败；按命名空间是否为空选择创建方式。

### 5.2 deploy / restore

- 自动发现所有 CAXA 版本目录（多来源探测，见下两条）。
- 提权前先把脚本复制到 `C:\ProgramData\caxa3dmytoolsaddin\`（真实文件系统），用 `-WorkRoot` 传原业务目录。
- 只对“干净状态”做一次覆盖式 `*.bak` 备份；当前文件已含本程序登记时跳过。
- **restore 永不整份回滚 `.bak`**：config/manifest 是所有插件共享文件，回滚会抹掉其他插件登记。改为手术式移除（`local-name()` XML 精确删除 + 无条件正则兜底），可重复执行。
- 删除操作全部 try/catch；CAXA 锁 DLL 时提示退出后重试，最后统一做残留校验。
- 旧独立插件自动撤销：config 条目、manifest 依赖、同名 DLL、按旧 CLSID 匹配的 Applications 键（含中文显示名键）。
- 独立插件（`caxa3didcskill`）专项：deploy 在 `[3c/5]` 校验 `plugins\caxa3didcskill\` 内启动器 DLL 与 `caxa3didcskill.exe` 都在（缺一即 `$hadError`），并删除 bin 根目录的散放旧 EXE；部署校验行追加 `独立插件=…/…`、`无散放EXE=…`。restore 先 `Stop-Process caxa3didcskill`（EXE 在跑就删不掉，结束它不影响 CAXA），再删插件目录与 bin 根散放 EXE，残留校验追加目录/EXE/进程三项。
- **安装位置以实际探测为准，不假设在 C 盘**（2026-10-31）：`Get-CaxaVersionDirs` 按序探测 ①环境变量 `CAXA3D_ROOT`（手工指定，最高优先级）→ ②注册表 `HKLM/HKCU\SOFTWARE[+WOW6432Node]\CAXA\CAXA 3D\<ver>\InstallDir`（实测值是**版本目录本身**，形如 `<根>\<年份>\`）→ ③卸载表 `Uninstall\*` 中 CAXA 条目的 `InstallLocation` → ④各固定盘 `<盘>:\Program Files\CAXA\CAXA 3D` → ⑤兜底 C 盘；每个候选要么本身是版本目录（名字纯数字且含 `bin`），要么是安装根（含 `<年份>\bin`），否则丢弃。本机取证：`HKLM\SOFTWARE\CAXA\CAXA 3D\26.0\InstallDir = ...\CAXA 3D\2024\`、`29.0 = ...\2027\`，2024 与 2027 **都是真实安装**（各自 `bin\IronCad.exe`、`Config\IronCad.Addin.config`、`bin\IronCad.exe.manifest` 齐全）。
- **陷阱：根去重与版本去重必须是两张表**（2026-10-31 踩过）：`InstallDir` 指向版本目录本身，若把它和"安装根"塞进同一个 `$seen`，该版本目录会先被登记成"已处理的根"，随后扫描真根时判定"已处理"而**整版漏装**——本机现象正是 2024 被丢只留 2027，而当次自检还把它误读成"已卸载残留"，结论是错的。修法：`$seenRoot` / `$seenVer` 分离；候选本身是 `…\<年份>` 且含 `bin` 时直接入列并 `continue`，不当根往下扫；版本列表按目录名排序，输出稳定。
- **陷阱：`<年份>\bin` 不等于「装了 CAXA 3D」**（2026-11-02 第三方机器翻车）：那台机器装了 `CAXA ICS VIEWER`，目录 `C:\Program Files\CAXA\CAXA ICS VIEWER\2027\bin` 与 CAXA 3D 完全同构，旧逻辑只测 `bin` 就把 Viewer 当成本体，先打印「发现 CAXA 3D 安装位置：…\CAXA ICS VIEWER」「发现版本目录：2027」，装到一半报「缺少 …\Config\IronCad.Addin.config」；`ErrorActionPreference=Stop` 下部署校验里的 `Select-String -Path <不存在的路径>` 抛 PathNotFound 把脚本直接打断（用户看到一屏红字 + `ObjectNotFound`）。修法：新增 `Test-CaxaVersionDir`（4 位年份目录 + `bin` + `Config\IronCad.Addin.config` 三条同时成立，`ICS VIEWER` 字样额外黑名单），所有候选与版本目录一律走它，被跳过的候选打黄字留痕；校验段改成只遍历「真正装成功的版本」`$deployed`，`Select-String` 前一律 `Test-Path` 兜底；`Get-CaxaVersionDirs` 空结果显式 `return [object[]]@()`（`@($versions | Sort-Object)` 在空集上会得到 1 个 `$null`，`.Count -eq 0` 判空失效，还会进 foreach 取 `.Name` 报错）。
- BOM 数量自检（必须为 1）：`$b=[IO.File]::ReadAllBytes($p); $n=0; while(($n+2) -lt $b.Length -and $b[$n] -eq 0xEF){$n+=3}; $n/3`。
- 实测（2026-10-22）：部署后 config 20 条（19 原生 + 1 本程序）/ manifest 依赖 24 条；卸载后回到 19/23，注册表、bin、插件目录零残留。

### 5.3 编译后处理

- Host：`mt.exe -managedassemblyname` → `strip-manifest.ps1` 剥离 `interop.*` 的 `clrClass/clrSurrogate` → `mt -outputresource:#2` 嵌入 → `sn -R Lib\ICApiAddin.snk` 重签。
- 子插件：**不做** mt/sn 后处理，DLL 不内嵌任何 SxS 清单。
- 校验：publicKeyToken = `56647e1eee90f6d4`；内嵌清单仅 1 条自身 `clrClass`。

### 5.4 无源码打包

`package.ps1`（或 `打包.bat`）产出 `dist\caxa3dmytoolsaddin-<版本>.zip`，仅含运行期文件：

```
caxa3dmytoolsaddin-<版本>/
├── caxa3dmytoolsaddin.dll
├── Caxa3dMyTools.Contracts.dll
├── plugins/<插件名>/<插件名>.dll（含 xlsx/xml/txt 等运行期资源，不含 pdb）
├── plugins/caxa3didcskill/caxa3didcskill.exe（独立插件列的进程外工具）
├── deploy.ps1 / restore.ps1
├── 安装.bat / 卸载.bat
└── README.txt
```

- 扩展名白名单需随插件新增资源同步扩充（如 `.xml`）；发现 `.cs/.csproj/.sln/.snk/.resx/.props/.targets` 立即中止。
- 缺件校验两道：①`plugins\` 下每个工程目录必须已在 `src\plugins` 有同名 DLL；②`src\plugins\caxa3didcskill` 必须同时有启动器 DLL 与 `caxa3didcskill.exe`（少了 EXE，界面照样有按钮、点击必报错，所以打包阶段直接中止）。
- 新增插件只需编译后放入 `src\plugins\<插件名>\` 再重新打包，无需改主程序。

