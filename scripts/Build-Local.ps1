[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64')]
    [string]$Platform = 'x64',
    [string]$OutputPath,
    [string]$ManifestPath,
    [string]$PackageVersion,
    [switch]$WindowsAppSDKSelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'Codex.TaskbarStatus.ExtensionApp\Codex.TaskbarStatus.ExtensionApp.csproj'
$packageRoot = Join-Path $root 'Codex.TaskbarStatus (Package)'
$artifacts = Join-Path $root 'artifacts'
$defaultTargetLayout = Join-Path $artifacts 'layout'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $targetLayout = $defaultTargetLayout
} elseif ([IO.Path]::IsPathRooted($OutputPath)) {
    $targetLayout = [IO.Path]::GetFullPath($OutputPath)
} else {
    $targetLayout = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
}

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $sourceManifest = Join-Path $packageRoot 'Package.local.appxmanifest'
} elseif ([IO.Path]::IsPathRooted($ManifestPath)) {
    $sourceManifest = [IO.Path]::GetFullPath($ManifestPath)
} else {
    $sourceManifest = [IO.Path]::GetFullPath((Join-Path $root $ManifestPath))
}
if (-not (Test-Path -LiteralPath $sourceManifest -PathType Leaf)) {
    throw "Package manifest was not found: $sourceManifest"
}

$targetParent = Split-Path $targetLayout -Parent
$targetName = Split-Path $targetLayout -Leaf
New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
$layout = Join-Path $targetParent ".$targetName-staging-$([Guid]::NewGuid().ToString('N'))"
$backupLayout = Join-Path $targetParent ".$targetName-backup-$([Guid]::NewGuid().ToString('N'))"

# The loose package runs directly from this folder. Stop only our plugin before
# replacing its assemblies so a later reinstall is safe while WidBar is open.
if ([string]::Equals(
        [IO.Path]::GetFullPath($targetLayout),
        [IO.Path]::GetFullPath($defaultTargetLayout),
        [StringComparison]::OrdinalIgnoreCase)) {
    $pluginProcesses = @(Get-Process -Name 'Codex.TaskbarStatus.ExtensionApp' -ErrorAction SilentlyContinue)
    if ($pluginProcesses.Count -gt 0) {
        $pluginProcesses | Stop-Process -Force
        $pluginProcesses | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

try {
    New-Item -ItemType Directory -Path $layout -Force | Out-Null

    $publishArguments = @(
        'publish',
        $project,
        '--configuration', $Configuration,
        '--runtime', "win-$Platform",
        '--self-contained', 'true',
        '--output', $layout,
        "-p:Platform=$Platform"
    )
    if ($WindowsAppSDKSelfContained) {
        $publishArguments += '-p:WindowsAppSDKSelfContained=true'
    }
    if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
        $versionSegments = $PackageVersion.Split('.')
        if ($versionSegments.Count -lt 3) {
            throw "PackageVersion must contain at least three numeric segments: $PackageVersion"
        }
        $pluginVersion = ($versionSegments[0..2] -join '.')
        $publishArguments += "-p:WidBarPluginVersion=$pluginVersion"
    }

    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Widget publish failed with exit code $LASTEXITCODE."
    }

    $pluginManifest = Join-Path (Split-Path $project) 'obj\widbar\plugin.json'
    if (-not (Test-Path $pluginManifest)) {
        throw "WidBar plugin manifest was not generated: $pluginManifest"
    }

    $public = Join-Path $layout 'Public'
    $images = Join-Path $layout 'Images'
    New-Item -ItemType Directory -Path $public -Force | Out-Null
    New-Item -ItemType Directory -Path $images -Force | Out-Null
    Copy-Item -LiteralPath $pluginManifest -Destination (Join-Path $public 'plugin.json') -Force
    Copy-Item -Path (Join-Path $packageRoot 'Images\*.png') -Destination $images -Force
    Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') `
        -Destination (Join-Path $layout 'THIRD_PARTY_NOTICES.md') -Force
    $baseImages = @('AppIcon', 'StoreLogo', 'SmallTile', 'MediumTile', 'WideTile', 'LargeTile')
    foreach ($baseImage in $baseImages) {
        Copy-Item -LiteralPath (Join-Path $images "$baseImage.scale-100.png") `
            -Destination (Join-Path $images "$baseImage.png") -Force
    }
    $layoutManifest = Join-Path $layout 'AppxManifest.xml'
    Copy-Item -LiteralPath $sourceManifest -Destination $layoutManifest -Force

    # x-generate is a packaging-project placeholder. Unlike MSBuild packaging,
    # loose Appx registration does not replace it and Windows rejects the manifest.
    [xml]$manifestXml = Get-Content -LiteralPath $layoutManifest -Raw
    if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
        $manifestXml.Package.Identity.Version = $PackageVersion
        $manifestXml.Save($layoutManifest)
    }
    $resourceLanguages = @($manifestXml.Package.Resources.Resource | ForEach-Object Language)
    if (-not $resourceLanguages -or $resourceLanguages -contains 'x-generate') {
        throw 'The local Appx manifest must contain a concrete resource language (for example en-US).'
    }

    $required = @(
        'AppxManifest.xml',
        'Codex.TaskbarStatus.ExtensionApp.exe',
        'coreclr.dll',
        'hostfxr.dll',
        'hostpolicy.dll',
        'THIRD_PARTY_NOTICES.md',
        'Public\plugin.json',
        'Images\AppIcon.scale-100.png'
    )
    foreach ($relativePath in $required) {
        if (-not (Test-Path (Join-Path $layout $relativePath))) {
            throw "Local package layout is incomplete: $relativePath"
        }
    }
} catch {
    if (Test-Path $layout) {
        Remove-Item -LiteralPath $layout -Recurse -Force -ErrorAction SilentlyContinue
    }
    throw
}

$previousLayoutMoved = $false
try {
    if (Test-Path $targetLayout) {
        Move-Item -LiteralPath $targetLayout -Destination $backupLayout -ErrorAction Stop
        $previousLayoutMoved = $true
    }
    Move-Item -LiteralPath $layout -Destination $targetLayout -ErrorAction Stop
} catch {
    $swapError = $_.Exception.Message
    if ($previousLayoutMoved -and (Test-Path $backupLayout)) {
        try {
            if (Test-Path $targetLayout) {
                Remove-Item -LiteralPath $targetLayout -Recurse -Force -ErrorAction Stop
            }
            Move-Item -LiteralPath $backupLayout -Destination $targetLayout -ErrorAction Stop
        } catch {
            throw "Failed to activate the new layout and failed to restore the previous one: $($_.Exception.Message). Original error: $swapError"
        }
    }
    if (Test-Path $layout) {
        Remove-Item -LiteralPath $layout -Recurse -Force -ErrorAction SilentlyContinue
    }
    $recoveryMessage = if ($previousLayoutMoved) {
        'The previous layout was restored.'
    } else {
        'No previous layout was changed.'
    }
    throw "Failed to activate the new layout. $recoveryMessage Original error: $swapError"
}

if ($previousLayoutMoved -and (Test-Path $backupLayout)) {
    try {
        Remove-Item -LiteralPath $backupLayout -Recurse -Force -ErrorAction Stop
    } catch {
        Write-Warning "The old layout could not be removed and was left at: $backupLayout"
    }
}

Write-Host "Local WidBar package layout: $targetLayout"
