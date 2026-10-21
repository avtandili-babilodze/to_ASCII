@echo off
rem Removes an installed copy of ASCII Vision (same as Settings > Apps > ASCII Vision > Uninstall).
setlocal
set "TARGET=%LOCALAPPDATA%\Programs\ASCII Vision\uninstall.ps1"
if not exist "%TARGET%" (
    echo ASCII Vision is not installed in "%LOCALAPPDATA%\Programs\ASCII Vision".
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%TARGET%" %*
