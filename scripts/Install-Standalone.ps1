[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$NoStartup,

    [switch]$SkipHooks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-RegistryValueOrNull {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Name
    )

    try {
        return Get-ItemPropertyValue `
            -Path $Path `
            -Name $Name `
            -ErrorAction Stop
    } catch [System.Management.Automation.ItemNotFoundException] {
        return $null
    } catch [System.Management.Automation.PSArgumentException] {
        return $null
    }
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\Codex Status Source'
$stagingRoot = Join-Path $root 'artifacts\manual-install'
$appStaging = Join-Path $stagingRoot 'app'
$bridgeStaging = Join-Path $stagingRoot 'bridge'
$bridgeProject = Join-Path $root 'Codex.TaskbarStatus.Bridge\Codex.TaskbarStatus.Bridge.csproj'
$configureHooks = Join-Path $root 'installer\Configure-CodexHooks.ps1'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupApprovedKey =
    'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$runValueName = 'Codex Status Source'
$existingInstallation = Test-Path -LiteralPath $installRoot -PathType Container
$existingRunValue = Get-RegistryValueOrNull `
    -Path $runKey `
    -Name $runValueName
$startupApproval = Get-RegistryValueOrNull `
    -Path $startupApprovedKey `
    -Name $runValueName
$startupExplicitlyDisabled =
    $startupApproval -is [byte[]] -and
    $startupApproval.Length -gt 0 -and
    $startupApproval[0] -eq 0x03
$startupWasEnabled =
    $null -ne $existingRunValue -and
    -not $startupExplicitlyDisabled

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 8 SDK is required for installation from source.'
}

Get-Process -Name 'Codex.TaskbarStatus.Standalone' -ErrorAction SilentlyContinue |
    Stop-Process -Force

if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
    Remove-Item -LiteralPath $stagingRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $appStaging,$bridgeStaging -Force | Out-Null

& (Join-Path $PSScriptRoot 'Build-Standalone.ps1') `
    -Configuration $Configuration `
    -OutputPath $appStaging

& dotnet publish $bridgeProject `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $bridgeStaging `
    --nologo `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Bridge publish failed with exit code $LASTEXITCODE."
}

$expectedInstallRoot = [IO.Path]::GetFullPath(
    (Join-Path $env:LOCALAPPDATA 'Programs\Codex Status Source'))
$fullInstallRoot = [IO.Path]::GetFullPath($installRoot)
if (-not [string]::Equals(
        $fullInstallRoot,
        $expectedInstallRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace an unexpected installation directory: $fullInstallRoot"
}

if (Test-Path -LiteralPath $fullInstallRoot -PathType Container) {
    Remove-Item -LiteralPath $fullInstallRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $fullInstallRoot -Force | Out-Null
Copy-Item -Path (Join-Path $appStaging '*') -Destination $fullInstallRoot -Recurse -Force
$installedBridgeRoot = Join-Path $fullInstallRoot 'bridge'
New-Item -ItemType Directory -Path $installedBridgeRoot -Force | Out-Null
Copy-Item -Path (Join-Path $bridgeStaging '*') -Destination $installedBridgeRoot -Recurse -Force
New-Item -ItemType Directory -Path (Join-Path $fullInstallRoot 'tools') -Force | Out-Null
Copy-Item -LiteralPath $configureHooks -Destination (Join-Path $fullInstallRoot 'tools') -Force

$appExecutable = Join-Path $fullInstallRoot 'Codex.TaskbarStatus.Standalone.exe'
$bridgeExecutable = Join-Path $fullInstallRoot 'bridge\Codex.TaskbarStatus.Bridge.exe'
foreach ($requiredFile in @($appExecutable, $bridgeExecutable)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Installation is incomplete: $requiredFile"
    }
}

if (-not $SkipHooks) {
    & (Join-Path $fullInstallRoot 'tools\Configure-CodexHooks.ps1') `
        -Action Install `
        -BridgePath $bridgeExecutable
}

New-Item -Path $runKey -Force | Out-Null
if ($NoStartup) {
    Remove-ItemProperty -Path $runKey -Name $runValueName -ErrorAction SilentlyContinue
    Remove-ItemProperty `
        -Path $startupApprovedKey `
        -Name $runValueName `
        -ErrorAction SilentlyContinue
} elseif (-not $existingInstallation -or $startupWasEnabled) {
    Remove-ItemProperty `
        -Path $startupApprovedKey `
        -Name $runValueName `
        -ErrorAction SilentlyContinue
    New-ItemProperty `
        -Path $runKey `
        -Name $runValueName `
        -PropertyType String `
        -Value ('"{0}" --startup' -f $appExecutable) `
        -Force | Out-Null
}

$process = Start-Process -FilePath $appExecutable -WorkingDirectory $fullInstallRoot -PassThru
Start-Sleep -Milliseconds 1200
$process.Refresh()
if ($process.HasExited) {
    throw "Codex Status exited during startup with code $($process.ExitCode)."
}

Write-Host "Codex Status installed in: $fullInstallRoot"
Write-Host "Codex Status started (PID: $($process.Id))."
