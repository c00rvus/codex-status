[CmdletBinding()]
param(
    [switch]$RemoveSettings
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$installRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA 'Programs\Codex Status Source'))
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupApprovedKey =
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'

Get-Process -Name 'Codex.TaskbarStatus.Standalone' -ErrorAction SilentlyContinue |
    Stop-Process -Force

$installedHookScript = Join-Path $installRoot 'tools\Configure-CodexHooks.ps1'
$hookScript = if (Test-Path -LiteralPath $installedHookScript -PathType Leaf) {
    $installedHookScript
} else {
    Join-Path $root 'installer\Configure-CodexHooks.ps1'
}

if (Test-Path -LiteralPath $hookScript -PathType Leaf) {
    & $hookScript `
        -Action Uninstall `
        -BridgePath (Join-Path $installRoot 'bridge\Codex.TaskbarStatus.Bridge.exe')
}

Remove-ItemProperty -Path $runKey -Name 'Codex Status Source' -ErrorAction SilentlyContinue
Remove-ItemProperty `
    -Path $startupApprovedKey `
    -Name 'Codex Status Source' `
    -ErrorAction SilentlyContinue

$expectedInstallRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA 'Programs\Codex Status Source'))
if (-not [string]::Equals(
        $installRoot,
        $expectedInstallRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove an unexpected installation directory: $installRoot"
}
if (Test-Path -LiteralPath $installRoot -PathType Container) {
    Remove-Item -LiteralPath $installRoot -Recurse -Force
}

if ($RemoveSettings) {
    $settingsRoot = Join-Path $env:LOCALAPPDATA 'CodexTaskbarStatus'
    if (Test-Path -LiteralPath $settingsRoot -PathType Container) {
        Remove-Item -LiteralPath $settingsRoot -Recurse -Force
    }
}

Write-Host 'Codex Status was removed.'
