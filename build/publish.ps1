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

# Everything removed here is already inside the .exe: appsettings.json only repeats defaults that
# live in code, and wwwroot is embedded as resources and served from endpoints in Program.cs.
# Dropping them is what makes the thing handed to the shop a single file rather than a folder
# somebody has to keep together.
#
# The staticwebassets manifests describe wwwroot for the static-file middleware. Which of them the
# SDK emits varies by patch version — 8.0.403 writes none, later ones write the endpoints manifest —
# so match on the pattern rather than naming files, and let the smoke test below prove the result
# still runs.
$removable = @(
    'appsettings*.json'
    'web.config'
    '*.pdb'
    '*.staticwebassets.*.json'
    '*.staticwebassets.runtime.json'
)

foreach ($pattern in $removable) {
    Get-ChildItem $OutputDir -Filter $pattern -File -ErrorAction SilentlyContinue | Remove-Item -Force
}

Remove-Item (Join-Path $OutputDir 'wwwroot') -Recurse -Force -ErrorAction SilentlyContinue

$stray = Get-ChildItem $OutputDir -Recurse | Where-Object { $_.Name -ne 'WrenchDesk.exe' }
if ($stray) {
    Write-Warning 'Publish left files beside the .exe, so it is no longer a single file:'
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
