# caxa3didcskill-探针与工具归纳

## 4. 与四个脚本的联动（缺一环就"装了但点不动"）

| 脚本 | 本插件相关的改动 |
| --- | --- |
| `build.ps1` | ①先编译 `tools\caxa3didcskill`（EXE）再编译插件；②`$plugins` 增 `caxa3didcskill`（`Extra=@('.exe')`）；③新增 `ExtraSrc` 机制：把非本工程产出的 EXE 显式补拷进 `src\plugins\caxa3didcskill`，缺件直接 throw |
| `deploy.ps1` | 插件整目录随 `[3/5]` 拷贝；新增 `[3c/5]` 校验启动器 DLL 与 EXE 同时就位（缺一即 `$hadError`），并清掉早期手工散放在 `<CAXA bin>` 根目录的旧 EXE（避免两份 EXE 各自为政）；部署校验行追加 `独立插件=…/…`、`无散放EXE=…` |
| `restore.ps1` | 先 `Stop-Process -Name caxa3didcskill`（EXE 在跑就删不掉；只清理 IRONCAD 后台进程，结束它不影响 CAXA），再删插件目录 + bin 根散放 EXE；残留校验追加「独立插件目录 / bin 根 EXE / 进程仍在」三项 |
| `package.ps1` | 第二道缺件校验：`src\plugins\caxa3didcskill` 必须同时有 `caxa3didcskill.dll` 与 `caxa3didcskill.exe`（否则打包中止——界面有按钮但点击必报错，不能等上线才发现）；`.exe` 已在扩展名白名单内；README 追加独立插件说明 |

