# MiniToolboxBridge 编译脚本（Release x64 → .crx）
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1
param(
    [switch]$NoPause
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$proj = Join-Path $root 'MiniToolboxBridge.vcxproj'

# 本机同时存在 http_proxy / HTTP_PROXY（大小写各一套）时，MSBuild 的 ToolTask 会因
# StringDictionary 重复键抛 MSB6001「CL.exe 的命令行开关无效」。编译不需要出网，先清小写那套。
foreach ($proxyKey in @('http_proxy', 'https_proxy')) {
    Remove-Item -Path ("env:" + $proxyKey) -ErrorAction SilentlyContinue
}

function Find-MSBuild {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $p = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null
        if ($p) { return @($p)[0] }
    }
    foreach ($c in @(
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\MSBuild.exe')) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

$msb = Find-MSBuild
if (-not $msb) { Write-Host '[ERROR] 未找到 MSBuild（需 Visual Studio C++ 工作负载）' -ForegroundColor Red; exit 1 }

Write-Host "MSBuild: $msb"
& $msb $proj /p:Configuration=Release /p:Platform=x64 /t:Rebuild /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { Write-Host '[ERROR] CRX 编译失败' -ForegroundColor Red; exit $LASTEXITCODE }

$out = Join-Path $root 'bin\caxa3dminitool.crx'
if (-not (Test-Path $out)) { Write-Host '[ERROR] 未生成 bin\caxa3dminitool.crx' -ForegroundColor Red; exit 1 }
Write-Host ("[OK] {0}  {1} B" -f $out, (Get-Item $out).Length) -ForegroundColor Green
Get-ChildItem (Join-Path $root 'obj') -Filter '*.log' -ErrorAction SilentlyContinue | Out-Null
if (-not $NoPause -and $Host.Name -eq 'ConsoleHost') { }
