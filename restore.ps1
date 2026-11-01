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
#>

param(
    [string]$WorkRoot = ''
)

$ScriptVersion = '2026-10-31.1'
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
# CAXA 3D 不一定装在 C 盘。探测顺序（全部命中后按「存在 <年份>\bin」校验、去重）：
#   0) 环境变量 CAXA3D_ROOT（手工指定根目录，最高优先级，命中即不再扫描其余来源）
#   1) 注册表 HKLM/HKCU\SOFTWARE[+WOW6432Node]\CAXA\CAXA 3D\<ver>\InstallDir
#      （本机实测值形如 <根>\2027\，即年份版本目录，取其父目录为根）
#   2) 卸载表 Uninstall\* 中 DisplayName 匹配 CAXA 3D 的 InstallLocation
#   3) 各固定盘 <盘>:\Program Files\CAXA\CAXA 3D
#   4) 兜底 C:\Program Files\CAXA\CAXA 3D
function Test-CaxaRoot([string]$root) {
    if ([string]::IsNullOrWhiteSpace($root)) { return $false }
    try {
        foreach ($v in @(Get-ChildItem $root -Directory -ErrorAction SilentlyContinue)) {
            if ($v.Name -match '^\d+$' -and (Test-Path (Join-Path $v.FullName 'bin'))) { return $true }
        }
    } catch { }
    return $false
}

