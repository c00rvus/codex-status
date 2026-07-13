[CmdletBinding()]
param(
    [switch]$SkipHooks,
    [switch]$SkipPrerequisites,
    [switch]$UseSignedMsix
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$packageName = 'Codex.TaskbarStatus.Widget'
$msix = Join-Path $root 'artifacts\Codex.TaskbarStatus_1.1.0.0_x64.msix'

function Get-DevelopmentManifestPaths {
    param(
        [Parameter(Mandatory)]
        [object[]]$Packages
    )

    foreach ($package in $Packages) {
        if (-not $package.IsDevelopmentMode) {
            continue
        }

        $manifestPath = Join-Path ([string]$package.InstallLocation) 'AppxManifest.xml'
        if (Test-Path $manifestPath) {
            Write-Output $manifestPath
        }
    }
}

function New-SignedMsixBackup {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [object[]]$Packages,
        [Parameter(Mandatory)]
        [string]$MsixPath
    )

    $hasSignedPackage = @($Packages | Where-Object { -not $_.IsDevelopmentMode }).Count -gt 0
    if (-not $hasSignedPackage -or -not (Test-Path $MsixPath)) {
        return $null
    }

    $backupPath = Join-Path ([IO.Path]::GetTempPath()) `
        "Codex.TaskbarStatus.rollback-$([Guid]::NewGuid().ToString('N')).msix"
    Copy-Item -LiteralPath $MsixPath -Destination $backupPath -Force
    return $backupPath
}

function Restore-PreviousPackage {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [string[]]$DevelopmentManifests,
        [AllowNull()]
        [string]$SignedMsixBackup
    )

    $healthyPackage = Get-AppxPackage $packageName -ErrorAction SilentlyContinue |
        Where-Object { [string]$_.Status -eq 'Ok' } |
        Select-Object -First 1
    if ($healthyPackage) {
        return $true
    }

    foreach ($manifestPath in $DevelopmentManifests) {
        if (-not (Test-Path $manifestPath)) {
            continue
        }

        try {
            Add-AppxPackage -Register $manifestPath -ForceApplicationShutdown -ErrorAction Stop | Out-Null
            return $true
        } catch {
            Write-Warning "Could not restore the previous development registration: $($_.Exception.Message)"
        }
    }

    if ($SignedMsixBackup -and (Test-Path $SignedMsixBackup)) {
        try {
            Add-AppxPackage -Path $SignedMsixBackup -ForceApplicationShutdown -ErrorAction Stop | Out-Null
            return $true
        } catch {
            Write-Warning "Could not restore the previous signed package: $($_.Exception.Message)"
        }
    }

    return $false
}

function Install-ReplacementPackage {
    param(
        [Parameter(Mandatory)]
        [object[]]$ExistingPackages,
        [Parameter(Mandatory)]
        [scriptblock]$InstallAction,
        [AllowNull()]
        [string]$SignedMsixBackup
    )

    $developmentManifests = @(Get-DevelopmentManifestPaths -Packages $ExistingPackages)
    $hasDevelopmentPackage = @($ExistingPackages | Where-Object { $_.IsDevelopmentMode }).Count -gt 0
    $hasSignedPackage = @($ExistingPackages | Where-Object { -not $_.IsDevelopmentMode }).Count -gt 0
    if ($hasDevelopmentPackage -and $developmentManifests.Count -eq 0) {
        throw 'The existing development package cannot be backed up, so it was left installed. Remove it manually only after resolving its missing layout.'
    }
    if ($hasSignedPackage -and -not $SignedMsixBackup) {
        throw 'The existing signed package cannot be backed up because its previous MSIX is unavailable, so it was left installed. Update from the original repository folder or remove the old package manually.'
    }

    try {
        foreach ($existingPackage in $ExistingPackages) {
            Remove-AppxPackage -Package $existingPackage.PackageFullName -ErrorAction Stop
        }
        & $InstallAction
    } catch {
        $originalError = $_.Exception.Message
        $restored = Restore-PreviousPackage `
            -DevelopmentManifests $developmentManifests `
            -SignedMsixBackup $SignedMsixBackup
        $recoveryMessage = if ($restored) {
            'The previous working package was restored.'
        } else {
            'The previous package could not be restored automatically.'
        }
        throw "Codex Status package replacement failed. $recoveryMessage Original error: $originalError"
    }
}

& (Join-Path $PSScriptRoot 'Install-Prerequisites.ps1') -CheckOnly:$SkipPrerequisites

$developerModeValue = Get-ItemPropertyValue `
    -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' `
    -Name AllowDevelopmentWithoutDevLicense `
    -ErrorAction SilentlyContinue
$developerMode = $developerModeValue -eq 1
if (-not $developerMode -and -not $UseSignedMsix) {
    Start-Process 'ms-settings:developers'
    throw @'
Developer Mode is required for the recommended local installation.
Enable Developer Mode in the Settings window that just opened, then run this command again.

To install a locally signed MSIX instead, rerun with -UseSignedMsix. That route
also requires a current Windows SDK and manual certificate trust.
'@
}

if ($UseSignedMsix) {
    # Fail before stopping WidBar or rebuilding anything when the optional
    # signed-package route is not available on this machine.
    & (Join-Path $PSScriptRoot 'Build-Msix.ps1') -Configuration Release -CheckOnly
}

$widBar = Get-AppxPackage -Name '30113AndreaDelBello.WidBar-WidgetTaskbarsystem' `
    -ErrorAction SilentlyContinue |
    Where-Object { [string]$_.Status -eq 'Ok' } |
    Select-Object -First 1
if (-not $widBar) {
    throw 'WidBar is not available. Rerun without -SkipPrerequisites to install it automatically.'
}

$widBarProcesses = @(Get-Process -Name 'WidBar' -ErrorAction SilentlyContinue)
$restartWidBar = $widBarProcesses.Count -gt 0
if ($restartWidBar) {
    $widBarProcesses | Stop-Process -Force
    $widBarProcesses | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

$signedMsixBackup = $null
try {
    $packagesBeforeBuild = @(Get-AppxPackage $packageName)
    if ($UseSignedMsix) {
        # Build-Msix overwrites the artifact, so preserve it first when it is
        # the only available rollback source for an installed signed package.
        $signedMsixBackup = New-SignedMsixBackup `
            -Packages $packagesBeforeBuild `
            -MsixPath $msix
        & (Join-Path $PSScriptRoot 'Build-Msix.ps1') -Configuration Release
    } else {
        & (Join-Path $PSScriptRoot 'Build-Local.ps1') -Configuration Release -Platform x64
    }

    if (-not $UseSignedMsix) {
        $manifest = Join-Path $root 'artifacts\layout\AppxManifest.xml'
        [xml]$manifestXml = Get-Content -LiteralPath $manifest -Raw
        $expectedName = [string]$manifestXml.Package.Identity.Name
        $expectedPublisher = [string]$manifestXml.Package.Identity.Publisher
        $expectedVersion = [version][string]$manifestXml.Package.Identity.Version
        $expectedArchitecture = [string]$manifestXml.Package.Identity.ProcessorArchitecture
        $expectedLocation = (Resolve-Path (Split-Path $manifest)).Path

        # A development registration already points directly at artifacts\layout,
        # so rebuilt files do not need to be registered again. Re-registering the
        # same identity/version is blocked by Windows and can make WidBar disable
        # the widget. Replace only registrations that point somewhere else.
        $registeredFromCurrentLayout = $false
        $packagesToRemove = [Collections.Generic.List[object]]::new()
        $existingPackages = @(Get-AppxPackage $packageName)
        foreach ($existingPackage in $existingPackages) {
            $sameIdentity =
                [string]$existingPackage.Name -eq $expectedName -and
                [string]$existingPackage.Publisher -eq $expectedPublisher -and
                [version]$existingPackage.Version -eq $expectedVersion
            $sameArchitecture = [string]$existingPackage.Architecture -eq $expectedArchitecture
            $sameLocation = [string]::Equals(
                [string]$existingPackage.InstallLocation,
                $expectedLocation,
                [StringComparison]::OrdinalIgnoreCase)
            $isHealthy = [string]$existingPackage.Status -eq 'Ok'
            if ($existingPackage.IsDevelopmentMode -and
                $sameIdentity -and
                $sameArchitecture -and
                $sameLocation -and
                $isHealthy) {
                $registeredFromCurrentLayout = $true
            } else {
                $packagesToRemove.Add($existingPackage)
            }
        }

        if ($registeredFromCurrentLayout) {
            foreach ($packageToRemove in $packagesToRemove) {
                Remove-AppxPackage -Package $packageToRemove.PackageFullName -ErrorAction Stop
            }
        } elseif ($packagesToRemove.Count -gt 0) {
            if (-not $signedMsixBackup) {
                $signedMsixBackup = New-SignedMsixBackup `
                    -Packages @($packagesToRemove) `
                    -MsixPath $msix
            }
            Install-ReplacementPackage `
                -ExistingPackages @($packagesToRemove) `
                -InstallAction {
                    Add-AppxPackage -Register $manifest -ForceApplicationShutdown -ErrorAction Stop
                } `
                -SignedMsixBackup $signedMsixBackup
        } else {
            Add-AppxPackage -Register $manifest -ForceApplicationShutdown -ErrorAction Stop
        }
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

        $signature = Get-AuthenticodeSignature -FilePath $msix
        if ($signature.Status -ne 'Valid' -or
            -not $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw "The generated MSIX signature is not valid: $($signature.StatusMessage)"
        }

        $existingPackages = @(Get-AppxPackage $packageName)
        if ($existingPackages.Count -gt 0) {
            Install-ReplacementPackage `
                -ExistingPackages $existingPackages `
                -InstallAction {
                    Add-AppxPackage -Path $msix -ForceApplicationShutdown -ErrorAction Stop
                } `
                -SignedMsixBackup $signedMsixBackup
        } else {
            Add-AppxPackage -Path $msix -ForceApplicationShutdown -ErrorAction Stop
        }
    }

    if (-not $SkipHooks) {
        & (Join-Path $PSScriptRoot 'Install-CodexHooks.ps1') -Configuration Release
    }

    $registered = Get-AppxPackage $packageName
    if (-not $registered) {
        throw 'Windows did not register the Codex Status package.'
    }

    Write-Host "Codex Status registered: $($registered.PackageFullName)"
    Write-Host 'Open WidBar Layout and place Codex Status in a free taskbar slot.'
} finally {
    if ($signedMsixBackup -and (Test-Path $signedMsixBackup)) {
        Remove-Item -LiteralPath $signedMsixBackup -Force -ErrorAction SilentlyContinue
    }

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
