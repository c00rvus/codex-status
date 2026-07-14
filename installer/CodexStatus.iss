#ifndef AppVersion
  #define AppVersion "1.1.1"
#endif

#define AppName "Codex Status"
#define AppPublisher "c00rvus"
#define AppUrl "https://github.com/c00rvus/codex-status"

[Setup]
AppId={{2C9BDF00-23D4-4469-8EE1-D79BD636A797}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases/latest
DefaultDirName={autopf}\Codex Status
DefaultGroupName={#AppName}
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible and not arm64
ArchitecturesInstallIn64BitMode=x64compatible and not arm64
MinVersion=10.0.22621
OutputDir=..\artifacts\release
OutputBaseFilename=CodexStatusSetup-{#AppVersion}-x64
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
WizardSizePercent=100
SetupLogging=yes
Uninstallable=yes
CreateUninstallRegKey=no
UpdateUninstallLogAppName=no
UninstallDisplayName={#AppName} (complete installation)
SignTool=codexstatus
SignedUninstaller=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Installs Codex Status for WidBar
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
CloseApplications=no
RestartApplications=no
UsePreviousAppDir=no

[InstallDelete]
Type: files; Name: "{app}\package\*.msix"
Type: files; Name: "{app}\package\*.cer"

[Files]
Source: "..\artifacts\release-staging\package\*.msix"; DestDir: "{app}\package"; Flags: ignoreversion
Source: "..\artifacts\release-staging\package\*.cer"; DestDir: "{app}\package"; Flags: ignoreversion
Source: "..\artifacts\release-staging\bridge\*"; DestDir: "{app}\bridge"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "Install-Release.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Uninstall-Release.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Uninstall-Launcher.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "Configure-CodexHooks.ps1"; DestDir: "{app}\tools"; Flags: ignoreversion
Source: "..\artifacts\release-staging\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; Flags: uninsdeletekey
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "DisplayName"; ValueData: "{#AppName} (complete installation)"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "DisplayVersion"; ValueData: "{#AppVersion}"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "Publisher"; ValueData: "{#AppPublisher}"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "URLInfoAbout"; ValueData: "{#AppUrl}"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "URLUpdateInfo"; ValueData: "{#AppUrl}/releases/latest"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "InstallLocation"; ValueData: "{app}"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "DisplayIcon"; ValueData: "{uninstallexe}"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "UninstallString"; ValueData: "{code:GetUninstallCommand}"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "QuietUninstallString"; ValueData: "{code:GetQuietUninstallCommand}"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: dword; ValueName: "NoModify"; ValueData: "1"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: dword; ValueName: "NoRepair"; ValueData: "1"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "Inno Setup: App Path"; ValueData: "{app}"
Root: HKLM64; Subkey: "Software\Microsoft\Windows\CurrentVersion\Uninstall\{{2C9BDF00-23D4-4469-8EE1-D79BD636A797}_is1"; ValueType: string; ValueName: "Inno Setup: Privileges Required"; ValueData: "admin"

[Code]
var
  InstallCompleted: Boolean;
  InstallHadWarnings: Boolean;
  UninstallHadWarnings: Boolean;

function PowerShellPath(): String;
begin
  Result := ExpandConstant('{sysnative}\WindowsPowerShell\v1.0\powershell.exe');
end;

function PersistedPowerShellPath(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function MachineLogPath(): String;
begin
  Result := ExpandConstant('{commonappdata}\CodexTaskbarStatus\installer.log');
end;

function UserLogPathHint(): String;
begin
  Result := '%LOCALAPPDATA%\CodexTaskbarStatus\installer.log (for the account that launched setup)';
end;

function MachinePhaseParameters(const ScriptName, PhaseName: String): String;
begin
  Result :=
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    AddQuotes(ExpandConstant('{app}\tools\' + ScriptName)) +
    ' -Phase ' + PhaseName +
    ' -InstallRoot ' + AddQuotes(ExpandConstant('{app}')) +
    ' -LogPath ' + AddQuotes(MachineLogPath());
end;

function UserPhaseParameters(const ScriptName, PhaseName: String): String;
begin
  Result :=
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File ' +
    AddQuotes(ExpandConstant('{app}\tools\' + ScriptName)) +
    ' -Phase ' + PhaseName +
    ' -InstallRoot ' + AddQuotes(ExpandConstant('{app}'));
end;

function RunMachinePhase(const ScriptName, PhaseName: String; var ResultCode: Integer): Boolean;
begin
  Result := Exec(
    PowerShellPath(),
    MachinePhaseParameters(ScriptName, PhaseName),
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  if Result and (ResultCode = 2) then
    InstallHadWarnings := True;
  Result := Result and ((ResultCode = 0) or (ResultCode = 2));
end;

function RunOriginalUserPhase(const ScriptName, PhaseName: String; var ResultCode: Integer): Boolean;
begin
  Result := ExecAsOriginalUser(
    PowerShellPath(),
    UserPhaseParameters(ScriptName, PhaseName),
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  if Result and (ResultCode = 2) then
    InstallHadWarnings := True;
  Result := Result and ((ResultCode = 0) or (ResultCode = 2));
end;

function BuildUninstallLauncherCommand(const Quiet: Boolean): String;
begin
  Result :=
    AddQuotes(PersistedPowerShellPath()) +
    ' -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File ' +
    AddQuotes(ExpandConstant('{app}\tools\Uninstall-Launcher.ps1')) +
    ' -UninstallerPath ' + AddQuotes(ExpandConstant('{uninstallexe}'));
  if Quiet then
    Result := Result + ' -Quiet';
end;

function GetUninstallCommand(Param: String): String;
begin
  Result := BuildUninstallLauncherCommand(False);
end;

function GetQuietUninstallCommand(Param: String): String;
begin
  Result := BuildUninstallLauncherCommand(True);
end;

procedure AbortInstall(const MessageText: String; const ResultCode: Integer);
begin
  RaiseException(
    MessageText + #13#10 + #13#10 +
    'Exit code: ' + IntToStr(ResultCode) + #13#10 +
    'Machine log: ' + MachineLogPath() + #13#10 +
    'User log: ' + UserLogPathHint());
end;

function RunOriginalUserTokenPreflight(var ResultCode: Integer): Boolean;
var
  CommandText: String;
begin
  CommandText :=
    '$identity = [Security.Principal.WindowsIdentity]::GetCurrent(); ' +
    'try { $principal = [Security.Principal.WindowsPrincipal]::new($identity); ' +
    'if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 1 } ' +
    '} finally { $identity.Dispose() }; exit 0';
  Result := ExecAsOriginalUser(
    PowerShellPath(),
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command ' + AddQuotes(CommandText),
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  Result := Result and (ResultCode = 0);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if not RunOriginalUserTokenPreflight(ResultCode) then
    Result :=
      'Setup must be started normally from a non-elevated Windows session. ' +
      'Do not use "Run as administrator" or start it from an elevated terminal. ' +
      'Windows will request administrator approval at the correct stage.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  CleanupResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;

  WizardForm.StatusLabel.Caption := 'Validating the original Windows user...';
  if not RunOriginalUserPhase('Install-Release.ps1', 'UserPreflight', ResultCode) then
    AbortInstall('Setup could not obtain a safe non-elevated token for the original user.', ResultCode);

  WizardForm.StatusLabel.Caption := 'Trusting the Codex Status package...';
  if not RunMachinePhase('Install-Release.ps1', 'MachineInstall', ResultCode) then
    AbortInstall('Windows could not trust the Codex Status package certificate.', ResultCode);

  WizardForm.StatusLabel.Caption := 'Installing Codex Status and configuring Codex hooks...';
  if not RunOriginalUserPhase('Install-Release.ps1', 'UserInstall', ResultCode) then
  begin
    { MachineCleanup removes only a certificate added by this setup and only
      when no Codex Status package remains registered for any user. }
    RunMachinePhase('Install-Release.ps1', 'MachineCleanup', CleanupResultCode);
    AbortInstall('Codex Status could not be installed.', ResultCode);
  end;

  InstallCompleted := True;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and InstallCompleted then
  begin
    if InstallHadWarnings then
      WizardForm.FinishedLabel.Caption :=
        'Codex Status is installed, but one or more Codex lifecycle hooks could not be configured. ' +
        'The widget can still run with fallback status detection. See ' + UserLogPathHint() + ' for details.'
    else
      WizardForm.FinishedLabel.Caption :=
        'Codex Status is installed. WidBar has been opened so you can add ' +
        'Codex Status to a free taskbar slot. Restart Codex and approve its lifecycle hooks.';
  end;
end;

function InitializeUninstall(): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareText(ParamStr(Index), '/MACHINEONLY') = 0 then
    begin
      Result := True;
      break;
    end;
  if not Result then
    MsgBox(
      'Use Windows Settings > Apps > Installed apps and select ' +
      '"Codex Status (complete installation)" to remove Codex Status safely.',
      mbError,
      MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if UninstallHadWarnings then
      MsgBox(
        'Codex Status was removed, but certificate-record cleanup needs attention.' + #13#10 +
        'See: ' + MachineLogPath(),
        mbInformation,
        MB_OK);
    exit;
  end;

  if CurUninstallStep <> usUninstall then
    exit;

  if not RunMachinePhase('Uninstall-Release.ps1', 'MachineUninstall', ResultCode) then
  begin
    MsgBox(
      'The Codex Status package certificate could not be removed.' + #13#10 +
      'See: ' + MachineLogPath(),
      mbError,
      MB_OK);
    Abort;
  end
  else if ResultCode = 2 then
    UninstallHadWarnings := True;
end;
