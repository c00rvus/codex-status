[CmdletBinding()]
param(
    [switch]$CheckOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$minimumWindowsBuild = 22621
$minimumAppRuntimeVersion = [version]'2.2.0.0'
$windowsBuild = [Environment]::OSVersion.Version.Build
$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()

if ($windowsBuild -lt $minimumWindowsBuild) {
    throw "Codex Status requires Windows 11 22H2 (build $minimumWindowsBuild) or later. Detected build: $windowsBuild."
}

if ($osArchitecture -ne 'X64') {
    throw "The local installer currently supports x64 Windows only. Detected architecture: $osArchitecture."
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory)]
        [string]$Id,
        [Parameter(Mandatory)]
        [string]$Source,
        [Parameter(Mandatory)]
        [string]$DisplayName
    )

    $winget = Get-Command 'winget.exe' -ErrorAction SilentlyContinue
    if (-not $winget) {
        throw @'
winget was not found. Install or update App Installer from the Microsoft Store,
then run this installer again.
'@
    }

    Write-Host "Installing $DisplayName..."
    $wingetPath = $winget.Source
    & $wingetPath install `
        --id $Id `
        --exact `
        --source $Source `
        --accept-package-agreements `
        --accept-source-agreements `
        --silent `
        --disable-interactivity
    if ($LASTEXITCODE -ne 0) {
        throw "$DisplayName installation failed with exit code $LASTEXITCODE."
    }
}

function Find-CompatibleDotNetSdk {
    $candidates = [Collections.Generic.List[string]]::new()
    $dotnetCommand = Get-Command 'dotnet.exe' -ErrorAction SilentlyContinue
    if ($dotnetCommand) {
        $candidates.Add($dotnetCommand.Source)
    }

    $standardDotNet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
    if (Test-Path $standardDotNet) {
        $candidates.Add($standardDotNet)
    }

    foreach ($candidate in $candidates | Select-Object -Unique) {
        $sdkLines = @(& $candidate --list-sdks 2>$null)
        if ($LASTEXITCODE -ne 0) {
            continue
        }

        foreach ($line in $sdkLines) {
            if ($line -match '^(?<major>\d+)\.' -and [int]$Matches.major -ge 8) {
                return $candidate
            }
        }
    }

    return $null
}

function Test-WindowsAppRuntime {
    return @(
        Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2' -ErrorAction SilentlyContinue |
            Where-Object {
                [string]$_.Architecture -eq 'X64' -and
                [version]$_.Version -ge $minimumAppRuntimeVersion -and
                [string]$_.Status -eq 'Ok'
            }
    ).Count -gt 0
}

function Test-WidBar {
    return @(
        Get-AppxPackage -Name '30113AndreaDelBello.WidBar-WidgetTaskbarsystem' -ErrorAction SilentlyContinue |
            Where-Object { [string]$_.Status -eq 'Ok' }
    ).Count -gt 0
}

function Wait-ForCondition {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Condition
    )

    for ($attempt = 1; $attempt -le 10; $attempt++) {
        if (& $Condition) {
            return $true
        }
        Start-Sleep -Seconds 1
    }

    return $false
}

$dotnetPath = Find-CompatibleDotNetSdk
if (-not $dotnetPath) {
    if ($CheckOnly) {
        throw '.NET SDK 8 or later was not found. Rerun without -SkipPrerequisites to install it automatically.'
    }

    Install-WingetPackage `
        -Id 'Microsoft.DotNet.SDK.8' `
        -Source 'winget' `
        -DisplayName '.NET SDK 8'

    $dotnetPath = Find-CompatibleDotNetSdk
    if (-not $dotnetPath) {
        throw '.NET SDK 8 was installed but is not available yet. Open a new PowerShell window and run the installer again.'
    }
}

$dotnetDirectory = Split-Path $dotnetPath
# Build scripts invoke `dotnet` by name. Always give the compatible host found
# above priority over an older or custom host that may already be on PATH.
$pathEntries = @($env:Path -split ';' | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_) -and
    -not [string]::Equals($_.TrimEnd('\\'), $dotnetDirectory.TrimEnd('\\'), [StringComparison]::OrdinalIgnoreCase)
})
$env:Path = (@($dotnetDirectory) + $pathEntries) -join ';'

if (-not (Test-WindowsAppRuntime)) {
    if ($CheckOnly) {
        throw 'Windows App Runtime 2.2 x64 was not found. Rerun without -SkipPrerequisites to install it automatically.'
    }

    Install-WingetPackage `
        -Id 'Microsoft.WindowsAppRuntime.2' `
        -Source 'winget' `
        -DisplayName 'Windows App Runtime 2.2'
    if (-not (Wait-ForCondition -Condition { Test-WindowsAppRuntime })) {
        throw 'Windows App Runtime 2.2 x64 was installed but its package is not registered for the current user.'
    }
}

if (-not (Test-WidBar)) {
    if ($CheckOnly) {
        throw 'WidBar was not found. Rerun without -SkipPrerequisites to install it automatically.'
    }

    Install-WingetPackage `
        -Id '9PKLDNM83TP9' `
        -Source 'msstore' `
        -DisplayName 'WidBar'
    if (-not (Wait-ForCondition -Condition { Test-WidBar })) {
        throw 'WidBar was installed but its package is not registered for the current user.'
    }
}

$sdkVersion = @(& $dotnetPath --list-sdks) |
    Where-Object { $_ -match '^(?<major>\d+)\.' -and [int]$Matches.major -ge 8 } |
    Select-Object -Last 1
Write-Host "Prerequisites ready: Windows build $windowsBuild, $sdkVersion, Windows App Runtime 2.2, and WidBar."
