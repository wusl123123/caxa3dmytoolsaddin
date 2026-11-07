<#
.SYNOPSIS
    caxa3dmytoolsaddin 一键卸载（撤销 deploy.ps1 的全部改动）。
.DESCRIPTION
    设计约束（踩坑后固化）：
      * 本脚本必须保存为“单个 UTF-8 BOM”；否则 PS5.1 按 GBK 解码会静默崩溃。
      * 绝不回滚 *.bak：备份可能已污染，还原会留下指向已删 DLL 的 manifest 依赖，
        SxS 解析失败将导致 CAXA 无法启动。这里只做“手术式删除 + 正则无条件兜底”。
      * 所有删除都带 try/catch：CAXA 进程锁住 DLL 时只警告，继续往下清理。
      * 提权时把脚本副本落到 C:\ProgramData（真实文件系统），避免沙箱文件不可见。
      * ★ 2026-11-07 卸载口径 = 「安装过的东西全部清掉」：
          ① bin 根：主程序 / 契约 / pdb / 9 个迁入插件的旧 DLL+pdb / 散放的 EXE 与独立版遗留资源；
          ② bin 下 caxa3dmytoolsaddin\plugins 整棵目录（11 个子插件 + 各自的 .crx/.txt/.xlsx/.exb/.exe）；
          ③ CAXADraft\bin 的 5 个 .crx + CxAutorun.cx 里对应的登记行（先摘登记再删文件）；
          ④ IronCad.Addin.config / IronCad.exe.manifest 的条目；
          ⑤ HKCU 两个 Applications 注册项（CAXA 3D 与 IRONCAD 两种写法都查）；
          ⑥ C:\ProgramData\caxa3dmytoolsaddin 下提权用的脚本副本（本脚本自身那份删不掉就留到下次）。
        任何一步失败都只记黄色警告，绝不中断、绝不抛异常（用户口径：卸载不能报错）。
#>

param(
    [string]$WorkRoot = ''
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
    $elevScript = Join-Path $elevDir ('restore-' + $ScriptVersion + '.ps1')
    Copy-Item $PSCommandPath $elevScript -Force

    Write-Host "以管理员权限重新启动：$elevScript" -ForegroundColor Yellow
    $argLine = "-NoProfile -ExecutionPolicy Bypass -File `"$elevScript`" -WorkRoot `"$scriptDir`""
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argLine
    exit
}

Write-Host "================ caxa3dmytoolsaddin 卸载 ================"
Write-Host "脚本版本 : $ScriptVersion"
Write-Host "脚本路径 : $PSCommandPath"
Write-Host ""

$hostName      = 'caxa3dmytoolsaddin'
$clsid         = '{6F1A9C24-3B57-4E8D-9A02-5C7D1E8B4F36}'
$contractsName = 'Caxa3dMyTools.Contracts.dll'

# 「2. 独立插件」列的后台进程清理工具（启动器 DLL + 进程外 EXE）
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

# 已并入集合体的旧插件：卸载时一并清理其独立登记（集合体不再承载的功能不列入，避免误删兄弟 Add-in）。
# ★ 这三张表必须与 deploy.ps1 的同名三张表**完全一致**（2026-11-07 对齐为 9 项）：
#   之前这里只列了 3 项，卸载后 bin 根会留下另外 5 个已迁入插件的旧 DLL —— 复核口径「已全部卸载」不成立。
$legacyDlls   = @('caxa3dAttributeManageraddin.dll', 'Caxa3dcudeaddin.dll', 'caxa3dcodenameextraction.dll', 'caxa3dsetMaterial.dll', 'caxa3dTreeBom.dll', 'caxa3dminitool.dll', 'caxa3dsheetmaster.dll', 'caxa3dCXDFTViewUpdate.dll', 'caxa3dBatchMaster.dll')
$legacyNames  = @('caxa3dAttributeManageraddin', 'Caxa3dcudeaddin', 'caxa3dcodenameextraction', 'caxa3dsetMaterial', 'caxa3dTreeBom', 'caxa3dminitool', 'caxa3dsheetmaster', 'caxa3dCXDFTViewUpdate', 'caxa3dBatchMaster')
$legacyClsids = @('{82B0A784-9822-4009-B83B-4AD63D6D2A88}', '{B3246FE6-6C06-446C-8A43-582D4C9690F9}', '{3B7D9E21-6C4A-4F2B-B8D5-7A1C9E3F5A62}', '{C4B2A6E7-9D31-4F58-B0A2-7E5C1D8F3A64}', '{2F6C8B41-9D34-4E52-9A7B-6C1D0E3A58F2}', '{9B5E3D71-4A2C-4F86-8E19-3C7D0B2A6F54}', '{7A3F9C21-5B4E-4D86-9E0A-3C1B7D5F2E48}', '{C9F5A7B3-6D4E-4F8A-AB1C-3E4D5F6A7B8C}', '{1EA4F316-3B4A-40E9-8D9D-F0448CD20EC3}')

