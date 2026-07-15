# Codex Status

Codex Status is a standalone Windows 11 taskbar app that shows Codex activity, changed files, subagents, elapsed time, and usage limits. It runs locally and starts with Windows.

## Install

### Installer (recommended)

1. Open the [latest GitHub Release](https://github.com/c00rvus/codex-status/releases/latest).
2. Download `CodexStatusSetup-<version>-x64.exe`.
3. Run the installer.

The installer includes the required runtime, configures the Codex lifecycle hooks, and starts Codex Status. No terminal commands, .NET SDK, or Developer Mode are required.

### Manual installation

Requirements: Windows 11 22H2 or later (x64), [Git](https://git-scm.com/download/win), and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/c00rvus/codex-status.git
cd codex-status
.\install.cmd
```

`install.cmd` builds the standalone app into a separate source-installation directory, configures the Codex hooks, and enables startup with Windows. It does not overwrite an installer-managed copy.

## Features

- Live activity, changed-file, subagent, and elapsed-time indicators.
- Five-hour and weekly usage indicators.
- Animated spinners with a configurable color.
- Configurable visibility and order for every indicator.
- Compact mode and optional hide-when-idle behavior.
- Local-only status processing with no telemetry.

## Settings and position

Click the taskbar widget to open its details. Use the notification-area icon to open settings, show details, restart, or exit.

Settings let you select a monitor and use automatic, left, center, right, or manual taskbar placement. Displayed indicators can be reordered or disabled independently.

## Update and uninstall

Run the newest installer to update while preserving your settings. To uninstall, open **Settings > Apps > Installed apps** and remove **Codex Status**.

Status and preferences are stored under `%LOCALAPPDATA%\CodexTaskbarStatus`. Hooks are stored in `%CODEX_HOME%\hooks.json` when `CODEX_HOME` is set, otherwise in `%USERPROFILE%\.codex\hooks.json`.

## Development

Run the main checks from the repository root:

```powershell
dotnet build .\Codex.TaskbarStatus.Standalone\Codex.TaskbarStatus.Standalone.csproj -c Release -p:Platform=x64
dotnet test .\Codex.TaskbarStatus.Tests\Codex.TaskbarStatus.Tests.csproj -c Release
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-ReleaseHelpers.ps1
```

For a local development build:

```powershell
.\scripts\Build-Standalone.cmd
.\scripts\Run-Standalone.cmd -SkipBuild -OpenSettings
```

## Release

Build the installer with [Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Compile-Installer.ps1 -Version 2.0.0
```

Pushing a `vX.Y.Z` tag runs GitHub Actions and publishes the installer with `SHA256SUMS.txt`.

## Attribution

Spinner frames are adapted from the MIT-licensed [Eronred/expo-agent-spinners](https://github.com/Eronred/expo-agent-spinners). See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Limitations

- Releases currently target x64 Windows only.
- Fallback session detection depends on an internal Codex JSONL format that may change.
