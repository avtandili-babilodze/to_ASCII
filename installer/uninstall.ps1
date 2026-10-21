<#
.SYNOPSIS
    Removes ASCII Vision (app files, the FFmpeg copy the installer downloaded, shortcuts and the
    "Installed apps" entry). Shared prerequisites (.NET runtime, Visual C++ runtime) are left in
    place because other programs may use them.

.PARAMETER RemoveUserData
    Also delete settings (%APPDATA%\AsciiVision) and logs (%LOCALAPPDATA%\AsciiVision).

.PARAMETER Quiet
    Do not ask questions.
#>
[CmdletBinding()]
param(
    [switch]$RemoveUserData,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$AppName      = 'ASCII Vision'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AsciiVision'
$InstallDir   = $PSScriptRoot

try {
    if (-not $Quiet) {
        $answer = Read-Host "Remove $AppName from $InstallDir ? [Y/n]"
        if ($answer -match '^[nN]') { exit 0 }
        if (-not $RemoveUserData) {
            $answer = Read-Host 'Also delete your settings and logs? [y/N]'
            $RemoveUserData = $answer -match '^[yY]'
        }
    }

    Get-Process -Name AsciiVision -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force

    foreach ($shortcut in @(
            (Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"),
            (Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"))) {
        if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
    }

    if (Test-Path $UninstallKey) { Remove-Item $UninstallKey -Recurse -Force }

    if ($RemoveUserData) {
        foreach ($dir in @((Join-Path $env:APPDATA 'AsciiVision'), (Join-Path $env:LOCALAPPDATA 'AsciiVision'))) {
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
        }
    }

    # This script lives in the folder being deleted; PowerShell has already read it, so that is fine.
    Set-Location $env:TEMP
    if (Test-Path (Join-Path $InstallDir 'AsciiVision.exe')) {
        Remove-Item $InstallDir -Recurse -Force
    }

    Write-Host "$AppName was removed." -ForegroundColor Green
    if (-not $Quiet) { Start-Sleep -Seconds 2 }
    exit 0
} catch {
    Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
    if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}
