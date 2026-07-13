[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,
    [string]$PfxPath = $env:CODEX_STATUS_PFX_PATH,
    [string]$PfxPassword = $env:CODEX_STATUS_PFX_PASSWORD,
    [string]$TimestampUrl = $env:CODEX_STATUS_TIMESTAMP_URL
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-MsixVersion {
    param([Parameter(Mandatory)][string]$Value)

    $candidate = $Value.Trim()
    if ($candidate.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) {
        $candidate = $candidate.Substring(1)
    }

    $segments = @($candidate.Split('.'))
    if ($segments.Count -eq 3) {
        $segments += '0'
    }
    if ($segments.Count -ne 4) {
        throw "Version must have three or four numeric segments (for example 1.1.0): $Value"
    }

    $normalized = foreach ($segment in $segments) {
        [uint16]$number = 0
        if (-not [uint16]::TryParse($segment, [ref]$number)) {
            throw "Every version segment must be between 0 and 65535: $Value"
        }
        [string]$number
    }
    return ($normalized -join '.')
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageVersion = ConvertTo-MsixVersion -Value $Version
$releaseRoot = Join-Path $root 'artifacts\release-staging'
$packageOutput = Join-Path $releaseRoot 'package'
$bridgeOutput = Join-Path $releaseRoot 'bridge'
$layoutOutput = Join-Path $releaseRoot '.msix-layout'
$signingMetadata = Join-Path $releaseRoot 'signing.json'
$releaseManifest = Join-Path $root 'Codex.TaskbarStatus (Package)\Package.release.appxmanifest'
$bridgeProject = Join-Path $root 'Codex.TaskbarStatus.Bridge\Codex.TaskbarStatus.Bridge.csproj'
$notices = Join-Path $root 'THIRD_PARTY_NOTICES.md'

[xml]$releaseManifestXml = Get-Content -LiteralPath $releaseManifest -Raw
$namespaceManager = [Xml.XmlNamespaceManager]::new($releaseManifestXml.NameTable)
$namespaceManager.AddNamespace('pkg', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
$publisher = [string]$releaseManifestXml.Package.Identity.Publisher
if ($publisher -ne 'CN=Codex Taskbar Status Local') {
    throw "Release manifest publisher must remain 'CN=Codex Taskbar Status Local'; found '$publisher'."
}
if ($releaseManifestXml.SelectNodes('//pkg:PackageDependency', $namespaceManager).Count -ne 0) {
    throw 'The self-contained release manifest must not declare Microsoft.WindowsAppRuntime or other framework package dependencies.'
}

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $packageOutput -Force | Out-Null
New-Item -ItemType Directory -Path $bridgeOutput -Force | Out-Null

$certificateOutput = Join-Path $packageOutput "Codex.TaskbarStatus_${packageVersion}_x64.cer"
$msixBuildParameters = @{
    Configuration = 'Release'
    Version = $packageVersion
    OutputDirectory = $packageOutput
    LayoutPath = $layoutOutput
    ManifestPath = $releaseManifest
    WindowsAppSDKSelfContained = $true
    CertificateOutputPath = $certificateOutput
    SigningMetadataPath = $signingMetadata
}
if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
    $msixBuildParameters.PfxPath = $PfxPath
    $msixBuildParameters.PfxPassword = $PfxPassword
}
if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $msixBuildParameters.TimestampUrl = $TimestampUrl
}
& (Join-Path $PSScriptRoot 'Build-Msix.ps1') @msixBuildParameters

$generatedManifestPath = Join-Path $layoutOutput 'AppxManifest.xml'
[xml]$generatedManifest = Get-Content -LiteralPath $generatedManifestPath -Raw
$generatedNamespaceManager = [Xml.XmlNamespaceManager]::new($generatedManifest.NameTable)
$generatedNamespaceManager.AddNamespace('pkg', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')
if ([string]$generatedManifest.Package.Identity.Version -ne $packageVersion) {
    throw "Generated manifest version does not match $packageVersion."
}
if ([string]$generatedManifest.Package.Identity.Publisher -ne 'CN=Codex Taskbar Status Local') {
    throw 'Generated manifest publisher does not match the signing identity.'
}
if ($generatedManifest.SelectNodes('//pkg:PackageDependency', $generatedNamespaceManager).Count -ne 0) {
    throw 'Generated release package unexpectedly contains a framework PackageDependency.'
}

$generatedPluginPath = Join-Path $layoutOutput 'Public\plugin.json'
$generatedPlugin = Get-Content -LiteralPath $generatedPluginPath -Raw | ConvertFrom-Json
$expectedPluginVersion = (($packageVersion.Split('.'))[0..2] -join '.')
if ([string]$generatedPlugin.version -ne $expectedPluginVersion) {
    throw "Generated plugin version '$($generatedPlugin.version)' does not match '$expectedPluginVersion'."
}

$bridgePublishArguments = @(
    'publish',
    $bridgeProject,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--output', $bridgeOutput,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false'
)
& dotnet @bridgePublishArguments
if ($LASTEXITCODE -ne 0) {
    throw "Bridge publish failed with exit code $LASTEXITCODE."
}

$bridgeExecutable = Join-Path $bridgeOutput 'Codex.TaskbarStatus.Bridge.exe'
if (-not (Test-Path -LiteralPath $bridgeExecutable -PathType Leaf)) {
    throw "Self-contained Bridge executable was not produced: $bridgeExecutable"
}
$unexpectedBridgeFiles = @(Get-ChildItem -LiteralPath $bridgeOutput -File |
    Where-Object { $_.FullName -ne $bridgeExecutable })
if ($unexpectedBridgeFiles.Count -gt 0) {
    throw "Bridge publish was not single-file. Unexpected output: $($unexpectedBridgeFiles.Name -join ', ')"
}

Copy-Item -LiteralPath $notices -Destination (Join-Path $releaseRoot 'THIRD_PARTY_NOTICES.md') -Force

$msix = Join-Path $packageOutput "Codex.TaskbarStatus_${packageVersion}_x64.msix"
foreach ($requiredArtifact in @($msix, $certificateOutput, $signingMetadata, $bridgeExecutable)) {
    if (-not (Test-Path -LiteralPath $requiredArtifact -PathType Leaf)) {
        throw "Release staging is incomplete: $requiredArtifact"
    }
}
Remove-Item -LiteralPath $layoutOutput -Recurse -Force

Write-Host "Release version: $Version -> $packageVersion"
Write-Host "Release staging: $releaseRoot"
Write-Host "MSIX: $msix"
Write-Host "Certificate: $certificateOutput"
Write-Host "Bridge: $bridgeExecutable"
Write-Host "Signing metadata: $signingMetadata"
