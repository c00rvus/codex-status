[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'Codex.TaskbarStatus.Bridge\Codex.TaskbarStatus.Bridge.csproj'
$installRoot = Join-Path $env:LOCALAPPDATA 'CodexTaskbarStatus'
$bridgeRoot = Join-Path $installRoot 'bridge'
$bridgeExe = Join-Path $bridgeRoot 'Codex.TaskbarStatus.Bridge.exe'
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
$hooksPath = Join-Path $codexHome 'hooks.json'

if (-not (Test-Path $project)) {
    throw "Bridge project not found: $project"
}

New-Item -ItemType Directory -Path $bridgeRoot -Force | Out-Null

if ($PSCmdlet.ShouldProcess($bridgeRoot, 'Publish the Codex status hook bridge')) {
    dotnet publish $project `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=false `
        --output $bridgeRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Bridge publish failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path $bridgeExe)) {
    throw "Bridge executable was not created: $bridgeExe"
}

New-Item -ItemType Directory -Path $codexHome -Force | Out-Null

if (Test-Path $hooksPath) {
    $raw = Get-Content -LiteralPath $hooksPath -Raw
    $document = if ([string]::IsNullOrWhiteSpace($raw)) {
        [pscustomobject]@{ hooks = [pscustomobject]@{} }
    } else {
        $raw | ConvertFrom-Json
    }
    $backup = "$hooksPath.backup-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $hooksPath -Destination $backup -Force
} else {
    $document = [pscustomobject]@{ hooks = [pscustomobject]@{} }
}

if (-not $document.PSObject.Properties['hooks']) {
    $document | Add-Member -MemberType NoteProperty -Name hooks -Value ([pscustomobject]@{})
}

$command = $bridgeExe
$events = @(
    @{ Name = 'SessionStart'; Matcher = '.*' },
    @{ Name = 'UserPromptSubmit'; Matcher = '.*' },
    @{ Name = 'PreToolUse'; Matcher = '.*' },
    @{ Name = 'PermissionRequest'; Matcher = '.*' },
    @{ Name = 'PostToolUse'; Matcher = '.*' },
    @{ Name = 'SubagentStart'; Matcher = '.*' },
    @{ Name = 'SubagentStop'; Matcher = '.*' },
    @{ Name = 'Stop'; Matcher = '.*' }
)

foreach ($event in $events) {
    $name = $event.Name
    $property = $document.hooks.PSObject.Properties[$name]
    $groups = if ($property) { @($property.Value) } else { @() }

    $alreadyInstalled = $false
    foreach ($group in $groups) {
        $handlersProperty = $group.PSObject.Properties['hooks']
        if (-not $handlersProperty) {
            continue
        }

        foreach ($handler in @($handlersProperty.Value)) {
            $commandWindowsProperty = $handler.PSObject.Properties['commandWindows']
            $commandProperty = $handler.PSObject.Properties['command']
            $isCodexStatusHandler =
                ($commandWindowsProperty -and $commandWindowsProperty.Value -like '*Codex.TaskbarStatus.Bridge.exe*') -or
                ($commandProperty -and $commandProperty.Value -like '*Codex.TaskbarStatus.Bridge.exe*')
            if ($isCodexStatusHandler) {
                $alreadyInstalled = $true
                foreach ($setting in @{
                    command = $command
                    commandWindows = $command
                    timeout = 3
                }.GetEnumerator()) {
                    $settingProperty = $handler.PSObject.Properties[$setting.Key]
                    if ($settingProperty) {
                        $settingProperty.Value = $setting.Value
                    } else {
                        $handler | Add-Member -MemberType NoteProperty -Name $setting.Key -Value $setting.Value
                    }
                }
            }
        }
    }

    if (-not $alreadyInstalled) {
        $handler = [pscustomobject]@{
            type = 'command'
            command = $command
            commandWindows = $command
            timeout = 3
        }
        $group = [pscustomobject]@{
            matcher = $event.Matcher
            hooks = @($handler)
        }
        $groups += $group
    }

    if ($property) {
        $property.Value = @($groups)
    } else {
        $document.hooks | Add-Member -MemberType NoteProperty -Name $name -Value @($groups)
    }
}

if ($PSCmdlet.ShouldProcess($hooksPath, 'Install Codex status lifecycle hooks')) {
    $json = $document | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($hooksPath, $json, [Text.UTF8Encoding]::new($false))
}

Write-Host "Codex hooks installed in: $hooksPath"
Write-Host 'Review and trust them in Codex before the first monitored turn.'
