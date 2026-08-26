<#
    Installs WrenchDesk on the shop PC.

    Copies the program somewhere permanent and creates the shortcuts:
      - Desktop
      - Start menu (so it appears in search, and can be pinned)
      - Optionally, start automatically when Windows starts

    Installs per-user under %LOCALAPPDATA% by default, which needs no administrator rights and no
    UAC prompt. Pass -InstallDir to put it somewhere else.

    Usage:  right-click "Install WrenchDesk.cmd" and choose Run
       or:  powershell -ExecutionPolicy Bypass -File build\install.ps1
#>

[CmdletBinding()]
param(
    [string]$InstallDir,
    [switch]$StartWithWindows,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$AppName = 'WrenchDesk'
$ShortcutName = "WrenchDesk - Walt's Small Engines"

function Say([string]$text, [string]$colour = 'Gray') {
    if (-not $Quiet) { Write-Host $text -ForegroundColor $colour }
}

# Read-Host throws when there is no console to read from — running this from another script or a
# build step should fall back to the default rather than blowing up.
function AskYesNo([string]$question, [bool]$defaultYes) {
    if ($Quiet) { return $defaultYes }

    try {
        $answer = Read-Host $question
    }
    catch {
        # No console to ask on. Take no action rather than guessing on someone's behalf.
        return $false
    }

    if ([string]::IsNullOrWhiteSpace($answer)) { return $defaultYes }
    return $answer -match '^(y|yes)$'
}

# The published files sit next to this script when it ships inside the output folder, and one
# level down in build\output when run straight from a source checkout.
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$candidates = @(
    $scriptDir,
    (Join-Path $scriptDir 'output'),
    (Join-Path (Split-Path -Parent $scriptDir) 'build\output')
)

$sourceDir = $null
foreach ($candidate in $candidates) {
    if ($candidate -and (Test-Path (Join-Path $candidate 'WrenchDesk.exe'))) {
        $sourceDir = (Resolve-Path $candidate).Path
        break
    }
}

if (-not $sourceDir) {
    Write-Error "Could not find WrenchDesk.exe. Run build\publish.ps1 first, then run this again."
    exit 1
}

if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    $InstallDir = Join-Path $env:LOCALAPPDATA $AppName
}

Say ''
Say "  Installing WrenchDesk" 'Cyan'
Say "  From: $sourceDir"
Say "  To:   $InstallDir"
Say ''

# A running copy holds a lock on the .exe, so stop it before overwriting.
$running = Get-Process -Name 'WrenchDesk' -ErrorAction SilentlyContinue
if ($running) {
    Say '  WrenchDesk is running. Closing it...' 'Yellow'
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

# Installing on top of an existing copy must not touch the shop's database, which lives in
# Documents, not here — so replacing the program folder is safe.
if ($sourceDir -ne $InstallDir) {
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path (Join-Path $sourceDir '*') -Destination $InstallDir -Recurse -Force
    Say '  Program files copied.' 'Green'
}
else {
    Say '  Already running from the install folder; nothing to copy.'
}

$exePath = Join-Path $InstallDir 'WrenchDesk.exe'

function New-Shortcut([string]$linkPath, [string]$target, [string]$workingDir, [string]$description) {
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($linkPath)
        $shortcut.TargetPath = $target
        $shortcut.WorkingDirectory = $workingDir
        $shortcut.Description = $description
        $shortcut.IconLocation = "$target,0"
        $shortcut.Save()
    }
    finally {
        [System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
    }
}

# ---- Desktop ----
$desktop = [Environment]::GetFolderPath('Desktop')
$desktopLink = Join-Path $desktop "$ShortcutName.lnk"
New-Shortcut $desktopLink $exePath $InstallDir 'Open the shop app'
Say "  Desktop shortcut created." 'Green'

# ---- Start menu ----
# Pinning to the taskbar is done from the Start menu entry, so this one is not optional.
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
$startLink = Join-Path $startMenu "$ShortcutName.lnk"
New-Shortcut $startLink $exePath $InstallDir 'Open the shop app'
Say "  Start menu shortcut created." 'Green'

# ---- Start with Windows ----
$startupDir = [Environment]::GetFolderPath('Startup')
$startupLink = Join-Path $startupDir "$ShortcutName.lnk"

if ($StartWithWindows -or (AskYesNo '  Start WrenchDesk automatically when Windows starts? (y/N)' $false)) {
    New-Shortcut $startupLink $exePath $InstallDir 'Open the shop app'
    Say '  Will start automatically with Windows.' 'Green'
}
elseif (Test-Path $startupLink) {
    # Answering no on a reinstall should actually turn it off.
    Remove-Item $startupLink -Force
}

# ---- Pinning ----
# Windows deliberately stops a program pinning itself. On Windows 11 the shell lists "Pin to
# taskbar" and "Pin to Start" but invoking either from a script returns E_ACCESSDENIED; only
# enterprise policy can place a pin. The attempt is still worth making because it does work on
# older builds — but the install must not pretend it succeeded when it did not.
function Try-Pin([string]$linkPath, [string]$verbPattern) {
    try {
        $shellApp = New-Object -ComObject Shell.Application
        $folder = $shellApp.Namespace((Split-Path -Parent $linkPath))
        $item = $folder.ParseName((Split-Path -Leaf $linkPath))
        if (-not $item) { return $false }

        foreach ($verb in $item.Verbs()) {
            if (($verb.Name -replace '&', '') -match $verbPattern) {
                $verb.DoIt()
                return $true
            }
        }
    }
    catch {
        # Blocked by Windows. Fall through and tell the user how to do it by hand.
    }

    return $false
}

$pinnedTaskbar = Try-Pin $startLink '^Pin to tas?k ?bar$'
$pinnedStart = Try-Pin $startLink '^Pin to Start'

Say ''
if ($pinnedStart) { Say '  Pinned to the Start menu.' 'Green' }

if ($pinnedTaskbar) {
    Say '  Pinned to the taskbar.' 'Green'
}
else {
    Say '  Taskbar pin - one manual step:' 'Yellow'
    Say '    Windows does not allow a program to pin itself to the taskbar, so this last'
    Say '    bit has to be done by hand. It is one right-click:'
    Say ''
    Say '      Right-click the desktop icon  ->  Show more options  ->  Pin to taskbar' 'White'
    Say ''
    Say '    Or: press Start, type WrenchDesk, right-click it, choose Pin to taskbar.'
    Say '    Dragging the desktop icon onto the taskbar works too.'
}

Say ''
Say '  Done.' 'Green'
Say "  Installed to: $InstallDir"
Say '  Your shop data stays in Documents\WrenchDesk and was not touched.'
Say ''

if (-not $Quiet -and (AskYesNo '  Start WrenchDesk now? (Y/n)' $true)) {
    Start-Process -FilePath $exePath -WorkingDirectory $InstallDir
    Say '  Started. Leave the console window open while the shop is using it.' 'Green'
}
