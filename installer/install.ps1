<#
.SYNOPSIS
    Installs ASCII Vision for the current user and any missing prerequisites.

.DESCRIPTION
    Checks (and installs only when missing):
      * Windows 10 version 1903 (build 18362) or newer, 64-bit
      * .NET 9 Desktop Runtime (x64)
      * Microsoft Visual C++ 2015-2022 Redistributable (x64), needed by OpenCV
      * FFmpeg with an H.264 encoder (for MP4 playback, recording and export)
    Then copies the app to %LOCALAPPDATA%\Programs\ASCII Vision, creates Start menu and
    desktop shortcuts and registers an uninstaller in "Installed apps".

    The app itself needs no administrator rights. Installing the .NET runtime or the
    Visual C++ runtime shows a Windows (UAC) prompt because those are system-wide.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -NoDesktopShortcut -NoLaunch
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\ASCII Vision'),
    [switch]$NoDesktopShortcut,
    [switch]$SkipPrerequisites,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # the PowerShell 5.1 progress bar makes downloads very slow
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$AppName      = 'ASCII Vision'
$ExeName      = 'AsciiVision.exe'
$Publisher    = 'ASCII Vision'
$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AsciiVision'
$PackageRoot  = $PSScriptRoot
$AppSource    = Join-Path $PackageRoot 'app'
$Version      = if (Test-Path (Join-Path $PackageRoot 'version.txt')) { (Get-Content (Join-Path $PackageRoot 'version.txt') -Raw).Trim() } else { '1.0.0' }

$DotnetRuntimeUrl = 'https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe'
$VCRedistUrl      = 'https://aka.ms/vs/17/release/vc_redist.x64.exe'
$FfmpegZipUrl     = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip'

function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    [OK] $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "    $Text" }
function Write-Warn([string]$Text) { Write-Host "    [!] $Text" -ForegroundColor Yellow }

function Test-Command([string]$Name) { [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

function Get-File([string]$Url, [string]$Name) {
    $target = Join-Path $env:TEMP $Name
    Write-Info "Downloading $Url"
    Invoke-WebRequest -Uri $Url -OutFile $target -UseBasicParsing
    return $target
}

# Runs an installer elevated (UAC prompt) and treats "already installed" / "reboot needed" as success.
function Invoke-ElevatedInstaller([string]$Path, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -Verb RunAs -Wait -PassThru
    if ($process.ExitCode -notin 0, 1638, 1641, 3010) {
        throw "$([IO.Path]::GetFileName($Path)) exited with code $($process.ExitCode)."
    }
    if ($process.ExitCode -in 1641, 3010) { Write-Warn 'Windows reports that a restart is required to finish this installation.' }
}

# ---------------------------------------------------------------- Windows

function Test-WindowsVersion {
    Write-Step 'Checking Windows'
    if (-not [Environment]::Is64BitOperatingSystem) { throw 'ASCII Vision requires 64-bit Windows.' }
    $build = [Environment]::OSVersion.Version.Build
    if ($build -lt 18362) {
        throw "ASCII Vision requires Windows 10 version 1903 (build 18362) or newer. This PC runs build $build."
    }
    Write-Ok "Windows build $build (64-bit)"
}

# ---------------------------------------------------------------- .NET 9 Desktop Runtime

# The same places the app launcher (apphost) searches for the runtime.
function Get-DotnetRoots {
    $roots = @()
    foreach ($variable in 'DOTNET_ROOT_X64', 'DOTNET_ROOT') {
        $value = [Environment]::GetEnvironmentVariable($variable)
        if ($value) { $roots += $value }
    }
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64', 'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64') {
        $location = (Get-ItemProperty -Path $key -Name InstallLocation -ErrorAction SilentlyContinue).InstallLocation
        if ($location) { $roots += $location }
    }
    $roots += (Join-Path $env:ProgramFiles 'dotnet')
    return $roots | Select-Object -Unique
}

function Find-DesktopRuntime {
    foreach ($root in Get-DotnetRoots) {
        $shared = Join-Path $root 'shared\Microsoft.WindowsDesktop.App'
        if (Test-Path $shared) {
            $match = Get-ChildItem $shared -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like '9.*' } | Sort-Object { [version]($_.Name -replace '-.*$', '') } | Select-Object -Last 1
            if ($match) { return "$($match.Name) in $root" }
        }
    }
    return $null
}

function Install-DesktopRuntime {
    Write-Step 'Checking .NET 9 Desktop Runtime (x64)'
    $found = Find-DesktopRuntime
    if ($found) { Write-Ok ".NET Desktop Runtime $found"; return }

    Write-Info 'Not found - installing (a Windows permission prompt will appear).'
    if (Test-Command 'winget') {
        & winget install --id Microsoft.DotNet.DesktopRuntime.9 --architecture x64 --exact --silent --accept-package-agreements --accept-source-agreements
    }
    if (-not (Find-DesktopRuntime)) {
        $installer = Get-File $DotnetRuntimeUrl 'windowsdesktop-runtime-9-win-x64.exe'
        Invoke-ElevatedInstaller $installer @('/install', '/quiet', '/norestart')
        Remove-Item $installer -ErrorAction SilentlyContinue
    }
    $found = Find-DesktopRuntime
    if (-not $found) { throw '.NET 9 Desktop Runtime installation could not be verified. Install it from https://dotnet.microsoft.com/download/dotnet/9.0 and run this installer again.' }
    Write-Ok ".NET Desktop Runtime $found"
}

