@echo off
chcp 65001 >nul
setlocal
rem Normalize single UTF-8 BOM before invoking PS5.1 (no-BOM UTF-8 is decoded as GBK)
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$p = Join-Path '%~dp0' 'deploy.ps1'; $b=[IO.File]::ReadAllBytes($p); $i=0; while($i+2 -lt $b.Length -and $b[$i] -eq 239 -and $b[$i+1] -eq 187 -and $b[$i+2] -eq 191){$i+=3}; if($i -ne 3){$body=if($i -gt 0){$b[$i..($b.Length-1)]}else{$b}; [IO.File]::WriteAllBytes($p,([byte[]](239,187,191)+$body))}"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0deploy.ps1"
pause