# 部署时投放到 CAXADraft\bin 的图纸环境 CRX 桥（第 5 登记点）。
# caxa3dapicapture.crx 不在 deploy 的 crxItems 里（它由 caxa3dapigetaddin 的「注册CRX模块」手工登记），
# 这里一并清理历史残留；清单与残留校验共用这一个变量，避免两处各写一份而漏项。
$crxNames = @('caxa3dcodenameextraction.crx', 'caxa3dCXDFTViewUpdate.crx',
              'caxa3dminitool.crx', 'caxa3dBatchMasterView.crx', 'caxa3dapicapture.crx')

# 独立版 Add-in 部署时散落在 bin 根的资源（迁入集合体后这些是孤儿文件，一并清掉）
$looseResidue = @('caxa3d图纸环境预热.exb', 'caxa3dBatchMaster属性表格.xlsx',
                  'caxa3dBatchMaster内部字段映射.txt', 'caxa3dBatchMaster图号分离规则.txt')

# ================================================================ 通用工具

function Write-TextWithBom([string]$path, [string]$text) {
    [IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($true)))
}

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
            if ($hit) { [void]$n.ParentNode.RemoveChild($n); $removed++ }
        }
        if ($removed -gt 0) { $doc.Save($configFile) }
    } catch {
        Write-Host "      警告：XML 解析失败（改用正则兜底）-> $($_.Exception.Message)" -ForegroundColor Yellow
    }
    return $removed
}

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

function Remove-FileSafe([string]$path) {
    if (-not (Test-Path $path)) { return $true }
    try {
        $item = Get-Item $path -Force -ErrorAction Stop
        if ($item.Attributes -band [IO.FileAttributes]::ReadOnly) {
            $item.Attributes = $item.Attributes -bxor [IO.FileAttributes]::ReadOnly
        }
        Remove-Item $path -Force -ErrorAction Stop
        Write-Host "      已删除：$path"
        return $true
    } catch {
        Write-Host "      警告：删除失败（进程占用？）$path -> $($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    }
}

function Remove-DirectorySafe([string]$path) {
    if (-not (Test-Path $path)) { return $true }
    try {
        Remove-Item $path -Recurse -Force -ErrorAction Stop
        Write-Host "      已删除目录：$path"
        return $true
    } catch {
        Write-Host "      警告：目录删除失败（进程占用？）$path -> $($_.Exception.Message)" -ForegroundColor Yellow
        return $false
    }
}

# 本程序自己的 bin 根文件集合（含旧布局散放资源，以及被占用时改名成 .inuse-old / .legacy-bak 的产物）。
# 判据只认「本程序自己的名字前缀 + 可执行/资源扩展名」：绝不整目录通配，
# 否则会误删同机兄弟 Add-in（caxa3dexbtracker 的 DLL/预热 exb、caxa3dexbTechSpecsbak 等）。
$ownBases = @($hostName, 'Caxa3dMyTools.Contracts', 'caxa3didcskill', 'caxa3dapicapture', 'caxa3dBatchMasterView') + $legacyNames
$ownExt   = @('.dll', '.pdb', '.xml', '.config', '.txt', '.xlsx', '.exb', '.ini', '.crx', '.exe', '.manifest')

function Get-OwnBinFile([string]$bin) {
    if (-not (Test-Path -LiteralPath $bin)) { return @() }
    $out = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $bin -File -ErrorAction SilentlyContinue)) {
        $n = $f.Name
        foreach ($b in $ownBases) {
            # 前缀匹配（旧布局把资源名接在插件名后面，如「caxa3dcodenameextraction使用说明.txt」），
            # 但必须同时满足扩展名白名单，避免误伤同前缀的其它文件
            if (($n -like "$b.dll.*") -or ($n -like "$b.*.inuse-*") -or
                (($n.StartsWith($b, [StringComparison]::OrdinalIgnoreCase)) -and
                 ($ownExt -contains [IO.Path]::GetExtension($n).ToLower()))) {
                $out += $f
                break
            }
        }
    }
    return $out
}