# ---------------------------------------------------------------- Visual C++ runtime

function Get-VCRedistVersion {
    foreach ($key in 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x64') {
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($item -and $item.Installed -eq 1) { return [version]"$($item.Major).$($item.Minor).$($item.Bld)" }
    }
    return $null
}

function Install-VCRedist {
    Write-Step 'Checking Microsoft Visual C++ 2015-2022 Redistributable (x64)'
    $minimum = [version]'14.30.0'   # OpenCV's native library is built with Visual Studio 2022
    $current = Get-VCRedistVersion
    if ($current -and $current -ge $minimum) { Write-Ok "Visual C++ runtime $current"; return }

    if ($current) { Write-Info "Version $current is older than $minimum - updating (a Windows permission prompt will appear)." }
    else { Write-Info 'Not found - installing (a Windows permission prompt will appear).' }
    $installer = Get-File $VCRedistUrl 'vc_redist.x64.exe'
    Invoke-ElevatedInstaller $installer @('/install', '/quiet', '/norestart')
    Remove-Item $installer -ErrorAction SilentlyContinue
    $current = Get-VCRedistVersion
    if (-not $current) { throw 'Visual C++ runtime installation could not be verified.' }
    Write-Ok "Visual C++ runtime $current"
}

# ---------------------------------------------------------------- FFmpeg

# Returns the path of a working ffmpeg.exe that has ffprobe.exe next to it and an H.264 encoder.
function Test-Ffmpeg([string]$Path) {
    if (-not $Path -or -not (Test-Path $Path)) { return $false }
    if (-not (Test-Path (Join-Path (Split-Path $Path) 'ffprobe.exe'))) { return $false }
    try {
        $encoders = & $Path -hide_banner -encoders 2>$null | Out-String
        return ($encoders -match ' libx264 ' -or $encoders -match ' h264_mf ')
    } catch { return $false }
}

