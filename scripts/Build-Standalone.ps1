<#
.SYNOPSIS
Publishes the self-contained standalone widget for local testing.

.EXAMPLE
.\scripts\Build-Standalone.ps1

.EXAMPLE
.\scripts\Build-Standalone.ps1 -Configuration Debug -StopRunning
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    [string]$Version,

    [string]$OutputPath,

    [switch]$StopRunning
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'Codex.TaskbarStatus.Standalone\Codex.TaskbarStatus.Standalone.csproj'
$defaultOutput = Join-Path $root 'artifacts\standalone-dev'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $publishDirectory = $defaultOutput
} elseif ([IO.Path]::IsPathRooted($OutputPath)) {
    $publishDirectory = [IO.Path]::GetFullPath($OutputPath)
} else {
    $publishDirectory = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
}

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Standalone project was not found: $project"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK is required, but dotnet was not found in PATH.'
}

$targetExecutable = Join-Path $publishDirectory 'Codex.TaskbarStatus.Standalone.exe'
$running = @(Get-Process -Name 'Codex.TaskbarStatus.Standalone' -ErrorAction SilentlyContinue | Where-Object {
    try {
        [string]::Equals(
            [IO.Path]::GetFullPath($_.Path),
            [IO.Path]::GetFullPath($targetExecutable),
            [StringComparison]::OrdinalIgnoreCase)
    } catch {
        $false
    }
})
if ($running.Count -gt 0) {
    if (-not $StopRunning) {
        $processIds = ($running.Id | Sort-Object) -join ', '
        throw "Codex Taskbar Status is running (PID: $processIds). Run scripts\Stop-Standalone.ps1 first, or rebuild with -StopRunning."
    }

    & (Join-Path $PSScriptRoot 'Stop-Standalone.ps1')
}

$fullPublishDirectory = [IO.Path]::GetFullPath($publishDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$fullRepositoryRoot = [IO.Path]::GetFullPath($root).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
$driveRoot = [IO.Path]::GetPathRoot($fullPublishDirectory).TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar)
if ([string]::IsNullOrWhiteSpace($fullPublishDirectory) -or
    $fullPublishDirectory -eq $driveRoot -or
    $fullPublishDirectory -eq $fullRepositoryRoot) {
    throw "Refusing to clear an unsafe publish directory: $fullPublishDirectory"
}

# dotnet publish does not remove files that disappeared from the dependency
# graph. Clear only the verified output directory so an earlier experimental
# build cannot leave stale runtime assemblies beside the current executable.
if (Test-Path -LiteralPath $fullPublishDirectory -PathType Container) {
    Get-ChildItem -LiteralPath $fullPublishDirectory -Force | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Recurse -Force
    }
} else {
    New-Item -ItemType Directory -Path $fullPublishDirectory -Force | Out-Null
}
$publishDirectory = $fullPublishDirectory

$publishArguments = @(
    'publish',
    $project,
    '--configuration', $Configuration,
    '--runtime', $Runtime,
    '--self-contained', 'true',
    '--output', $publishDirectory,
    '--nologo',
    '-p:Platform=x64',
    '-p:WindowsAppSDKSelfContained=true'
)
if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $publishArguments += "-p:Version=$Version"
}

Write-Host "Publishing standalone widget to: $publishDirectory"
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Standalone publish failed with exit code $LASTEXITCODE."
}

$executable = $targetExecutable
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Standalone publish is incomplete; executable was not found: $executable"
}

$notice = Join-Path $root 'THIRD_PARTY_NOTICES.md'
if (Test-Path -LiteralPath $notice -PathType Leaf) {
    Copy-Item -LiteralPath $notice -Destination (Join-Path $publishDirectory 'THIRD_PARTY_NOTICES.md') -Force
}

Write-Host 'Standalone build completed successfully.'
Write-Host "Executable: $executable"
