[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$configureScript = Join-Path $root 'installer\Configure-CodexHooks.ps1'

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,
        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Get-OwnedHandlers {
    param(
        [AllowNull()]
        [object]$HooksObject,
        [Parameter(Mandatory)]
        [string]$ExpectedCommand
    )

    $result = [Collections.Generic.List[object]]::new()
    if ($null -eq $HooksObject) {
        return $result.ToArray()
    }
    foreach ($eventProperty in @($HooksObject.PSObject.Properties)) {
        foreach ($group in @($eventProperty.Value)) {
            if ($null -eq $group) {
                continue
            }
            $handlersProperty = $group.PSObject.Properties['hooks']
            if (-not $handlersProperty) {
                continue
            }
            foreach ($handler in @($handlersProperty.Value)) {
                foreach ($propertyName in @('commandWindows', 'command')) {
                    $property = $handler.PSObject.Properties[$propertyName]
                    if ($property -and [string]$property.Value -eq $ExpectedCommand) {
                        $result.Add([pscustomobject]@{
                            Event = $eventProperty.Name
                            Handler = $handler
                        })
                        break
                    }
                }
            }
        }
    }
    return $result.ToArray()
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "Codex Status Release Tests-$([Guid]::NewGuid().ToString('N'))"
$bridgeDirectory = Join-Path $temporaryRoot 'app\bridge'
$bridgePath = Join-Path $bridgeDirectory 'Codex.TaskbarStatus.Bridge.exe'
$quotedBridgeCommand = '"{0}"' -f $bridgePath
$codexDirectory = Join-Path $temporaryRoot '.codex'
$hooksPath = Join-Path $codexDirectory 'hooks.json'
$logPath = Join-Path $temporaryRoot 'installer.log'

try {
    New-Item -ItemType Directory -Path $bridgeDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $codexDirectory -Force | Out-Null
    New-Item -ItemType File -Path $bridgePath -Force | Out-Null

    $legacyBridgePath = Join-Path $env:LOCALAPPDATA 'CodexTaskbarStatus\bridge\Codex.TaskbarStatus.Bridge.exe'
    $sameNameCollisionPath = Join-Path $temporaryRoot 'third party\Codex.TaskbarStatus.Bridge.exe'
    $quotedCollisionCommand = '"{0}"' -f $sameNameCollisionPath
    $initialHooks = [pscustomobject]@{
        version = 1
        customSetting = 'preserve-me'
        hooks = [pscustomobject]@{
            SessionStart = @(
                [pscustomobject]@{
                    matcher = '.*'
                    hooks = @(
                        [pscustomobject]@{
                            type = 'command'
                            command = 'C:\Tools\unrelated.exe'
                            commandWindows = 'C:\Tools\unrelated.exe'
                            timeout = 9
                        },
                        [pscustomobject]@{
                            type = 'command'
                            command = $legacyBridgePath
                            commandWindows = $legacyBridgePath
                            timeout = 30
                        }
                    )
                },
                [pscustomobject]@{
                    matcher = '.*'
                    hooks = @(
                        [pscustomobject]@{
                            type = 'command'
                            command = ('"{0}"' -f $legacyBridgePath)
                            commandWindows = ('"{0}"' -f $legacyBridgePath)
                            timeout = 30
                        }
                    )
                }
            )
            CustomEvent = @(
                [pscustomobject]@{
                    matcher = 'keep'
                    label = 'third-party'
                    hooks = @(
                        [pscustomobject]@{
                            type = 'command'
                            command = 'C:\Tools\custom-hook.exe'
                            timeout = 5
                        },
                        [pscustomobject]@{
                            type = 'command'
                            command = 'C:\Tools\NotCodex.TaskbarStatus.Bridge.exe'
                            timeout = 5
                        },
                        [pscustomobject]@{
                            type = 'command'
                            command = $legacyBridgePath
                            timeout = 3
                        },
                        [pscustomobject]@{
                            type = 'command'
                            command = $quotedCollisionCommand
                            commandWindows = $quotedCollisionCommand
                            timeout = 5
                        }
                    )
                }
            )
        }
    }
    [IO.File]::WriteAllText(
        $hooksPath,
        ($initialHooks | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false))

    & $configureScript `
        -Action Install `
        -BridgePath $bridgePath `
        -HooksPath $hooksPath `
        -LogPath $logPath

    $installed = Get-Content -LiteralPath $hooksPath -Raw | ConvertFrom-Json
    Assert-True ($installed.version -eq 1) 'top-level settings must be preserved'
    Assert-True ($installed.customSetting -eq 'preserve-me') 'unrelated top-level values must be preserved'
    $ownedAfterInstall = @(
        Get-OwnedHandlers -HooksObject $installed.hooks -ExpectedCommand $quotedBridgeCommand
    )
    Assert-True ($ownedAfterInstall.Count -eq 8) 'install must normalize the configuration to exactly eight owned handlers'

    $expectedEvents = @(
        'SessionStart',
        'UserPromptSubmit',
        'PreToolUse',
        'PermissionRequest',
        'PostToolUse',
        'SubagentStart',
        'SubagentStop',
        'Stop'
    )
    foreach ($eventName in $expectedEvents) {
        Assert-True (
            $installed.hooks.PSObject.Properties[$eventName].Value -is [Array]
        ) "$eventName must remain a JSON array even when it contains one matcher group"
        $eventHandlers = @($ownedAfterInstall | Where-Object Event -eq $eventName)
        Assert-True ($eventHandlers.Count -eq 1) "$eventName must contain exactly one Codex Status handler"
        $handler = $eventHandlers[0].Handler
        Assert-True ($handler.command -eq $quotedBridgeCommand) "$eventName command must quote the installed bridge"
        Assert-True ($handler.commandWindows -eq $quotedBridgeCommand) "$eventName commandWindows must quote the installed bridge"
        Assert-True ($handler.timeout -eq 3) "$eventName timeout must be three seconds"
    }

    $sessionHandlers = @($installed.hooks.SessionStart[0].hooks)
    Assert-True (
        @($sessionHandlers | Where-Object command -eq 'C:\Tools\unrelated.exe').Count -eq 1
    ) 'the unrelated SessionStart handler must be preserved'
    Assert-True (
        @($installed.hooks.CustomEvent[0].hooks | Where-Object command -eq 'C:\Tools\custom-hook.exe').Count -eq 1
    ) 'the unrelated custom event handler must be preserved'
    Assert-True (
        @($installed.hooks.CustomEvent[0].hooks | Where-Object command -eq $legacyBridgePath).Count -eq 0
    ) 'install must remove a legacy owned handler from a noncanonical event'
    Assert-True (
        @($installed.hooks.CustomEvent[0].hooks |
            Where-Object command -eq $quotedCollisionCommand).Count -eq 1
    ) 'install must preserve a same-named bridge executable from another path'

    $backupsAfterInstall = @(Get-ChildItem -LiteralPath $codexDirectory -Filter 'hooks.json.backup-*')
    Assert-True ($backupsAfterInstall.Count -eq 1) 'install must back up an existing hooks file before changing it'

    & $configureScript `
        -Action Install `
        -BridgePath $bridgePath `
        -HooksPath $hooksPath `
        -LogPath $logPath
    $backupsAfterIdempotentInstall = @(Get-ChildItem -LiteralPath $codexDirectory -Filter 'hooks.json.backup-*')
    Assert-True (
        $backupsAfterIdempotentInstall.Count -eq $backupsAfterInstall.Count
    ) 'an idempotent install must not rewrite or back up hooks'

    & $configureScript `
        -Action Uninstall `
        -BridgePath $bridgePath `
        -HooksPath $hooksPath `
        -LogPath $logPath

    $uninstalled = Get-Content -LiteralPath $hooksPath -Raw | ConvertFrom-Json
    $ownedAfterUninstall = @(
        Get-OwnedHandlers -HooksObject $uninstalled.hooks -ExpectedCommand $quotedBridgeCommand
    )
    Assert-True ($ownedAfterUninstall.Count -eq 0) 'uninstall must remove every Codex Status bridge handler'
    Assert-True (
        @($uninstalled.hooks.SessionStart[0].hooks | Where-Object command -eq 'C:\Tools\unrelated.exe').Count -eq 1
    ) 'uninstall must preserve the unrelated SessionStart handler'
    Assert-True (
        @($uninstalled.hooks.CustomEvent[0].hooks | Where-Object command -eq 'C:\Tools\custom-hook.exe').Count -eq 1
    ) 'uninstall must preserve handlers from unrelated events'
    Assert-True (
        @($uninstalled.hooks.CustomEvent[0].hooks |
            Where-Object command -eq 'C:\Tools\NotCodex.TaskbarStatus.Bridge.exe').Count -eq 1
    ) 'uninstall must not remove a different executable whose name merely contains the bridge name'
    Assert-True (
        @($uninstalled.hooks.CustomEvent[0].hooks |
            Where-Object command -eq $quotedCollisionCommand).Count -eq 1
    ) 'uninstall must preserve a same-named bridge executable from another path'
    Assert-True ($uninstalled.hooks.CustomEvent[0].label -eq 'third-party') 'uninstall must preserve unrelated group metadata'

    $backupsAfterUninstall = @(Get-ChildItem -LiteralPath $codexDirectory -Filter 'hooks.json.backup-*')
    Assert-True ($backupsAfterUninstall.Count -eq 2) 'uninstall must back up hooks before changing them'

    $malformedPath = Join-Path $codexDirectory 'malformed-hooks.json'
    $malformedContent = '{ this is not json'
    [IO.File]::WriteAllText($malformedPath, $malformedContent, [Text.UTF8Encoding]::new($false))
    $malformedRejected = $false
    try {
        & $configureScript `
            -Action Install `
            -BridgePath $bridgePath `
            -HooksPath $malformedPath `
            -LogPath $logPath
    } catch {
        $malformedRejected = $true
    }
    Assert-True $malformedRejected 'malformed hooks JSON must be rejected'
    Assert-True (
        (Get-Content -LiteralPath $malformedPath -Raw) -eq $malformedContent
    ) 'malformed hooks JSON must remain unchanged'

    Write-Host 'Release helper tests passed.'
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
