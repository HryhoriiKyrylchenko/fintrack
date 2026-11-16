#Requires -Version 7.0
<#
.SYNOPSIS
    Checks that this machine can build and run FinTrack locally.

.DESCRIPTION
    Reports each local prerequisite (docs/LOCAL-SETUP.md) and the state of the MongoDB
    replica set. Tools are looked up on PATH first, then in their default install
    locations, so the check also works when a tool was installed without adding it to PATH.

    Required: .NET SDK (matching global.json), MongoDB server 8.0 or later, mongosh,
    Azure Functions Core Tools v4, Node.js and Azurite (which runs on Node.js), and a reachable
    MongoDB replica-set primary.
    Information only: whether Azurite is running, Docker.

    The script only reads; it never changes configuration or starts anything.

.PARAMETER MongoUri
    Connection string of the local MongoDB. Defaults to the FINTRACK_TEST_MONGO environment
    variable, otherwise mongodb://localhost:27017/?replicaSet=rs0.

.OUTPUTS
    A report on the console. Exit code 0 when every required item is present, 1 otherwise.

.EXAMPLE
    pwsh scripts/check-prereqs.ps1

.EXAMPLE
    pwsh scripts/check-prereqs.ps1 -MongoUri "mongodb://localhost:27018/?replicaSet=rs0"
