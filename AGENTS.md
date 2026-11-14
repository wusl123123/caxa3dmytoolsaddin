# AGENTS.md — caxa3dmytoolsaddin

CAXA 3D 集合体 Add-in（.NET Framework 4.8 / x64 / WinForms / C#）。

本文件只约定**项目合并与发布（同步到远端仓库）时的工作流程**。开发规范、构建命令、API 契约与踩坑记录不在本仓库，见本地规则目录（`C:\code\ICAPI\00-caxa3d-csharp-二次开发规则\`）与各项目本地总结文档。

---

## 1. 仓库与分支

| 远端 | 地址 | 分支 |
| --- | --- | --- |
| `origin`（Gitee） | https://gitee.com/px408231928/caxa3dmytoolsaddin | `master` |
| `github` | https://github.com/wusl123123/caxa3dmytoolsaddin | `main` |

- 日常开发与提交都在 `master`；`main` 只由 `master` 合并而来，合并后两分支文件树必须完全一致。
- 合并固定动作：`git checkout main` → `git merge master` → `git diff --stat master main` 确认无差异。

---

## 2. 推送

**Gitee**（git 协议直推）：

```bash
git push origin master
```

**GitHub**（`github.com` 的 git 协议在本环境不可用，统一走 Git Data API 脚本）：

```powershell
$env:GITHUB_TOKEN = "<fine-grained PAT，需 Contents: Read and write>"
python gh_api_push.py
```

脚本自动完成：读取远端 `main` 当前 SHA 作父提交 → 只为与远端有差异的文件创建 blob（其余按本地 SHA-1 直接引用）→ 从零建全量树 → 建提交 → 更新 `main` 引用。

- 推送后校验：Gitee 用 `git ls-remote origin refs/heads/master`；GitHub 用 API 比对远端树 SHA 与本地 `git rev-parse main^{tree}`。
- 令牌只在当次命令 / 环境变量中使用，不写入 `.git/config` 或任何配置文件；用过的令牌建议在平台上删除重建。
- GitHub 细粒度令牌走 git HTTPS 时用户名必须用**账号名**（`x-access-token` 会 Authentication failed）；本环境直接走 API 脚本，无此问题。

---

## 3. 不入库内容（.gitignore 已固化）

| 规则 | 说明 |
| --- | --- |
| `*项目总结*` | 本项目、各子插件与 tools 下的总结文档**只保留本地**，永不提交/上传。让已有文件退出仓库只能 `git rm --cached` 在原分支提交；**禁止靠跨分支合并落实删除**——会把磁盘上的文档一起删掉 |
| `*.snk` / `*.pfx` | 强名称私钥不入库，克隆后需自备 `Lib\ICApiAddin.snk` 及各插件 `Lib\` 副本（见 README 第四节） |
| `bin/ obj/ dist/ src/plugins/` | 构建产物 |
| `*.crx` | CRX 桥编译产物，仓库只保留 C++ 源码 |
| `.screenshots/` | 本地截图 |

---

## 4. 提交与发布核对单

1. `git status` 中不得出现：`.snk`、`bin/`、`obj/`、`项目总结`、`.crx`、`.screenshots/`。
2. 提交信息用 `feat:` / `fix:` / `chore:` 前缀，正文列要点。
3. 顺序：提交 `master` → 推 Gitee 并校验 → 合并进 `main` → API 推 GitHub 并校验 → 两边文件数一致。

---

## 5. 预热铁律（2026-11-13：图纸环境预热统一上收主界面）

图纸环境预热的**入口与载体只允许存在于集合体主界面（Host）**，子插件一律剥离：

1. **预热入口**：`MainToolboxForm` 顶栏「图纸环境预热」状态灯（绿=已就绪 / 红=未预热）＋ OnShown 后 1 秒自动预热，**每进程只自动跑一次**（static 记账）；已热（窗口判据或本进程预热过）只秒回不重复预热。
2. **唯一实现**：预热引擎在 `src\Contracts\DrawingWarmup.cs`（窗口结构判据 + OpenFile2 唯一直线 + 载体立即不保存关闭）。子插件业务流程确需热环境时只允许调它（UI 线程）——不得再写第二份引擎。
3. **子插件入库禁带**：预热按钮 / 状态灯 / 启动自动预热 / `caxa3d图纸环境预热.exb`（csproj 的 None Include 与 bin/暂存副本一并删除）。发布核对单第 1 条同口径：`git status` 里出现子插件目录的预热 .exb 即打回。
4. **例外（业务安全兜底，不算预热入口）**：子插件流程内部的按需预热调用可保留——caxa3ddiffrename `WarmUpDrawingEnv(closeRightAway)`（坑 148 防崩：流程内留载体至目标 exb 打开）、BatchMaster `ViewBridge.WarmUpDrawingEnv`（后台线程 + 自有 CRX CLOSERAW）。这些调用不得注册成 UI 按钮、不得在 OnShown/构造期自动触发、不得自带 .exb。
5. **载体部署**：exb 只随 Host 输出走（deploy.ps1 白名单拷到 CAXA bin 根），`DrawingWarmup.FindExb` 从 Contracts.dll 位置逐级向上找（安装布局下 bin 根即命中）；子插件目录出现该文件即视为部署残留。
