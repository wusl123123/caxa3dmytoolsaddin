<#
.SYNOPSIS
    一键编译：契约 -> 子插件 -> 主程序，并把子插件输出汇总到 src\plugins。
.NOTES
    在仓库根目录执行：powershell -ExecutionPolicy Bypass -File build.ps1
#>

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# PowerShell 5.1 对无 BOM 的 UTF-8 按 GBK 解码，中文脚本会静默崩溃。
# 每次编译前把仓库内脚本统一成“单个 UTF-8 BOM”。
function Set-SingleBom([string]$path) {
    if (-not (Test-Path $path)) { return }
    $bytes = [IO.File]::ReadAllBytes($path)
    $i = 0
    while (($i + 2) -lt $bytes.Length -and $bytes[$i] -eq 0xEF -and $bytes[$i + 1] -eq 0xBB -and $bytes[$i + 2] -eq 0xBF) { $i += 3 }
    if ($i -eq 3) { return }
    $body = if ($i -gt 0 -and $i -lt $bytes.Length) { [byte[]]$bytes[$i..($bytes.Length - 1)] } elseif ($i -eq 0) { $bytes } else { [byte[]]@() }
    [IO.File]::WriteAllBytes($path, ([byte[]](0xEF, 0xBB, 0xBF) + $body))
}

foreach ($s in @(
        (Join-Path $root 'build.ps1'),
        (Join-Path $root 'deploy.ps1'),
        (Join-Path $root 'restore.ps1'),
        (Join-Path $root 'package.ps1'),
        (Join-Path $root 'src\Host\strip-manifest.ps1'))) {
    Set-SingleBom $s
}

$contracts = Join-Path $root 'src\Contracts\Contracts.csproj'
$hostProj  = Join-Path $root 'src\Host\caxa3dmytoolsaddin.csproj'
$stage     = Join-Path $root 'src\plugins'

# 子插件：项目文件 + 暂存目录名（与插件 Id 一致）
$plugins = @(
    @{ Name = 'caxa3dAttributeManageraddin'; Project = (Join-Path $root 'plugins\caxa3dAttributeManageraddin\caxa3dAttributeManageraddin.csproj') },
    @{ Name = 'Caxa3dcudeaddin';             Project = (Join-Path $root 'plugins\Caxa3dcudeaddin\Caxa3dcudeaddin.csproj') },
    @{ Name = 'caxa3didcskill';              Project = (Join-Path $root 'plugins\caxa3didcskill\caxa3didcskill.csproj') },
    # 带图改名及图号分离：除 DLL 外还要带规则/说明/控件基线/CRX 注册脚本/CRX 桥本体
    @{ Name = 'caxa3dcodenameextraction';    Project = (Join-Path $root 'plugins\caxa3dcodenameextraction\caxa3dcodenameextraction.csproj');
       Extra = @('.crx', '.txt', '.ini', '.exe', '.ps1') },
    # 材料工具：需带色卡.xml 与资料库种子 自定义材料.xml
    @{ Name = 'caxa3dsetMaterial';           Project = (Join-Path $root 'plugins\caxa3dsetMaterial\caxa3dsetMaterial.csproj');
       Extra = @('.xml') },
    # 树形BOM：需带默认表头模板 .xlsx 与合并规则种子 .txt
    @{ Name = 'caxa3dTreeBom';               Project = (Join-Path $root 'plugins\caxa3dTreeBom\caxa3dTreeBom.csproj');
       Extra = @('.txt') },
    # 小工具合集：需带坐标数据示例 .txt
    @{ Name = 'caxa3dminitool';              Project = (Join-Path $root 'plugins\caxa3dminitool\caxa3dminitool.csproj');
       Extra = @('.txt') },
    # 折弯管理大师：需带使用说明 .txt
    @{ Name = 'caxa3dsheetmaster';           Project = (Join-Path $root 'plugins\caxa3dsheetmaster\caxa3dsheetmaster.csproj');
       Extra = @('.txt') },
    # 图纸环境矩形测试：需带 CRX 桥接模块 .crx 与模块登记 .info
    @{ Name = 'caxa3dexbtest';               Project = (Join-Path $root 'plugins\caxa3dexbtest\caxa3dexbtest.csproj');
       Extra = @('.crx', '.info') }
)

function Build-Project([string]$project) {
    Write-Host ""
    Write-Host "==== 编译 $(Split-Path -Leaf $project) ====" -ForegroundColor Cyan
    & dotnet build $project -c Release -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "编译失败：$project" }
}

Build-Project $contracts
foreach ($p in $plugins) { Build-Project $p.Project }
Build-Project $hostProj

# ---------- 汇总插件输出 ----------
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

foreach ($p in $plugins) {
    $out = Join-Path (Split-Path -Parent $p.Project) 'bin\x64\Release\net48'
    if (-not (Test-Path $out)) { throw "找不到插件输出目录：$out" }

    $dst = Join-Path $stage $p.Name
    New-Item -ItemType Directory -Path $dst | Out-Null

    # 契约程序集/符号不随插件分发：Host 侧那份才是唯一实例
    $extra = if ($p.Extra) { $p.Extra } else { @() }
    Get-ChildItem $out -File | Where-Object {
        ($_.Extension -in (@('.dll', '.pdb', '.xlsx', '.json', '.config') + $extra)) -and
        ($_.Name -notlike 'Caxa3dMyTools.Contracts.*')
    } | Copy-Item -Destination $dst -Force

    Write-Host "已暂存插件：$($p.Name) -> $dst"
}

Write-Host ""
Write-Host "编译与暂存完成。" -ForegroundColor Green
Write-Host "  Host : $(Join-Path $root 'src\Host\bin\x64\Release\net48\caxa3dmytoolsaddin.dll')"
Write-Host "  插件 : $stage"
Write-Host "安装 : 运行仓库根目录的 安装.bat；卸载：运行 卸载.bat；打包：运行 打包.bat"
