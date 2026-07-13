[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('UserPreflight', 'MachineInstall', 'UserInstall', 'MachineCleanup')]
    [string]$Phase,

    [string]$InstallRoot,

    [string]$MsixPath,

    [string]$CertificatePath,

    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageName = 'Codex.TaskbarStatus.Widget'
$widBarPackageName = '30113AndreaDelBello.WidBar-WidgetTaskbarsystem'
$widBarStoreId = '9PKLDNM83TP9'
$certificateOwnershipRoot = 'HKLM:\SOFTWARE\c00rvus\CodexStatus\TrustedCertificates'
$script:completedWithWarnings = $false

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Split-Path -Parent $PSScriptRoot
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logRoot = if ($Phase -in @('UserPreflight', 'UserInstall')) {
        [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    } else {
        [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)
    }
    $LogPath = Join-Path $logRoot 'CodexTaskbarStatus\installer.log'
}

function Test-IsProcessElevated {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        return $principal.IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    } finally {
        $identity.Dispose()
    }
}

function Assert-ElevatedProcess {
    if (-not (Test-IsProcessElevated)) {
        throw "Phase '$Phase' must run from the protected elevated installer."
    }
}

function Assert-NonElevatedProcess {
    if (Test-IsProcessElevated) {
        throw "Phase '$Phase' must run with the original user's non-elevated token. Enable UAC and run setup normally."
    }
}

function Write-ReleaseLog {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    try {
        $parent = Split-Path -Parent $LogPath
        if ($parent) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Add-Content -LiteralPath $LogPath `
            -Value ('{0:u} [{1}] {2}' -f (Get-Date), $Phase, $Message) `
            -Encoding UTF8
    } catch {
        # Logging must not hide the actual installation result.
    }
}

function Initialize-SecureReleaseLog {
    $programDataRoot = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::CommonApplicationData)
    $expectedDirectory = [IO.Path]::GetFullPath(
        (Join-Path $programDataRoot 'CodexTaskbarStatus'))
    $expectedLogPath = [IO.Path]::GetFullPath(
        (Join-Path $expectedDirectory 'installer.log'))
    $requestedLogPath = [IO.Path]::GetFullPath($LogPath)

    if (-not [string]::Equals(
        $requestedLogPath,
        $expectedLogPath,
        [StringComparison]::OrdinalIgnoreCase)) {
        # Custom paths are useful for local diagnostics, but the release setup
        # always supplies the protected ProgramData path above.
        return
    }

    if (Test-Path -LiteralPath $expectedDirectory) {
        $directoryItem = Get-Item -LiteralPath $expectedDirectory -Force
        if (-not $directoryItem.PSIsContainer -or
            ($directoryItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The Codex Status ProgramData log directory is not a safe local directory.'
        }
    } else {
        New-Item -ItemType Directory -Path $expectedDirectory -Force | Out-Null
    }

    $administrators = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    $inheritance = [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [Security.AccessControl.PropagationFlags]::None
    $allow = [Security.AccessControl.AccessControlType]::Allow
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetOwner($administrators)
    $acl.SetAccessRuleProtection($true, $false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $administrators,
        [Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        $propagation,
        $allow))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $system,
        [Security.AccessControl.FileSystemRights]::FullControl,
        $inheritance,
        $propagation,
        $allow))
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
        $users,
        [Security.AccessControl.FileSystemRights]::ReadAndExecute,
        $inheritance,
        $propagation,
        $allow))
    Set-Acl -LiteralPath $expectedDirectory -AclObject $acl

    $securedDirectory = Get-Item -LiteralPath $expectedDirectory -Force
    if ($securedDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The Codex Status ProgramData log directory changed while it was being secured.'
    }
    $securedAcl = Get-Acl -LiteralPath $expectedDirectory
    $securedOwner = $securedAcl.GetOwner(
        [Security.Principal.SecurityIdentifier])
    if (-not $securedOwner.Equals($administrators)) {
        throw 'The Codex Status ProgramData log directory does not have a protected owner.'
    }

    if ($Phase -eq 'MachineInstall' -and (Test-Path -LiteralPath $expectedLogPath)) {
        # Remove only the directory entry. This is safe even if a low-privilege
        # user pre-created a symbolic or hard link before the ACL was secured.
        Remove-Item -LiteralPath $expectedLogPath -Force -ErrorAction Stop
    }
    if (Test-Path -LiteralPath $expectedLogPath) {
        $logItem = Get-Item -LiteralPath $expectedLogPath -Force
        if ($logItem.PSIsContainer -or
            ($logItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'The Codex Status installer log is not a safe local file.'
        }
    } else {
        $newLog = [IO.File]::Open(
            $expectedLogPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::Read)
        $newLog.Dispose()
    }
    $script:LogPath = $expectedLogPath
}

