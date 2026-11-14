<#
.SYNOPSIS
    打包成“无源码”的交付件：dist\caxa3dmytoolsaddin-<版本>.zip + 同名自包含文件夹。
.DESCRIPTION
    只收集运行期必需文件：
      caxa3dmytoolsaddin.dll            主程序（内嵌 SxS 清单）
      Caxa3dMyTools.Contracts.dll       契约
      caxa3d图纸环境预热.exb             ★ 预热载体：只随 Host 输出走，放交付件根目录
      plugins\<插件名>\<插件名>.dll      各子插件（编译产物，不含源码）
      plugins\<插件名>\<插件自带资源>    .crx/.txt/.ini/.exe/.ps1 等，按扩展名白名单收集
                                        （白名单见 $pluginResExt；新增资源类型必须同步，
                                         否则是静默缺件 —— 见下方“缺件校验”段）
      deploy.ps1 / restore.ps1 / 安装.bat / 卸载.bat / README.txt
    不含任何 .cs / .csproj / .sln / .snk / Lib 等源码或签名私钥。

    ★ 2026-11-13 预热铁律（AGENTS.md 第 5 节）：预热入口与载体只属于集合体主界面，
      子插件一律剥离。因此：
        ① 交付件根目录必须带 caxa3d图纸环境预热.exb（deploy.ps1 从根目录拷到 CAXA bin 根）；
        ② src\plugins 与 stage\plugins 下出现该 .exb 一律判为部署残留，打包直接中止打回；
        ③ 白名单仍保留 .exb 只为兼容其它业务图纸，但预热 .exb 只允许出现在根目录。
.NOTES
    仓库根目录执行：powershell -ExecutionPolicy Bypass -File package.ps1
    只打包，不编译后自动安装；安装由用户自行双击 安装.bat（规则总纲第 31 条）。
#>

param(
    [switch]$NoBuild
)

# ★ 铁律 11：每轮脚本执行都重置两个输出日志，避免跨轮旧数据混进本轮排障。
# 只清本集合体自己的两个日志（子插件的 _crx.log 由各插件加载时自行截断，铁律 10）。
foreach ($suffix in @('_trace.log', '_crx.log')) {
    $logPath = Join-Path $env:TEMP ('caxa3dmytoolsaddin' + $suffix)
    if (Test-Path $logPath) { Remove-Item $logPath -Force -ErrorAction SilentlyContinue }
}

$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$dist  = Join-Path $root 'dist'
$warmExbName = 'caxa3d图纸环境预热.exb'

function Set-SingleBom([string]$path) {
    if (-not (Test-Path $path)) { return }
    $bytes = [IO.File]::ReadAllBytes($path)
    $i = 0
    while (($i + 2) -lt $bytes.Length -and $bytes[$i] -eq 0xEF -and $bytes[$i + 1] -eq 0xBB -and $bytes[$i + 2] -eq 0xBF) { $i += 3 }
    if ($i -eq 3) { return }
    $body = if ($i -gt 0 -and $i -lt $bytes.Length) { [byte[]]$bytes[$i..($bytes.Length - 1)] } elseif ($i -eq 0) { $bytes } else { [byte[]]@() }
    [IO.File]::WriteAllBytes($path, ([byte[]](0xEF, 0xBB, 0xBF) + $body))
}

# 自己也归一：package.ps1 常被编辑器/脚本改写掉 BOM，PS5.1 会按 GBK 解码中文而静默崩溃。
foreach ($s in @((Join-Path $root 'package.ps1'), (Join-Path $root 'build.ps1'),
                 (Join-Path $root 'deploy.ps1'), (Join-Path $root 'restore.ps1'))) { Set-SingleBom $s }

if (-not $NoBuild) {
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -ne 0) { throw "build.ps1 失败" }
}

$hostOut  = Join-Path $root 'src\Host\bin\x64\Release\net48'
$hostDll  = Join-Path $hostOut 'caxa3dmytoolsaddin.dll'
$contract = Join-Path $hostOut 'Caxa3dMyTools.Contracts.dll'
$warmExb  = Join-Path $hostOut $warmExbName
$pluginSrc = Join-Path $root 'src\plugins'

# ---------- 缺件校验 A：主程序三件套（预热载体是硬缺件，不再软跳过） ----------
# 载体缺席时安装脚本会静默降级成“请人工开一张图纸”，属于最难查的功能性缺件；
# 打包阶段就必须拦住（AGENTS 第 5 节：exb 只随 Host 输出走）。
foreach ($need in @($hostDll, $contract, $pluginSrc, $warmExb)) {
    if (-not (Test-Path $need)) { throw "缺少构建产物：$need" }
}
Write-Host "缺件校验通过：主程序 / 契约 / 预热载体 $warmExbName" -ForegroundColor Green

