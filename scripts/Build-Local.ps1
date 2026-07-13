[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('x64')]
    [string]$Platform = 'x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'Codex.TaskbarStatus.ExtensionApp\Codex.TaskbarStatus.ExtensionApp.csproj'
$packageRoot = Join-Path $root 'Codex.TaskbarStatus (Package)'
$artifacts = Join-Path $root 'artifacts'
$layout = Join-Path $artifacts 'layout'

# The loose package runs directly from this folder. Stop only our plugin before
# replacing its assemblies so a later reinstall is safe while WidBar is open.
$pluginProcesses = @(Get-Process -Name 'Codex.TaskbarStatus.ExtensionApp' -ErrorAction SilentlyContinue)
if ($pluginProcesses.Count -gt 0) {
    $pluginProcesses | Stop-Process -Force
    $pluginProcesses | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

if (Test-Path $layout) {
    $resolvedLayout = (Resolve-Path $layout).Path
    $resolvedRoot = (Resolve-Path $root).Path
    if (-not $resolvedLayout.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a layout outside the repository: $resolvedLayout"
    }
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        try {
            Remove-Item -LiteralPath $resolvedLayout -Recurse -Force -ErrorAction Stop
            break
        }
        catch [System.UnauthorizedAccessException], [System.IO.IOException] {
            if ($attempt -eq 12) {
                throw
            }

            # A just-terminated self-contained .NET process can retain native
            # module handles for a brief moment while Windows tears it down.
            Start-Sleep -Milliseconds 250
        }
    }
}

New-Item -ItemType Directory -Path $layout -Force | Out-Null

dotnet publish $project `
    --configuration $Configuration `
    --runtime "win-$Platform" `
    --self-contained true `
    --output $layout `
    -p:Platform=$Platform
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
Copy-Item -LiteralPath (Join-Path $packageRoot 'Package.local.appxmanifest') `
    -Destination $layoutManifest -Force

# x-generate is a packaging-project placeholder. Unlike MSBuild packaging,
# loose Appx registration does not replace it and Windows rejects the manifest.
[xml]$manifestXml = Get-Content -LiteralPath $layoutManifest -Raw
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

Write-Host "Local WidBar package layout: $layout"
