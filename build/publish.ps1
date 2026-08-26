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

$exe = Join-Path $OutputDir 'WrenchDesk.exe'
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)

Write-Host ''
Write-Host '  Done.' -ForegroundColor Green
Write-Host "  Program:  $exe  ($sizeMb MB)"
Write-Host ''
Write-Host '  Copy the whole output folder to the shop PC and double-click WrenchDesk.exe.'
Write-Host ''
