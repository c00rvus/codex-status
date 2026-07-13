[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Action = 'Install',

    [string]$BridgePath,

    [string]$HooksPath,

    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ownedExecutableName = 'Codex.TaskbarStatus.Bridge.exe'
$script:ownedExecutablePaths = @()
$eventDefinitions = @(
    [pscustomobject]@{ Name = 'SessionStart'; Matcher = '.*' },
    [pscustomobject]@{ Name = 'UserPromptSubmit'; Matcher = '.*' },
    [pscustomobject]@{ Name = 'PreToolUse'; Matcher = '.*' },
    [pscustomobject]@{ Name = 'PermissionRequest'; Matcher = '.*' },
    [pscustomobject]@{ Name = 'PostToolUse'; Matcher = '.*' },
    [pscustomobject]@{ Name = 'SubagentStart'; Matcher = '.*' },
    [pscustomobject]@{ Name = 'SubagentStop'; Matcher = '.*' },
    [pscustomobject]@{ Name = 'Stop'; Matcher = '.*' }
)

function Write-ReleaseLog {
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    if ([string]::IsNullOrWhiteSpace($LogPath)) {
        return
    }

    try {
        $parent = Split-Path -Parent $LogPath
        if ($parent) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        $line = '{0:u} [hooks] {1}' -f (Get-Date), $Message
        Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8
    } catch {
        # Logging must never make hook configuration fail.
    }
}

function ConvertTo-ObjectArray {
    param(
        [AllowNull()]
        [object]$Value
    )

    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Get-NoteProperty {
    param(
        [AllowNull()]
        [object]$InputObject,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    return $InputObject.PSObject.Properties[$Name]
}

function Set-NoteProperty {
    param(
        [Parameter(Mandatory)]
        [object]$InputObject,
        [Parameter(Mandatory)]
        [string]$Name,
        [AllowNull()]
        [object]$Value
    )

    $property = Get-NoteProperty -InputObject $InputObject -Name $Name
    if ($property) {
        $property.Value = $Value
    } else {
        $InputObject | Add-Member -MemberType NoteProperty -Name $Name -Value $Value
    }
}

function Test-CodexStatusHandler {
    param(
        [AllowNull()]
        [object]$Handler
    )

    if ($null -eq $Handler) {
        return $false
    }

    foreach ($propertyName in @('commandWindows', 'command')) {
        $property = Get-NoteProperty -InputObject $Handler -Name $propertyName
        if ($property -and $null -ne $property.Value) {
            $commandPath = ConvertFrom-CommandExecutablePath -CommandText ([string]$property.Value)
            if ($commandPath -and $script:ownedExecutablePaths -contains $commandPath) {
                return $true
            }
        }
    }

    return $false
}

function ConvertFrom-CommandExecutablePath {
    param(
        [AllowNull()]
        [string]$CommandText
    )

    if ([string]::IsNullOrWhiteSpace($CommandText)) {
        return $null
    }

    $candidate = $CommandText.Trim()
    if ($candidate.StartsWith('"') -or $candidate.EndsWith('"')) {
        if ($candidate.Length -lt 2 -or
            -not ($candidate.StartsWith('"') -and $candidate.EndsWith('"'))) {
            return $null
        }
        $candidate = $candidate.Substring(1, $candidate.Length - 2)
        if ($candidate.Contains('"')) {
            return $null
        }
    }

    try {
        $expanded = [Environment]::ExpandEnvironmentVariables($candidate)
        return [IO.Path]::GetFullPath($expanded)
    } catch {
        return $null
    }
}

function ConvertTo-QuotedExecutableCommand {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    if ($ExecutablePath.Contains('"')) {
        throw 'The bridge path contains a character that cannot be represented in a Windows command.'
    }

    # Codex executes hook commands through the Windows command processor. A
    # quoted executable-only command is unambiguous even when any directory in
    # the installed path (including the user profile) contains spaces.
    return '"{0}"' -f $ExecutablePath
}

function Test-DisposableOwnedGroup {
    param(
        [Parameter(Mandatory)]
        [object]$Group
    )

    $propertyNames = @($Group.PSObject.Properties | ForEach-Object Name)
    return @($propertyNames | Where-Object { $_ -notin @('matcher', 'hooks') }).Count -eq 0
}

function Remove-OwnedHandlersFromGroup {
    param(
        [Parameter(Mandatory)]
        [object]$Group
    )

    $handlersProperty = Get-NoteProperty -InputObject $Group -Name 'hooks'
    if (-not $handlersProperty) {
        return [pscustomobject]@{
            Removed = 0
            Handlers = $null
        }
    }

    $keptHandlers = [Collections.Generic.List[object]]::new()
    $removed = 0
    foreach ($handler in (ConvertTo-ObjectArray -Value $handlersProperty.Value)) {
        if (Test-CodexStatusHandler -Handler $handler) {
            $removed++
        } else {
            $keptHandlers.Add($handler)
        }
    }

    $handlersProperty.Value = $keptHandlers.ToArray()
    return [pscustomobject]@{
        Removed = $removed
        Handlers = $handlersProperty.Value
    }
}

function New-CodexStatusHandler {
    param(
        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    $commandText = ConvertTo-QuotedExecutableCommand -ExecutablePath $ExecutablePath
    return [pscustomobject]@{
        type = 'command'
        command = $commandText
        commandWindows = $commandText
        timeout = 3
    }
}

function Get-HooksFileFingerprint {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return '<missing>'
    }

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Install-EventHandler {
    param(
        [Parameter(Mandatory)]
        [object]$HooksObject,
        [Parameter(Mandatory)]
        [string]$EventName,
        [Parameter(Mandatory)]
        [string]$Matcher,
        [Parameter(Mandatory)]
        [string]$ExecutablePath
    )

    $eventProperty = Get-NoteProperty -InputObject $HooksObject -Name $EventName
    $groups = if ($eventProperty) {
        ConvertTo-ObjectArray -Value $eventProperty.Value
    } else {
        @()
    }

    $resultGroups = [Collections.Generic.List[object]]::new()
    $targetGroup = $null
    foreach ($group in $groups) {
        if ($null -eq $group) {
            $resultGroups.Add($group)
            continue
        }

        $removal = Remove-OwnedHandlersFromGroup -Group $group
        if ($removal.Removed -gt 0 -and $null -eq $targetGroup) {
            $targetGroup = $group
        }

        $handlerCount = if ($null -eq $removal.Handlers) { -1 } else { @($removal.Handlers).Count }
        if ($removal.Removed -gt 0 -and
            $handlerCount -eq 0 -and
            $group -ne $targetGroup -and
            (Test-DisposableOwnedGroup -Group $group)) {
            continue
        }

        $resultGroups.Add($group)
    }

    if ($null -eq $targetGroup) {
        foreach ($group in $resultGroups) {
            if ($null -eq $group) {
                continue
            }
            $matcherProperty = Get-NoteProperty -InputObject $group -Name 'matcher'
            $handlersProperty = Get-NoteProperty -InputObject $group -Name 'hooks'
            if ($handlersProperty -and $matcherProperty -and [string]$matcherProperty.Value -eq $Matcher) {
                $targetGroup = $group
                break
            }
        }
    }

    if ($null -eq $targetGroup) {
        $targetGroup = [pscustomobject]@{
            matcher = $Matcher
            hooks = @()
        }
        $resultGroups.Add($targetGroup)
    }

    $targetMatcher = Get-NoteProperty -InputObject $targetGroup -Name 'matcher'
    if (-not $targetMatcher) {
        Set-NoteProperty -InputObject $targetGroup -Name 'matcher' -Value $Matcher
    }

    $targetHooks = Get-NoteProperty -InputObject $targetGroup -Name 'hooks'
    if (-not $targetHooks) {
        Set-NoteProperty -InputObject $targetGroup -Name 'hooks' -Value @()
        $targetHooks = Get-NoteProperty -InputObject $targetGroup -Name 'hooks'
    }

    $handlers = [Collections.Generic.List[object]]::new()
    foreach ($handler in (ConvertTo-ObjectArray -Value $targetHooks.Value)) {
        $handlers.Add($handler)
    }
    $handlers.Add((New-CodexStatusHandler -ExecutablePath $ExecutablePath))
    $targetHooks.Value = $handlers.ToArray()

    if ($eventProperty) {
        $eventProperty.Value = $resultGroups.ToArray()
    } else {
        $HooksObject | Add-Member -MemberType NoteProperty -Name $EventName -Value $resultGroups.ToArray()
    }
}

function Uninstall-EventHandlers {
    param(
        [Parameter(Mandatory)]
        [object]$HooksObject
    )

    $eventProperties = @($HooksObject.PSObject.Properties)
    foreach ($eventProperty in $eventProperties) {
        $groups = ConvertTo-ObjectArray -Value $eventProperty.Value
        $resultGroups = [Collections.Generic.List[object]]::new()
        $removedFromEvent = 0

        foreach ($group in $groups) {
            if ($null -eq $group) {
                $resultGroups.Add($group)
                continue
            }

            $removal = Remove-OwnedHandlersFromGroup -Group $group
            $removedFromEvent += $removal.Removed
            $handlerCount = if ($null -eq $removal.Handlers) { -1 } else { @($removal.Handlers).Count }
            if ($removal.Removed -gt 0 -and
                $handlerCount -eq 0 -and
                (Test-DisposableOwnedGroup -Group $group)) {
                continue
            }

            $resultGroups.Add($group)
        }

        if ($removedFromEvent -eq 0) {
            continue
        }

        if ($resultGroups.Count -eq 0) {
            $HooksObject.PSObject.Properties.Remove($eventProperty.Name)
        } else {
            $eventProperty.Value = $resultGroups.ToArray()
        }
    }
}

function Read-HooksDocument {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return [pscustomobject]@{ hooks = [pscustomobject]@{} }
    }

    $raw = Get-Content -LiteralPath $Path -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) {
        return [pscustomobject]@{ hooks = [pscustomobject]@{} }
    }

    try {
        $document = $raw | ConvertFrom-Json
    } catch {
        throw "The Codex hooks file is not valid JSON, so it was left unchanged: $Path"
    }

    if ($null -eq $document -or $document -is [Array]) {
        throw "The Codex hooks file has an unsupported structure, so it was left unchanged: $Path"
    }

    return $document
}

function Write-HooksDocument {
    param(
        [Parameter(Mandatory)]
        [object]$Document,
        [Parameter(Mandatory)]
        [string]$Path,
        [Parameter(Mandatory)]
        [string]$ExpectedFingerprint
    )

    $directory = Split-Path -Parent $Path
    if ($directory) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }

    $currentFingerprint = Get-HooksFileFingerprint -Path $Path
    if ($currentFingerprint -ne $ExpectedFingerprint) {
        throw "The Codex hooks file changed while it was being updated, so the newer changes were left untouched: $Path"
    }

    if (Test-Path -LiteralPath $Path) {
        $backupPath = '{0}.backup-{1}-{2}' -f @(
            $Path,
            (Get-Date -Format 'yyyyMMdd-HHmmssfff'),
            ([Guid]::NewGuid().ToString('N').Substring(0, 8)))
        Copy-Item -LiteralPath $Path -Destination $backupPath -Force
        Write-ReleaseLog "Backed up hooks to $backupPath"
    }

    $json = $Document | ConvertTo-Json -Depth 32
    $temporaryPath = '{0}.tmp-{1}' -f $Path, [Guid]::NewGuid().ToString('N')
    $replacementBackupPath = '{0}.replace-{1}' -f $Path, [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporaryPath, $json, [Text.UTF8Encoding]::new($false))
        if (Test-Path -LiteralPath $Path) {
            [IO.File]::Replace($temporaryPath, $Path, $replacementBackupPath, $true)
        } else {
            [IO.File]::Move($temporaryPath, $Path)
        }
    } finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $replacementBackupPath) {
            Remove-Item -LiteralPath $replacementBackupPath -Force -ErrorAction SilentlyContinue
        }
    }
}

