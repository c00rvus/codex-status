[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version
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

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseVersion = ConvertTo-ReleaseVersion -Value $Version
$stagingRoot = Join-Path $root 'artifacts\release-staging'
$appOutput = Join-Path $stagingRoot 'app'
$bridgeOutput = Join-Path $stagingRoot 'bridge'
$toolsOutput = Join-Path $stagingRoot 'tools'
$bridgeProject = Join-Path $root 'Codex.TaskbarStatus.Bridge\Codex.TaskbarStatus.Bridge.csproj'
$configureHooks = Join-Path $root 'installer\Configure-CodexHooks.ps1'

$fullStagingRoot = [IO.Path]::GetFullPath($stagingRoot)
$fullArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts'))
if (-not $fullStagingRoot.StartsWith(
        $fullArtifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clear a release directory outside artifacts: $fullStagingRoot"
}

if (Test-Path -LiteralPath $fullStagingRoot -PathType Container) {
    Remove-Item -LiteralPath $fullStagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $appOutput,$bridgeOutput,$toolsOutput -Force | Out-Null

& (Join-Path $PSScriptRoot 'Build-Standalone.ps1') `
    -Configuration Release `
    -Runtime win-x64 `
    -Version $releaseVersion `
    -OutputPath $appOutput

$bridgePublishArguments = @(
    'publish',
    $bridgeProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $bridgeOutput,
    '--nologo',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:Version=$releaseVersion"
)
& dotnet @bridgePublishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Bridge publish failed with exit code $LASTEXITCODE."
}

$appExecutable = Join-Path $appOutput 'Codex.TaskbarStatus.Standalone.exe'
$bridgeExecutable = Join-Path $bridgeOutput 'Codex.TaskbarStatus.Bridge.exe'
foreach ($requiredFile in @($appExecutable, $bridgeExecutable, $configureHooks)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Release staging is incomplete: $requiredFile"
    }
}

$unexpectedBridgeFiles = @(Get-ChildItem -LiteralPath $bridgeOutput -File |
    Where-Object { $_.FullName -ne $bridgeExecutable })
if ($unexpectedBridgeFiles.Count -gt 0) {
    throw "Bridge publish was not single-file: $($unexpectedBridgeFiles.Name -join ', ')"
}

Copy-Item -LiteralPath $configureHooks -Destination $toolsOutput -Force

$manifest = [ordered]@{
    version = $releaseVersion
    architecture = 'x64'
    appExecutable = 'app/Codex.TaskbarStatus.Standalone.exe'
    bridgeExecutable = 'bridge/Codex.TaskbarStatus.Bridge.exe'
}
$manifestJson = $manifest | ConvertTo-Json
[IO.File]::WriteAllText(
    (Join-Path $stagingRoot 'release.json'),
    $manifestJson,
    [Text.UTF8Encoding]::new($false))

Write-Host "Release version: $releaseVersion"
Write-Host "Standalone application: $appExecutable"
Write-Host "Hook bridge: $bridgeExecutable"
Write-Host "Release staging: $stagingRoot"
