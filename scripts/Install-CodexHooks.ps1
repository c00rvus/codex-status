[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'Codex.TaskbarStatus.Bridge\Codex.TaskbarStatus.Bridge.csproj'
$installRoot = Join-Path $env:LOCALAPPDATA 'CodexTaskbarStatus'
$bridgeRoot = Join-Path $installRoot 'bridge'
$bridgeExe = Join-Path $bridgeRoot 'Codex.TaskbarStatus.Bridge.exe'
$configureHooks = Join-Path $root 'installer\Configure-CodexHooks.ps1'

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
    throw "Bridge project not found: $project"
}
if (-not (Test-Path -LiteralPath $configureHooks -PathType Leaf)) {
    throw "Hook configuration script not found: $configureHooks"
}

New-Item -ItemType Directory -Path $bridgeRoot -Force | Out-Null

if ($PSCmdlet.ShouldProcess($bridgeRoot, 'Publish the Codex status hook bridge')) {
    & dotnet publish $project `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $bridgeRoot `
        --nologo `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None `
        -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) {
        throw "Bridge publish failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $bridgeExe -PathType Leaf)) {
    throw "Bridge executable was not created: $bridgeExe"
}

if ($PSCmdlet.ShouldProcess($bridgeExe, 'Configure Codex status lifecycle hooks')) {
    & $configureHooks -Action Install -BridgePath $bridgeExe
}