function Resolve-BundledFile {
    param(
        [AllowEmptyString()]
        [string]$RequestedPath,
        [Parameter(Mandatory)]
        [string]$Filter,
        [Parameter(Mandatory)]
        [string]$FriendlyName
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "$FriendlyName is missing: $resolved"
        }
        return $resolved
    }

    $candidates = @(Get-ChildItem -LiteralPath $InstallRoot -Filter $Filter -File -Recurse -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 0) {
        throw "$FriendlyName was not found in the installer files."
    }
    if ($candidates.Count -gt 1) {
        throw "More than one $FriendlyName was found in the installer files. Repair or reinstall Codex Status."
    }
    return $candidates[0].FullName
}

function Get-BundledCertificate {
    $script:CertificatePath = Resolve-BundledFile `
        -RequestedPath $script:CertificatePath `
        -Filter 'Codex.TaskbarStatus*.cer' `
        -FriendlyName 'Codex Status signing certificate'

    try {
        return [Security.Cryptography.X509Certificates.X509Certificate2]::new($script:CertificatePath)
    } catch {
        throw "The bundled Codex Status signing certificate is invalid."
    }
}

function Open-TrustedPeopleStore {
    param(
        [Parameter(Mandatory)]
        [Security.Cryptography.X509Certificates.OpenFlags]$Flags
    )

    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $store.Open($Flags)
    return $store
}

function Install-ReleaseCertificate {
    $certificate = Get-BundledCertificate
    $thumbprint = $certificate.Thumbprint.ToUpperInvariant()
    $certificateWasAdded = $false
    $store = Open-TrustedPeopleStore -Flags ([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $installed = @($store.Certificates | Where-Object Thumbprint -eq $thumbprint)
        if ($installed.Count -eq 0) {
            $store.Add($certificate)
            $certificateWasAdded = $true
            Write-ReleaseLog "Trusted release certificate $thumbprint."
        } else {
            Write-ReleaseLog "Release certificate $thumbprint is already trusted."
        }
    } finally {
        $store.Close()
        $certificate.Dispose()
    }

    if (-not $certificateWasAdded) {
        return
    }

    $ownershipPath = Join-Path $certificateOwnershipRoot $thumbprint
    try {
        New-Item -Path $ownershipPath -Force | Out-Null
        New-ItemProperty `
            -Path $ownershipPath `
            -Name 'AddedBySetup' `
            -Value 1 `
            -PropertyType DWord `
            -Force | Out-Null
        New-ItemProperty `
            -Path $ownershipPath `
            -Name 'AddedAtUtc' `
            -Value ([DateTime]::UtcNow.ToString('o')) `
            -PropertyType String `
            -Force | Out-Null
        Write-ReleaseLog "Recorded setup ownership of certificate $thumbprint."
    } catch {
        $trackingError = $_.Exception.Message
        # Never leave a newly trusted certificate behind without a protected
        # ownership record. A pre-existing certificate is never removed here.
        $rollbackStore = Open-TrustedPeopleStore -Flags ([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        try {
            foreach ($item in @($rollbackStore.Certificates | Where-Object Thumbprint -eq $thumbprint)) {
                $rollbackStore.Remove($item)
            }
        } finally {
            $rollbackStore.Close()
        }
        Remove-Item -LiteralPath $ownershipPath -Recurse -Force -ErrorAction SilentlyContinue
        throw "The release certificate was not trusted because setup could not record its ownership. Details: $trackingError"
    }
}

function Remove-OwnedReleaseCertificateIfUnused {
    $certificate = Get-BundledCertificate
    try {
        $thumbprint = $certificate.Thumbprint.ToUpperInvariant()
    } finally {
        $certificate.Dispose()
    }

    $ownershipPath = Join-Path $certificateOwnershipRoot $thumbprint
    if (-not (Test-Path -LiteralPath $ownershipPath -PathType Container)) {
        Write-ReleaseLog "Certificate $thumbprint was not added by this setup; trust was left unchanged."
        return
    }

    try {
        $installedPackages = @(Get-AppxPackage -AllUsers -Name $packageName -ErrorAction Stop)
    } catch {
        throw "Setup could not verify whether Codex Status is still installed, so certificate $thumbprint was kept trusted. Details: $($_.Exception.Message)"
    }
    if ($installedPackages.Count -gt 0) {
        Write-ReleaseLog "Certificate $thumbprint remains trusted because a Codex Status package is still registered."
        return
    }

    $store = Open-TrustedPeopleStore -Flags ([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        foreach ($item in @($store.Certificates | Where-Object Thumbprint -eq $thumbprint)) {
            Write-ReleaseLog "Removing setup-owned release certificate $thumbprint."
            $store.Remove($item)
        }
    } finally {
        $store.Close()
    }

    Remove-Item -LiteralPath $ownershipPath -Recurse -Force -ErrorAction Stop
    Write-ReleaseLog "Removed setup ownership record for certificate $thumbprint."
}

function Assert-SupportedWindows {
    if (-not [Environment]::Is64BitOperatingSystem) {
        throw 'Codex Status requires 64-bit Windows 11.'
    }

    $nativeArchitecture = if (-not [string]::IsNullOrWhiteSpace($env:PROCESSOR_ARCHITEW6432)) {
        $env:PROCESSOR_ARCHITEW6432
    } else {
        $env:PROCESSOR_ARCHITECTURE
    }
    if ($nativeArchitecture -ne 'AMD64') {
        throw 'This Codex Status release supports x64 Windows computers only.'
    }

    $osVersion = [Environment]::OSVersion.Version
    if ($osVersion.Major -lt 10 -or $osVersion.Build -lt 22621) {
        throw 'Codex Status requires Windows 11 version 22H2 or later.'
    }
}

function Get-WidBarPackage {
    return Get-AppxPackage -Name $widBarPackageName -ErrorAction SilentlyContinue |
        Where-Object { [string]$_.Status -eq 'Ok' } |
        Select-Object -First 1
}

function Open-WidBarStorePage {
    try {
        Start-Process 'ms-windows-store://pdp/?ProductId=9PKLDNM83TP9'
        Write-ReleaseLog 'Opened the WidBar page in Microsoft Store.'
    } catch {
        Write-ReleaseLog "WARNING: Microsoft Store could not be opened: $($_.Exception.Message)"
    }
}

function Install-WidBarIfMissing {
    $package = Get-WidBarPackage
    if ($package) {
        Write-ReleaseLog "WidBar is available: $($package.Version)."
        return $package
    }

    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) {
        Open-WidBarStorePage
        throw 'WidBar is required, but Windows Package Manager (winget) is unavailable. Complete the WidBar installation in Microsoft Store, then run this setup again.'
    }

    Write-ReleaseLog 'Installing WidBar from Microsoft Store with winget.'
    $arguments = @(
        'install',
        '--id', $widBarStoreId,
        '--source', 'msstore',
        '--exact',
        '--silent',
        '--disable-interactivity',
        '--accept-source-agreements',
        '--accept-package-agreements'
    )
    $output = & $winget.Source @arguments 2>&1
    $exitCode = $LASTEXITCODE
    foreach ($line in @($output)) {
        Write-ReleaseLog "winget: $line"
    }

    if ($exitCode -ne 0) {
        $package = Get-WidBarPackage
        if (-not $package) {
            Open-WidBarStorePage
            throw 'WidBar could not be installed automatically. Open Microsoft Store, install WidBar, and run this setup again.'
        }
    }

    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $package = Get-WidBarPackage
        if ($package) {
            return $package
        }
        Start-Sleep -Seconds 2
    }

    Open-WidBarStorePage
    throw 'WidBar installation finished, but Windows has not registered it for this user yet. Complete or open it in Microsoft Store, then run this setup again.'
}

function Stop-WidBar {
    $processes = @(Get-Process -Name 'WidBar' -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        return
    }

    Write-ReleaseLog 'Stopping WidBar so it can reload the widget package.'
    $processes | Stop-Process -Force -ErrorAction SilentlyContinue
    $processes | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

function Start-WidBar {
    $package = Get-WidBarPackage
    if (-not $package) {
        return
    }

    try {
        [xml]$manifest = Get-AppxPackageManifest -Package $package
        $application = @($manifest.Package.Applications.Application)[0]
        if (-not $application) {
            throw 'WidBar application metadata is missing.'
        }
        $appUserModelId = "$($package.PackageFamilyName)!$($application.Id)"
        Start-Process -FilePath 'explorer.exe' -ArgumentList "shell:AppsFolder\$appUserModelId"
        Write-ReleaseLog 'WidBar was launched.'
    } catch {
        Write-ReleaseLog "WARNING: WidBar could not be launched automatically: $($_.Exception.Message)"
    }
}

function Get-MsixIdentity {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $archive.Entries |
            Where-Object { $_.FullName -ieq 'AppxManifest.xml' } |
            Select-Object -First 1
        if (-not $manifestEntry) {
            throw 'AppxManifest.xml is missing.'
        }
        $stream = $manifestEntry.Open()
        $reader = [IO.StreamReader]::new($stream)
        try {
            [xml]$manifest = $reader.ReadToEnd()
        } finally {
            $reader.Dispose()
            $stream.Dispose()
        }
        return [pscustomobject]@{
            Name = [string]$manifest.Package.Identity.Name
            Publisher = [string]$manifest.Package.Identity.Publisher
            Version = [version][string]$manifest.Package.Identity.Version
            Architecture = [string]$manifest.Package.Identity.ProcessorArchitecture
        }
    } catch {
        throw "The bundled Codex Status MSIX is invalid: $($_.Exception.Message)"
    } finally {
        $archive.Dispose()
    }
}

function Assert-MsixSignature {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $certificate = Get-BundledCertificate
    try {
        $signature = Get-AuthenticodeSignature -FilePath $Path
        if (-not $signature.SignerCertificate) {
            throw 'The bundled MSIX is not signed.'
        }
        if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw 'The bundled MSIX signer does not match its release certificate.'
        }
        $acceptableStatuses = @('Valid', 'NotTrusted', 'UnknownError')
        if ([string]$signature.Status -notin $acceptableStatuses) {
            throw "The bundled MSIX signature could not be verified: $($signature.StatusMessage)"
        }
        if ([string]$signature.Status -ne 'Valid') {
            # Self-signed MSIX certificates intentionally live in TrustedPeople,
            # not Trusted Root. Authenticode can therefore report an untrusted
            # chain even though Windows package deployment accepts the signer.
            # Add-AppxPackage performs the final package-integrity validation.
            Write-ReleaseLog "MSIX signer matched; Authenticode trust status was $($signature.Status)."
        }
        Write-ReleaseLog "Verified MSIX signer $($certificate.Thumbprint)."
    } finally {
        $certificate.Dispose()
    }
}

function Restore-DevelopmentPackages {
    param(
        [Parameter(Mandatory)]
        [string[]]$ManifestPaths
    )

    foreach ($manifestPath in $ManifestPaths) {
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            continue
        }
        try {
            Add-AppxPackage -Register $manifestPath -ForceApplicationShutdown -ErrorAction Stop
            Write-ReleaseLog "Restored previous development registration from $manifestPath."
        } catch {
            Write-ReleaseLog "ERROR: Could not restore development package from ${manifestPath}: $($_.Exception.Message)"
        }
    }
}

function Install-WidgetPackage {
    $script:MsixPath = Resolve-BundledFile `
        -RequestedPath $script:MsixPath `
        -Filter 'Codex.TaskbarStatus*.msix' `
        -FriendlyName 'Codex Status MSIX package'

    $identity = Get-MsixIdentity -Path $script:MsixPath
    if ($identity.Name -ne $packageName -or $identity.Architecture -ne 'x64') {
        throw 'The bundled Codex Status MSIX has an unexpected package identity.'
    }
    Assert-MsixSignature -Path $script:MsixPath

    $existingPackages = @(Get-AppxPackage -Name $identity.Name -ErrorAction SilentlyContinue)
    $newerPackages = @($existingPackages | Where-Object {
        [string]$_.Status -eq 'Ok' -and
        [version]$_.Version -gt $identity.Version
    })
    if ($newerPackages.Count -gt 0) {
        $newestVersion = @($newerPackages | ForEach-Object { [version]$_.Version } |
            Sort-Object -Descending)[0]
        throw "Codex Status $newestVersion is already installed. Setup will not downgrade it to $($identity.Version)."
    }

    $alreadyInstalled = $existingPackages |
        Where-Object {
            -not $_.IsDevelopmentMode -and
            [string]$_.Status -eq 'Ok' -and
            [version]$_.Version -eq $identity.Version -and
            [string]$_.Publisher -eq $identity.Publisher
        } |
        Select-Object -First 1
    if ($alreadyInstalled) {
        Write-ReleaseLog "Codex Status package $($identity.Version) is already registered and healthy."
        return
    }

    try {
        Write-ReleaseLog "Registering Codex Status MSIX $($identity.Version) without removing the existing package."
        Add-AppxPackage `
            -Path $script:MsixPath `
            -ForceApplicationShutdown `
            -ErrorAction Stop
    } catch {
        $directInstallError = $_.Exception.Message
        Write-ReleaseLog "Direct MSIX registration failed: $directInstallError"

        $healthySignedPackages = @($existingPackages | Where-Object {
            -not $_.IsDevelopmentMode -and [string]$_.Status -eq 'Ok'
        })
        if ($healthySignedPackages.Count -gt 0) {
            throw "Windows kept the currently installed Codex Status package, but could not update it. Close WidBar and try setup again. Details: $directInstallError"
        }

        $healthyDevelopmentPackages = @($existingPackages | Where-Object {
            $_.IsDevelopmentMode -and [string]$_.Status -eq 'Ok'
        })
        $developmentManifests = @($healthyDevelopmentPackages | ForEach-Object {
            Join-Path ([string]$_.InstallLocation) 'AppxManifest.xml'
        } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })
        if ($healthyDevelopmentPackages.Count -gt 0 -and
            $developmentManifests.Count -ne $healthyDevelopmentPackages.Count) {
            throw "A development copy of Codex Status is installed and cannot be backed up, so it was left untouched. Details: $directInstallError"
        }

        $replaceablePackages = @($existingPackages | Where-Object {
            $_.IsDevelopmentMode -or [string]$_.Status -ne 'Ok'
        })
        if ($replaceablePackages.Count -eq 0) {
            throw "Codex Status could not be registered. Details: $directInstallError"
        }

        try {
            foreach ($package in $replaceablePackages) {
                Write-ReleaseLog "Removing replaceable package registration $($package.PackageFullName)."
                Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
            }
            Add-AppxPackage `
                -Path $script:MsixPath `
                -ForceApplicationShutdown `
                -ErrorAction Stop
        } catch {
            $replacementError = $_.Exception.Message
            Restore-DevelopmentPackages -ManifestPaths $developmentManifests
            throw "Codex Status could not replace the previous package. The development registration was restored when possible. Details: $replacementError"
        }
    }

    $registered = Get-AppxPackage -Name $identity.Name -ErrorAction SilentlyContinue |
        Where-Object {
            [string]$_.Status -eq 'Ok' -and
            [version]$_.Version -eq $identity.Version -and
            [string]$_.Publisher -eq $identity.Publisher
        } |
        Select-Object -First 1
    if (-not $registered) {
        throw 'Windows did not register the Codex Status package after setup completed.'
    }

    Write-ReleaseLog "Codex Status package registered: $($registered.PackageFullName)."
}