# ---------- 缺件校验 B：plugins\ 下的每个插件工程都必须已编译并暂存到 src\plugins ----------
# build.ps1 的 plugins 清单是手写的：新插件漏登记时既不编译也不暂存，
# package.ps1 只会打包 src\plugins 里已有的东西 —— 交付件静默缺件、全程 0 错误（规则坑 80）。
$pluginRoot = Join-Path $root 'plugins'
$pluginDirs = @(Get-ChildItem $pluginRoot -Directory | Where-Object {
    @(Get-ChildItem $_.FullName -Filter '*.csproj' -File).Count -gt 0 })
$missing = @()
foreach ($dir in $pluginDirs) {
    # Host 按「目录名 = 程序集名」加载，所以暂存目录里的同名 dll 必须存在
    $stagedDll = Join-Path (Join-Path $pluginSrc $dir.Name) ($dir.Name + '.dll')
    if (-not (Test-Path $stagedDll)) { $missing += $dir.Name }
}
if ($missing.Count -gt 0) {
    throw ("打包中止：以下插件工程未进入 src\plugins（先在 build.ps1 的 plugins 清单里登记再重跑 build）：" +
           [Environment]::NewLine + "  " + ($missing -join ([Environment]::NewLine + "  ")))
}
Write-Host ("缺件校验通过：{0} 个插件工程已全部暂存" -f $pluginDirs.Count) -ForegroundColor Green

# ---------- 独立插件（后台进程清理）缺件校验 ----------
# 「2. 独立插件」列 = 启动器 DLL + 进程外工具 EXE；少了 EXE 时界面照样有按钮，
# 但点击必定报错 —— 与其上线后才发现，不如打包时直接中止。
$idcDir = Join-Path $pluginSrc 'caxa3didcskill'
foreach ($pair in @(
        @{ Name = 'caxa3didcskill.dll'; Path = (Join-Path $idcDir 'caxa3didcskill.dll') },
        @{ Name = 'caxa3didcskill.exe'; Path = (Join-Path $idcDir 'caxa3didcskill.exe') })) {
    if (-not (Test-Path $pair.Path)) {
        throw ("打包中止：独立插件缺少 {0}（先编译 tools\caxa3didcskill 并重跑 build.ps1）：{1}" -f $pair.Name, $pair.Path)
    }
}
Write-Host "缺件校验通过：独立插件 caxa3didcskill（启动器 DLL + 工具 EXE）" -ForegroundColor Green

# ---------- 插件自带资源缺件校验（不算扩展名白名单，逐个点名） ----------
# 预热 .exb 已于 2026-11-13 从本表移除：它现在归 Host，放在交付件根目录（见校验 A/C）。
$requiredRes = @(
    @{ Plugin = 'caxa3ddiffrename'; File = 'ExbLinkPatch.exe';          Why = 'exb 外链就地改写工具' },
    @{ Plugin = 'caxa3dsetMaterial';        File = 'caxa3dsetMaterialaddfix.txt'; Why = '材料属性回填规则模板（缺席时 BackfillRule 静默回落到内置 DefaultText，改项目内模板不生效）' },
    # 批量大师：两件缺一都会静默降级（CRX 缺席＝「刷新工程图」永远等不到图纸环境应答；
    # 表格缺席＝首次使用没有默认列）
    @{ Plugin = 'caxa3dBatchMaster';        File = 'caxa3dBatchMasterView.crx';      Why = '图纸环境 CRX 桥（第 5 登记点）' },
    @{ Plugin = 'caxa3dBatchMaster';        File = 'caxa3dBatchMaster属性表格.xlsx';  Why = '默认属性表格（首次使用的列定义）' }
)
foreach ($r in $requiredRes) {
    $path = Join-Path (Join-Path $pluginSrc $r.Plugin) $r.File
    if (-not (Test-Path $path)) {
        throw ("打包中止：{0} 缺少自带资源 {1}（{2}）——检查 build.ps1 里该插件的 Extra 是否含对应扩展名：{3}" -f
               $r.Plugin, $r.File, $r.Why, $path)
    }
}
Write-Host "缺件校验通过：插件自带资源（CRX 桥 / 规则模板 / 默认表格）" -ForegroundColor Green

