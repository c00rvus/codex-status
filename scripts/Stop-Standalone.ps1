<#
.SYNOPSIS
Stops the locally running standalone widget.

.EXAMPLE
.\scripts\Stop-Standalone.ps1

#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Stop-NamedProcess {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    $announced = $false
    do {
        $processes = @(Get-Process -Name $Name -ErrorAction SilentlyContinue)
        if ($processes.Count -eq 0) {
            if (-not $announced) {
                Write-Host "$Name is not running."
            }
            return
        }

        if (-not $announced) {
            $ids = ($processes.Id | Sort-Object) -join ', '
            Write-Host "Stopping $Name (PID: $ids)..."
            $announced = $true
        }

        $processes | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)

    $remainingIds = (@(Get-Process -Name $Name -ErrorAction SilentlyContinue).Id | Sort-Object) -join ', '
    throw "$Name did not stop within 10 seconds (PID: $remainingIds)."
}

Stop-NamedProcess -Name 'Codex.TaskbarStatus.Standalone'
