[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
& (Join-Path $PSScriptRoot 'Build-Local.ps1') -Configuration $Configuration -Platform x64

$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
$sdkBin = Get-ChildItem $sdkRoot -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64\makeappx.exe') } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1
if (-not $sdkBin) {
    throw 'Windows SDK MakeAppx and SignTool were not found.'
}

$makeAppx = Join-Path $sdkBin.FullName 'x64\makeappx.exe'
$signTool = Join-Path $sdkBin.FullName 'x64\signtool.exe'
$layout = Join-Path $root 'artifacts\layout'
$msix = Join-Path $root 'artifacts\Codex.TaskbarStatus_1.0.0.0_x64.msix'
$certificatePath = Join-Path $root 'artifacts\Codex.TaskbarStatus.Local.cer'
$subject = 'CN=Codex Taskbar Status Local'

& $makeAppx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) {
    throw "MakeAppx failed with exit code $LASTEXITCODE."
}

$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        $_.Subject -eq $subject -and
        $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date).AddDays(30)
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $certificate) {
    $certificate = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -FriendlyName 'Codex Status Local Development' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -KeyUsage DigitalSignature `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') `
        -NotAfter (Get-Date).AddYears(2)
}

Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null
& $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $msix
if ($LASTEXITCODE -ne 0) {
    throw "SignTool failed with exit code $LASTEXITCODE."
}

Write-Host "Signed MSIX: $msix"
Write-Host "Local certificate: $certificatePath"