# ---------- 禁止项校验：子插件暂存目录不得出现预热 .exb ----------
# AGENTS 第 5 节第 3 条 + 发布核对单第 1 条：子插件目录出现该文件即视为部署残留、打回。
# 旧 csproj 的 Link 引用会在插件 bin 留副本，build.ps1 的 Extra '.exb' 曾把它带进 src\plugins；
# 两处都已同步剥离，这里做最后一道闸（源目录 + 交付目录各查一次）。
function Assert-NoPluginWarmExb([string]$scanRoot, [string]$tag) {
    if (-not (Test-Path $scanRoot)) { return }
    $hits = @(Get-ChildItem $scanRoot -Recurse -File -Filter $warmExbName -ErrorAction SilentlyContinue)
    if ($hits.Count -gt 0) {
        $list = ($hits | ForEach-Object { $_.FullName }) -join ([Environment]::NewLine + '  ')
        throw ("打包中止（{0}）：子插件目录出现预热载体 .exb，按 AGENTS 预热铁律判为部署残留：" +
               [Environment]::NewLine + "  " + $list +
               [Environment]::NewLine + "  处理：删除插件 csproj 的 None Include 与 bin/暂存副本，载体只走 Host 输出。")
    }
}
Assert-NoPluginWarmExb $pluginSrc 'src\plugins'
Write-Host "禁止项校验通过：src\plugins 下无预热载体 .exb" -ForegroundColor Green

# 版本与主窗口标题一致：主程序 DLL 写入时间 yyyyMMddHHmm（编译即自动更新）
$version = (Get-Item $hostDll).LastWriteTime.ToString('yyyyMMddHHmm')
$name    = "caxa3dmytoolsaddin-$version"
$stage   = Join-Path $dist $name
$zip     = Join-Path $dist "$name.zip"

# 暂存目录必须每次都是干净的：删不掉时**换一个唯一名字**，绝不在旧目录上追加，
# 否则 Compress-Archive 会把上一轮的残留文件一起打进交付件。
# （受管环境下一次删几十个文件会触发 safe-delete 阈值而失败，属于本机策略，不代表脏数据。）
if (Test-Path $stage) {
    try { Remove-Item $stage -Recurse -Force -ErrorAction Stop }
    catch {
        $stamp = Get-Date -Format 'HHmmss'
        $stage = "$stage-$stamp"
        Write-Host "旧暂存目录无法清理（批量删除受限），本轮改用新目录：$stage" -ForegroundColor Yellow
    }
}
New-Item -ItemType Directory -Path (Join-Path $stage 'plugins') -Force | Out-Null

# ---------- 主程序 + 契约 + 预热载体（不含 pdb） ----------
# 三件同级放交付件根目录：deploy.ps1 从 $hostOutDir / $scriptDir 找这三件并拷到 CAXA bin 根。
Copy-Item $hostDll  $stage -Force
Copy-Item $contract $stage -Force
Copy-Item $warmExb  $stage -Force

# ---------- CAXA 环境配置包（.ucex） ----------
# ★ 用户基础环境配置包：集合体不干预 CAXA 自带功能的安装时机，安装脚本（deploy.ps1）
# 一律不把 .ucex 拷进 CAXA 目录；由用户启动 CAXA 后，通过「工具 → 环境配置 → 导入」自行加载。
# 打包只负责把 .ucex 带到交付件根目录，与 README.txt 同级。
foreach ($f in @('2024环境配置.ucex', '2027环境配置.ucex')) {
    $src = Join-Path $root $f
    if (Test-Path $src) { Copy-Item $src $stage -Force }
    else { Write-Host "[警告] 缺少环境配置包 $f（仓库根目录应存在，用户可后补）" -ForegroundColor Yellow }
}

# ---------- 子插件（每个插件一个子目录，只取运行期文件） ----------
# 按扩展名白名单收集：**插件新增自带资源类型时必须同步这里**（.pdb 除外），
# 漏一个就是静默缺件。.exb 保留只为兼容其它业务图纸，预热 .exb 禁止出现在 plugins 下（见下方校验）。
$pluginResExt = @('.dll', '.xlsx', '.json', '.config', '.crx', '.txt', '.ini', '.exe', '.ps1', '.xml', '.info', '.exb')
foreach ($p in @(Get-ChildItem $pluginSrc -Directory)) {
    $dst = Join-Path (Join-Path $stage 'plugins') $p.Name
    New-Item -ItemType Directory -Path $dst -Force | Out-Null
    Get-ChildItem $p.FullName -File | Where-Object { $pluginResExt -contains $_.Extension.ToLower() } |
        Copy-Item -Destination $dst -Force
}

# ---------- 安装/卸载脚本 ----------
foreach ($f in @('deploy.ps1', 'restore.ps1', '安装.bat', '卸载.bat')) {
    Copy-Item (Join-Path $root $f) $stage -Force
}

# ---------- README（纯文本，UTF-8 BOM，任何编辑器都能正确显示中文） ----------
$readme = @"
caxa3dmytoolsaddin $version
==========================================
CAXA 3D 插件集合体（主程序 + 全部子插件，交付件不含源码）