#>
[CmdletBinding()]
param(
    [string] $MongoUri = $(if ($env:FINTRACK_TEST_MONGO) { $env:FINTRACK_TEST_MONGO } else { 'mongodb://localhost:27017/?replicaSet=rs0' })
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$MinimumMongoVersion = [version] '8.0'
$MinimumMongoshVersion = [version] '2.0'
$MinimumAzuriteVersion = [version] '3.0'
$RequiredFuncMajor = 4

$repoRoot = Split-Path -Parent $PSScriptRoot
$results = [System.Collections.Generic.List[object]]::new()

function Add-Result {
    param(
        [string] $Item,
        [ValidateSet('OK', 'MISSING', 'FAIL', 'INFO')] [string] $Status,
        [string] $Detail
    )
    $results.Add([pscustomobject] @{ Item = $Item; Status = $Status; Detail = $Detail })
}

# Directories to search after PATH, per tool and operating system.
function Get-CandidateDirectories {
    param([string] $Tool)

    $unixBins = @('/usr/local/bin', '/usr/bin', '/opt/homebrew/bin', (Join-Path $HOME '.local/bin'))

    if ($IsWindows) {
        $programFiles = @($env:ProgramFiles, ${env:ProgramFiles(x86)}) | Where-Object { $_ }
        switch ($Tool) {
            'dotnet' {
                return @($env:DOTNET_ROOT) + ($programFiles | ForEach-Object { Join-Path $_ 'dotnet' }) + @(Join-Path $HOME '.dotnet')
            }
            'mongod' {
                # One folder per installed server version; newest first.
                return $programFiles | ForEach-Object { Join-Path $_ 'MongoDB\Server' } |
                    Where-Object { Test-Path $_ } |
                    ForEach-Object { Get-ChildItem $_ -Directory } |
                    Sort-Object { $v = $null; if ([version]::TryParse($_.Name, [ref] $v)) { $v } else { [version] '0.0' } } -Descending |
                    ForEach-Object { Join-Path $_.FullName 'bin' }
            }
            'mongosh' {
                return @((Join-Path $env:LOCALAPPDATA 'Programs\mongosh')) +
                    ($programFiles | ForEach-Object { Join-Path $_ 'mongosh'; Join-Path $_ 'MongoDB\mongosh\bin' }) +
                    @((Join-Path $env:APPDATA 'npm'))
            }
            'func' {
                return ($programFiles | ForEach-Object { Join-Path $_ 'Microsoft\Azure Functions Core Tools' }) +
                    @((Join-Path $env:APPDATA 'npm'))
            }
            'azurite' { return @((Join-Path $env:APPDATA 'npm')) }
            'node' { return $programFiles | ForEach-Object { Join-Path $_ 'nodejs' } }
            'docker' {
                return ($programFiles | ForEach-Object { Join-Path $_ 'Docker\Docker\resources\bin' }) +
                    @((Join-Path $env:LOCALAPPDATA 'Programs\DockerDesktop\resources\bin'))
            }
        }
    }
    else {
        switch ($Tool) {
            'dotnet' {
                return @($env:DOTNET_ROOT, '/usr/share/dotnet', '/usr/lib/dotnet', '/usr/local/share/dotnet', (Join-Path $HOME '.dotnet')) + $unixBins
            }
            { $_ -in 'azurite', 'func' } { return $unixBins + @((Join-Path $HOME '.npm-global/bin')) }
            default { return $unixBins }
        }
    }
    return @()
}

# Full path of a tool: PATH first, then the default install locations. $null when not found.
function Find-Tool {
    param([string] $Tool)

    $onPath = Get-Command $Tool -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { return $onPath.Source }

    $extensions = if ($IsWindows) { @('.exe', '.cmd') } else { @('') }
    foreach ($dir in (Get-CandidateDirectories $Tool | Where-Object { $_ })) {
        foreach ($extension in $extensions) {
            $candidate = Join-Path $dir ($Tool + $extension)
            if (Test-Path $candidate -PathType Leaf) { return $candidate }
        }
    }
    return $null
}

# Runs a tool and returns its combined output as text, or $null when it cannot be run.
function Invoke-Tool {
    param([string] $Path, [string[]] $Arguments)
    try {
        $output = & $Path @Arguments 2>&1 | Out-String
        if ($LASTEXITCODE -ne 0) { return $null }
        return $output
    }
    catch {
        return $null
    }
}

# The first x.y.z version found in a text, or $null.
function Get-VersionFromText {
    param([string] $Text)
    if ($Text -and $Text -match '(\d+)\.(\d+)\.(\d+)') { return [version] "$($Matches[1]).$($Matches[2]).$($Matches[3])" }
    return $null
}

# Checks a tool that must exist with at least a minimum version.
function Test-VersionedTool {
    param([string] $Item, [string] $Tool, [string[]] $VersionArguments, [version] $Minimum, [switch] $Optional)

    $path = Find-Tool $Tool
    if (-not $path) {
        if ($Optional) { Add-Result $Item 'INFO' 'not installed (optional)' }
        else { Add-Result $Item 'MISSING' "not found on PATH or in the default install locations; see docs/LOCAL-SETUP.md" }
        return $null
    }

    $version = Get-VersionFromText (Invoke-Tool $path $VersionArguments)
    if (-not $version) {
        Add-Result $Item ($Optional ? 'INFO' : 'FAIL') "found at $path, but its version could not be read"
        return $path
    }
    if ($Minimum -and $version -lt $Minimum) {
        Add-Result $Item ($Optional ? 'INFO' : 'FAIL') "$version at $path; $Minimum or later is required"
        return $path
    }
    Add-Result $Item ($Optional ? 'INFO' : 'OK') "$version at $path"
    return $path
}

# --- .NET SDK: a version allowed by global.json (same major.minor, rollForward latestFeature).
function Test-DotnetSdk {
    $globalJson = Get-Content (Join-Path $repoRoot 'global.json') -Raw | ConvertFrom-Json
    $wanted = [version] $globalJson.sdk.version
    $wantedBand = [math]::Floor($wanted.Build / 100)

    $path = Find-Tool 'dotnet'
    if (-not $path) {
        Add-Result '.NET SDK' 'MISSING' "need $wanted or a later patch or feature band of $($wanted.Major).$($wanted.Minor)"
        return
    }

    $sdks = (Invoke-Tool $path @('--list-sdks')) -split "`r?`n" |
        Where-Object { $_ -match '^(\d+\.\d+\.\d+)\s' } |
        ForEach-Object { [version] $Matches[1] }

    $matching = $sdks | Where-Object {
        $band = [math]::Floor($_.Build / 100)
        $_.Major -eq $wanted.Major -and $_.Minor -eq $wanted.Minor -and
            ($band -gt $wantedBand -or ($band -eq $wantedBand -and $_.Build -ge $wanted.Build))
    } | Sort-Object -Descending | Select-Object -First 1

    if ($matching) { Add-Result '.NET SDK' 'OK' "$matching at $path (global.json: $wanted, latestFeature)" }
    else { Add-Result '.NET SDK' 'FAIL' "installed: $($sdks -join ', '); global.json needs $wanted or later within $($wanted.Major).$($wanted.Minor)" }
}

# --- Azure Functions Core Tools: major version 4.
function Test-FunctionsCoreTools {
    $path = Find-Tool 'func'
    if (-not $path) {
        Add-Result 'Functions Core Tools' 'MISSING' 'not found on PATH or in the default install locations; see docs/LOCAL-SETUP.md'
        return
    }
    $version = Get-VersionFromText (Invoke-Tool $path @('--version'))
    if (-not $version) { Add-Result 'Functions Core Tools' 'FAIL' "found at $path, but its version could not be read" }
    elseif ($version.Major -ne $RequiredFuncMajor) { Add-Result 'Functions Core Tools' 'FAIL' "$version at $path; version $RequiredFuncMajor.x is required" }
    else { Add-Result 'Functions Core Tools' 'OK' "$version at $path" }
}

# Hides a password in a connection string before it is printed.
function Get-SafeUri {
    param([string] $Uri)
    return $Uri -replace '//[^/@]*@', '//***@'
}

# Adds a query option to a connection string.
function Add-UriOption {
    param([string] $Uri, [string] $Option)
    if ($Uri -match '\?') { return "$Uri&$Option" }
    if ($Uri -match '^mongodb(\+srv)?://[^/]+$') { return "$Uri/?$Option" }
    return "${Uri}?$Option"
}

# Asks the server described by a connection string for its hello() reply. $null when unreachable.
function Get-MongoHello {
    param([string] $MongoshPath, [string] $Uri)

    $probeUri = Add-UriOption $Uri 'serverSelectionTimeoutMS=3000'
    # No quotes inside the script, so it survives native argument passing on every platform.
    $script = 'const h=db.hello(); print(JSON.stringify({setName:h.setName||null,isReplicaSet:h.isreplicaset===true,primary:h.isWritablePrimary===true,version:db.version()}))'
    $output = Invoke-Tool $MongoshPath @($probeUri, '--quiet', '--norc', '--eval', $script)
    if (-not $output) { return $null }

    $json = ($output -split "`r?`n" | Where-Object { $_.TrimStart().StartsWith('{') } | Select-Object -Last 1)
    if (-not $json) { return $null }
    return $json | ConvertFrom-Json
}

# Same server, connected to directly (no replica-set discovery), to explain a failure.
function Get-DirectUri {
    param([string] $Uri)
    $withoutSet = $Uri -replace '([?&])replicaSet=[^&]*&?', '$1' -replace '[?&]$', ''
    return Add-UriOption $withoutSet 'directConnection=true'
}

# --- MongoDB replica set: reachable through the given URI, with a writable primary.
function Test-ReplicaSet {
    param([string] $MongoshPath)

    $item = 'MongoDB replica set'
    $safeUri = Get-SafeUri $MongoUri
    if (-not $MongoshPath) {
        Add-Result $item 'FAIL' 'cannot be checked without mongosh'
        return
    }

    $hello = Get-MongoHello $MongoshPath $MongoUri
    if ($hello -and $hello.setName -and $hello.primary) {
        $serverVersion = [version] $hello.version
        if ($serverVersion -lt $MinimumMongoVersion) {
            Add-Result $item 'FAIL' "primary of '$($hello.setName)' runs $serverVersion; $MinimumMongoVersion or later is required ($safeUri)"
        }
        else {
            Add-Result $item 'OK' "primary of '$($hello.setName)', server $serverVersion ($safeUri)"
        }
        return
    }

    # The URI did not give a primary. Connect to the node directly to say why.
    $direct = Get-MongoHello $MongoshPath (Get-DirectUri $MongoUri)
    if (-not $direct) {
        Add-Result $item 'FAIL' "MongoDB is not reachable at $safeUri; is the service running?"
    }
    elseif (-not $direct.isReplicaSet -and -not $direct.setName) {
        Add-Result $item 'FAIL' "MongoDB $($direct.version) runs standalone; transactions need a replica set (docs/LOCAL-SETUP.md)"
    }
    elseif (-not $direct.setName) {
        Add-Result $item 'FAIL' 'MongoDB runs with a replica-set name, but the set is not initiated; run rs.initiate() once (docs/LOCAL-SETUP.md)'
    }
    elseif (-not $direct.primary) {
        Add-Result $item 'FAIL' "the node belongs to '$($direct.setName)' but is not primary"
    }
    else {
        Add-Result $item 'FAIL' "the node is primary of '$($direct.setName)', but $safeUri does not connect to it; check the host and replicaSet name"
    }
}

# --- Azurite: an npm package, so it runs only when Node.js is on PATH.
function Test-Azurite {
    $path = Test-VersionedTool -Item 'Azurite' -Tool 'azurite' -VersionArguments @('--version') -Minimum $MinimumAzuriteVersion
    $last = $results[$results.Count - 1]
    if ($path -and $last.Status -eq 'FAIL' -and -not (Get-Command 'node' -CommandType Application -ErrorAction SilentlyContinue)) {
        $last.Detail = "found at $path, but it cannot run because Node.js is not on PATH"
    }
}

# --- Azurite: is it listening on the blob port? Information only; the run script starts it.
function Test-AzuriteRunning {
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $listening = $client.ConnectAsync('127.0.0.1', 10000).Wait(500)
    }
    catch {
        $listening = $false
    }
    finally {
        $client.Dispose()
    }
    Add-Result 'Azurite running' 'INFO' ($listening ? 'listening on 127.0.0.1:10000' : 'not running (scripts/run-local.ps1 will start it)')
}

