[CmdletBinding()]
param(
    [switch]$SkipHooks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$developerMode = (Get-ItemProperty `
    -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
    -Name AllowDevelopmentWithoutDevLicense `
    -ErrorAction SilentlyContinue).AllowDevelopmentWithoutDevLicense -eq 1

$widBarProcesses = @(Get-Process -Name 'WidBar' -ErrorAction SilentlyContinue)
$restartWidBar = $widBarProcesses.Count -gt 0
if ($restartWidBar) {
    $widBarProcesses | Stop-Process -Force
    $widBarProcesses | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

try {
if ($developerMode) {
    & (Join-Path $PSScriptRoot 'Build-Local.ps1') -Configuration Release -Platform x64
} else {
    & (Join-Path $PSScriptRoot 'Build-Msix.ps1') -Configuration Release
}

$widBar = Get-AppxPackage '*WidBar*' | Select-Object -First 1
if (-not $widBar) {
    winget install --id 9PKLDNM83TP9 --exact --source msstore `
        --accept-package-agreements --accept-source-agreements --silent --disable-interactivity
    if ($LASTEXITCODE -ne 0) {
        throw "WidBar installation failed with exit code $LASTEXITCODE."
    }
}

if ($developerMode) {
    $manifest = Join-Path $root 'artifacts\layout\AppxManifest.xml'
    [xml]$manifestXml = Get-Content -LiteralPath $manifest -Raw
    $expectedArchitecture = [string]$manifestXml.Package.Identity.ProcessorArchitecture
    $expectedLocation = (Resolve-Path (Split-Path $manifest)).Path

    # Replace an older neutral build or a signed/MSIX installation of the same
    # package family. Windows otherwise reports 0x80073CF3 for the conflict.
    $existingPackages = @(Get-AppxPackage 'Codex.TaskbarStatus.Widget')
    foreach ($existingPackage in $existingPackages) {
        $sameArchitecture = [string]$existingPackage.Architecture -eq $expectedArchitecture
        $sameLocation = [string]::Equals(
            [string]$existingPackage.InstallLocation,
            $expectedLocation,
            [StringComparison]::OrdinalIgnoreCase)
        if (-not ($sameArchitecture -and $sameLocation)) {
            Remove-AppxPackage -Package $existingPackage.PackageFullName
        }
    }

    Add-AppxPackage -Register $manifest -ForceApplicationShutdown
} else {
    $certificatePath = Join-Path $root 'artifacts\Codex.TaskbarStatus.Local.cer'
    $certificate = Get-PfxCertificate -FilePath $certificatePath
    $isTrusted = Get-ChildItem Cert:\CurrentUser\Root |
        Where-Object Thumbprint -eq $certificate.Thumbprint
    if (-not $isTrusted) {
        throw @"
The local package certificate is not trusted yet.
Open this file and install it for Current User in Trusted Root Certification Authorities:
$certificatePath

Alternatively, enable Windows Developer Mode and run this script again.
"@
    }

    $msix = Join-Path $root 'artifacts\Codex.TaskbarStatus_1.0.0.0_x64.msix'
    Add-AppxPackage -Path $msix -ForceApplicationShutdown
}

if (-not $SkipHooks) {
    & (Join-Path $PSScriptRoot 'Install-CodexHooks.ps1') -Configuration Release
}

$registered = Get-AppxPackage 'Codex.TaskbarStatus.Widget'
if (-not $registered) {
    throw 'Windows did not register the Codex Status package.'
}

Write-Host "Codex Status registered: $($registered.PackageFullName)"
Write-Host 'Open WidBar Layout and place Codex Status in a free taskbar slot.'
}
finally {
    if ($restartWidBar) {
        $widBarPackage = Get-AppxPackage '*WidBar*' | Select-Object -First 1
        if ($widBarPackage) {
            [xml]$widBarManifest = Get-AppxPackageManifest -Package $widBarPackage
            $widBarApp = @($widBarManifest.Package.Applications.Application)[0]
            $widBarAppUserModelId = "$($widBarPackage.PackageFamilyName)!$($widBarApp.Id)"
            Start-Process -FilePath 'explorer.exe' `
                -ArgumentList "shell:AppsFolder\$widBarAppUserModelId"
        }
    }
}