安装
  1. 完全退出 CAXA 3D（含后台无窗口进程 IRONCAD Design Collaboration Suite）
  2. 双击 安装.bat，按提示同意管理员权限
  3. 启动 CAXA 3D，功能区会出现「我的工具箱」按钮，点击后在弹出窗口中选择各子插件
  说明：编译/打包都不会自动安装，安装一律由你手动执行 安装.bat。

卸载
  双击 卸载.bat。若提示文件被占用，请完全退出 CAXA 3D 后重试。

日志
  %TEMP%\caxa3dmytoolsaddin_trace.log
  每轮打包/安装/卸载脚本执行时自动重置，日志一律 UTF-8 带 BOM。

预热载体（重要）
  caxa3d图纸环境预热.exb 只放在本目录（与 caxa3dmytoolsaddin.dll 同级），
  安装脚本会把它拷到 CAXA bin 根；主界面的「图纸环境预热」从 Contracts.dll 位置逐级向上找它。
  plugins\<插件名>\ 下不允许出现该文件；子插件不再自带预热入口、状态灯或启动自动预热。

CAXA 环境配置包（2024环境配置.ucex / 2027环境配置.ucex）
  这两个文件只随交付件走，安装脚本一律不把它们拷进 CAXA 目录，也不自动导入。
  启用方式：启动 CAXA 3D → 工具 → 环境配置 → 导入 → 选对版本的 .ucex。
  （2024版对应 CAXA 3D 2024，2027版对应 CAXA 3D 2027 及以上；高版本向下兼容 2024 配置包）

说明
  - 本目录所有 .ps1 必须保持单个 UTF-8 BOM（bat 已自动校正）
  - 子插件放在 plugins\<插件名>\ 下，主程序启动时自动扫描加载
  - 「2. 独立插件」列的后台进程清理是进程外工具：plugins\caxa3didcskill\caxa3didcskill.exe，
    点击后以独立进程运行，只清理无可见窗口的 IRONCAD 残留进程（不杀带窗口的前台实例）
"@
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

# ---------- 暂存件端到端校验 ----------
# ① 白名单必须真的把自带资源收进来；② 预热 exb 必须在根目录；
# ③ plugins 下不得有预热 exb（源有、stage 也可能因白名单误收，必须单独反证）。
foreach ($r in $requiredRes) {
    $staged = Join-Path (Join-Path (Join-Path $stage 'plugins') $r.Plugin) $r.File
    if (-not (Test-Path $staged)) {
        throw ("打包中止：{0} 没被收进交付件（扩展名白名单 `$pluginResExt 未包含 .{1}）：{2}" -f
               $r.File, ([IO.Path]::GetExtension($r.File).TrimStart('.')), $staged)
    }
}
$stagedWarm = Join-Path $stage $warmExbName
if (-not (Test-Path $stagedWarm)) { throw "打包中止：交付件根目录缺少 $warmExbName" }
Assert-NoPluginWarmExb (Join-Path $stage 'plugins') 'stage\plugins'
Write-Host "暂存校验通过：自带资源 + 根目录预热载体 + 子插件零残留" -ForegroundColor Green

# ---------- 源码泄漏检查 ----------
$forbidden = @('.cs', '.csproj', '.sln', '.snk', '.resx', '.props', '.targets', '.suo', '.user')
$leaks = @(Get-ChildItem $stage -Recurse -File | Where-Object { $forbidden -contains $_.Extension.ToLower() })
if ($leaks.Count -gt 0) {
    $leaks | ForEach-Object { Write-Host "源码泄漏：$($_.FullName)" -ForegroundColor Red }
    throw "打包中止：交付件中混入了源码/签名文件"
}

# ---------- 压缩（同时保留自包含文件夹，换机可直接双击安装） ----------
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip -CompressionLevel Optimal

Write-Host ""
Write-Host "打包完成（文件夹 + zip 两种形态，均未安装）：" -ForegroundColor Green
Write-Host "  文件夹 : $stage"
Write-Host "  压缩包 : $zip"
Write-Host "交付内容（解压后）："
Write-Host "  caxa3dmytoolsaddin.dll / Caxa3dMyTools.Contracts.dll / $warmExbName"
Write-Host "  2024环境配置.ucex / 2027环境配置.ucex（★ 用户自行加载，安装脚本不处理）"
Write-Host "  plugins\<插件名>\<插件名>.dll  (+ 插件自带资源，不含预热 .exb)"
Write-Host "  plugins\caxa3didcskill\caxa3didcskill.exe  (独立插件列的后台进程清理工具)"
Write-Host "  deploy.ps1 / restore.ps1 / 安装.bat / 卸载.bat / README.txt"
Write-Host ""
Write-Host "下一步：自行运行 安装.bat（编译/打包不会自动安装插件）。" -ForegroundColor Cyan
