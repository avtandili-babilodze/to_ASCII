@echo off
rem Double-click to install ASCII Vision. Runs install.ps1 regardless of the PowerShell execution policy.
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
set RESULT=%ERRORLEVEL%
echo.
pause
exit /b %RESULT%