Test-DotnetSdk
$null = Test-VersionedTool -Item 'MongoDB server (mongod)' -Tool 'mongod' -VersionArguments @('--version') -Minimum $MinimumMongoVersion
$mongosh = Test-VersionedTool -Item 'mongosh' -Tool 'mongosh' -VersionArguments @('--version') -Minimum $MinimumMongoshVersion
Test-ReplicaSet -MongoshPath $mongosh
Test-FunctionsCoreTools
$null = Test-VersionedTool -Item 'Node.js' -Tool 'node' -VersionArguments @('--version')
Test-Azurite
Test-AzuriteRunning
$null = Test-VersionedTool -Item 'Docker' -Tool 'docker' -VersionArguments @('--version') -Optional

$colours = @{ OK = 'Green'; MISSING = 'Red'; FAIL = 'Red'; INFO = 'DarkGray' }
$width = ($results | ForEach-Object { $_.Item.Length } | Measure-Object -Maximum).Maximum
Write-Host ''
Write-Host 'FinTrack local prerequisites'
Write-Host ''
foreach ($result in $results) {
    Write-Host ('  {0}  ' -f $result.Item.PadRight($width)) -NoNewline
    Write-Host ('{0,-7}' -f $result.Status) -ForegroundColor $colours[$result.Status] -NoNewline
    Write-Host "  $($result.Detail)"
}
Write-Host ''

$problems = @($results | Where-Object { $_.Status -in 'MISSING', 'FAIL' })
if ($problems.Count -gt 0) {
    Write-Host "$($problems.Count) required item(s) missing or failing. See docs/LOCAL-SETUP.md." -ForegroundColor Red
    exit 1
}
Write-Host 'All required prerequisites are present.' -ForegroundColor Green
exit 0
