#Requires -Version 7.0
<#
.SYNOPSIS
    Shared helpers of the FinTrack scripts: finding local tools.

.DESCRIPTION
    Tools are looked up on PATH first, then in their default install locations, because several of
    them (mongod, sometimes func or node) are installed without being added to PATH.
#>

Set-StrictMode -Version Latest

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

Export-ModuleMember -Function Find-Tool
