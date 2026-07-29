#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif

#define AppName "Codex Status"
#define AppPublisher "c00rvus"
#define AppUrl "https://github.com/c00rvus/codex-status"
#define AppExe "Codex.TaskbarStatus.Standalone.exe"
#define BridgeExe "Codex.TaskbarStatus.Bridge.exe"
#define StartupRegistryKey "Software\Microsoft\Windows\CurrentVersion\Run"
#define StartupApprovedRegistryKey "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"
#define StartupValueName "Codex Status"
#define UninstallRegistryKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"

[Setup]
AppId={{2C9BDF00-23D4-4469-8EE1-D79BD636A797}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases/latest
DefaultDirName={localappdata}\Programs\Codex Status
DefaultGroupName={#AppName}
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible and not arm64
MinVersion=10.0.22000
OutputDir=..\artifacts\release
OutputBaseFilename=CodexStatusSetup-{#AppVersion}-x64
SetupIconFile=..\Codex.TaskbarStatus.Standalone\Assets\CodexStatus.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=100
SetupLogging=yes
CloseApplications=force
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Installs the Codex Status taskbar application
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}

[Files]
Source: "..\artifacts\release-staging\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\release-staging\bridge\*"; DestDir: "{app}\bridge"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\artifacts\release-staging\tools\Configure-CodexHooks.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--open-settings"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExe}"

[Registry]
Root: HKCU; Subkey: "{#StartupRegistryKey}"; ValueType: string; ValueName: "{#StartupValueName}"; ValueData: """{app}\{#AppExe}"" --startup"; Flags: uninsdeletevalue; Check: ShouldRegisterStartup

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\tools\Configure-CodexHooks.ps1"" -Action Install -BridgePath ""{app}\bridge\{#BridgeExe}"""; WorkingDir: "{app}"; StatusMsg: "Configuring Codex status hooks..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM {#AppExe} /T /F"; Flags: runhidden; RunOnceId: "StopCodexStatus"
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\tools\Configure-CodexHooks.ps1"" -Action Uninstall -BridgePath ""{app}\bridge\{#BridgeExe}"""; WorkingDir: "{app}"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveCodexStatusHooks"

[Code]
var
  ExistingInstallDetected: Boolean;

function InitializeSetup(): Boolean;
begin
  ExistingInstallDetected :=
    RegKeyExists(HKCU, '{#UninstallRegistryKey}');
  Result := True;
end;

function IsStartupExplicitlyDisabled(): Boolean;
var
  ApprovalData: AnsiString;
begin
  Result := False;
  if RegQueryBinaryValue(
      HKCU,
      '{#StartupApprovedRegistryKey}',
      '{#StartupValueName}',
      ApprovalData) then
  begin
    if Length(ApprovalData) > 0 then
      Result := Ord(ApprovalData[1]) = $03;
  end;
end;

function ShouldRegisterStartup(): Boolean;
begin
  Result :=
    (not ExistingInstallDetected) or
    (
      RegValueExists(
        HKCU,
        '{#StartupRegistryKey}',
        '{#StartupValueName}') and
      (not IsStartupExplicitlyDisabled())
    );
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if ShouldRegisterStartup() then
    RegDeleteValue(
      HKCU,
      '{#StartupApprovedRegistryKey}',
      '{#StartupValueName}');

  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/IM {#AppExe} /T /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(
      HKCU,
      '{#StartupRegistryKey}',
      '{#StartupValueName}');
    RegDeleteValue(
      HKCU,
      '{#StartupApprovedRegistryKey}',
      '{#StartupValueName}');
  end;
end;
