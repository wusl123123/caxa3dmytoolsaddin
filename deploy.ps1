<#
.SYNOPSIS
    caxa3dmytoolsaddin 一键部署（插件集合体主程序 + 全部子插件）。
.DESCRIPTION
    文件 -> 旧插件清理 -> IronCad.Addin.config -> IronCad.exe.manifest -> Applications 注册表。
    设计约束（踩坑后固化）：
      * 本脚本必须保存为“单个 UTF-8 BOM”，否则 Windows PowerShell 5.1 按 GBK 解码，
        中文会吞掉引号/换行并静默崩溃。
      * 只对“干净状态”做一次备份（*.bak，覆盖式），不制造大量时间戳备份。
      * 只有确认 DLL 内嵌了 SxS 清单，才登记 manifest dependency；
        否则登记一个无清单的依赖会让 CAXA 启动失败。
      * 所有删除/拷贝都带 try/catch，单个文件被占用不中断整体流程。
      * 提权时把脚本副本落到 C:\ProgramData（真实文件系统），避免沙箱临时文件
        对提权进程不可见。
#>

param(
    [string]$WorkRoot = '',
    # 无人值守部署：跳过「已全部退出？」这类交互确认（CI / Agent 调用）。
    # CAXA 仍在运行时被占用的文件照样会失败，脚本只做记录，不代用户杀进程。
    [switch]$Yes
)

$ScriptVersion = '2026-11-07.1'
$ErrorActionPreference = 'Stop'

$scriptDir = if ($WorkRoot) { $WorkRoot } else { $PSScriptRoot }
if (-not $scriptDir) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }

# ---------- 自动提权（先落真实路径再提权） ----------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $elevDir = Join-Path $env:ProgramData 'caxa3dmytoolsaddin'
    New-Item -ItemType Directory -Path $elevDir -Force | Out-Null
    $elevScript = Join-Path $elevDir ('deploy-' + $ScriptVersion + '.ps1')
    Copy-Item $PSCommandPath $elevScript -Force

    Write-Host "以管理员权限重新启动：$elevScript" -ForegroundColor Yellow
    Write-Host "业务目录：$scriptDir"
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$elevScript`" -WorkRoot `"$scriptDir`""
    if ($Yes) { $argLine += ' -Yes' }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argLine
    exit
}

Write-Host "================ caxa3dmytoolsaddin 安装 ================"
Write-Host "脚本版本 : $ScriptVersion"
Write-Host "脚本路径 : $PSCommandPath"
Write-Host ""

# ---------- 常量 ----------
$hostName      = 'caxa3dmytoolsaddin'
$clsid         = '{6F1A9C24-3B57-4E8D-9A02-5C7D1E8B4F36}'
$contractsName = 'Caxa3dMyTools.Contracts.dll'

# 「2. 独立插件」列的后台进程清理工具：DLL 是启动器，EXE 是真正干活的进程外工具
$idcPluginName = 'caxa3didcskill'
$idcExeName    = 'caxa3didcskill.exe'

# ================================================================ 定位安装位置
# CAXA 3D 不一定装在 C 盘。探测顺序（全部命中后用 Test-CaxaVersionDir 校验、去重）：
#   0) 环境变量 CAXA3D_ROOT（手工指定安装根或版本目录，最高优先级，命中即不再扫描其余来源）
#   1) 注册表 SOFTWARE\CAXA 下**任意**层的 InstallDir / Install 值（HKLM 与 HKCU，64 位 + 32 位视图都查）
#      本机实测三处都给版本目录，键名形态并不统一：
#        HKLM\SOFTWARE\CAXA\CAXA 3D\<verTag>\InstallDir            = <根>\<年份>\
#        HKCU\SOFTWARE\CAXA\CAXA 3D <verTag>\Directories\Install   = <根>\<年份>\
#        HKLM\SOFTWARE\CAXA\CAXALiveUpdate\{GUID}\InstallDir       = <根>\<年份>\
#      只查 'SOFTWARE\CAXA\CAXA 3D' 一条基线会漏掉后两种形态。
#   2) 卸载表 Uninstall\* 中 DisplayName 含 CAXA 的 InstallLocation
#   3) 各固定盘的 <盘>:\Program Files\CAXA\CAXA 3D、<盘>:\Program Files (x86)\CAXA\CAXA 3D、
#      <盘>:\CAXA\CAXA 3D、<盘>:\CAXA
#   4) 兜底 C:\Program Files\CAXA\CAXA 3D
#   5) 有界浅扫（1~4 全落空才跑）：每个固定盘根往下最多 4 层，只进名字像安装目录的分支
#      （CAXA / CAD / IRONCAD / ICM / InnovationSuite / Program Files / 软件 …），命中 4 位年份目录即校验
#
# 【2026-11-03 第三方机器事故 = 本段重写的真因】CAXA 3D 装在 D 盘的机器报「未在本机找到 CAXA 3D
# 安装目录」，日志里却只有 C 盘的 ICS VIEWER 跳过行。根因：Windows PowerShell 5.1 下
# `Get-PSDrive -PSProvider FileSystem` 的 **DriveType 属性恒为空**（本机 C/D/E/F 四盘实测全空），
# `Where-Object { $_.DriveType -eq 'Fixed' }` 于是把整张表清空 ⇒ 第 3 步「各固定盘」在**任何机器上
# 都从未真正执行过**；本机之所以正常，是注册表/卸载表恰好命中 C 盘。盘枚举改用
# [System.IO.DriveInfo]::GetDrives()（实测 DriveType / IsReady 均正常），Get-PSDrive 只作盘符补充。
#
# 判据（三条同时成立才算真·CAXA 3D）：1) 目录名是 4 位年份；2) 有 bin；3) 有 Config\IronCad.Addin.config
# 第 3 条是 2026-11-02 事故后加的：CAXA ICS VIEWER 同样是 <根>\<年份>\bin 布局但没有 IronCad.Addin.config，
# 只测 bin 会把 Viewer 认成 CAXA 3D，随后校验段 Select-String 对不存在的路径抛 PathNotFound 中断安装。

# 探测台账：每个候选 + 判定结果各记一行；找不到时整表打印，避免「未找到」三个字让人无从下手。
$script:CaxaProbe = New-Object System.Collections.Generic.List[string]
function Add-CaxaProbe([string]$text) { [void]$script:CaxaProbe.Add($text) }
function Write-CaxaProbe {
    Write-Host "---- 安装位置探测台账（$($script:CaxaProbe.Count) 条）----" -ForegroundColor DarkGray
    foreach ($line in $script:CaxaProbe) { Write-Host "  $line" -ForegroundColor DarkGray }
}

