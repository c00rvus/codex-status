[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('UserUninstall', 'MachineUninstall')]
    [string]$Phase,

    [string]$InstallRoot,

    [string]$CertificatePath,

    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$packageName = 'Codex.TaskbarStatus.Widget'
$widBarPackageName = '30113AndreaDelBello.WidBar-WidgetTaskbarsystem'
$certificateOwnershipRoot = 'HKLM:\SOFTWARE\c00rvus\CodexStatus\TrustedCertificates'
$script:completedWithWarnings = $false

if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    $InstallRoot = Split-Path -Parent $PSScriptRoot
}
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $logRoot = if ($Phase -eq 'UserUninstall') {
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
        throw "Phase '$Phase' must run from the protected elevated uninstaller."
    }
}

function Assert-NonElevatedProcess {
    if (Test-IsProcessElevated) {
        throw "Phase '$Phase' must run with the original user's non-elevated token. Start uninstall from Windows Settings."
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
        # Logging must not prevent cleanup.
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

    if ($Phase -eq 'MachineUninstall' -and (Test-Path -LiteralPath $expectedLogPath)) {
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

function Resolve-BundledCertificatePath {
    if (-not [string]::IsNullOrWhiteSpace($script:CertificatePath)) {
        $resolved = [IO.Path]::GetFullPath($script:CertificatePath)
        if (Test-Path -LiteralPath $resolved -PathType Leaf) {
            return $resolved
        }
    }

    $candidates = @(Get-ChildItem `
        -LiteralPath $InstallRoot `
        -Filter 'Codex.TaskbarStatus*.cer' `
        -File `
        -Recurse `
        -ErrorAction SilentlyContinue)
    if ($candidates.Count -eq 1) {
        return $candidates[0].FullName
    }
    if ($candidates.Count -gt 1) {
        throw 'More than one Codex Status release certificate was found during uninstall.'
    }
    throw 'The Codex Status release certificate is missing, so its trust entry could not be removed.'
}

function Get-WidBarPackage {
    return Get-AppxPackage -Name $widBarPackageName -ErrorAction SilentlyContinue |
        Where-Object { [string]$_.Status -eq 'Ok' } |
        Select-Object -First 1
}

function Stop-WidBar {
    $processes = @(Get-Process -Name 'WidBar' -ErrorAction SilentlyContinue)
    if ($processes.Count -eq 0) {
        return
    }
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
        $appUserModelId = "$($package.PackageFamilyName)!$($application.Id)"
        Start-Process -FilePath 'explorer.exe' -ArgumentList "shell:AppsFolder\$appUserModelId"
    } catch {
        Write-ReleaseLog "WARNING: WidBar could not be restarted: $($_.Exception.Message)"
    }
}

function Uninstall-UserComponents {
    $warningErrors = [Collections.Generic.List[string]]::new()
    $packageErrors = [Collections.Generic.List[string]]::new()
    $hookScript = Join-Path $PSScriptRoot 'Configure-CodexHooks.ps1'
    if (Test-Path -LiteralPath $hookScript -PathType Leaf) {
        try {
            & $hookScript -Action Uninstall -LogPath $LogPath
        } catch {
            $message = "Codex hooks could not be cleaned up safely: $($_.Exception.Message)"
            $warningErrors.Add($message)
            Write-ReleaseLog "WARNING: $message"
        }
    } else {
        $message = 'The hook cleanup helper is missing.'
        $warningErrors.Add($message)
        Write-ReleaseLog "WARNING: $message"
    }

    Stop-WidBar
    try {
        $packages = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
        foreach ($package in $packages) {
            try {
                Write-ReleaseLog "Removing package $($package.PackageFullName)."
                Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
            } catch {
                $message = "Package $($package.PackageFullName) could not be removed: $($_.Exception.Message)"
                $packageErrors.Add($message)
                Write-ReleaseLog "ERROR: $message"
            }
        }
    } finally {
        # WidBar is a shared dependency and intentionally remains installed.
        Start-WidBar
    }

    $legacyRoot = Join-Path $env:LOCALAPPDATA 'CodexTaskbarStatus'
    foreach ($legacyPath in @(
        (Join-Path $legacyRoot 'bridge'),
        (Join-Path $legacyRoot 'status.json')
    )) {
        if (-not (Test-Path -LiteralPath $legacyPath)) {
            continue
        }
        try {
            Remove-Item -LiteralPath $legacyPath -Recurse -Force
            Write-ReleaseLog "Removed legacy data: $legacyPath"
        } catch {
            $message = "Legacy data could not be removed from ${legacyPath}: $($_.Exception.Message)"
            $warningErrors.Add($message)
            Write-ReleaseLog "WARNING: $message"
        }
    }

    if ($packageErrors.Count -gt 0) {
        throw ($packageErrors -join ' ')
    }
    if ($warningErrors.Count -gt 0) {
        $script:completedWithWarnings = $true
    }
}

function Uninstall-ReleaseCertificates {
    try {
        $remainingPackages = @(Get-AppxPackage -AllUsers -Name $packageName -ErrorAction Stop)
    } catch {
        throw "Windows could not verify whether Codex Status is still installed, so setup-owned certificates were kept trusted. Details: $($_.Exception.Message)"
    }
    if ($remainingPackages.Count -gt 0) {
        throw 'A Codex Status package is still registered, so setup-owned certificates were kept trusted.'
    }

    if (-not (Test-Path -LiteralPath $certificateOwnershipRoot -PathType Container)) {
        Write-ReleaseLog 'No setup-owned certificate records exist; trust was left unchanged.'
        return
    }

    $ownedCertificateKeys = @(Get-ChildItem -LiteralPath $certificateOwnershipRoot -ErrorAction Stop)

    $store = [Security.Cryptography.X509Certificates.X509Store]::new(
        [Security.Cryptography.X509Certificates.StoreName]::TrustedPeople,
        [Security.Cryptography.X509Certificates.StoreLocation]::LocalMachine)
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        foreach ($ownershipKey in $ownedCertificateKeys) {
            $thumbprint = [string]$ownershipKey.PSChildName
            $ownership = Get-ItemProperty -LiteralPath $ownershipKey.PSPath -ErrorAction Stop
            $addedBySetupProperty = $ownership.PSObject.Properties['AddedBySetup']
            if ($thumbprint -notmatch '^[A-Fa-f0-9]{40}$' -or
                -not $addedBySetupProperty -or
                [string]$addedBySetupProperty.Value -ne '1') {
                Write-ReleaseLog "WARNING: Ignored an invalid certificate ownership record: $($ownershipKey.PSChildName)"
                $script:completedWithWarnings = $true
                continue
            }

            $thumbprint = $thumbprint.ToUpperInvariant()
            foreach ($item in @($store.Certificates | Where-Object Thumbprint -eq $thumbprint)) {
                Write-ReleaseLog "Removing setup-owned release certificate $thumbprint."
                $store.Remove($item)
            }

            Remove-Item -LiteralPath $ownershipKey.PSPath -Recurse -Force -ErrorAction Stop
            Write-ReleaseLog "Removed setup ownership record for certificate $thumbprint."
        }
    } finally {
        $store.Close()
    }

    if (@(Get-ChildItem -LiteralPath $certificateOwnershipRoot -ErrorAction SilentlyContinue).Count -eq 0) {
        Remove-Item -LiteralPath $certificateOwnershipRoot -Force -ErrorAction SilentlyContinue
    }
}

try {
    Initialize-SecureReleaseLog
    Write-ReleaseLog "Starting phase. Install root: $InstallRoot"
    switch ($Phase) {
        'UserUninstall' {
            Assert-NonElevatedProcess
            Uninstall-UserComponents
        }
        'MachineUninstall' {
            Assert-ElevatedProcess
            Uninstall-ReleaseCertificates
        }
    }
    Write-ReleaseLog 'Phase completed successfully.'
    if ($script:completedWithWarnings) {
        exit 2
    }
} catch {
    $friendlyMessage = $_.Exception.Message
    Write-ReleaseLog "ERROR: $friendlyMessage"
    [Console]::Error.WriteLine("Codex Status uninstall encountered a problem. $friendlyMessage")
    exit 1
}