# ---------- CAXA 进程检查 ----------
$running = Get-Process -Name 'IRONCAD', 'IronCad', 'CDRAFT_M_IC' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "[警告] 检测到 CAXA 相关进程（含后台无窗口进程），文件可能被占用。" -ForegroundColor Yellow
    $ans = Read-Host '已全部退出？继续卸载请输入 y'
    if ($ans -ne 'y') { exit 1 }
}

# ---------- 独立工具进程检查 ----------
# caxa3didcskill.exe 是进程外工具，正在运行时 EXE 被锁、删不掉；
# 它只清理 IRONCAD 后台进程，结束它不影响正在使用的 CAXA。
$idcProc = @(Get-Process -Name 'caxa3didcskill' -ErrorAction SilentlyContinue)
if ($idcProc.Count -gt 0) {
    Write-Host "[提示] 独立工具 caxa3didcskill 正在运行，先行结束以便删除 EXE。" -ForegroundColor Yellow
    try {
        Stop-Process -Name 'caxa3didcskill' -Force -ErrorAction Stop
        Start-Sleep -Milliseconds 300
        Write-Host "      已结束 caxa3didcskill 进程"
    } catch {
        Write-Host "      警告：结束独立工具进程失败 -> $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

$versions = Get-CaxaVersionDirs
if ($versions.Count -eq 0) {
    Write-Host "[错误] 未在本机找到 CAXA 3D 安装目录。已依次探测：" -ForegroundColor Red
    Write-Host "       1) 注册表 SOFTWARE\CAXA 下任意层的 InstallDir / Install（HKLM+HKCU，64 位与 32 位视图）" -ForegroundColor Red
    Write-Host "       2) 卸载表 DisplayName 含 CAXA 的 InstallLocation" -ForegroundColor Red
    Write-Host "       3) 每个固定盘的 \Program Files\CAXA\CAXA 3D、\Program Files (x86)\CAXA\CAXA 3D、\CAXA\CAXA 3D、\CAXA" -ForegroundColor Red
    Write-Host "       4) 有界浅扫（盘根往下 4 层，只进名字像安装目录的分支）" -ForegroundColor Red
    Write-Host "       请设置环境变量 CAXA3D_ROOT 指向安装根目录或版本目录（指到 bin 也行）后重跑，例如：" -ForegroundColor Yellow
    Write-Host "         set CAXA3D_ROOT=D:\Program Files\CAXA\CAXA 3D   或   D:\Program Files\CAXA\CAXA 3D\2026" -ForegroundColor Yellow
    Write-CaxaProbe
    Read-Host '按回车退出'
    exit 1
}
$foundRoots = @($versions | ForEach-Object { Split-Path -Parent $_.FullName } | Select-Object -Unique)
Write-Host ("发现 CAXA 3D 安装位置：{0}" -f ($foundRoots -join '  ')) -ForegroundColor Cyan
Write-Host ("发现版本目录：{0}" -f (($versions | ForEach-Object { $_.Name }) -join ', '))
Write-Host ""

$hadError = $false
$allNeedles = @("$hostName.dll", $hostName, $clsid) + $legacyDlls + $legacyNames + $legacyClsids
$allDlls    = @("$hostName.dll") + $legacyDlls
$allNames   = @($hostName) + $legacyNames
$allClsids  = @($clsid) + $legacyClsids

foreach ($v in $versions) {
    $year         = $v.Name
    $caxaBin      = Join-Path $v.FullName 'bin'
    $configFile   = Join-Path $v.FullName 'Config\IronCad.Addin.config'
    $manifestFile = Join-Path $caxaBin 'IronCad.exe.manifest'
    $verTag       = '{0}.0' -f ([int]$year - 1998)

    Write-Host "---------------- CAXA 3D $year（注册版本 $verTag） ----------------"

    # ---------- 1. 文件（逐个 try/catch，锁住也继续） ----------
    $files = @(
        (Join-Path $caxaBin "$hostName.dll"),
        (Join-Path $caxaBin "$hostName.pdb"),
        (Join-Path $caxaBin $contractsName),
        (Join-Path $caxaBin $contractsName.Replace('.dll', '.pdb'))
    )
    foreach ($legacy in $legacyNames) {
        $files += (Join-Path $caxaBin "$legacy.dll")
        $files += (Join-Path $caxaBin "$legacy.pdb")
    }
    foreach ($f in $files) {
        if (-not (Remove-FileSafe $f)) { $hadError = $true }
    }
    # 独立插件列的工具：整目录会被下面这行删掉，这里再兜底清 bin 根目录的散放拷贝
    if (-not (Remove-FileSafe (Join-Path $caxaBin $idcExeName))) { $hadError = $true }
    if (-not (Remove-DirectorySafe (Join-Path $caxaBin $hostName))) { $hadError = $true }
    # 独立版 Add-in 散落在 bin 根的随包资源（集合体不往这里放，属孤儿文件）
    foreach ($loose in $looseResidue) {
        if (-not (Remove-FileSafe (Join-Path $caxaBin $loose))) { $hadError = $true }
    }
    # 旧布局残留（散放资源、.inuse-old / .legacy-bak / .inuse-xxxxxx.dll 改写名）：按本程序名字定向清扫
    foreach ($f in @(Get-OwnBinFile $caxaBin)) {
        if (-not (Remove-FileSafe $f.FullName)) { $hadError = $true }
    }

    # ---------- 1b. 图纸环境 CRX 桥（只删本插件条目，绝不整体重写共享清单） ----------
    # 顺序不能反：先摘 CxAutorun.cx 登记项，再删 .crx；否则重启 CAXA 会因悬空引用加载失败。
    $crxBinDir  = Join-Path $caxaBin 'CAXADraft\Bin'
    $crxAutorun = Join-Path $caxaBin 'CAXADraft\Modules\CxAutorun.cx'
    if (Test-Path $crxAutorun) {
        try {
            $cx = [IO.File]::ReadAllText($crxAutorun)
            $changed = $false
            foreach ($crxName in $crxNames) {
                # 按文件名整行删除：登记行里的盘符大小写由写入方决定，不能靠字符串精确匹配
                $pattern = '(?im)^[^\r\n]*' + [regex]::Escape($crxName) + '[^\r\n]*(?:\r?\n|$)'
                if ([regex]::IsMatch($cx, $pattern)) {
                    $cx = [regex]::Replace($cx, $pattern, '')
                    $changed = $true
                }
            }
            if ($changed) {
                [IO.File]::WriteAllText($crxAutorun, $cx, (New-Object System.Text.UTF8Encoding($true)))
                Write-Host "      CxAutorun.cx 已移除本插件登记项"
            }
        } catch {
            Write-Host "      警告：CxAutorun.cx 清理异常 -> $($_.Exception.Message)" -ForegroundColor Yellow
            $hadError = $true
        }
    }
    foreach ($crxName in $crxNames) {
        if (-not (Remove-FileSafe (Join-Path $crxBinDir $crxName))) { $hadError = $true }
    }

    # ---------- 2. 配置与清单：XML 精确删除 + 正则无条件兜底 ----------
    if (Test-Path $configFile) {
        try {
            $n = Remove-AddinEntries $configFile $allDlls $allClsids
            $n += Remove-ConfigBlocksByRegex $configFile $allNeedles
            Write-Host "      IronCad.Addin.config 清理条目：$n"
        } catch {
            Write-Host "      警告：config 清理异常 -> $($_.Exception.Message)" -ForegroundColor Yellow
            $hadError = $true
        }
    }
    if (Test-Path $manifestFile) {
        try {
            $n = Remove-ManifestDependencies $manifestFile $allNames
            $n += Remove-ManifestBlocksByRegex $manifestFile $allNames
            Write-Host "      IronCad.exe.manifest 清理依赖：$n"
        } catch {
            Write-Host "      警告：manifest 清理异常 -> $($_.Exception.Message)" -ForegroundColor Yellow
            $hadError = $true
        }
    }

    # ---------- 3. 注册表（名称 + CLSID 双条件） ----------
    try {
        Remove-RegistryEntries $verTag $allNames $allClsids
    } catch {
        Write-Host "      警告：注册表清理异常 -> $($_.Exception.Message)" -ForegroundColor Yellow
        $hadError = $true
    }
    Write-Host ""
}

# ---------- 3b. 提权脚本副本（C:\ProgramData\caxa3dmytoolsaddin） ----------
# 这些副本是安装/卸载时为了绕开沙箱文件而落地的“一次性脚本”，也属于安装文件，必须清掉。
# 本脚本自己那份正在运行，Windows 允许删除正在执行的 .ps1（句柄在解析阶段已释放），但为稳妥仍跳过，
# 留给下一次卸载再清（目录非空就保留，不报错）。
$programDataDir = if ([string]::IsNullOrWhiteSpace($env:ProgramData)) { 'C:\ProgramData' } else { $env:ProgramData }
$elevDir = Join-Path $programDataDir $hostName
if (Test-Path $elevDir) {
    foreach ($f in @(Get-ChildItem $elevDir -File -Force -ErrorAction SilentlyContinue |
                     Where-Object { $_.Name -match '^(deploy|restore)-.*\.ps1$' })) {
        if ($PSCommandPath -and $f.FullName.Equals($PSCommandPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
        try {
            Remove-Item $f.FullName -Force -ErrorAction Stop
            Write-Host "      已删除提权脚本副本：$($f.FullName)"
        } catch {
            Write-Host "      提示：$($f.Name) 暂被占用，下次卸载会清掉" -ForegroundColor Yellow
        }
    }
    try {
        if (@(Get-ChildItem $elevDir -Force -ErrorAction SilentlyContinue).Count -eq 0) {
            Remove-Item $elevDir -Force -ErrorAction Stop
            Write-Host "      已删除目录：$elevDir"
        } else {
            Write-Host "      提示：$elevDir 仍含本次运行的脚本副本，下次卸载后自动消失" -ForegroundColor Yellow
        }
    } catch { }
}

# ---------- 4. 残留校验（无条件执行） ----------
Write-Host "================ 卸载校验 ================"
$left = @()
foreach ($v in $versions) {
    $caxaBin      = Join-Path $v.FullName 'bin'
    $configFile   = Join-Path $v.FullName 'Config\IronCad.Addin.config'
    $manifestFile = Join-Path $caxaBin 'IronCad.exe.manifest'
    $verTag       = '{0}.0' -f ([int]$v.Name - 1998)

    if (Test-Path (Join-Path $caxaBin "$hostName.dll")) { $left += "$($v.Name): bin 仍有 $hostName.dll" }
    if (Test-Path (Join-Path $caxaBin $hostName))       { $left += "$($v.Name): bin 仍有插件目录 $hostName" }
    if (Test-Path (Join-Path $caxaBin "$hostName\plugins\$idcPluginName")) {
        $left += "$($v.Name): 仍有独立插件目录 $hostName\plugins\$idcPluginName"
    }
    if (Test-Path (Join-Path $caxaBin $idcExeName)) {
        $left += "$($v.Name): bin 仍有 $idcExeName"
    }
    if (@(Get-Process -Name 'caxa3didcskill' -ErrorAction SilentlyContinue).Count -gt 0) {
        $left += "$($v.Name): 仍有 caxa3didcskill 进程在运行（关掉后重跑）"
    }
    foreach ($crxName in $crxNames) {
        if (Test-Path (Join-Path $caxaBin "CAXADraft\Bin\$crxName")) {
            $left += "$($v.Name): CAXADraft\bin 仍有 $crxName"
        }
    }
    foreach ($loose in $looseResidue) {
        if (Test-Path (Join-Path $caxaBin $loose)) {
            $left += "$($v.Name): bin 根仍有散放资源 $loose"
        }
    }
    foreach ($f in @(Get-OwnBinFile $caxaBin)) {
        $left += "$($v.Name): bin 根仍有本程序文件 $($f.Name)"
    }
    $autorunCheck = Join-Path $caxaBin 'CAXADraft\Modules\CxAutorun.cx'
    if (Test-Path $autorunCheck) {
        foreach ($needle in $crxNames) {
            if (Select-String -Path $autorunCheck -Pattern $needle -SimpleMatch -ErrorAction SilentlyContinue) {
                $left += "$($v.Name): CxAutorun.cx 仍含 $needle"
            }
        }
    }
    if (Test-Path $configFile) {
        foreach ($needle in @("$hostName.dll", $clsid) + $legacyDlls + $legacyClsids) {
            if (Select-String -Path $configFile -Pattern $needle -SimpleMatch -ErrorAction SilentlyContinue) {
                $left += "$($v.Name): config 仍含 $needle"
            }
        }
    }
    if (Test-Path $manifestFile) {
        foreach ($needle in @("`"$hostName`"") + ($legacyNames | ForEach-Object { "`"$_`"" })) {
            if (Select-String -Path $manifestFile -Pattern $needle -SimpleMatch -ErrorAction SilentlyContinue) {
                $left += "$($v.Name): manifest 仍含 $needle"
            }
        }
    }
    foreach ($base in @("HKCU:\SOFTWARE\CAXA\CAXA 3D $verTag\Applications",
                        "HKCU:\SOFTWARE\IronCAD\IRONCAD $verTag\Applications")) {
        if (-not (Test-Path $base)) { continue }
        foreach ($k in @(Get-ChildItem $base -ErrorAction SilentlyContinue)) {
            $default = $null
            try { $default = (Get-ItemProperty $k.PSPath -ErrorAction SilentlyContinue).'(default)' } catch { }
            if (($allNames -contains $k.PSChildName) -or ($default -and ($allClsids -contains [string]$default))) {
                $left += "$($v.Name): 注册表仍有 $($k.PSChildName)"
            }
        }
    }
}

if ($left.Count -gt 0) {
    Write-Host "[警告] 仍有残留：" -ForegroundColor Yellow
    $left | ForEach-Object { Write-Host "    $_" }
    Write-Host "        多为 CAXA 进程占用；完全退出 CAXA 后重新运行本脚本即可。" -ForegroundColor Yellow
    $hadError = $true
} else {
    Write-Host "校验通过：CAXA 已恢复到未部署本集合体的状态。" -ForegroundColor Green
}

Write-Host ""
if ($hadError) { Write-Host "卸载完成（存在警告，见上）。" -ForegroundColor Yellow }
else { Write-Host "卸载完成。" -ForegroundColor Green }
if ($Host.Name -eq 'ConsoleHost') { Read-Host '按回车退出' }