function Test-CaxaVersionDir([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    $dir = $dir.TrimEnd('\', '/')
    if ((Split-Path -Leaf $dir) -notmatch '^\d{4}$') { return $false }
    if ($dir -match '(?i)ICS\s*VIEWER') { return $false }        # 旁系产品直接排除
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'bin'))) { return $false }
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'Config\IronCad.Addin.config'))) { return $false }
    return $true
}

function Test-CaxaRoot([string]$root) {
    if ([string]::IsNullOrWhiteSpace($root)) { return $false }
    try {
        foreach ($v in @(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue)) {
            if (Test-CaxaVersionDir $v.FullName) { return $true }
        }
    } catch { }
    return $false
}

# 把 <盘>:\bin 之类的路径归一化；允许用户把 CAXA3D_ROOT 直接指到 bin 或版本目录
function Normalize-CaxaPath([string]$p) {
    if ([string]::IsNullOrWhiteSpace($p)) { return '' }
    $q = $p.Trim().TrimEnd('\', '/')
    if ($q -match '(?i)\\bin$') { $q = Split-Path -Parent $q }
    return $q
}

# 盘符枚举：PS 5.1 的 Get-PSDrive 没有可用的 DriveType，必须以 DriveInfo 为准
function Get-FixedDriveRoots {
    $out = @()
    try {
        foreach ($d in [System.IO.DriveInfo]::GetDrives()) {
            if ($d.DriveType -ne 'Fixed') { continue }
            if (-not $d.IsReady) { Add-CaxaProbe ('固定盘未就绪，跳过：' + $d.Name); continue }
            $out += $d.Name.TrimEnd('\') + '\'
        }
    } catch { Add-CaxaProbe ('DriveInfo 枚举失败：' + $_.Exception.Message) }
    foreach ($p in @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        if (-not $p.Root) { continue }
        if ($out -notcontains $p.Root) { $out += $p.Root }
    }
    return $out
}

# 读一个注册表键里的 InstallDir / Install 值（返回字符串数组，不向外层集合写入）
function Read-CaxaInstallValues($keyObj) {
    $res = @()
    if (-not $keyObj) { return $res }
    $names = @($keyObj.Property)
    foreach ($vn in @('InstallDir', 'Install')) {
        if ($names -notcontains $vn) { continue }
        $v = ''
        try { $v = [string]$keyObj.GetValue($vn) } catch { continue }
        if ([string]::IsNullOrWhiteSpace($v)) { continue }
        if ($v -match '(?i)\\Users\\|AppData|\\Documents\\') { continue }   # 用户目录里的是工作/备份路径
        $res += $v
    }
    return $res
}

# 注册表收割：SOFTWARE\CAXA 下 1~3 层的所有 InstallDir / Install 值
function Get-CaxaRegInstallDirs {
    $out = @()
    foreach ($hive in @('HKLM:\SOFTWARE\CAXA', 'HKLM:\SOFTWARE\WOW6432Node\CAXA',
                        'HKCU:\SOFTWARE\CAXA', 'HKCU:\SOFTWARE\WOW6432Node\CAXA')) {
        if (-not (Test-Path -LiteralPath $hive)) { continue }
        foreach ($k1 in @(Get-ChildItem -LiteralPath $hive -ErrorAction SilentlyContinue)) {
            $out += Read-CaxaInstallValues $k1
            foreach ($k2 in @(Get-ChildItem -LiteralPath $k1.PSPath -ErrorAction SilentlyContinue)) {
                $out += Read-CaxaInstallValues $k2
                foreach ($k3 in @(Get-ChildItem -LiteralPath $k2.PSPath -ErrorAction SilentlyContinue)) {
                    $out += Read-CaxaInstallValues $k3      # CAXA 3D <ver>\Directories\Install 在第 3 层
                }
            }
        }
    }
    return $out
}

function Get-CaxaRoots {
    $roots = @()

    if ($env:CAXA3D_ROOT) {
        $r = Normalize-CaxaPath $env:CAXA3D_ROOT
        Write-Host ("安装位置来源：环境变量 CAXA3D_ROOT = {0}" -f $r) -ForegroundColor Cyan
        Add-CaxaProbe ('CAXA3D_ROOT = ' + $r)
        $roots += $r
        $parent = Split-Path -Parent $r
        if ($parent) { $roots += $parent }
        return $roots
    }

    # 1) 注册表
    foreach ($d in @(Get-CaxaRegInstallDirs)) {
        $dir = Normalize-CaxaPath $d
        if (-not $dir) { continue }
        $roots += $dir                                   # 可能就是版本目录本身
        $parent = Split-Path -Parent $dir
        if ($parent) { $roots += $parent }               # 也可能是安装根下某一层
    }

    # 2) 卸载表 InstallLocation
    $unins = @(Get-ItemProperty -Path @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*') -ErrorAction SilentlyContinue)
    foreach ($u in $unins) {
        if ($u.DisplayName -and ($u.DisplayName -match 'CAXA') -and $u.InstallLocation) {
            $loc = ([string]$u.InstallLocation).TrimEnd('\')
            if ($loc) { $roots += $loc }
        }
    }

    # 3) 各固定盘的常规布局 + 4) 兜底 C 盘
    foreach ($drv in @(Get-FixedDriveRoots)) {
        foreach ($rel in @('Program Files\CAXA\CAXA 3D', 'Program Files (x86)\CAXA\CAXA 3D',
                           'CAXA\CAXA 3D', 'CAXA')) {
            $p = Join-Path $drv $rel
            if (-not (Test-Path -LiteralPath $p)) { Add-CaxaProbe ('盘探测（不存在）：' + $p); continue }
            Add-CaxaProbe ('盘探测：' + $p)
            $roots += $p
        }
    }
    $roots += 'C:\Program Files\CAXA\CAXA 3D'

    return $roots
}

# 5) 有界浅扫：常规探测全落空才跑。只进名字像安装目录的分支，避免整盘递归把安装卡死。
function Find-CaxaScanned {
    $out = @()
    $descend = '(?i)^(CAXA|CAD|IRONCAD|ICM|InnovationSuite|Program Files|软件|安装)'
    $skip    = '(?i)^(System Volume Information|Windows|WinSxS|Users|ProgramData|PerfLogs|Recovery|Temp|node_modules|\$)'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($drv in @(Get-FixedDriveRoots)) {
        # 盘根必须带反斜杠：Get-ChildItem -LiteralPath 'C:' 取的是「C 盘当前目录」而不是根目录
        $level = @($drv)
        Add-CaxaProbe ('浅扫起点：' + $level[0])
        for ($depth = 0; $depth -lt 4 -and $level.Count -gt 0; $depth++) {
            if ($sw.Elapsed.TotalSeconds -gt 90) { Add-CaxaProbe '浅扫超过 90 秒，中止'; return $out }
            $next = @()
            foreach ($dir in $level) {
                $children = @()
                try { $children = @(Get-ChildItem -LiteralPath $dir -Directory -ErrorAction SilentlyContinue) } catch { continue }
                foreach ($c in $children) {
                    $n = $c.Name
                    if ($n -match $skip) { continue }
                    if ($c.Attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }   # 不进链接，防环
                    if ($n -match '^\d{4}$') {
                        if (Test-CaxaVersionDir $c.FullName) {
                            Add-CaxaProbe ('浅扫命中版本目录：' + $c.FullName)
                            $out += $c.FullName
                        }
                        continue
                    }
                    if ($n -match $descend) {
                        if (Test-CaxaRoot $c.FullName) {
                            Add-CaxaProbe ('浅扫命中安装根：' + $c.FullName)
                            $out += $c.FullName
                        }
                        $next += $c.FullName
                    }
                }
            }
            $level = @($next)
        }
    }
    return $out
}

# 把候选收敛成「已验证的版本目录（<根>\<年份>）」列表，去重保序。
# 陷阱：根去重与版本去重必须各自记账。注册表 InstallDir 常直接指向 <根>\<年份>，
# 若两者共用一张表，该版本目录会先被登记成「根」，随后扫描真根时被误判为已处理而漏装该版本。
function Resolve-CaxaCandidate {
    param([string]$r, [hashtable]$seenRoot, [hashtable]$seenVer)
    $local = @()
    if ([string]::IsNullOrWhiteSpace($r)) { return $local }
    $rk = $r.TrimEnd('\').ToLower()
    if ($seenRoot.ContainsKey($rk)) { return $local }
    $seenRoot[$rk] = $true

    # 情形 A：候选本身就是版本目录（…\<年份>）→ 通过 Test-CaxaVersionDir 才入列，不再当根往下扫
    if ((Split-Path -Leaf $r) -match '^\d{4}$') {
        if (Test-CaxaVersionDir $r) {
            if (-not $seenVer.ContainsKey($rk)) {
                $seenVer[$rk] = $true
                Add-CaxaProbe ('收为版本目录：' + $r)
                $local += (Get-Item -LiteralPath $r)
            }
        } elseif (Test-Path -LiteralPath (Join-Path $r 'bin')) {
            Add-CaxaProbe ('跳过非完整 CAXA 3D 版本目录（缺 Config\IronCad.Addin.config）：' + $r)
            Write-Host ("  跳过非完整 CAXA 3D 版本目录：{0}（有 bin 但缺 Config\IronCad.Addin.config，典型如 CAXA ICS VIEWER）" -f $r) -ForegroundColor DarkYellow
        }
        return $local
    }

    # 情形 B：候选是安装根（其下有 <年份>\bin）
    if (-not (Test-CaxaRoot $r)) { return $local }
    foreach ($v in @(Get-ChildItem -LiteralPath $r -Directory -ErrorAction SilentlyContinue)) {
        if ($v.Name -notmatch '^\d{4}$') { continue }
        if (-not (Test-CaxaVersionDir $v.FullName)) {
            if (Test-Path -LiteralPath (Join-Path $v.FullName 'bin')) {
                Add-CaxaProbe ('跳过非 CAXA 3D 目录（无 Config\IronCad.Addin.config）：' + $v.FullName)
                Write-Host ("  跳过非 CAXA 3D 目录：{0}（无 Config\IronCad.Addin.config）" -f $v.FullName) -ForegroundColor DarkYellow
            }
            continue
        }
        $vk = $v.FullName.ToLower()
        if ($seenVer.ContainsKey($vk)) { continue }
        $seenVer[$vk] = $true
        Add-CaxaProbe ('收为版本目录：' + $v.FullName)
        $local += $v
    }
    return $local
}

function Get-CaxaVersionDirs {
    $versions = @()
    $seenRoot = @{}
    $seenVer  = @{}

    foreach ($r in @(Get-CaxaRoots)) {
        $versions += Resolve-CaxaCandidate -r $r -seenRoot $seenRoot -seenVer $seenVer
    }

    # 常规探测零命中 ⇒ 有界浅扫，结果仍走同一套收敛逻辑
    if ($versions.Count -eq 0) {
        Add-CaxaProbe '常规探测零命中，启动有界浅扫'
        foreach ($hit in @(Find-CaxaScanned)) {
            $versions += Resolve-CaxaCandidate -r $hit -seenRoot $seenRoot -seenVer $seenVer
        }
    }

    if ($versions.Count -eq 0) { return [object[]]@() }   # @($null | Sort-Object) 会得到 1 个 $null，必须显式返回空数组
    return ,@($versions | Sort-Object Name)               # 按版本目录名升序，输出稳定
}

# 已并入集合体的旧插件：部署时撤销其独立登记
# 三张表必须等长且顺序对应（下标 i 的 DLL / 名 / CLSID 描述同一个旧插件）。
# 2026-11-07 补入 caxa3dBatchMaster：独立 Add-in 若仍登记就抢在集合体前面被加载，
# LoadFrom 返回的是已加载那份（同为 1.0.0.0 / PKT 56647e1eee90f6d4）⇒ 工具箱静默缺席。
$legacyDlls   = @('caxa3dAttributeManageraddin.dll', 'Caxa3dcudeaddin.dll', 'caxa3dcodenameextraction.dll', 'caxa3dsetMaterial.dll', 'caxa3dTreeBom.dll', 'caxa3dminitool.dll', 'caxa3dsheetmaster.dll', 'caxa3dCXDFTViewUpdate.dll', 'caxa3dBatchMaster.dll')
$legacyNames  = @('caxa3dAttributeManageraddin', 'Caxa3dcudeaddin', 'caxa3dcodenameextraction', 'caxa3dsetMaterial', 'caxa3dTreeBom', 'caxa3dminitool', 'caxa3dsheetmaster', 'caxa3dCXDFTViewUpdate', 'caxa3dBatchMaster')
$legacyClsids = @('{82B0A784-9822-4009-B83B-4AD63D6D2A88}', '{B3246FE6-6C06-446C-8A43-582D4C9690F9}', '{3B7D9E21-6C4A-4F2B-B8D5-7A1C9E3F5A62}', '{C4B2A6E7-9D31-4F58-B0A2-7E5C1D8F3A64}', '{2F6C8B41-9D34-4E52-9A7B-6C1D0E3A58F2}', '{9B5E3D71-4A2C-4F86-8E19-3C7D0B2A6F54}', '{7A3F9C21-5B4E-4D86-9E0A-3C1B7D5F2E48}', '{C9F5A7B3-6D4E-4F8A-AB1C-3E4D5F6A7B8C}', '{1EA4F316-3B4A-40E9-8D9D-F0448CD20EC3}')

# 图纸环境 CRX 桥：插件目录 -> 随包 .crx（[3b/5] 逐个拷进 CAXADraft\bin 并登记 CxAutorun.cx）
$crxItems = @(
    @{ Crx = 'caxa3dcodenameextraction.crx'; Plugin = 'caxa3dcodenameextraction' },
    @{ Crx = 'caxa3dCXDFTViewUpdate.crx';    Plugin = 'caxa3dCXDFTViewUpdate' },
    # 小工具合集「删除所有布局(除模型)」的执行体。
    # ★ 与独立 Add-in 的 MiniToolBridge.crx 是两套东西（临时文件名前缀 caxa3dminitool_sub_ vs
    #   caxa3dminitool_），同机共存不会互相取走对方的请求；两个 crx 各自登记各自的 CxAutorun 行。
    @{ Crx = 'caxa3dminitool.crx';           Plugin = 'caxa3dminitool' },
    # 批量大师「刷新工程图 / 图号分离（图纸链）」的执行体（11-07 随插件迁入集合体）。
    # 临时文件名前缀 caxa3dBatchMaster_（ViewBridge.Prefix），与 codenameextraction / minitool 互不冲突。
    @{ Crx = 'caxa3dBatchMasterView.crx';    Plugin = 'caxa3dBatchMaster' }
)

# ================================================================ 通用工具

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Write-TextWithBom([string]$path, [string]$text) {
    # 显式 UTF-8 BOM：CAXA 的配置文件本身也是带 BOM 的 UTF-8
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($true)))
}

function Set-NotReadOnly([string]$file) {
    try {
        $item = Get-Item $file -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReadOnly) {
            $item.Attributes = $item.Attributes -bxor [IO.FileAttributes]::ReadOnly
        }
    } catch {
        Write-Host "      警告：去除只读失败 $file -> $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function New-XmlElement([System.Xml.XmlDocument]$doc, [string]$name) {
    $uri = $doc.DocumentElement.NamespaceURI
    if ([string]::IsNullOrEmpty($uri)) { return $doc.CreateElement($name) }
    return $doc.CreateElement($name, $uri)
}

# 按 local-name 精确删除 config 中匹配的 AddIn（不依赖命名空间）
function Remove-AddinEntries([string]$configFile, [string[]]$dllNames, [string[]]$clsids) {
    $removed = 0
    try {
        [xml]$doc = Get-Content $configFile -Raw -Encoding UTF8
        foreach ($n in @($doc.SelectNodes("//*[local-name()='AddIn']"))) {
            $inproc = $n.SelectSingleNode("*[local-name()='inprocserver']")
            $site   = $n.SelectSingleNode("*[local-name()='siteclsid']")
            $hit = $false
            if ($inproc -and $dllNames) {
                foreach ($d in $dllNames) {
                    if ([string]::Equals($inproc.InnerText.Trim(), $d, [StringComparison]::OrdinalIgnoreCase)) { $hit = $true }
                }
            }
            if (-not $hit -and $site -and $clsids) {
                foreach ($c in $clsids) {
                    if ([string]::Equals($site.InnerText.Trim(), $c, [StringComparison]::OrdinalIgnoreCase)) { $hit = $true }
                }
            }
            if ($hit) {
                [void]$n.ParentNode.RemoveChild($n)
                $removed++
            }
        }
        if ($removed -gt 0) { $doc.Save($configFile) }
    } catch {
        Write-Host "      警告：XML 解析失败（改用正则兜底）$configFile -> $($_.Exception.Message)" -ForegroundColor Yellow
    }
    return $removed
}

# 正则兜底：XML 结构被破坏时也能把目标块整段删掉
function Remove-ConfigBlocksByRegex([string]$configFile, [string[]]$needles) {
    $text = [IO.File]::ReadAllText($configFile)
    $rx = [regex]'(?is)<AddIn\b[^>]*>.*?</AddIn>'
    $sb = New-Object System.Text.StringBuilder
    $pos = 0
    $removed = 0
    foreach ($m in $rx.Matches($text)) {
        [void]$sb.Append($text.Substring($pos, $m.Index - $pos))
        $keep = $true
        foreach ($needle in $needles) {
            if ($m.Value -like "*$needle*") { $keep = $false; break }
        }
        if ($keep) { [void]$sb.Append($m.Value) } else { $removed++ }
        $pos = $m.Index + $m.Length
    }
    [void]$sb.Append($text.Substring($pos))
    if ($removed -gt 0) { Write-TextWithBom $configFile $sb.ToString() }
    return $removed
}

function Remove-ManifestDependencies([string]$manifestFile, [string[]]$asmNames) {
    $removed = 0
    try {
        [xml]$doc = Get-Content $manifestFile -Raw -Encoding UTF8
        foreach ($n in @($doc.SelectNodes("//*[local-name()='dependency']"))) {
            $id = $n.SelectSingleNode(".//*[local-name()='assemblyIdentity']")
            if (-not $id) { continue }
            foreach ($a in $asmNames) {
                if ([string]::Equals($id.GetAttribute('name'), $a, [StringComparison]::OrdinalIgnoreCase)) {
                    [void]$n.ParentNode.RemoveChild($n)
                    $removed++
                    break
                }
            }
        }
        if ($removed -gt 0) { $doc.Save($manifestFile) }
    } catch {
        Write-Host "      警告：manifest 解析失败（改用正则兜底）-> $($_.Exception.Message)" -ForegroundColor Yellow
    }
    return $removed
}

function Remove-ManifestBlocksByRegex([string]$manifestFile, [string[]]$needles) {
    $text = [IO.File]::ReadAllText($manifestFile)
    $rx = [regex]'(?is)<dependency\b[^>]*>.*?</dependency>'
    $sb = New-Object System.Text.StringBuilder
    $pos = 0
    $removed = 0
    foreach ($m in $rx.Matches($text)) {
        [void]$sb.Append($text.Substring($pos, $m.Index - $pos))
        $keep = $true
        foreach ($needle in $needles) {
            if ($m.Value -like "*$needle*") { $keep = $false; break }
        }
        if ($keep) { [void]$sb.Append($m.Value) } else { $removed++ }
        $pos = $m.Index + $m.Length
    }
    [void]$sb.Append($text.Substring($pos))
    if ($removed -gt 0) { Write-TextWithBom $manifestFile $sb.ToString() }
    return $removed
}

# 只有内嵌清单的 DLL 才能登记 dependency
function Test-EmbeddedManifest([string]$dllPath) {
    $mt = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.19041.0\x64\mt.exe'
    if (Test-Path $mt) {
        $probe = Join-Path $env:TEMP ("host_manifest_probe_{0}.xml" -f [Guid]::NewGuid().ToString('N'))
        try {
            & $mt "-inputresource:$dllPath;#2" "-out:$probe" -nologo 2>$null | Out-Null
            if ((Test-Path $probe) -and (Get-Item $probe).Length -gt 0) { return $true }
        } catch {
        } finally {
            if (Test-Path $probe) { Remove-Item $probe -Force -ErrorAction SilentlyContinue }
        }
    }
    # 兜底：直接扫描 PE 内是否有 clrClass 文本
    try {
        $text = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($dllPath))
        return $text.Contains('clrClass')
    } catch {
        return $false
    }
}

function Add-AddinEntry([string]$configFile, [string]$dllName, [string]$displayName, [string]$description) {
    [xml]$doc = Get-Content $configFile -Raw -Encoding UTF8

    $addin = New-XmlElement $doc 'AddIn'
    foreach ($key in @('identify', 'inprocserver', 'siteclsid', 'modulever', 'platformver', 'autoload', 'system')) {
        $e = New-XmlElement $doc $key
        switch ($key) {
            'inprocserver' { $e.InnerText = $dllName }
            'siteclsid'    { $e.InnerText = $clsid }
            'system'       { $e.InnerText = 'true' }
            default        { $e.InnerText = '' }
        }
        [void]$addin.AppendChild($e)
    }

    $display = New-XmlElement $doc 'displayname'
    $d1 = New-XmlElement $doc 'default'; $d1.InnerText = $hostName; [void]$display.AppendChild($d1)
    $d2 = New-XmlElement $doc 'zh-cn';   $d2.InnerText = $displayName; [void]$display.AppendChild($d2)
    [void]$addin.AppendChild($display)

    $desc = New-XmlElement $doc 'description'
    $x1 = New-XmlElement $doc 'default'; $x1.InnerText = $description; [void]$desc.AppendChild($x1)
    $x2 = New-XmlElement $doc 'zh-cn';   $x2.InnerText = $description; [void]$desc.AppendChild($x2)
    [void]$addin.AppendChild($desc)

    $addIns = $doc.SelectSingleNode("//*[local-name()='AddIns']")
    if (-not $addIns) { $addIns = $doc.DocumentElement }
    [void]$addIns.AppendChild($addin)
    $doc.Save($configFile)
}

function Add-ManifestDependency([string]$manifestFile) {
    [xml]$doc = Get-Content $manifestFile -Raw -Encoding UTF8
    $dep = New-XmlElement $doc 'dependency'
    $da  = New-XmlElement $doc 'dependentAssembly'
    $ai  = New-XmlElement $doc 'assemblyIdentity'
    $ai.SetAttribute('name', $hostName)
    $ai.SetAttribute('version', $version)
    $ai.SetAttribute('publicKeyToken', $publicToken)
    $ai.SetAttribute('processorArchitecture', 'amd64')
    [void]$da.AppendChild($ai)
    [void]$dep.AppendChild($da)
    [void]$doc.DocumentElement.AppendChild($dep)
    $doc.Save($manifestFile)
}

function Remove-RegistryEntries([string]$verTag, [string[]]$names, [string[]]$clsids) {
    $bases = @("HKCU:\SOFTWARE\CAXA\CAXA 3D $verTag\Applications",
               "HKCU:\SOFTWARE\IronCAD\IRONCAD $verTag\Applications")
    foreach ($base in $bases) {
        if (-not (Test-Path $base)) { continue }
        foreach ($k in @(Get-ChildItem $base -ErrorAction SilentlyContinue)) {
            $default = $null
            try { $default = (Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).'(default)' } catch { }
            $hit = $false
            if ($names -contains $k.PSChildName) { $hit = $true }
            if (-not $hit -and $default -and $clsids) {
                foreach ($c in $clsids) {
                    if ([string]::Equals([string]$default, $c, [StringComparison]::OrdinalIgnoreCase)) { $hit = $true }
                }
            }
            if ($hit) {
                try {
                    Remove-Item $k.PSPath -Recurse -Force -ErrorAction Stop
                    Write-Host "      已删除注册表项：$($k.PSChildName)"
                } catch {
                    Write-Host "      警告：注册表删除失败 $($k.PSChildName) -> $($_.Exception.Message)" -ForegroundColor Yellow
                }
            }
        }
    }
}

function Add-RegistryEntry([string]$verTag) {
    $path = "HKCU:\SOFTWARE\CAXA\CAXA 3D $verTag\Applications\$hostName"
    New-Item -Path $path -Force | Out-Null
    Set-ItemProperty -Path $path -Name '(default)'    -Value $clsid
    Set-ItemProperty -Path $path -Name 'LoadOnStartup' -Value 1 -Type DWord
    Set-ItemProperty -Path $path -Name 'ShowInList'    -Value 1 -Type DWord
}

# ================================================================ 定位输入

function Find-HostDll {
    $candidates = @(
        $scriptDir,
        (Join-Path $scriptDir 'src\Host\bin\x64\Release\net48'),
        (Join-Path $scriptDir 'bin\x64\Release\net48')
    )
    $d = [IO.DirectoryInfo]$scriptDir
    for ($i = 0; $i -lt 6 -and $d; $i++) {
        $candidates += (Join-Path $d.FullName 'src\Host\bin\x64\Release\net48')
        $d = $d.Parent
    }
    foreach ($c in $candidates) {
        $p = Join-Path $c "$hostName.dll"
        if (Test-Path $p) { return $p }
    }
    return $null
}

function Find-PluginStage {
    $candidates = @(
        (Join-Path $scriptDir 'src\plugins'),
        (Join-Path $scriptDir 'plugins')
    )
    $d = [IO.DirectoryInfo]$scriptDir
    for ($i = 0; $i -lt 6 -and $d; $i++) {
        $candidates += (Join-Path $d.FullName 'src\plugins')
        $d = $d.Parent
    }
    foreach ($c in $candidates) {
        if (-not (Test-Path $c)) { continue }
        $subs = @(Get-ChildItem $c -Directory -ErrorAction SilentlyContinue |
                  Where-Object { @(Get-ChildItem $_.FullName -Filter *.dll -File -ErrorAction SilentlyContinue).Count -gt 0 })
        if ($subs.Count -gt 0) { return $c }
    }
    return $null
}

$dllPath = Find-HostDll
if (-not $dllPath) {
    Write-Host "[错误] 找不到 $hostName.dll（请先执行 build.ps1 或解压完整安装包）" -ForegroundColor Red
    Read-Host '按回车退出'
    exit 1
}
$hostOutDir = Split-Path -Parent $dllPath
$pdbPath    = [IO.Path]::ChangeExtension($dllPath, '.pdb')

$stageDir = Find-PluginStage
if (-not $stageDir) {
    Write-Host "[错误] 找不到插件汇总目录（src\plugins 或 plugins）" -ForegroundColor Red
    Read-Host '按回车退出'
    exit 1
}

$asmName      = [Reflection.AssemblyName]::GetAssemblyName($dllPath)
$version      = $asmName.Version.ToString()
$publicToken  = (($asmName.GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') }) -join '')
$hasManifest  = Test-EmbeddedManifest $dllPath

Write-Host "主程序  : $dllPath"
Write-Host "插件源  : $stageDir"
Write-Host "版本    : $version  (publicKeyToken=$publicToken)"
Write-Host "内嵌清单: $hasManifest"
Write-Host ""

if ($publicToken -eq '') {
    Write-Host "[错误] 主程序未强签名，CAXA 无法加载。" -ForegroundColor Red
    Read-Host '按回车退出'
    exit 1
}
if (-not $hasManifest) {
    Write-Host "[警告] 主程序没有内嵌 SxS 清单，将跳过 IronCad.exe.manifest 登记。" -ForegroundColor Yellow
    Write-Host "       此时 CAXA 不会加载本 Add-in，请用 Release 配置重新编译。" -ForegroundColor Yellow
}

# ---------- CAXA 进程检查 ----------
$running = Get-Process -Name 'IRONCAD', 'IronCad', 'CDRAFT_M_IC' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[警告] 检测到 CAXA 相关进程（含后台无窗口进程），文件可能被占用。" -ForegroundColor Yellow
    if ($Yes) {
        Write-Host "       -Yes 已指定：跳过交互确认继续部署（被占用的文件会失败并计入校验结果）。" -ForegroundColor Yellow
    }
    else {
        $ans = Read-Host '已全部退出？继续部署请输入 y'
        if ($ans -ne 'y') { exit 1 }
    }
}

# ---------- 发现已安装版本（实际安装位置，不假设在 C 盘） ----------
$versions = Get-CaxaVersionDirs
if ($versions.Count -eq 0) {
    Write-Host "[错误] 未在本机找到 CAXA 3D 安装目录。已依次探测：" -ForegroundColor Red
    Write-Host "       1) 注册表 SOFTWARE\CAXA 下任意层的 InstallDir / Install（HKLM+HKCU，64 位与 32 位视图）" -ForegroundColor Red
    Write-Host "       2) 卸载表 DisplayName 含 CAXA 的 InstallLocation" -ForegroundColor Red
    Write-Host "       3) 每个固定盘的 \Program Files\CAXA\CAXA 3D、\Program Files (x86)\CAXA\CAXA 3D、\CAXA\CAXA 3D、\CAXA" -ForegroundColor Red
    Write-Host "       4) 有界浅扫（盘根往下 4 层，只进名字像安装目录的分支）" -ForegroundColor Red
    Write-Host "       请设置环境变量 CAXA3D_ROOT 指向安装根目录或版本目录（指到 bin 也行）后重跑，例如：" -ForegroundColor Yellow
    Write-Host "         set CAXA3D_ROOT=D:\Program Files\CAXA\CAXA 3D   或   D:\Program Files\CAXA\CAXA 3D\2026" -ForegroundColor Yellow
    Write-Host "       提示：CAXA ICS VIEWER 等旁系产品（无 Config\IronCad.Addin.config）会被自动排除，" -ForegroundColor Yellow
    Write-Host "             如本机确实只装了 Viewer，本插件无处可装，属正常提示。" -ForegroundColor Yellow
    Write-CaxaProbe
    Read-Host '按回车退出'
    exit 1
}
$foundRoots = @($versions | ForEach-Object { Split-Path -Parent $_.FullName } | Select-Object -Unique)
Write-Host ("发现 CAXA 3D 安装位置：{0}" -f ($foundRoots -join '  ')) -ForegroundColor Cyan
Write-Host ("发现版本目录：{0}" -f (($versions | ForEach-Object { $_.Name }) -join ', '))
Write-Host ""

$hadError = $false
$deployed = @()          # 真正装成功的版本；校验只遍历它，避免对残缺目录做 Select-String

foreach ($v in $versions) {
    $year         = $v.Name
    $caxaBin      = Join-Path $v.FullName 'bin'
    $configFile   = Join-Path $v.FullName 'Config\IronCad.Addin.config'
    $manifestFile = Join-Path $caxaBin 'IronCad.exe.manifest'
    $verTag       = '{0}.0' -f ([int]$year - 1998)

    Write-Host "================ CAXA 3D $year（注册版本 $verTag） ================"

    if (-not (Test-Path $configFile))   { Write-Host "[错误] 缺少 $configFile" -ForegroundColor Red; $hadError = $true; continue }
    if (-not (Test-Path $manifestFile)) { Write-Host "[错误] 缺少 $manifestFile" -ForegroundColor Red; $hadError = $true; continue }

    Set-NotReadOnly $configFile
    Set-NotReadOnly $manifestFile

    # ---------- 1. 只对干净状态做一次备份（覆盖式） ----------
    $configText = [IO.File]::ReadAllText($configFile)
    $manifestText = [IO.File]::ReadAllText($manifestFile)
    if ($configText -notlike "*$hostName*" -and $manifestText -notlike "*$hostName*") {
        try {
            Copy-Item $configFile   "$configFile.bak"   -Force
            Copy-Item $manifestFile "$manifestFile.bak" -Force
            Write-Host "[1/5] 已备份干净配置：*.bak"
        } catch {
            Write-Host "[1/5] 备份失败：$($_.Exception.Message)" -ForegroundColor Yellow
        }
    } else {
        Write-Host "[1/5] 当前已含本程序登记，跳过备份（避免脏备份）"
    }

    # ---------- 2. 主程序与契约 ----------
    $copyOk = $true
    foreach ($item in @(
            @{ Src = $dllPath; Dst = (Join-Path $caxaBin "$hostName.dll") },
            @{ Src = (Join-Path $hostOutDir $contractsName); Dst = (Join-Path $caxaBin $contractsName) })) {
        try {
            if (-not (Test-Path $item.Src)) { throw "源文件不存在：$($item.Src)" }
            Copy-Item $item.Src $item.Dst -Force -ErrorAction Stop
        } catch {
            Write-Host "[2/5] 拷贝失败 $($item.Dst) -> $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "      请先完全退出 CAXA（含后台无窗口进程）后重试。" -ForegroundColor Yellow
            $copyOk = $false
        }
    }
    if ($pdbPath -and (Test-Path $pdbPath)) {
        try { Copy-Item $pdbPath (Join-Path $caxaBin "$hostName.pdb") -Force -ErrorAction Stop } catch { }
    }
    if (-not $copyOk) { $hadError = $true; continue }
    Write-Host "[2/5] 主程序与契约已拷贝到 $caxaBin"

    # ---------- 3. 子插件目录 ----------
    $pluginDst = Join-Path (Join-Path $caxaBin $hostName) 'plugins'
    try {
        if (Test-Path $pluginDst) { Remove-Item $pluginDst -Recurse -Force -ErrorAction Stop }
    } catch {
        Write-Host "[3/5] 警告：旧插件目录清理失败（可能被占用）：$($_.Exception.Message)" -ForegroundColor Yellow
    }
    New-Item -ItemType Directory -Path $pluginDst -Force | Out-Null
    foreach ($sub in @(Get-ChildItem $stageDir -Directory)) {
        try {
            Copy-Item $sub.FullName (Join-Path $pluginDst $sub.Name) -Recurse -Force -ErrorAction Stop
        } catch {
            Write-Host "[3/5] 插件拷贝失败 $($sub.Name) -> $($_.Exception.Message)" -ForegroundColor Red
            $hadError = $true
        }
    }
    Write-Host "[3/5] 子插件已拷贝到 $pluginDst"

    # ---------- 3b. 图纸环境 CRX 桥（逐个插件各自的 .crx 就位并登记） ----------
    # 图纸环境的“保存 + 关闭 exb”只能由 CRX 内部的原生命令链完成；
    # 缺对应 .crx 时插件投递的 CLOSEDOC 没人执行，现象就是关图卡住/超时。
    $draftDir   = Join-Path $caxaBin 'CAXADraft'
    $crxBinDir  = Join-Path $draftDir 'bin'
    $crxAutorun = Join-Path $draftDir 'Modules\CxAutorun.cx'
    foreach ($crxItem in $crxItems) {
        $crxName = $crxItem.Crx
        $crxSrc  = Join-Path (Join-Path $pluginDst $crxItem.Plugin) $crxName
        if (-not (Test-Path $crxSrc)) {
            Write-Host "[3b/5] 警告：交付件缺少 $crxName（该插件的图纸保存/关闭 exb 将不可用）" -ForegroundColor Yellow
            continue
        }
        $crxDst = Join-Path $crxBinDir $crxName
        try {
            if (-not (Test-Path $crxBinDir)) { throw "缺少目录 $crxBinDir" }
            Copy-Item $crxSrc $crxDst -Force -ErrorAction Stop
            Write-Host "[3b/5] CRX 桥已就位：$crxDst"
        } catch {
            Write-Host "[3b/5] CRX 拷贝失败（CAXA 正在运行？）：$($_.Exception.Message)" -ForegroundColor Red
            $hadError = $true
        }
        try {
            if (Test-Path $crxAutorun) {
                $cx = [IO.File]::ReadAllText($crxAutorun)
                if ($cx -notlike "*$crxDst*") {
                    $line = '  <File Template="ID_DFT_TMPL">' + $crxDst + '</File>'
                    $cx = $cx.Replace('</Modules>', $line + [Environment]::NewLine + '</Modules>')
                    Write-TextWithBom $crxAutorun $cx
                    Write-Host "[3b/5] 已登记 Modules\CxAutorun.cx"
                } else {
                    Write-Host "[3b/5] CxAutorun.cx 已存在登记项（跳过）"
                }
            } else {
                New-Item -ItemType Directory -Path (Split-Path -Parent $crxAutorun) -Force | Out-Null
                $cx = @('<?xml version="1.0" encoding="utf-8"?>', '<Modules>',
                        ('  <File Template="ID_DFT_TMPL">' + $crxDst + '</File>'), '</Modules>', '') -join [Environment]::NewLine
                Write-TextWithBom $crxAutorun $cx
                Write-Host "[3b/5] 已新建 CxAutorun.cx 并登记"
            }
        } catch {
            Write-Host "[3b/5] CxAutorun.cx 登记失败：$($_.Exception.Message)" -ForegroundColor Red
            $hadError = $true
        }
    }

    # ---------- 3c. 「2. 独立插件」列的后台进程清理工具 ----------
    # 启动器 DLL 和工具 EXE 都放在 plugins\caxa3didcskill\ 里，[3/5] 已整目录拷贝；
    # 这里只做校验 + 清理早期手工散放在 bin 根目录的旧 EXE（避免两份 EXE 各自为政）。
    $idcDll = Join-Path (Join-Path $pluginDst $idcPluginName) "$idcPluginName.dll"
    $idcExe = Join-Path (Join-Path $pluginDst $idcPluginName) $idcExeName
    if ((Test-Path $idcDll) -and (Test-Path $idcExe)) {
        Write-Host "[3c/5] 独立插件已就位：$idcPluginName（启动器 DLL + $idcExeName）"
    } else {
        Write-Host "[3c/5] [错误] 独立插件不完整：DLL=$(Test-Path $idcDll) EXE=$(Test-Path $idcExe)" -ForegroundColor Red
        Write-Host "        请先运行 build.ps1（需先编译 tools\caxa3didcskill 才能产出 EXE）。" -ForegroundColor Yellow
        $hadError = $true
    }
    $idcLoose = Join-Path $caxaBin $idcExeName
    if (Test-Path $idcLoose) {
        try {
            Remove-Item $idcLoose -Force -ErrorAction Stop
            Write-Host "      已清除 bin 根目录的旧 $idcExeName（统一走 plugins\$idcPluginName）"
        } catch {
            # 旧 EXE 正在运行时先结束它（只清理 IRONCAD 后台进程，不影响 CAXA），再删一次
            try {
                Stop-Process -Name 'caxa3didcskill' -Force -ErrorAction SilentlyContinue
                Start-Sleep -Milliseconds 300
                Remove-Item $idcLoose -Force -ErrorAction Stop
                Write-Host "      已结束旧 $idcExeName 进程并删除文件"
            } catch {
                Write-Host "      警告：旧 $idcExeName 删除失败（被占用）" -ForegroundColor Yellow
            }
        }
    }

    # ---------- 4. 旧独立插件清理（XML + 正则 + 注册表 + 文件） ----------
    $legacyNeedles = @($legacyDlls) + @($legacyNames) + @($legacyClsids)
    $n1 = Remove-AddinEntries    $configFile   $legacyDlls $legacyClsids
    $n1 += Remove-ConfigBlocksByRegex $configFile $legacyNeedles
    $n2 = Remove-ManifestDependencies $manifestFile $legacyNames
    $n2 += Remove-ManifestBlocksByRegex $manifestFile $legacyNames
    Remove-RegistryEntries $verTag $legacyNames $legacyClsids
    foreach ($legacy in $legacyNames) {
        foreach ($f in @((Join-Path $caxaBin "$legacy.dll"), (Join-Path $caxaBin "$legacy.pdb"))) {
            if (Test-Path $f) {
                try { Remove-Item $f -Force -ErrorAction Stop; Write-Host "      已删除旧插件文件：$(Split-Path -Leaf $f)" }
                catch { Write-Host "      警告：旧插件文件删除失败（被占用）$(Split-Path -Leaf $f)" -ForegroundColor Yellow }
            }
        }
    }
    if ($n1 -gt 0 -or $n2 -gt 0) { Write-Host "      旧插件登记已撤销（config $n1 / manifest $n2）" }

    # ---------- 5. 登记主程序 ----------
    try {
        $null = Remove-AddinEntries $configFile @("$hostName.dll") @($clsid)
        $null = Remove-ConfigBlocksByRegex $configFile @("$hostName.dll", $clsid)
        $null = Remove-ManifestDependencies $manifestFile @($hostName)
        $null = Remove-ManifestBlocksByRegex $manifestFile @($hostName)

        Add-AddinEntry $configFile "$hostName.dll" 'CAXA 3D 插件集合体' '统一加载多个独立子插件的集合体主程序'
        if ($hasManifest) {
            Add-ManifestDependency $manifestFile
            Write-Host "[4/5] 已登记 IronCad.Addin.config 与 IronCad.exe.manifest"
        } else {
            Write-Host "[4/5] 已登记 IronCad.Addin.config（manifest 已跳过：无内嵌清单）" -ForegroundColor Yellow
        }

        Remove-RegistryEntries $verTag @($hostName) @($clsid)
        Add-RegistryEntry $verTag
        Write-Host "[5/5] 已登记 Applications 注册表（CAXA 3D $verTag）"
    } catch {
        Write-Host "[4/5] 登记失败：$($_.Exception.Message)" -ForegroundColor Red
        $hadError = $true
        continue
    }
    $deployed += $v
    Write-Host ""
}

# ---------- 读回校验 ----------
# 只校验真正装成功的版本：前面因缺 config/manifest 被 continue 掉的版本若仍在此遍历，
# Select-String 会对不存在的路径抛 PathNotFound（ErrorActionPreference=Stop 下直接中断）。
Write-Host "================ 部署校验 ================"
if ($deployed.Count -eq 0) { Write-Host "没有成功部署的版本，跳过读回校验。" -ForegroundColor Yellow }
foreach ($v in $deployed) {
    $caxaBin      = Join-Path $v.FullName 'bin'
    $configFile   = Join-Path $v.FullName 'Config\IronCad.Addin.config'
    $manifestFile = Join-Path $caxaBin 'IronCad.exe.manifest'
    $verTag       = '{0}.0' -f ([int]$v.Name - 1998)

    $okFile = Test-Path (Join-Path $caxaBin "$hostName.dll")
    $okCfg  = (Test-Path $configFile) -and ((Select-String -Path $configFile -Pattern "$hostName.dll" -SimpleMatch -ErrorAction SilentlyContinue) -ne $null)
    $okDep  = (-not $hasManifest) -or ((Test-Path $manifestFile) -and ((Select-String -Path $manifestFile -Pattern "`"$hostName`"" -SimpleMatch -ErrorAction SilentlyContinue) -ne $null))
    $okReg  = Test-Path "HKCU:\SOFTWARE\CAXA\CAXA 3D $verTag\Applications\$hostName"
    $okCrxList = @()
    foreach ($crxItem in $crxItems) {
        $n = $crxItem.Crx
        $onBin = Test-Path (Join-Path $caxaBin "CAXADraft\bin\$n")
        $onCx  = (Select-String -Path (Join-Path $caxaBin 'CAXADraft\Modules\CxAutorun.cx') -Pattern $n -SimpleMatch -ErrorAction SilentlyContinue) -ne $null
        $okCrxList += ('{0}={1}/{2}' -f $n, $onBin, $onCx)
        if (-not ($onBin -and $onCx)) { $hadError = $true }
    }
    $okIdcDll = Test-Path (Join-Path $caxaBin "$hostName\plugins\$idcPluginName\$idcPluginName.dll")
    $okIdcExe = Test-Path (Join-Path $caxaBin "$hostName\plugins\$idcPluginName\$idcExeName")
    $okIdcLoose = -not (Test-Path (Join-Path $caxaBin $idcExeName))

    Write-Host ("CAXA 3D {0}: DLL={1} config={2} manifest={3} registry={4} CRX={5} 独立插件={6}/{7} 无散放EXE={8}" -f $v.Name, $okFile, $okCfg, $okDep, $okReg, ($okCrxList -join ' '), $okIdcDll, $okIdcExe, $okIdcLoose)
    if (-not ($okFile -and $okCfg -and $okDep -and $okReg -and $okIdcDll -and $okIdcExe -and $okIdcLoose)) { $hadError = $true }
}

Write-Host ""
Write-Host "安装完成。日志：%TEMP%\caxa3dmytoolsaddin_trace.log" -ForegroundColor Green
if ($hadError) { Write-Host "注意：上面有失败项，请按提示处理后重新运行。" -ForegroundColor Yellow }
if ($Host.Name -eq 'ConsoleHost' -and -not $Yes) { Read-Host '按回车退出' }