try {
    $appRoot = Split-Path -Parent $PSScriptRoot
    if ([string]::IsNullOrWhiteSpace($BridgePath)) {
        $BridgePath = Join-Path $appRoot "bridge\$ownedExecutableName"
    }
    $BridgePath = [IO.Path]::GetFullPath($BridgePath)

    $legacyLocalAppData = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($legacyLocalAppData)) {
        $legacyLocalAppData = $env:LOCALAPPDATA
    }
    $legacyBridgePath = Join-Path $legacyLocalAppData "CodexTaskbarStatus\bridge\$ownedExecutableName"
    $script:ownedExecutablePaths = @(
        [IO.Path]::GetFullPath($BridgePath)
        [IO.Path]::GetFullPath($legacyBridgePath)
    ) | Select-Object -Unique

    if ([string]::IsNullOrWhiteSpace($HooksPath)) {
        $codexHome = if (-not [string]::IsNullOrWhiteSpace($env:CODEX_HOME)) {
            $env:CODEX_HOME
        } else {
            Join-Path $env:USERPROFILE '.codex'
        }
        $HooksPath = Join-Path $codexHome 'hooks.json'
    }
    $HooksPath = [IO.Path]::GetFullPath($HooksPath)

    if ($Action -eq 'Install' -and -not (Test-Path -LiteralPath $BridgePath -PathType Leaf)) {
        throw "The Codex Status bridge is missing: $BridgePath"
    }

    if ($Action -eq 'Uninstall' -and -not (Test-Path -LiteralPath $HooksPath)) {
        Write-ReleaseLog 'No Codex hooks file exists; nothing to remove.'
        return
    }

    $initialFingerprint = Get-HooksFileFingerprint -Path $HooksPath
    $document = Read-HooksDocument -Path $HooksPath
    if ((Get-HooksFileFingerprint -Path $HooksPath) -ne $initialFingerprint) {
        throw "The Codex hooks file changed while it was being read, so the newer changes were left untouched: $HooksPath"
    }
    $hooksProperty = Get-NoteProperty -InputObject $document -Name 'hooks'
    if (-not $hooksProperty) {
        $document | Add-Member -MemberType NoteProperty -Name hooks -Value ([pscustomobject]@{})
        $hooksProperty = Get-NoteProperty -InputObject $document -Name 'hooks'
    } elseif ($null -eq $hooksProperty.Value) {
        $hooksProperty.Value = [pscustomobject]@{}
    } elseif ($hooksProperty.Value -is [Array] -or $hooksProperty.Value -is [string] -or
        $hooksProperty.Value -is [ValueType]) {
        throw "The 'hooks' entry in the Codex hooks file has an unsupported structure, so it was left unchanged: $HooksPath"
    }

    $before = $document | ConvertTo-Json -Depth 32 -Compress
    if ($Action -eq 'Install') {
        # Normalize legacy or misplaced registrations first so the release has
        # exactly one owned handler for each supported lifecycle event.
        Uninstall-EventHandlers -HooksObject $hooksProperty.Value
        foreach ($event in $eventDefinitions) {
            Install-EventHandler `
                -HooksObject $hooksProperty.Value `
                -EventName $event.Name `
                -Matcher $event.Matcher `
                -ExecutablePath $BridgePath
        }
    } else {
        Uninstall-EventHandlers -HooksObject $hooksProperty.Value
    }
    $after = $document | ConvertTo-Json -Depth 32 -Compress

    if ($before -eq $after) {
        Write-ReleaseLog "Hooks already match the requested '$Action' state."
        return
    }

    if ($PSCmdlet.ShouldProcess($HooksPath, "$Action Codex Status lifecycle hooks")) {
        Write-HooksDocument `
            -Document $document `
            -Path $HooksPath `
            -ExpectedFingerprint $initialFingerprint
        Write-ReleaseLog "Hook action '$Action' completed in $HooksPath"
    }
} catch {
    Write-ReleaseLog "ERROR: $($_.Exception.Message)"
    throw
}
