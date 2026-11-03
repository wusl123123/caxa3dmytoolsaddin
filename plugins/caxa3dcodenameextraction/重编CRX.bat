@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"
echo ==== Rebuild CRX bridge (caxa3dcodenameextraction) ====
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-crx.ps1"
set RC=%ERRORLEVEL%
echo.
if not "%RC%"=="0" (
    echo [FAILED] exit code %RC% - see messages above.
) else (
    echo [OK] now run 安装.bat in repository root to deploy.
)
pause
exit /b %RC%
