<#
    Removes WrenchDesk's shortcuts and program files.

    The shop's data is NOT touched. Customers, tickets, payments and backups live in
    Documents\WrenchDesk and are left exactly where they are, so reinstalling picks up
    right where things left off.

    Usage:  powershell -ExecutionPolicy Bypass -File build\uninstall.ps1
#>

[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$AppName = 'WrenchDesk'
$ShortcutName = "WrenchDesk - Walt's Small Engines"

function Say([string]$text, [string]$colour = 'Gray') {
    if (-not $Quiet) { Write-Host $text -ForegroundColor $colour }
}

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:LOCALAPPDATA $AppName
}

Say ''
Say '  Removing WrenchDesk' 'Cyan'
Say ''

$running = Get-Process -Name 'WrenchDesk' -ErrorAction SilentlyContinue
if ($running) {
    Say '  Closing WrenchDesk...' 'Yellow'
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

$links = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) "$ShortcutName.lnk"),
    (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$ShortcutName.lnk"),
    (Join-Path ([Environment]::GetFolderPath('Startup')) "$ShortcutName.lnk")
)

foreach ($link in $links) {
    if (Test-Path $link) {
        Remove-Item $link -Force
        Say "  Removed $(Split-Path -Leaf $link)" 'Green'
    }
}

if (Test-Path $InstallDir) {
    try {
        Remove-Item $InstallDir -Recurse -Force
        Say "  Removed program files from $InstallDir" 'Green'
    }
    catch {
        Say "  Could not remove $InstallDir - $($_.Exception.Message)" 'Yellow'
    }
}

Say ''
Say '  Done. Your shop data was left alone:' 'Green'
Say "    $([Environment]::GetFolderPath('MyDocuments'))\WrenchDesk"
Say ''
Say '  If WrenchDesk was pinned to the taskbar, right-click the pin and choose Unpin.'
Say ''