function Install-UserComponents {
    Assert-SupportedWindows
    Install-WidBarIfMissing | Out-Null

    $bridgePath = Join-Path $InstallRoot 'bridge\Codex.TaskbarStatus.Bridge.exe'
    if (-not (Test-Path -LiteralPath $bridgePath -PathType Leaf)) {
        throw 'The Codex Status bridge is missing from the installer. Repair or reinstall Codex Status.'
    }

    Stop-WidBar
    try {
        Install-WidgetPackage
        try {
            & (Join-Path $PSScriptRoot 'Configure-CodexHooks.ps1') `
                -Action Install `
                -BridgePath $bridgePath `
                -LogPath $LogPath
        } catch {
            # The MSIX is already installed at this point. Treat hook setup as
            # a visible warning so Inno does not roll back the protected bridge
            # directory while leaving a registered package behind.
            $script:completedWithWarnings = $true
            Write-ReleaseLog "WARNING: Codex lifecycle hooks could not be configured: $($_.Exception.Message)"
        }

        $legacyBridgePath = Join-Path $env:LOCALAPPDATA 'CodexTaskbarStatus\bridge'
        $resolvedLegacyPath = [IO.Path]::GetFullPath($legacyBridgePath).TrimEnd('\')
        $resolvedReleasePath = [IO.Path]::GetFullPath((Split-Path -Parent $bridgePath)).TrimEnd('\')
        if (-not [string]::Equals(
            $resolvedLegacyPath,
            $resolvedReleasePath,
            [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $legacyBridgePath)) {
            try {
                Remove-Item -LiteralPath $legacyBridgePath -Recurse -Force
                Write-ReleaseLog "Removed legacy source-install bridge from $legacyBridgePath."
            } catch {
                Write-ReleaseLog "WARNING: Legacy bridge cleanup failed: $($_.Exception.Message)"
            }
        }
    } finally {
        Start-WidBar
    }
}

try {
    Initialize-SecureReleaseLog
    Write-ReleaseLog "Starting phase. Install root: $InstallRoot"
    switch ($Phase) {
        'UserPreflight' {
            Assert-NonElevatedProcess
            if ([string]::IsNullOrWhiteSpace(
                [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData))) {
                throw 'The original user LocalAppData directory is unavailable.'
            }
        }
        'MachineInstall' {
            Assert-ElevatedProcess
            Install-ReleaseCertificate
        }
        'UserInstall' {
            Assert-NonElevatedProcess
            Install-UserComponents
        }
        'MachineCleanup' {
            Assert-ElevatedProcess
            Remove-OwnedReleaseCertificateIfUnused
        }
    }
    Write-ReleaseLog 'Phase completed successfully.'
    if ($script:completedWithWarnings) {
        exit 2
    }
} catch {
    $friendlyMessage = $_.Exception.Message
    Write-ReleaseLog "ERROR: $friendlyMessage"
    [Console]::Error.WriteLine("Codex Status setup could not continue. $friendlyMessage")
    exit 1
}
