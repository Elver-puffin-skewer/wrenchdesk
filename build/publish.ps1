<#
    Builds the shop-ready copy of WrenchDesk.

    Produces build\output\WrenchDesk.exe — a single self-contained program. The shop PC does not
    need the .NET runtime, Node, or anything else installed; copy the folder across and run it.

    Usage:   pwsh build\publish.ps1
#>

[CmdletBinding()]
param(
    [string]$Runtime = 'win-x64',
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 does not populate $PSScriptRoot in the param block, so resolve it here.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir
$project = Join-Path $repoRoot 'WrenchDesk.csproj'

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $scriptDir 'output'
}

Write-Host ''
Write-Host '  Building WrenchDesk...' -ForegroundColor Cyan
Write-Host ''

# A stale exe from a previous run will be locked if the app is still open.
$running = Get-Process -Name 'WrenchDesk' -ErrorAction SilentlyContinue
if ($running) {
    Write-Warning 'WrenchDesk is currently running. Close it and run this again.'
    exit 1
}

if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}

dotnet publish $project `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    --output $OutputDir

if ($LASTEXITCODE -ne 0) {
    Write-Error 'Publish failed.'
    exit $LASTEXITCODE
}

# The published folder carries a debug symbols file that the shop does not need.
Get-ChildItem $OutputDir -Filter '*.pdb' -ErrorAction SilentlyContinue | Remove-Item -Force

# Everything left here is already compiled into the .exe: appsettings.json only repeats defaults
# that live in code, and wwwroot is embedded as resources. Removing them is what makes the thing
# handed to the shop a single file rather than a folder they have to keep together.
foreach ($leftover in @('appsettings.json', 'appsettings.Development.json', 'web.config')) {
    Remove-Item (Join-Path $OutputDir $leftover) -Force -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path $OutputDir 'wwwroot') -Recurse -Force -ErrorAction SilentlyContinue

$stray = Get-ChildItem $OutputDir | Where-Object { $_.Name -ne 'WrenchDesk.exe' }
if ($stray) {
    Write-Warning "Publish left files beside the .exe, so it is no longer self-contained:"
    $stray | ForEach-Object { Write-Warning "  $($_.Name)" }
}

$exe = Join-Path $OutputDir 'WrenchDesk.exe'
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ''
Write-Host '  Done.' -ForegroundColor Green
Write-Host "  Program:  $exe  ($sizeMb MB)"
Write-Host ''
Write-Host '  That one file is everything. Give WrenchDesk.exe to the shop and double-click it —'
Write-Host '  it sets itself up, makes the desktop icon, and starts.'
Write-Host ''
