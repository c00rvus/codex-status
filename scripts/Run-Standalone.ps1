<#
.SYNOPSIS
Builds and starts the standalone widget for local testing.

.EXAMPLE
.\scripts\Run-Standalone.ps1

.EXAMPLE
.\scripts\Run-Standalone.ps1 -OpenSettings
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$OutputPath,

    [switch]$SkipBuild,

    [switch]$Restart,

    [switch]$OpenSettings,

    [switch]$OpenFlyout
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($OpenSettings -and $OpenFlyout) {
    throw 'Use either -OpenSettings or -OpenFlyout, not both.'
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$defaultOutput = Join-Path $root 'artifacts\standalone-dev'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $publishDirectory = $defaultOutput
} elseif ([IO.Path]::IsPathRooted($OutputPath)) {
    $publishDirectory = [IO.Path]::GetFullPath($OutputPath)
} else {
    $publishDirectory = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
}

$running = @(Get-Process -Name 'Codex.TaskbarStatus.Standalone' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if (-not $Restart) {
        $processIds = ($running.Id | Sort-Object) -join ', '
        Write-Host "Codex Taskbar Status is already running (PID: $processIds)."
        if ($OpenSettings -or $OpenFlyout) {
            $executable = Join-Path $publishDirectory 'Codex.TaskbarStatus.Standalone.exe'
            if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
                throw "The standalone executable does not exist at $executable."
            }

            $activationArgument = if ($OpenFlyout) { '--open-flyout' } else { '--open-settings' }
            Start-Process -FilePath $executable -ArgumentList $activationArgument -WorkingDirectory $publishDirectory
            Write-Host 'The existing instance was activated.'
        }
        Write-Host 'Use -Restart to rebuild and restart it.'
        return
    }

    & (Join-Path $PSScriptRoot 'Stop-Standalone.ps1')
}

$executable = Join-Path $publishDirectory 'Codex.TaskbarStatus.Standalone.exe'
if (-not $SkipBuild) {
    $buildParameters = @{
        Configuration = $Configuration
        OutputPath = $publishDirectory
    }
    & (Join-Path $PSScriptRoot 'Build-Standalone.ps1') @buildParameters
} elseif (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "The standalone executable does not exist at $executable. Run without -SkipBuild first."
}

$applicationArguments = @()
if ($OpenSettings) {
    $applicationArguments += '--open-settings'
} elseif ($OpenFlyout) {
    $applicationArguments += '--open-flyout'
}

$startParameters = @{
    FilePath = $executable
    WorkingDirectory = $publishDirectory
    PassThru = $true
}
if ($applicationArguments.Count -gt 0) {
    $startParameters.ArgumentList = $applicationArguments
}

$process = Start-Process @startParameters
Start-Sleep -Milliseconds 1500
$process.Refresh()
if ($process.HasExited) {
    throw "The standalone widget exited during startup with code $($process.ExitCode)."
}

Write-Host "Standalone widget started (PID: $($process.Id))."
