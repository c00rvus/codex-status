[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$UninstallerPath,

    [switch]$Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot)).TrimEnd('\')
$userLogRoot = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::LocalApplicationData)
$logPath = Join-Path $userLogRoot 'CodexTaskbarStatus\installer.log'

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

function Write-LauncherLog {
    param([Parameter(Mandatory)][string]$Message)

    try {
        $parent = Split-Path -Parent $logPath
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Add-Content -LiteralPath $logPath `
            -Value ('{0:u} [launcher] {1}' -f (Get-Date), $Message) `
            -Encoding UTF8
    } catch {
        # User-scoped logging must not mask the real uninstall result.
    }
}

function Show-LauncherMessage {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('Error', 'Warning')][string]$Kind = 'Error'
    )

    if ($Quiet) {
        return
    }
    Add-Type -AssemblyName System.Windows.Forms
    $icon = if ($Kind -eq 'Warning') {
        [Windows.Forms.MessageBoxIcon]::Warning
    } else {
        [Windows.Forms.MessageBoxIcon]::Error
    }
    [void][Windows.Forms.MessageBox]::Show(
        $Message,
        'Codex Status',
        [Windows.Forms.MessageBoxButtons]::OK,
        $icon)
}

function Assert-ProtectedUninstaller {
    $script:UninstallerPath = [IO.Path]::GetFullPath($script:UninstallerPath)
    $uninstallerDirectory = [IO.Path]::GetFullPath(
        (Split-Path -Parent $script:UninstallerPath)).TrimEnd('\')
    if (-not [string]::Equals(
        $uninstallerDirectory,
        $installRoot,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The registered uninstaller is outside the protected Codex Status directory.'
    }
    if ((Split-Path -Leaf $script:UninstallerPath) -notmatch '^unins\d{3}\.exe$' -or
        -not (Test-Path -LiteralPath $script:UninstallerPath -PathType Leaf)) {
        throw 'The protected Codex Status uninstaller is missing or invalid.'
    }

    $installDirectory = Get-Item -LiteralPath $installRoot -Force
    if ($installDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'The Codex Status installation directory is not a safe local directory.'
    }

    $certificates = @(Get-ChildItem `
        -LiteralPath (Join-Path $installRoot 'package') `
        -Filter 'Codex.TaskbarStatus*.cer' `
        -File `
        -ErrorAction Stop)
    if ($certificates.Count -ne 1) {
        throw 'The Codex Status signing certificate is missing or ambiguous.'
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $certificates[0].FullName)
    try {
        $signature = Get-AuthenticodeSignature -FilePath $script:UninstallerPath
        if (-not $signature.SignerCertificate -or
            $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw 'The protected uninstaller signature does not match this installation.'
        }
        if ([string]$signature.Status -notin @('Valid', 'NotTrusted', 'UnknownError')) {
            throw "The protected uninstaller signature is invalid: $($signature.Status)."
        }
    } finally {
        $certificate.Dispose()
    }
}

try {
    if (Test-IsProcessElevated) {
        throw 'Start uninstall from Windows Settings so user data can be removed without elevation.'
    }

    Assert-ProtectedUninstaller
    $userCleanupScript = Join-Path $PSScriptRoot 'Uninstall-Release.ps1'
    if (-not (Test-Path -LiteralPath $userCleanupScript -PathType Leaf)) {
        throw 'The protected Codex Status cleanup helper is missing.'
    }

    $powerShellPath = Join-Path `
        ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) `
        'WindowsPowerShell\v1.0\powershell.exe'
    Write-LauncherLog 'Starting non-elevated user cleanup.'
    & $powerShellPath `
        -NoLogo `
        -NoProfile `
        -NonInteractive `
        -ExecutionPolicy Bypass `
        -File $userCleanupScript `
        -Phase UserUninstall `
        -InstallRoot $installRoot `
        -LogPath $logPath
    $userCleanupExitCode = $LASTEXITCODE
    if ($userCleanupExitCode -notin @(0, 2)) {
        throw "The widget package could not be removed (exit code $userCleanupExitCode). The machine-wide certificate and program files were kept. See $logPath."
    }
    $userCleanupHadWarnings = $userCleanupExitCode -eq 2

    $uninstallerArguments = [Collections.Generic.List[string]]::new()
    $uninstallerArguments.Add('/MACHINEONLY')
    if ($Quiet) {
        $uninstallerArguments.Add('/VERYSILENT')
        $uninstallerArguments.Add('/SUPPRESSMSGBOXES')
        $uninstallerArguments.Add('/NORESTART')
    }

    Write-LauncherLog 'Requesting elevation for machine cleanup.'
    $uninstallerProcess = Start-Process `
        -FilePath $UninstallerPath `
        -ArgumentList ($uninstallerArguments.ToArray()) `
        -Verb RunAs `
        -Wait `
        -PassThru
    if ($uninstallerProcess.ExitCode -ne 0) {
        throw "Machine cleanup did not complete (exit code $($uninstallerProcess.ExitCode)). Run uninstall again from Windows Settings."
    }

    if ($userCleanupHadWarnings) {
        Show-LauncherMessage `
            -Kind Warning `
            -Message "Codex Status was removed, but stale hooks or legacy files need attention. See $logPath."
    }
    exit 0
} catch {
    Write-LauncherLog "ERROR: $($_.Exception.Message)"
    Show-LauncherMessage -Kind Error -Message $_.Exception.Message
    exit 1
}
