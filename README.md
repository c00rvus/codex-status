# Codex Status for WidBar

A Windows 11 taskbar widget that shows the live status of local Codex tasks through [WidBar](https://andelby.github.io/widbar/).

## Install

1. Open the [latest GitHub Release](https://github.com/c00rvus/codex-status/releases/latest).
2. Under **Assets**, download `CodexStatusSetup-<version>-x64.exe`.
3. Double-click the downloaded file and follow the installer.

The release contains the runtime components required by the widget. Setup deploys the widget and self-contained Codex hook bridge, configures the lifecycle hooks, and automatically installs WidBar when it is missing. End users do not need to run commands, install the .NET SDK, install Windows App Runtime separately, or enable Windows Developer Mode. Internet access is required when WidBar must be downloaded.

Double-click Setup normally while signed in to the same Windows account that runs Codex; do not use **Run as administrator**. Setup itself then requests User Account Control permission to install under Program Files and add the package certificate. Current releases use the project's self-signed certificate and can display Microsoft Defender SmartScreen's **Unknown publisher** warning. Review that the file came from this repository before choosing **More info** and **Run anyway**.

After installation:

1. Open WidBar.
2. Open **Layout** and add **Codex Status** to a free taskbar slot.
3. Restart Codex and approve the new lifecycle hooks. In Codex CLI, review them with `/hooks`.

If WidBar cannot be installed automatically, Setup explains which Microsoft Store component is missing. Install Microsoft App Installer or WidBar as indicated, then run Codex Status Setup again.

## Update and uninstall

To update, download the newest installer from the [Releases page](https://github.com/c00rvus/codex-status/releases) and run it. Your widget preferences and Codex hook configuration are preserved.

To uninstall, open **Settings > Apps > Installed apps**, find **Codex Status (complete installation)**, and select **Uninstall**. Use that entry rather than the plain **Codex Status** package entry so the bridge, certificate, and only the lifecycle hooks installed by Codex Status are cleaned up too. WidBar remains installed because other widgets may use it.

## Features

- Current Codex state and activity.
- Changed-file and subagent counts, including zero values when enabled.
- Five-hour and weekly usage indicators.
- Elapsed task time.
- A random animated text spinner for each active request.
- Configurable item order, visibility, spinner color, compact mode, and hide-when-idle behavior.
- A details flyout with project, model, last update time, and data source.
- Local-only status processing with no telemetry or network requests from the widget.

Spinner frames are adapted from the MIT-licensed [Eronred/expo-agent-spinners](https://github.com/Eronred/expo-agent-spinners). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for attribution and license terms.

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

## Troubleshooting

| Problem | Action |
|---|---|
| SmartScreen reports an unknown publisher | Confirm that the installer came from this repository's Release page, then use **More info > Run anyway**. |
| Setup says it was started elevated | Close it and double-click the installer normally from File Explorer; do not use **Run as administrator** or an elevated terminal. |
| The widget is missing from WidBar | Restart WidBar and check **Widgets** for a disabled Codex Status entry, then add it under **Layout**. |
| Status does not update | Restart Codex and approve the lifecycle hooks. In Codex CLI, review them with `/hooks`. |
| WidBar installation did not finish | Complete its Microsoft Store installation, open WidBar once, and rerun Codex Status Setup. |
| Hook configuration needs recovery | Restore the timestamped `hooks.json.backup-*` file beside the active hooks file. |

## Development

Source builds require:

- Windows 11 22H2 (build 22621) or later, x64.
- [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0).
- [`winget`](https://learn.microsoft.com/windows/package-manager/winget/) through Microsoft App Installer when prerequisites must be installed automatically.
- Windows Developer Mode for unpackaged local registration, or a current Windows SDK for locally signed MSIX builds.

Clone the repository and install a development build from its root:

```powershell
git clone https://github.com/c00rvus/codex-status.git
cd codex-status
start ms-settings:developers
.\install.cmd
```

The installer checks the environment, restores packages, builds and registers the widget, publishes the bridge, backs up the existing hooks configuration, adds Codex Status lifecycle hooks, and restarts WidBar when needed.

Development installer options:

| Option | Purpose |
|---|---|
| `-SkipHooks` | Installs the widget without changing Codex hooks. Status detection then uses the local-session fallback. |
| `-SkipPrerequisites` | Does not install missing dependencies, but still validates the environment. |
| `-UseSignedMsix` | Uses a locally signed MSIX instead of Developer Mode. Requires the Windows SDK and manual trust of the generated certificate. |

Build and test from the repository root:

```powershell
dotnet build .\Codex.TaskbarStatus.ExtensionApp\Codex.TaskbarStatus.ExtensionApp.csproj -c Release -p:Platform=x64
dotnet test .\Codex.TaskbarStatus.Tests\Codex.TaskbarStatus.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-ReleaseHelpers.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Compile-Installer.ps1 -Version 1.1.0
```

`Compile-Installer.ps1` builds the signed MSIX and bridge, stages the installer resources, and creates the signed Setup plus `SHA256SUMS.txt`. Local compilation requires [Inno Setup 6](https://jrsoftware.org/isinfo.php); the GitHub workflow provides it automatically.

Pushing a new tag such as `v1.1.0` runs the GitHub Actions release workflow. It tests the project, builds and timestamps the self-signed package and installer, generates `SHA256SUMS.txt`, and publishes both files to a new GitHub Release. A maintainer can alternatively run the workflow manually and provide an unused `X.Y.Z` version. Existing tags, releases, and release assets are never overwritten by the workflow.

Generated files are written under `artifacts\` and are ignored by Git.

## Project structure

```text
Codex.TaskbarStatus.ExtensionApp/  WidBar preview, flyout, and settings UI
Codex.TaskbarStatus.Core/          State model, hook processing, and JSONL fallback
Codex.TaskbarStatus.Bridge/        Self-contained executable invoked by Codex hooks
Codex.TaskbarStatus.Tests/         State, parser, spinner, and presentation tests
Codex.TaskbarStatus (Package)/     MSIX manifests and image assets
installer/                         Inno Setup end-user installer
scripts/                           Prerequisite, build, test, installation, and hook scripts
install.cmd                        Source-development installer wrapper
```

## Current limitations

- WidBar must remain installed and running.
- Release packages currently target x64 Windows only.
- The JSONL fallback depends on an internal Codex format.
- Codex does not expose a stable total step count, so the widget does not invent a `Step X/Y` value.
