@echo off
rem Double-click to install ASCII Vision from this copy of the repository.
rem Uses the matching GitHub release if one exists, otherwise builds from source.
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\setup.ps1" %*
set RESULT=%ERRORLEVEL%
echo.
pause
exit /b %RESULT%
