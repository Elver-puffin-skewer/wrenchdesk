<#
    Proves the published WrenchDesk.exe actually works on its own.

    Counting files only shows nothing is sitting beside the .exe; it does not show the .exe still
    serves the pages once those files are gone. This starts the real program in portable mode
    against a throwaway data folder and asks it for the pages that matter — including the ones
    that used to come out of wwwroot.

    Usage:  pwsh build\smoke-test.ps1
#>

[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$Port = 5399,
    [int]$TimeoutSeconds = 90
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $scriptDir 'output\WrenchDesk.exe'
}

if (-not (Test-Path $ExePath)) {
    Write-Error "No published program at $ExePath. Run build\publish.ps1 first."
    exit 1
}

$dataDir = Join-Path ([System.IO.Path]::GetTempPath()) "wrenchdesk-smoke-$([Guid]::NewGuid().ToString('N'))"

Write-Host ''
Write-Host '  Smoke testing the published program' -ForegroundColor Cyan
Write-Host "  Program: $ExePath"
Write-Host "  Data:    $dataDir"
Write-Host ''

# --portable stops it installing itself onto the build machine.
$env:WrenchDesk__Port = "$Port"
$env:WrenchDesk__OpenBrowser = 'false'
$env:WrenchDesk__DataDirectory = $dataDir

$process = Start-Process -FilePath $ExePath -ArgumentList '--portable' -PassThru -WindowStyle Hidden
$failures = @()

try {
    $base = "http://localhost:$Port"
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $ready = $false

    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            Write-Error "The program exited on its own with code $($process.ExitCode)."
            exit 1
        }

        try {
            Invoke-WebRequest -Uri "$base/" -UseBasicParsing -TimeoutSec 5 | Out-Null
            $ready = $true
            break
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }

    if (-not $ready) {
        Write-Error "The program never started serving on $base within $TimeoutSeconds seconds."
        exit 1
    }

    # Every page, plus the assets that used to be files in wwwroot. If embedding them broke,
    # the app.css request is what catches it.
    $checks = @(
        @{ Path = '/';                        Name = 'Dashboard' }
        @{ Path = '/customers';               Name = 'Customers' }
        @{ Path = '/tickets';                 Name = 'Tickets' }
        @{ Path = '/schedule';                Name = 'Schedule' }
        @{ Path = '/money';                   Name = 'Money' }
        @{ Path = '/settings';                Name = 'Settings' }
        @{ Path = '/help';                    Name = 'Help' }
        @{ Path = '/app.css';                 Name = 'Stylesheet (embedded)' }
        @{ Path = '/favicon.ico';             Name = 'Icon (embedded)' }
        @{ Path = '/_framework/blazor.web.js'; Name = 'Blazor script (framework)' }
        @{ Path = '/export/payments.csv';     Name = 'CSV export' }
        @{ Path = '/export/schedule.ics?days=30'; Name = 'Calendar export' }
    )

    foreach ($check in $checks) {
        try {
            $response = Invoke-WebRequest -Uri "$base$($check.Path)" -UseBasicParsing -TimeoutSec 15

            if ($response.StatusCode -ne 200) {
                $failures += "$($check.Name) returned $($response.StatusCode)"
                Write-Host "  FAIL  $($check.Name) -> $($response.StatusCode)" -ForegroundColor Red
            }
            elseif ($response.RawContentLength -eq 0 -and $response.Content.Length -eq 0) {
                $failures += "$($check.Name) returned nothing"
                Write-Host "  FAIL  $($check.Name) -> empty" -ForegroundColor Red
            }
            else {
                $size = if ($response.RawContentLength -gt 0) { $response.RawContentLength } else { $response.Content.Length }
                Write-Host "  ok    $($check.Name) ($size bytes)" -ForegroundColor Green
            }
        }
        catch {
            $failures += "$($check.Name): $($_.Exception.Message)"
            Write-Host "  FAIL  $($check.Name) -> $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    # The shop's database must have been created and migrated by the run above.
    $db = Join-Path $dataDir 'wrenchdesk.db'
    if (Test-Path $db) {
        Write-Host "  ok    Database created ($((Get-Item $db).Length) bytes)" -ForegroundColor Green
    }
    else {
        $failures += 'No database was created'
        Write-Host '  FAIL  No database was created' -ForegroundColor Red
    }
}
finally {
    if ($process -and -not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit(10000) | Out-Null
    }
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "  $($failures.Count) check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host '  All checks passed. The single .exe works on its own.' -ForegroundColor Green
Write-Host ''
