# Codex Status for WidBar

A Windows 11 taskbar widget that shows live Codex activity, changed files, subagents, elapsed time, and usage limits through [WidBar](https://andelby.github.io/widbar/).

## Installation

### Option 1 — Installer (recommended)

1. Open the [latest GitHub Release](https://github.com/c00rvus/codex-status/releases/latest).
2. Download `CodexStatusSetup-<version>-x64.exe`.
3. Double-click the installer and follow the prompts.

The installer includes the required runtime, configures the Codex lifecycle hooks, and installs WidBar when needed. No terminal commands, .NET SDK, Windows App Runtime, or Developer Mode are required.

Start the installer normally from the Windows account that runs Codex. Do not use **Run as administrator**; Setup requests UAC permission when needed. Because releases currently use a self-signed certificate, Windows may show **Unknown publisher**. Confirm that the file came from this repository before selecting **More info > Run anyway**.

### Option 2 — Manual installation from source

Requirements: Windows 11 22H2 or later (x64), [.NET SDK 8](https://dotnet.microsoft.com/download/dotnet/8.0), `winget`, and Windows Developer Mode.

```powershell
git clone https://github.com/c00rvus/codex-status.git
cd codex-status
start ms-settings:developers
.\install.cmd
```

`install.cmd` checks prerequisites, builds and registers the widget, installs the bridge, configures the Codex hooks, and restarts WidBar when needed.

## After installation

1. Open WidBar.
2. Open **Layout** and add **Codex Status** to a free taskbar slot.
3. Restart Codex and approve the lifecycle hooks. In Codex CLI, use `/hooks` to review them.

## Features

- Live Codex status and activity.
- Changed-file and subagent counts.
- Five-hour and weekly usage indicators.
- Elapsed task time and animated spinners.
- Configurable order, visibility, spinner color, compact mode, and hide-when-idle behavior.
- Local-only status processing with no telemetry.

## Update and uninstall

To update, run the newest installer from the [Releases page](https://github.com/c00rvus/codex-status/releases). Your preferences and hook configuration are preserved.

To uninstall everything, open **Settings > Apps > Installed apps** and remove **Codex Status (complete installation)**. Use this entry instead of the plain **Codex Status** package entry so the bridge, certificate, and installed hooks are also removed. WidBar is kept because other widgets may use it.

## How it works

The widget combines Codex lifecycle hooks with read-only parsing of local Codex session events. It stores only structured status in `%LOCALAPPDATA%\CodexTaskbarStatus\status.json`; prompts, command bodies, tool output, model responses, and complete transcripts are not copied or sent over the network.

Hooks are installed in `%CODEX_HOME%\hooks.json` when `CODEX_HOME` is set, otherwise in `%USERPROFILE%\.codex\hooks.json`. Existing hook configuration is backed up before changes are made.

## Development

Run the main checks from the repository root:

```powershell
dotnet build .\Codex.TaskbarStatus.ExtensionApp\Codex.TaskbarStatus.ExtensionApp.csproj -c Release -p:Platform=x64
dotnet test .\Codex.TaskbarStatus.Tests\Codex.TaskbarStatus.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-ReleaseHelpers.ps1
```

Build the release installer with [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Compile-Installer.ps1 -Version 1.1.1
```

Pushing a new `vX.Y.Z` tag runs the GitHub Actions workflow and publishes the installer plus `SHA256SUMS.txt` to a new GitHub Release.

## Attribution

Spinner frames are adapted from the MIT-licensed [Eronred/expo-agent-spinners](https://github.com/Eronred/expo-agent-spinners). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Current limitations

- WidBar must remain installed and running.
- Releases currently target x64 Windows only.
- The JSONL fallback depends on an internal Codex format that may change.
