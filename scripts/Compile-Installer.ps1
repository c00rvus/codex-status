[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,

    [switch]$SkipPayloadBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-ReleaseVersion {
    param([Parameter(Mandatory)][string]$Value)

    $candidate = $Value.Trim().TrimStart('v', 'V')
    if ($candidate -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must contain three numeric segments (for example 2.0.0): $Value"
    }

    return $candidate
}

function Find-InnoCompiler {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    return $candidates |
        Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
        Select-Object -First 1
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseVersion = ConvertTo-ReleaseVersion -Value $Version
$stagingRoot = Join-Path $root 'artifacts\release-staging'
$releaseRoot = Join-Path $root 'artifacts\release'
$installerScript = Join-Path $root 'installer\CodexStatus.iss'
$manifestPath = Join-Path $stagingRoot 'release.json'

if (-not $SkipPayloadBuild) {
    & (Join-Path $PSScriptRoot 'Build-Release.ps1') -Version $releaseVersion
}

foreach ($requiredFile in @(
        $installerScript,
        $manifestPath,
        (Join-Path $stagingRoot 'app\Codex.TaskbarStatus.Standalone.exe'),
        (Join-Path $stagingRoot 'bridge\Codex.TaskbarStatus.Bridge.exe'),
        (Join-Path $stagingRoot 'tools\Configure-CodexHooks.ps1'))) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Release payload is incomplete: $requiredFile"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$manifest.version -ne $releaseVersion) {
    throw "Staged version '$($manifest.version)' does not match '$releaseVersion'."
}

$innoCompiler = Find-InnoCompiler
if (-not $innoCompiler) {
    throw 'Inno Setup 6 was not found. Install JRSoftware.InnoSetup and run this command again.'
}

$fullReleaseRoot = [IO.Path]::GetFullPath($releaseRoot)
$fullArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (-not $fullReleaseRoot.StartsWith(
        $fullArtifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear a release directory outside artifacts: $fullReleaseRoot"
}

if (Test-Path -LiteralPath $fullReleaseRoot -PathType Container) {
    Remove-Item -LiteralPath $fullReleaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $fullReleaseRoot -Force | Out-Null

& $innoCompiler "/DAppVersion=$releaseVersion" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $releaseRoot "CodexStatusSetup-$releaseVersion-x64.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup did not produce the expected installer: $installerPath"
}

$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
[IO.File]::WriteAllText(
    $checksumPath,
    "$hash  $([IO.Path]::GetFileName($installerPath))`n",
    [Text.UTF8Encoding]::new($false))

$unexpectedFiles = @(Get-ChildItem -LiteralPath $releaseRoot -File |
    Where-Object { $_.FullName -notin @($installerPath, $checksumPath) })
if ($unexpectedFiles.Count -gt 0) {
    throw "Unexpected release files were generated: $($unexpectedFiles.Name -join ', ')"
}

Write-Host "Installer: $installerPath"
Write-Host "Checksums: $checksumPath"
