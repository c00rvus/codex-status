[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Version,
    [switch]$SkipPayloadBuild,
    [string]$TimestampUrl = $env:CODEX_STATUS_TIMESTAMP_URL
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-ReleaseVersion {
    param([Parameter(Mandatory)][string]$Value)

    $candidate = $Value.Trim().TrimStart('v', 'V')
    if ($candidate -notmatch '^\d+\.\d+\.\d+$') {
        throw "Version must contain three numeric segments (for example 1.1.0): $Value"
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
    return $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}

function Find-SignTool {
    $programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    $sdkRoot = Join-Path $programFilesX86 'Windows Kits\10\bin'
    return Get-ChildItem $sdkRoot -Filter 'signtool.exe' -File -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object {
            $versionDirectory = Split-Path (Split-Path $_.DirectoryName -Parent) -Leaf
            try { [version]$versionDirectory } catch { [version]'0.0' }
        } -Descending |
        Select-Object -First 1
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseVersion = ConvertTo-ReleaseVersion -Value $Version
$stagingRoot = Join-Path $root 'artifacts\release-staging'
$releaseRoot = Join-Path $root 'artifacts\release'
$metadataPath = Join-Path $stagingRoot 'signing.json'
$installerScript = Join-Path $root 'installer\CodexStatus.iss'

if (-not $SkipPayloadBuild) {
    $parameters = @{ Version = $releaseVersion }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $parameters.TimestampUrl = $TimestampUrl
    }

    # Release Setup currently has a fixed self-signed MSIX identity. Do not let
    # ambient developer signing variables silently change the published signer.
    $previousPfxPath = [Environment]::GetEnvironmentVariable('CODEX_STATUS_PFX_PATH', 'Process')
    $previousPfxPassword = [Environment]::GetEnvironmentVariable('CODEX_STATUS_PFX_PASSWORD', 'Process')
    try {
        [Environment]::SetEnvironmentVariable('CODEX_STATUS_PFX_PATH', $null, 'Process')
        [Environment]::SetEnvironmentVariable('CODEX_STATUS_PFX_PASSWORD', $null, 'Process')
        & (Join-Path $PSScriptRoot 'Build-Release.ps1') @parameters
    }
    finally {
        [Environment]::SetEnvironmentVariable('CODEX_STATUS_PFX_PATH', $previousPfxPath, 'Process')
        [Environment]::SetEnvironmentVariable('CODEX_STATUS_PFX_PASSWORD', $previousPfxPassword, 'Process')
    }
}

if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
    throw "Release signing metadata was not found: $metadataPath"
}
if (-not (Test-Path -LiteralPath $installerScript -PathType Leaf)) {
    throw "Inno Setup source was not found: $installerScript"
}

$expectedPackageVersion = "$releaseVersion.0"
$packageDirectory = Join-Path $stagingRoot 'package'
$msixFiles = @(Get-ChildItem -LiteralPath $packageDirectory -Filter '*.msix' -File -ErrorAction SilentlyContinue)
if ($msixFiles.Count -ne 1) {
    throw "Release staging must contain exactly one MSIX package; found $($msixFiles.Count)."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($msixFiles[0].FullName)
try {
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if (-not $manifestEntry) {
        throw "The staged MSIX does not contain AppxManifest.xml: $($msixFiles[0].FullName)"
    }

    $stream = $manifestEntry.Open()
    $reader = [IO.StreamReader]::new($stream)
    try {
        [xml]$manifest = $reader.ReadToEnd()
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}
finally {
    $archive.Dispose()
}

$actualPackageVersion = [string]$manifest.Package.Identity.Version
if ($actualPackageVersion -ne $expectedPackageVersion) {
    throw "Staged MSIX version '$actualPackageVersion' does not match requested installer version '$expectedPackageVersion'. Rebuild without -SkipPayloadBuild."
}

$metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
$thumbprint = ([string]$metadata.thumbprint).Replace(' ', '').ToUpperInvariant()
if ([string]::IsNullOrWhiteSpace($thumbprint)) {
    throw 'Release signing metadata does not contain a certificate thumbprint.'
}
$certificate = Get-ChildItem 'Cert:\CurrentUser\My' |
    Where-Object { $_.Thumbprint -eq $thumbprint -and $_.HasPrivateKey } |
    Select-Object -First 1
if (-not $certificate) {
    throw "The release signing certificate is not available with a private key: $thumbprint"
}

$innoCompiler = Find-InnoCompiler
if (-not $innoCompiler) {
    throw 'Inno Setup 6 was not found. Install JRSoftware.InnoSetup and run this command again.'
}
$signTool = Find-SignTool
if (-not $signTool) {
    throw 'Windows SDK SignTool.exe was not found.'
}

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null

$signCommand = '$q' + $signTool.FullName + '$q sign /fd SHA256 /sha1 ' +
    $thumbprint + ' /s My'
if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $signCommand += ' /tr ' + $TimestampUrl + ' /td SHA256'
}
$signCommand += ' $f'

& $innoCompiler `
    "/DAppVersion=$releaseVersion" `
    "/Scodexstatus=$signCommand" `
    $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $releaseRoot "CodexStatusSetup-$releaseVersion-x64.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Inno Setup did not produce the expected installer: $installerPath"
}
$signature = Get-AuthenticodeSignature -FilePath $installerPath
$actualThumbprint = if ($signature.SignerCertificate) {
    $signature.SignerCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant()
} else {
    ''
}
if ($actualThumbprint -ne $thumbprint) {
    throw 'The installer signature does not match the MSIX signing certificate.'
}
$acceptableSignatureStatuses = @(
    [System.Management.Automation.SignatureStatus]::Valid,
    [System.Management.Automation.SignatureStatus]::NotTrusted,
    [System.Management.Automation.SignatureStatus]::UnknownError
)
if ($signature.Status -notin $acceptableSignatureStatuses) {
    throw "The installer signature is invalid: $($signature.Status) - $($signature.StatusMessage)"
}
if (-not [string]::IsNullOrWhiteSpace($TimestampUrl) -and -not $signature.TimeStamperCertificate) {
    throw "The installer signature was not timestamped by $TimestampUrl."
}

$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
[IO.File]::WriteAllText(
    $checksumPath,
    "$hash  $([IO.Path]::GetFileName($installerPath))`n",
    [Text.UTF8Encoding]::new($false))

Write-Host "Installer: $installerPath"
Write-Host "Signature: $thumbprint ($($signature.Status))"
Write-Host "Checksums: $checksumPath"
