#Requires -Version 7.0
<#
.SYNOPSIS
    Starts FinTrack locally: Azurite, the Api (Azure Functions host) and the Web app.

.DESCRIPTION
    1. Runs scripts/check-prereqs.ps1 and stops if anything required is missing.
    2. Creates src/Api/FinTrack.Api/local.settings.json from local.settings.example.json if it is missing.
    3. Starts Azurite (unless it already runs) with its data outside the repository.
    4. Starts the Api and the Web app with `dotnet run` and waits until both answer: the Api at
       http://localhost:7071 (the Functions SDK builds it and starts the Core Tools host, as Core
       Tools recommends for .NET isolated apps), the Web app at http://localhost:5180.
    5. Runs until Ctrl+C, or until one of them stops; then it stops everything it started,
       including child processes.

    MongoDB is not started here: it runs as a service (docs/LOCAL-SETUP.md).

.PARAMETER AzuriteLocation
    Folder for Azurite's data. Default: %LOCALAPPDATA%\FinTrack\azurite on Windows,
    $XDG_DATA_HOME/FinTrack/azurite (or ~/.local/share/FinTrack/azurite) elsewhere.
    It must be outside the repository.

.EXAMPLE
    pwsh scripts/run-local.ps1
#>
[CmdletBinding()]
param(
    [string] $AzuriteLocation
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'FinTrack.Tools.psm1') -Force

$repoRoot = Split-Path -Parent $PSScriptRoot
$apiDirectory = Join-Path $repoRoot 'src/Api/FinTrack.Api'
$webDirectory = Join-Path $repoRoot 'src/Web/FinTrack.Web'

$ApiPort = 7071
$WebPort = 5180
$AzuritePort = 10000
$ApiHealthUrl = "http://localhost:$ApiPort/api/v1/health"
$WebUrl = "http://localhost:$WebPort/"
$StartupTimeout = [TimeSpan]::FromMinutes(5)

function Write-Step([string] $Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

function Get-DataDirectory {
    if ($IsWindows) { return Join-Path $env:LOCALAPPDATA 'FinTrack' }
    $base = if ($env:XDG_DATA_HOME) { $env:XDG_DATA_HOME } else { Join-Path $HOME '.local/share' }
    return Join-Path $base 'FinTrack'
}

function Test-PortInUse([int] $Port) {
    $client = [System.Net.Sockets.TcpClient]::new()
    try { return $client.ConnectAsync('127.0.0.1', $Port).Wait(500) }
    catch { return $false }
    finally { $client.Dispose() }
}

# True once the URL answers with any HTTP status (the health check answers 503 while MongoDB is down).
function Test-Answers([string] $Url) {
    try {
        $null = Invoke-WebRequest $Url -SkipHttpErrorCheck -TimeoutSec 10
        return $true
    }
    catch {
        return $false
    }
}

function Stop-Started([System.Collections.Generic.List[object]] $Started) {
    for ($index = $Started.Count - 1; $index -ge 0; $index--) {
        $entry = $Started[$index]
        if (-not $entry.Process.HasExited) {
            try { $entry.Process.Kill($true) } catch { Write-Verbose "Stopping $($entry.Name): $($_.Exception.Message)" }
        }
    }
}

# 1. Prerequisites.
Write-Step 'Checking prerequisites'
& (Join-Path $PSScriptRoot 'check-prereqs.ps1')
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Fix the items above first (docs/LOCAL-SETUP.md).' -ForegroundColor Red
    exit 1
}

# 2. Local settings of the Api (ignored by git).
$settings = Join-Path $apiDirectory 'local.settings.json'
if (-not (Test-Path $settings)) {
    Copy-Item (Join-Path $apiDirectory 'local.settings.example.json') $settings
    Write-Step "Created $settings from the example. FinTrack__LocalOwnerId in it owns all local data: keep it once chosen."
}

# 3. Ports and data folder.
foreach ($port in $ApiPort, $WebPort) {
    if (Test-PortInUse $port) {
        Write-Host "Port $port is already in use. Stop the other instance (Api 7071, Web 5180), then run this again." -ForegroundColor Red
        exit 1
    }
}