function Get-CaxaRoots {
    $roots = New-Object System.Collections.Generic.List[string]

    if ($env:CAXA3D_ROOT) {
        Write-Host ("安装位置来源：环境变量 CAXA3D_ROOT = {0}" -f $env:CAXA3D_ROOT) -ForegroundColor Cyan
        $roots.Add($env:CAXA3D_ROOT.TrimEnd('\'))
        return ,$roots
    }

    # 1) 注册表 InstallDir（HKLM/HKCU 的 64 位与 32 位视图都查）
    foreach ($base in @('HKLM:\SOFTWARE\CAXA\CAXA 3D',
                        'HKLM:\SOFTWARE\WOW6432Node\CAXA\CAXA 3D',
                        'HKCU:\SOFTWARE\CAXA\CAXA 3D',
                        'HKCU:\SOFTWARE\WOW6432Node\CAXA\CAXA 3D')) {
        if (-not (Test-Path $base)) { continue }
        foreach ($k in @(Get-ChildItem $base -ErrorAction SilentlyContinue)) {
            $dir = $null
            try { $dir = [string]$k.GetValue('InstallDir') } catch { }
            if ([string]::IsNullOrWhiteSpace($dir)) { continue }
            $dir = $dir.TrimEnd('\')
            if ($dir) { $roots.Add($dir) }                      # InstallDir 本身可能是版本目录
            $parent = Split-Path -Parent $dir
            if ($parent) { $roots.Add($parent) }                # 也可能是根目录下某个子层
        }
    }

    # 2) 卸载表 InstallLocation（CAXA 3D 实体设计 / 三维数据接口等条目）
    $unins = @(Get-ItemProperty -Path @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*') -ErrorAction SilentlyContinue)
    foreach ($u in $unins) {
        if ($u.DisplayName -and ($u.DisplayName -match 'CAXA') -and $u.InstallLocation) {
            $loc = ([string]$u.InstallLocation).TrimEnd('\')
            if ($loc) { $roots.Add($loc) }
        }
    }

    # 3) 各固定盘默认安装路径 + 4) 兜底 C 盘
    foreach ($d in @(Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
                     Where-Object { $_.DriveType -eq 'Fixed' })) {
        $roots.Add((Join-Path $d.Root 'Program Files\CAXA\CAXA 3D'))
    }
    $roots.Add('C:\Program Files\CAXA\CAXA 3D')

    return ,$roots
}

# 把候选根收敛成「已验证的版本目录（<根>\<年份>）」列表，去重保序。
# 陷阱：根去重与版本去重必须各自记账。注册表 InstallDir 常直接指向 <根>\<年份>，
# 若两者共用一张表，该版本目录会先被登记成「根」，随后扫描真根时被误判为已处理而漏卸载该版本。
function Get-CaxaVersionDirs {
    $versions = @()
    $seenRoot = @{}
    $seenVer  = @{}
    $roots = Get-CaxaRoots
    foreach ($r in $roots) {
        if ([string]::IsNullOrWhiteSpace($r)) { continue }
        $rk = $r.TrimEnd('\').ToLower()
        if ($seenRoot.ContainsKey($rk)) { continue }
        $seenRoot[$rk] = $true

        # 情形 A：候选本身就是版本目录（…\<年份>，其下有 bin）→ 直接入列，不再当根往下扫
        if ((Split-Path -Leaf $r) -match '^\d+$' -and (Test-Path (Join-Path $r 'bin'))) {
            if (-not $seenVer.ContainsKey($rk)) {
                $seenVer[$rk] = $true
                $versions += (Get-Item -LiteralPath $r)
            }
            continue
        }

        # 情形 B：候选是安装根（其下有 <年份>\bin）
        if (-not (Test-CaxaRoot $r)) { continue }
        foreach ($v in @(Get-ChildItem -LiteralPath $r -Directory -ErrorAction SilentlyContinue)) {
            if ($v.Name -notmatch '^\d+$') { continue }
            if (-not (Test-Path (Join-Path $v.FullName 'bin'))) { continue }
            $vk = $v.FullName.ToLower()
            if ($seenVer.ContainsKey($vk)) { continue }
            $seenVer[$vk] = $true
            $versions += $v
        }
    }
    return ,@($versions | Sort-Object Name)   # 按版本目录名升序，输出稳定
}

$legacyDlls   = @('caxa3dAttributeManageraddin.dll', 'Caxa3dcudeaddin.dll', 'caxa3dCXDFTViewUpdate.dll')
$legacyNames  = @('caxa3dAttributeManageraddin', 'Caxa3dcudeaddin', 'caxa3dCXDFTViewUpdate')
$legacyClsids = @('{82B0A784-9822-4009-B83B-4AD63D6D2A88}', '{B3246FE6-6C06-446C-8A43-582D4C9690F9}', '{C9F5A7B3-6D4E-4F8A-AB1C-3E4D5F6A7B8C}')

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
    Write-Host "[错误] 未在本机找到 CAXA 3D 安装目录（已查注册表 InstallDir、卸载表、各固定盘 Program Files；" -ForegroundColor Red
    Write-Host "       如安装在非常规位置，请设置环境变量 CAXA3D_ROOT 指向安装根目录后重试）" -ForegroundColor Red
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

    # ---------- 1b. 图纸环境 CRX 桥（只删本插件条目，绝不整体重写共享清单） ----------
    # 顺序不能反：先摘 CxAutorun.cx 登记项，再删 .crx；否则重启 CAXA 会因悬空引用加载失败。
    $crxNames   = @('caxa3dcodenameextraction.crx', 'caxa3dCXDFTViewUpdate.crx', 'caxa3dapicapture.crx')
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
    if (Test-Path (Join-Path $caxaBin 'CAXADraft\Bin\caxa3dcodenameextraction.crx')) {
        $left += "$($v.Name): CAXADraft\bin 仍有 caxa3dcodenameextraction.crx"
    }
    if (Test-Path (Join-Path $caxaBin 'CAXADraft\Bin\caxa3dCXDFTViewUpdate.crx')) {
        $left += "$($v.Name): CAXADraft\bin 仍有 caxa3dCXDFTViewUpdate.crx"
    }
    if (Test-Path (Join-Path $caxaBin 'CAXADraft\Bin\caxa3dapicapture.crx')) {
        $left += "$($v.Name): CAXADraft\bin 仍有 caxa3dapicapture.crx"
    }
    $autorunCheck = Join-Path $caxaBin 'CAXADraft\Modules\CxAutorun.cx'
    if (Test-Path $autorunCheck) {
        foreach ($needle in @('caxa3dcodenameextraction.crx', 'caxa3dCXDFTViewUpdate.crx', 'caxa3dapicapture.crx')) {
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
