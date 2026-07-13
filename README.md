# Codex Status for WidBar

A Windows 11 taskbar widget that shows the live status of local Codex tasks through [WidBar](https://andelby.github.io/widbar/).

Windows 11 does not provide a public API for placing an arbitrary status panel inside unused taskbar space. WidBar provides that host and discovers this widget through its `com.widbar.widget` extension contract.

## Features

- Current Codex state and activity.
- Changed-file and subagent counts, including zero values when enabled.
- Elapsed task time.
- A random animated text spinner for each active request.
- Configurable visibility, compact mode, and hide-when-idle behavior.
- A details flyout with project, model, last update time, and data source.
- Local-only status processing with no telemetry or network requests from the widget.

Spinner frames are adapted from the MIT-licensed [Eronred/expo-agent-spinners](https://github.com/Eronred/expo-agent-spinners). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for attribution and license terms.

## Requirements

You need:

- Windows 11 22H2 (build 22621) or later, x64.
- [`winget`](https://learn.microsoft.com/windows/package-manager/winget/) through Microsoft App Installer when dependencies need to be installed automatically.
- Internet access for the first dependency installation and NuGet restore.
- Windows Developer Mode enabled for the recommended installation.

The installer automatically checks and, when missing, installs:

- .NET SDK 8.0. A newer compatible SDK is also accepted.
- Windows App Runtime 2.2, x64.
- WidBar from the Microsoft Store.

It then restores packages, builds and registers the widget, builds the self-contained Codex hook bridge, backs up and updates the Codex hooks configuration, and restarts WidBar if it was already running.

The installed widget and bridge are self-contained. The .NET SDK is needed to build them from source, but a separate .NET runtime is not required afterward.

## Quick start

Clone the repository and enter its root folder:

```powershell
git clone https://github.com/c00rvus/codex-status.git
cd codex-status
```

If you downloaded a ZIP instead, extract it and open PowerShell in the extracted repository folder.

Open Developer settings and enable **Developer Mode**:

```powershell
start ms-settings:developers
```

Then run the installer from the repository root:

```cmd
.\install.cmd
```

The wrapper starts the PowerShell installer with a process-only execution-policy bypass and forwards any installer options. If Developer Mode is still disabled, the installer opens the correct Settings page and stops without changing that security setting. Enable it manually and run the same command again.

After installation:

1. Open WidBar.
2. Open **Layout**.
3. Add **Codex Status** to a free taskbar slot.
4. Restart Codex and approve the new lifecycle hooks. In Codex CLI, use `/hooks`.

## Installer options

| Option | Purpose |
|---|---|
| `-SkipHooks` | Installs the widget without changing the Codex hooks file. Status detection then relies on the local-session fallback. |
| `-SkipPrerequisites` | Prevents automatic dependency installation but still validates the environment. Intended for development or CI environments that are already prepared. |
| `-UseSignedMsix` | Uses a locally signed MSIX instead of Developer Mode. Requires a current Windows SDK and manual certificate trust. |

Example without hook installation:

```cmd
.\install.cmd -SkipHooks
```

## Install without Developer Mode

This alternative requires a current [Windows 10/11 SDK](https://developer.microsoft.com/windows/downloads/windows-sdk/) containing `MakeAppx.exe` and `SignTool.exe`.

Run:

```cmd
.\install.cmd -UseSignedMsix
```

On the first run, the script creates:

```text
artifacts\Codex.TaskbarStatus_1.0.0.0_x64.msix
artifacts\Codex.TaskbarStatus.Local.cer
```

Open the `.cer` file and install it for **Current User** in **Trusted Root Certification Authorities**, then run the same `-UseSignedMsix` command again. The script never trusts the certificate automatically.

## How status detection works

The widget combines two local sources:

1. Codex lifecycle hooks for task start, tools, permission waits, subagents, and completion.
2. Read-only parsing of supported events from the local Codex session JSONL as a fallback and for extra details.

The bridge stores only structured status at:

```text
%LOCALAPPDATA%\CodexTaskbarStatus\status.json
```

The widget does not copy full prompts, command bodies, tool outputs, model responses, or complete transcript lines into this file, and it does not send status data over the network.

Hooks are installed at `%CODEX_HOME%\hooks.json` when `CODEX_HOME` is set, otherwise at `%USERPROFILE%\.codex\hooks.json`. Existing hook configuration is backed up before it is updated.

The current JSONL fallback reads `%USERPROFILE%\.codex\sessions`. Its schema is internal to Codex and may require updates after Codex changes.

## Update

If you cloned the repository with Git, update it from the same folder:

```powershell
git pull --ff-only
.\install.cmd
```

If you downloaded a ZIP, download the latest ZIP, extract it, open PowerShell in the new repository folder, and run `.\install.cmd`. Keep the previous folder until the update succeeds. If the existing installation uses `-UseSignedMsix` and you changed folders, first copy the previous `artifacts\Codex.TaskbarStatus_1.0.0.0_x64.msix` into the same relative path in the new folder so the installer can restore it if deployment fails.

Repeat any options used during the original installation. For example, keep `-UseSignedMsix` if Developer Mode is disabled, and keep `-SkipHooks` if you do not want the installer to add hooks.

Developer Mode registration points directly to `artifacts\layout`, so keep the repository at the same location while the widget is installed.

## Uninstall

Remove the widget package:

```powershell
Get-AppxPackage 'Codex.TaskbarStatus.Widget' | Remove-AppxPackage
```

If hooks were installed, open the active hooks file and remove only handlers whose command references `Codex.TaskbarStatus.Bridge.exe`:

```powershell
$codexHome = if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' }
notepad (Join-Path $codexHome 'hooks.json')
```

Do not delete the whole `hooks.json` file because it may contain unrelated hooks. After removing those handlers, close and reopen Codex so it reloads the configuration. The widget state and bridge can then be deleted with:

```powershell
Remove-Item "$env:LOCALAPPDATA\CodexTaskbarStatus" -Recurse -Force -ErrorAction SilentlyContinue
```

WidBar is intentionally left installed because other widgets may use it.

If you used `-UseSignedMsix`, also remove the local certificate created only for this widget:

```powershell
$subject = 'CN=Codex Taskbar Status Local'
Get-ChildItem Cert:\CurrentUser\Root |
    Where-Object Subject -eq $subject |
    Remove-Item
foreach ($certificate in @(Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $subject)) {
    Remove-Item -LiteralPath $certificate.PSPath -DeleteKey
}
```

## Troubleshooting

| Problem | Action |
|---|---|
| Developer Mode is disabled | Enable it in the Settings page opened by the installer, then rerun the command. |
| The widget is missing from WidBar | Rerun the installer, restart WidBar, and check **Widgets** in WidBar for a disabled Codex Status entry. |
| Status does not update | Restart Codex and verify the active hooks file. In Codex CLI, review hooks with `/hooks`; in Codex Desktop, use the hook approval flow shown by the app. |
| A dependency installation failed | Run `winget source update`, then rerun the installer. |
| `MakeAppx` or `SignTool` was not found | Install a current Windows SDK or use the recommended Developer Mode route. |
| The signed package certificate is not trusted | Install the generated `.cer` for Current User in Trusted Root Certification Authorities, then rerun with `-UseSignedMsix`. |
| Hook configuration needs recovery | Restore the timestamped `hooks.json.backup-*` file created beside the active hooks file. |

## Build and test

From the repository root:

```powershell
dotnet build .\Codex.TaskbarStatus.ExtensionApp\Codex.TaskbarStatus.ExtensionApp.csproj -c Release -p:Platform=x64
dotnet test .\Codex.TaskbarStatus.Tests\Codex.TaskbarStatus.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Msix.ps1
```

Generated files are written under `artifacts\` and are ignored by Git.

## Project structure

```text
Codex.TaskbarStatus.ExtensionApp/  WidBar preview, flyout, and settings UI
Codex.TaskbarStatus.Core/          state model, hook processing, and JSONL fallback
Codex.TaskbarStatus.Bridge/        self-contained executable invoked by Codex hooks
Codex.TaskbarStatus.Tests/         state, parser, spinner, and presentation tests
Codex.TaskbarStatus (Package)/     MSIX manifests and image assets
scripts/                           prerequisites, build, installation, and hook scripts
install.cmd                        one-command installer wrapper
```

## Current limitations

- WidBar must remain installed and running.
- The local installer currently produces x64 packages only.
- The JSONL fallback depends on an internal Codex format.
- Codex does not expose a stable total step count, so the widget does not invent a `Step X/Y` value.