function Find-Ffmpeg {
    $candidates = @(
        (Join-Path $InstallDir 'ffmpeg\bin\ffmpeg.exe'),
        (Get-Command ffmpeg.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links\ffmpeg.exe'),
        (Join-Path $env:ProgramData 'chocolatey\bin\ffmpeg.exe'),
        (Join-Path $env:USERPROFILE 'scoop\shims\ffmpeg.exe'),
        'C:\ffmpeg\bin\ffmpeg.exe',
        (Join-Path $env:ProgramFiles 'ffmpeg\bin\ffmpeg.exe')
    )
    foreach ($candidate in $candidates) {
        if (Test-Ffmpeg $candidate) { return $candidate }
    }
    return $null
}

# Downloads a full FFmpeg build into the app folder (no PATH changes; removed by the uninstaller).
function Install-FfmpegLocal {
    $zip = Get-File $FfmpegZipUrl 'ffmpeg-release-essentials.zip'
    $extract = Join-Path $env:TEMP ('ffmpeg-' + [guid]::NewGuid().ToString('N'))
    try {
        Expand-Archive -Path $zip -DestinationPath $extract -Force
        $exe = Get-ChildItem $extract -Recurse -Filter ffmpeg.exe | Select-Object -First 1
        if (-not $exe) { throw 'The downloaded archive does not contain ffmpeg.exe.' }
        $target = Join-Path $InstallDir 'ffmpeg\bin'
        New-Item -ItemType Directory -Force $target | Out-Null
        # Only the two tools the app runs (the build also contains ffplay, which is not needed).
        foreach ($tool in 'ffmpeg.exe', 'ffprobe.exe') { Copy-Item (Join-Path $exe.DirectoryName $tool) $target -Force }
        $license = Get-ChildItem $extract -Recurse -Filter 'LICENSE*' | Select-Object -First 1
        if ($license) { Copy-Item $license.FullName (Join-Path $InstallDir 'ffmpeg') -Force }
    } finally {
        Remove-Item $zip -ErrorAction SilentlyContinue
        Remove-Item $extract -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Install-Ffmpeg {
    Write-Step 'Checking FFmpeg (needed for MP4 playback, recording and export)'
    $found = Find-Ffmpeg
    if ($found) { Write-Ok "FFmpeg at $found"; return }

    Write-Info 'Not found - downloading a full build from gyan.dev (about 100 MB) into the app folder.'
    try {
        Install-FfmpegLocal
    } catch {
        Write-Warn "Download failed: $($_.Exception.Message)"
        if (Test-Command 'winget') {
            Write-Info 'Trying winget instead.'
            & winget install --id Gyan.FFmpeg --exact --silent --accept-package-agreements --accept-source-agreements
        }
    }
    $found = Find-Ffmpeg
    if ($found) { Write-Ok "FFmpeg at $found" }
    else { Write-Warn 'FFmpeg could not be installed. Screen and window capture still work; for MP4 features install FFmpeg later (winget install Gyan.FFmpeg) or choose ffmpeg.exe in the app under Settings > FFmpeg.' }
}

# ---------------------------------------------------------------- App files, shortcuts, uninstaller

function Stop-RunningApp {
    $running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        Write-Warn 'ASCII Vision is running; closing it to update the files.'
        $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }
        Start-Sleep -Seconds 3
        $running | Where-Object { -not $_.HasExited } | Stop-Process -Force
    }
}

function Copy-AppFiles {
    Write-Step "Installing $AppName $Version to $InstallDir"
    if (-not (Test-Path (Join-Path $AppSource $ExeName))) {
        throw "The 'app' folder next to this script is missing or incomplete. Extract the whole zip before running the installer."
    }
    $isPreviousInstall = Test-Path (Join-Path $InstallDir $ExeName)
    if ((Test-Path $InstallDir) -and -not $isPreviousInstall -and (Get-ChildItem $InstallDir -Force | Select-Object -First 1)) {
        throw "'$InstallDir' already contains other files. Choose an empty folder with -InstallDir."
    }
    Stop-RunningApp
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    if ($isPreviousInstall) {
        # Remove files of the previous version, but keep an FFmpeg that the installer downloaded earlier.
        Get-ChildItem $InstallDir -Force | Where-Object { $_.Name -ne 'ffmpeg' } | Remove-Item -Recurse -Force
    }
    Copy-Item (Join-Path $AppSource '*') $InstallDir -Recurse -Force
    Copy-Item (Join-Path $PackageRoot 'uninstall.ps1') $InstallDir -Force
    Set-Content -Path (Join-Path $InstallDir 'version.txt') -Value $Version -Encoding ASCII
    # Files extracted from a downloaded zip carry the "from the internet" mark; clear it for the installed copy.
    Get-ChildItem $InstallDir -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
    Write-Ok 'Files copied'
}

function New-Shortcut([string]$Path, [string]$Target, [string]$WorkingDirectory) {
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    $shortcut.TargetPath = $Target
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$Target,0"
    $shortcut.Description = 'Real-time ASCII video engine'
    $shortcut.Save()
}

function New-Shortcuts {
    Write-Step 'Creating shortcuts'
    $exe = Join-Path $InstallDir $ExeName
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
    New-Shortcut $startMenu $exe $InstallDir
    Write-Ok "Start menu: $startMenu"
    if (-not $NoDesktopShortcut) {
        $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$AppName.lnk"
        New-Shortcut $desktop $exe $InstallDir
        Write-Ok "Desktop: $desktop"
    }
}

function Register-Uninstaller {
    Write-Step 'Registering in Settings > Apps'
    $uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'uninstall.ps1')`""
    $sizeKb = [int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    New-Item -Path $UninstallKey -Force | Out-Null
    $values = @{
        DisplayName     = $AppName
        DisplayVersion  = $Version
        Publisher       = $Publisher
        InstallLocation = $InstallDir
        DisplayIcon     = (Join-Path $InstallDir $ExeName)
        UninstallString = $uninstall
        QuietUninstallString = "$uninstall -Quiet"
        InstallDate     = (Get-Date -Format 'yyyyMMdd')
    }
    foreach ($name in $values.Keys) { Set-ItemProperty -Path $UninstallKey -Name $name -Value $values[$name] }
    Set-ItemProperty -Path $UninstallKey -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallKey -Name NoRepair -Value 1 -Type DWord
    Set-ItemProperty -Path $UninstallKey -Name EstimatedSize -Value $sizeKb -Type DWord
    Write-Ok 'Uninstall entry created'
}

# ---------------------------------------------------------------- Main

try {
    Write-Host ''
    Write-Host "  ASCII Vision $Version installer" -ForegroundColor White
    Write-Host '  ---------------------------------'
    Test-WindowsVersion
    if ($SkipPrerequisites) {
        Write-Warn 'Skipping prerequisite checks (-SkipPrerequisites).'
    } else {
        Install-DesktopRuntime
        Install-VCRedist
    }
    Copy-AppFiles
    if (-not $SkipPrerequisites) { Install-Ffmpeg }   # after the copy so it can go into the app folder
    New-Shortcuts
    Register-Uninstaller

    Write-Host ''
    Write-Host "  $AppName was installed successfully." -ForegroundColor Green
    Write-Host "  Start it from the desktop or Start menu shortcut. Uninstall from Settings > Apps."
    if (-not $NoLaunch) { Start-Process (Join-Path $InstallDir $ExeName) -WorkingDirectory $InstallDir }
    exit 0
} catch {
    Write-Host ''
    Write-Host "  Installation failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
