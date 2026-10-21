<#
.SYNOPSIS
    Builds the distributable package: release\AsciiVision-<version>-win-x64\ (installer folder)
    and release\AsciiVision-<version>-win-x64.zip (+ .sha256) for a GitHub release.

.EXAMPLE
    .\build-release.ps1
.EXAMPLE
    .\build-release.ps1 -SkipTests
#>
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Version not found in Directory.Build.props.' }
$name = "AsciiVision-$version-win-x64"
$releaseDir = Join-Path $root 'release'
$stage = Join-Path $releaseDir $name
$zip = Join-Path $releaseDir "$name.zip"
$publishDir = Join-Path $root 'artifacts\publish\win-x64'

function Invoke-Checked([string]$Description, [scriptblock]$Command) {
    Write-Host "==> $Description" -ForegroundColor Cyan
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Description failed (exit code $LASTEXITCODE)." }
}

if (-not $SkipTests) {
    Invoke-Checked 'Running tests' { dotnet test (Join-Path $root 'AsciiVision.sln') -c Release }
}

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
Invoke-Checked 'Publishing (Release, win-x64, framework-dependent)' {
    dotnet publish (Join-Path $root 'src\AsciiVision.App\AsciiVision.App.csproj') -p:PublishProfile=win-x64
}

Write-Host "==> Assembling $stage" -ForegroundColor Cyan
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'app') | Out-Null
Copy-Item (Join-Path $publishDir '*') (Join-Path $stage 'app') -Recurse -Force
Get-ChildItem (Join-Path $stage 'app') -Recurse -Filter '*.pdb' | Remove-Item -Force
foreach ($file in 'Install.cmd', 'Uninstall.cmd', 'install.ps1', 'uninstall.ps1', 'README.txt') {
    Copy-Item (Join-Path $root "installer\$file") $stage -Force
}
Set-Content -Path (Join-Path $stage 'version.txt') -Value $version -Encoding ASCII
if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') $stage -Force }

Write-Host "==> Creating $zip" -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ASCII

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ''
Write-Host "Done: $zip ($sizeMb MB)" -ForegroundColor Green
Write-Host "SHA-256: $hash"