if (-not $AzuriteLocation) {
    $AzuriteLocation = Join-Path (Get-DataDirectory) 'azurite'
}
$AzuriteLocation = [System.IO.Path]::GetFullPath($AzuriteLocation)
if ($AzuriteLocation.StartsWith([System.IO.Path]::GetFullPath($repoRoot), [StringComparison]::OrdinalIgnoreCase)) {
    Write-Host "Azurite's data must be outside the repository: $AzuriteLocation" -ForegroundColor Red
    exit 1
}

# Azurite runs on Node.js, and `dotnet run` of the Api starts `func`: when a tool was found only in
# its install folder, the started processes need its folder on PATH.
foreach ($tool in 'node', 'func') {
    $path = Find-Tool $tool
    if ($path -and -not (Get-Command $tool -CommandType Application -ErrorAction SilentlyContinue)) {
        $env:PATH = (Split-Path -Parent $path) + [System.IO.Path]::PathSeparator + $env:PATH
    }
}

$started = [System.Collections.Generic.List[object]]::new()
$exitCode = 0

try {
    # 4. Azurite, the Api and the Web app share this console, so Ctrl+C reaches all of them.
    if (Test-PortInUse $AzuritePort) {
        Write-Step "Azurite already runs on port $AzuritePort; using it"
    }
    else {
        New-Item -ItemType Directory -Force $AzuriteLocation | Out-Null
        Write-Step "Starting Azurite (data in $AzuriteLocation)"
        $azurite = Start-Process (Find-Tool 'azurite') -ArgumentList @('--silent', '--location', "`"$AzuriteLocation`"") -NoNewWindow -PassThru
        $started.Add([pscustomobject] @{ Name = 'Azurite'; Process = $azurite })
    }

    # Core Tools' default port is 7071, which the Web app's appsettings.json expects.
    Write-Step "Starting the Api (dotnet run, Functions host on port $ApiPort)"
    $api = Start-Process (Find-Tool 'dotnet') -ArgumentList @('run') -WorkingDirectory $apiDirectory -NoNewWindow -PassThru
    $started.Add([pscustomobject] @{ Name = 'Api'; Process = $api })

    Write-Step "Starting the Web app (dotnet run, port $WebPort)"
    $web = Start-Process (Find-Tool 'dotnet') -ArgumentList @('run', '--launch-profile', 'http') -WorkingDirectory $webDirectory -NoNewWindow -PassThru
    $started.Add([pscustomobject] @{ Name = 'Web'; Process = $web })

    # 5. Wait until both answer (the first run also builds both projects).
    $deadline = [DateTime]::UtcNow + $StartupTimeout
    $apiReady = $false
    $webReady = $false
    while (-not ($apiReady -and $webReady)) {
        if ($started | Where-Object { $_.Process.HasExited }) { break }
        if ([DateTime]::UtcNow -gt $deadline) {
            Write-Host "Not ready after $($StartupTimeout.TotalMinutes) minutes; see the output above." -ForegroundColor Yellow
            break
        }

        Start-Sleep -Seconds 2
        if (-not $apiReady) { $apiReady = Test-Answers $ApiHealthUrl }
        if (-not $webReady) { $webReady = Test-Answers $WebUrl }
    }

    if ($apiReady -and $webReady) {
        Write-Host ''
        Write-Host "FinTrack is running. Open $WebUrl (Api health: $ApiHealthUrl)." -ForegroundColor Green
        Write-Host 'Press Ctrl+C to stop everything.' -ForegroundColor Green
        Write-Host ''
    }

    # 6. Run until Ctrl+C or until one of them stops.
    while ($true) {
        $stopped = @($started | Where-Object { $_.Process.HasExited })
        if ($stopped.Count -gt 0) {
            foreach ($entry in $stopped) {
                Write-Host "$($entry.Name) stopped (exit code $($entry.Process.ExitCode)); stopping the others." -ForegroundColor Red
            }
            $exitCode = 1
            break
        }

        Start-Sleep -Milliseconds 500
    }
}
finally {
    # Runs on Ctrl+C as well.
    Stop-Started $started
    Write-Host 'Stopped.'
}

exit $exitCode
