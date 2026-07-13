[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Version = '1.1.0',
    [string]$OutputDirectory,
    [string]$LayoutPath,
    [string]$ManifestPath,
    [switch]$WindowsAppSDKSelfContained,
    [string]$PfxPath = $env:CODEX_STATUS_PFX_PATH,
    [string]$PfxPassword = $env:CODEX_STATUS_PFX_PASSWORD,
    [string]$TimestampUrl = $env:CODEX_STATUS_TIMESTAMP_URL,
    [string]$CertificateOutputPath,
    [string]$SigningMetadataPath,
    [switch]$CheckOnly
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

function Get-AbsolutePath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$BasePath
    )

    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path $BasePath $Path))
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageVersion = ConvertTo-MsixVersion -Value $Version
$subject = 'CN=Codex Taskbar Status Local'

$programFilesX86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
if ([string]::IsNullOrWhiteSpace($programFilesX86)) {
    throw 'The x86 Program Files location could not be detected. Install a current Windows 10/11 SDK.'
}
$sdkRoot = Join-Path $programFilesX86 'Windows Kits\10\bin'
if (-not (Test-Path $sdkRoot)) {
    throw 'Windows SDK MakeAppx and SignTool were not found. Install a current Windows 10/11 SDK.'
}

$sdkBin = Get-ChildItem $sdkRoot -Directory |
    Where-Object {
        (Test-Path (Join-Path $_.FullName 'x64\makeappx.exe')) -and
        (Test-Path (Join-Path $_.FullName 'x64\signtool.exe'))
    } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $sdkBin) {
    throw 'Windows SDK MakeAppx and SignTool were not found. Install a current Windows 10/11 SDK.'
}

if ($CheckOnly) {
    Write-Host "Windows SDK tools ready: $($sdkBin.Name)"
    return
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $resolvedOutputDirectory = Join-Path $root 'artifacts'
} else {
    $resolvedOutputDirectory = Get-AbsolutePath -Path $OutputDirectory -BasePath $root
}
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null

if ([string]::IsNullOrWhiteSpace($LayoutPath)) {
    $resolvedLayoutPath = Join-Path $root 'artifacts\layout'
} else {
    $resolvedLayoutPath = Get-AbsolutePath -Path $LayoutPath -BasePath $root
}
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $resolvedManifestPath = Join-Path $root 'Codex.TaskbarStatus (Package)\Package.local.appxmanifest'
} else {
    $resolvedManifestPath = Get-AbsolutePath -Path $ManifestPath -BasePath $root
}

$buildLocalParameters = @{
    Configuration = $Configuration
    Platform = 'x64'
    OutputPath = $resolvedLayoutPath
    ManifestPath = $resolvedManifestPath
    PackageVersion = $packageVersion
}
if ($WindowsAppSDKSelfContained) {
    $buildLocalParameters.WindowsAppSDKSelfContained = $true
}
& (Join-Path $PSScriptRoot 'Build-Local.ps1') @buildLocalParameters

$makeAppx = Join-Path $sdkBin.FullName 'x64\makeappx.exe'
$signTool = Join-Path $sdkBin.FullName 'x64\signtool.exe'
$msix = Join-Path $resolvedOutputDirectory "Codex.TaskbarStatus_${packageVersion}_x64.msix"
if ([string]::IsNullOrWhiteSpace($CertificateOutputPath)) {
    $certificatePath = Join-Path $resolvedOutputDirectory 'Codex.TaskbarStatus.Local.cer'
} else {
    $certificatePath = Get-AbsolutePath -Path $CertificateOutputPath -BasePath $root
}
New-Item -ItemType Directory -Path (Split-Path $certificatePath -Parent) -Force | Out-Null

& $makeAppx pack /d $resolvedLayoutPath /p $msix /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed with exit code $LASTEXITCODE."
}

$certificateSource = 'self-signed'
if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
    if ([string]::IsNullOrEmpty($PfxPassword)) {
        throw 'PfxPassword is required when PfxPath is supplied.'
    }

    $resolvedPfxPath = Get-AbsolutePath -Path $PfxPath -BasePath $root
    if (-not (Test-Path -LiteralPath $resolvedPfxPath -PathType Leaf)) {
        throw "PFX file was not found: $resolvedPfxPath"
    }

    $securePassword = ConvertTo-SecureString -String $PfxPassword -AsPlainText -Force
    $certificate = Import-PfxCertificate `
        -FilePath $resolvedPfxPath `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -Password $securePassword `
        -Exportable |
        Where-Object HasPrivateKey |
        Select-Object -First 1
    if (-not $certificate) {
        throw 'The supplied PFX did not contain an importable certificate with a private key.'
    }
    $certificateSource = 'pfx'
} else {
    $certificate = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object {
            $hasBasicConstraints = $null -ne ($_.Extensions |
                Where-Object { $_.Oid.Value -eq '2.5.29.19' } |
                Select-Object -First 1)
            $_.Subject -eq $subject -and
            $_.HasPrivateKey -and
            $_.NotAfter -gt (Get-Date).AddDays(30) -and
            $hasBasicConstraints
        } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if (-not $certificate) {
        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject $subject `
            -FriendlyName 'Codex Status Local Release Signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -KeyAlgorithm RSA `
            -KeyLength 2048 `
            -HashAlgorithm SHA256 `
            -KeyUsage DigitalSignature `
            -TextExtension @(
                '2.5.29.37={text}1.3.6.1.5.5.7.3.3',
                '2.5.29.19={text}'
            ) `
            -NotAfter (Get-Date).AddYears(2)
    }
}

if ($certificate.Subject -ne $subject) {
    throw "Signing certificate subject '$($certificate.Subject)' must exactly match package publisher '$subject'."
}
if (-not $certificate.HasPrivateKey) {
    throw 'The signing certificate does not have an accessible private key.'
}

Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null
$signArguments = @(
    'sign',
    '/fd', 'SHA256',
    '/sha1', $certificate.Thumbprint,
    '/s', 'My'
)
if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256')
}
$signArguments += $msix
& $signTool @signArguments
if ($LASTEXITCODE -ne 0) {
    throw "SignTool failed with exit code $LASTEXITCODE."
}
$authenticodeSignature = Get-AuthenticodeSignature -LiteralPath $msix
if (-not $authenticodeSignature.SignerCertificate -or
    $authenticodeSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
    throw 'The MSIX Authenticode signer does not match the selected signing certificate.'
}

if (-not [string]::IsNullOrWhiteSpace($SigningMetadataPath)) {
    $resolvedSigningMetadataPath = Get-AbsolutePath -Path $SigningMetadataPath -BasePath $root
    New-Item -ItemType Directory -Path (Split-Path $resolvedSigningMetadataPath -Parent) -Force | Out-Null
    [ordered]@{
        subject = $certificate.Subject
        thumbprint = $certificate.Thumbprint
        certificateSource = $certificateSource
        certificatePath = $certificatePath
        notAfter = $certificate.NotAfter.ToUniversalTime().ToString('o')
    } | ConvertTo-Json | Set-Content -LiteralPath $resolvedSigningMetadataPath -Encoding UTF8
}

Write-Host "Signed MSIX: $msix"
Write-Host "Public certificate: $certificatePath"
Write-Host "Signing certificate: $($certificate.Thumbprint) ($($certificate.Subject))"
