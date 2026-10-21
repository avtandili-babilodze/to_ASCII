<#
.SYNOPSIS
    One-click setup from a cloned (or "Download ZIP") copy of the repository.

.DESCRIPTION
    1. If a GitHub Release with the same version as this source exists, downloads its ready-made
       build (fast, about 42 MB) and verifies its SHA-256 checksum.
    2. Otherwise builds the app from this source code. If no .NET 9 (or newer) SDK is installed,
       the SDK is installed for the current user first (no administrator rights needed).
       Source builds are self-contained, so the .NET runtime does not have to be installed separately.
    3. Runs installer\install.ps1: lets you choose the install folder, checks/installs the remaining
       requirements (Visual C++ runtime, FFmpeg) and creates desktop and Start menu shortcuts.

.EXAMPLE
    .\installer\setup.ps1
.EXAMPLE
    .\installer\setup.ps1 -FromSource -InstallDir "D:\Apps\ASCII Vision" -NoLaunch
#>
[CmdletBinding()]
param(
    [switch]$FromSource,            # always build from source, even if a release exists
    [string]$InstallDir,            # skip the folder picker and install here
    [switch]$NoDesktopShortcut,
    [switch]$NoLaunch,
    [string]$Repository = 'avtandili-babilodze/to_ASCII'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$RepoRoot = Split-Path $PSScriptRoot -Parent
$Project = Join-Path $RepoRoot 'src\AsciiVision.App\AsciiVision.App.csproj'
$UserDotnetDir = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'

function Write-Step([string]$Text) { Write-Host ''; Write-Host "==> $Text" -ForegroundColor Cyan }
function Write-Ok([string]$Text)   { Write-Host "    [OK] $Text" -ForegroundColor Green }
function Write-Info([string]$Text) { Write-Host "    $Text" }
function Write-Warn([string]$Text) { Write-Host "    [!] $Text" -ForegroundColor Yellow }

function Get-SourceVersion {
    [xml]$props = Get-Content (Join-Path $RepoRoot 'Directory.Build.props')
    return ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
}

# ---------------------------------------------------------------- Prebuilt release

# Returns the folder of an extracted release package, or $null when no matching release exists.
function Get-ReleasePackage([string]$Version) {
    Write-Step "Looking for a ready-made build of version $Version on GitHub"
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/latest" -Headers @{ 'User-Agent' = 'AsciiVision-Setup' }
    } catch {
        Write-Info 'No published release found - the app will be built from source.'
        return $null
    }

    if ($release.tag_name.TrimStart('v', 'V') -ne $Version) {
        Write-Info "The latest release is $($release.tag_name), but this source is $Version - building from source instead."
        return $null
    }

    $zipAsset = $release.assets | Where-Object { $_.name -like 'AsciiVision-*-win-x64.zip' } | Select-Object -First 1
    if (-not $zipAsset) {
        Write-Info 'The release has no Windows package attached - building from source.'
        return $null
    }

    $work = Join-Path $env:TEMP ('AsciiVision-setup-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $work | Out-Null
    $zip = Join-Path $work $zipAsset.name
    Write-Info "Downloading $($zipAsset.name) ($([math]::Round($zipAsset.size / 1MB, 1)) MB)"
    Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zip -UseBasicParsing

    $shaAsset = $release.assets | Where-Object { $_.name -eq "$($zipAsset.name).sha256" } | Select-Object -First 1
    if ($shaAsset) {
        $expected = ((Invoke-WebRequest -Uri $shaAsset.browser_download_url -UseBasicParsing).Content.ToString().Trim() -split '\s+')[0]
        $actual = (Get-FileHash $zip -Algorithm SHA256).Hash
        if ($expected -ne $actual) { throw 'The downloaded package does not match its published SHA-256 checksum.' }
        Write-Ok 'Checksum verified'
    }

    Expand-Archive -Path $zip -DestinationPath $work -Force
    $installer = Get-ChildItem $work -Recurse -Filter install.ps1 | Select-Object -First 1
    if (-not $installer) { throw 'The release package does not contain install.ps1.' }
    Write-Ok 'Release package ready'
    return $installer.DirectoryName
}

# ---------------------------------------------------------------- Build from source

function Find-Dotnet {
    $candidates = @(
        (Get-Command dotnet.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'dotnet.exe' }),
        (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'),
        (Join-Path $UserDotnetDir 'dotnet.exe')
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique

    foreach ($dotnet in $candidates) {
        $sdks = & $dotnet --list-sdks 2>$null
        foreach ($line in $sdks) {
            $major = 0
            if ([int]::TryParse(($line -split '\.')[0], [ref]$major) -and $major -ge 9) { return $dotnet }
        }
    }
    return $null
}

function Install-DotnetSdk {
    Write-Info 'No .NET 9 SDK found - installing it for your user account (no administrator rights needed, about 250 MB).'
    $script = Join-Path $env:TEMP 'dotnet-install.ps1'
    Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
    & $script -Channel 9.0 -InstallDir $UserDotnetDir -NoPath | Out-Host
    Remove-Item $script -ErrorAction SilentlyContinue
    $dotnet = Join-Path $UserDotnetDir 'dotnet.exe'
    if (-not (Test-Path $dotnet)) { throw '.NET SDK installation failed.' }
    return $dotnet
}

function Build-FromSource {
    Write-Step 'Building ASCII Vision from source'
    if (-not (Test-Path $Project)) { throw "Project file not found: $Project. Run this script from a complete copy of the repository." }

    $dotnet = Find-Dotnet
    if ($dotnet) { Write-Ok ".NET SDK: $dotnet" } else { $dotnet = Install-DotnetSdk; Write-Ok ".NET SDK installed: $dotnet" }

    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_NOLOGO = '1'
    Write-Info 'Compiling (the first build downloads packages and takes a few minutes)...'
    # Self-contained: the built app carries its own .NET runtime.
    # Out-Host keeps the build log out of this function's return value.
    & $dotnet publish $Project -p:PublishProfile=win-x64-selfcontained -nologo -v:minimal | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit code $LASTEXITCODE). See the messages above." }

    $output = Join-Path $RepoRoot 'artifacts\publish\win-x64-selfcontained'
    if (-not (Test-Path (Join-Path $output 'AsciiVision.exe'))) { throw "Build output not found in $output." }
    Write-Ok "Built: $output"
    return $output
}

# ---------------------------------------------------------------- Main

try {
    Write-Host ''
    Write-Host '  ASCII Vision setup' -ForegroundColor White
    Write-Host '  ------------------'
    $version = Get-SourceVersion

    $installerArgs = @{}
    if ($InstallDir) { $installerArgs.InstallDir = $InstallDir } else { $installerArgs.ChooseLocation = $true }
    if ($NoDesktopShortcut) { $installerArgs.NoDesktopShortcut = $true }
    if ($NoLaunch) { $installerArgs.NoLaunch = $true }

    $package = if ($FromSource) { $null } else { Get-ReleasePackage $version }
    if ($package) {
        & (Join-Path $package 'install.ps1') @installerArgs
    } else {
        $appSource = Build-FromSource
        & (Join-Path $PSScriptRoot 'install.ps1') @installerArgs -AppSource $appSource -Version $version
    }
    exit $LASTEXITCODE
} catch {
    Write-Host ''
    Write-Host "  Setup failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
