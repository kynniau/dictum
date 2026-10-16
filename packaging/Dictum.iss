; Dictum's direct-download installer (SPEC section 18.3), for Inno Setup 7 (6.x compiles it too).
; Build it with scripts\build-installer.ps1, which defines AppVersion and NumericVersion from the project's <Version>,
; PublishDir (the self-contained build in dist\Dictum) and OutputDir (dist).
;
; Per user, no administrator rights: the program goes to %LOCALAPPDATA%\Programs\Dictum, the folder install.ps1 uses
; and Dictum itself looks for (SPEC section 18.2). A Start Menu entry, an optional desktop shortcut, an optional
; launch at login (the same HKCU Run value Dictum's own toggle writes), launch after install, and an Apps & features
; entry that runs the uninstaller. Settings (%APPDATA%\Dictum) and models, logs and recordings (%LOCALAPPDATA%\Dictum)
; live outside the program folder, so an upgrade never touches them; the uninstaller asks before deleting them and a
; silent uninstall keeps them, as scripts\uninstall.ps1 does.

#ifndef AppVersion
  #error Build with scripts\build-installer.ps1: AppVersion is not defined.
#endif
#ifndef NumericVersion
  #error Build with scripts\build-installer.ps1: NumericVersion is not defined.
#endif
#ifndef PublishDir
  #error Build with scripts\build-installer.ps1: PublishDir is not defined.
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
; CheckVcRedist: the build carries no Visual C++ runtime (publish.ps1 without -VcRuntimeDir), so Setup checks for the
; Microsoft Visual C++ Redistributable at the end. build-installer.ps1 defines it, with MinVcMajor and MinVcMinor.
#ifdef CheckVcRedist
  #ifndef MinVcMajor
    #error Build with scripts\build-installer.ps1: MinVcMajor is not defined.
  #endif
  #ifndef MinVcMinor
    #error Build with scripts\build-installer.ps1: MinVcMinor is not defined.
  #endif
#endif

[Setup]
; AppId ties every version together (an upgrade replaces the previous one; Apps & features finds the uninstaller).
; Never change it.
AppId={{91637A2B-54D2-44E8-A6F4-B7A8319E9E65}
AppName=Dictum
AppVersion={#AppVersion}
AppVerName=Dictum {#AppVersion}
AppPublisher=Kynni Pty Ltd
AppPublisherURL=https://dictum.kynni.au
AppSupportURL=https://github.com/kynniau/dictum/issues
AppUpdatesURL=https://github.com/kynniau/dictum/releases
AppContact=hello@kynni.com.au
AppCopyright=Copyright (C) 2026 Kynni Pty Ltd
VersionInfoVersion={#NumericVersion}
VersionInfoProductName=Dictum
VersionInfoDescription=Dictum installer
; Per user only: {autopf} is %LOCALAPPDATA%\Programs and {autoprograms} the user's Start Menu\Programs.
PrivilegesRequired=lowest
DefaultDirName={autopf}\Dictum
DisableDirPage=yes
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 1809, the oldest Windows Dictum.exe supports (SupportedOSPlatformVersion 10.0.17763.0).
MinVersion=10.0.17763
; Dictum's single-instance mutex (src\Dictum.Windows\Shell\SingleInstance.cs): Setup and Uninstall ask the user to
; quit Dictum first instead of replacing files under it.
AppMutex=Local\Dictum.SingleInstance
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=Dictum-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
UninstallDisplayName=Dictum
UninstallDisplayIcon={app}\Dictum.exe

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "{cm:AutoStartProgram,Dictum}"; GroupDescription: "{cm:AutoStartProgramGroupDescription}"; Flags: unchecked

[InstallDelete]
; Native runtimes are replaced as a set, so an upgrade never loads a stale whisper or llama DLL from an older version.
Type: filesandordirs; Name: "{app}\runtimes"

[Files]
; A .env beside Dictum.exe is the user's secrets file: never shipped, and an upgrade leaves the user's own in place.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.env"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Dictum"; Filename: "{app}\Dictum.exe"; WorkingDir: "{app}"; Comment: "Dictum - voice dictation"
Name: "{autodesktop}\Dictum"; Filename: "{app}\Dictum.exe"; WorkingDir: "{app}"; Comment: "Dictum - voice dictation"; Tasks: desktopicon

[Registry]
; Launch at login: the value Dictum's tray toggle and install.ps1 -LaunchAtLogin write (RunKeyStartup: "<exe path>").
; The uninstaller removes it only while it still starts this copy (see [Code]).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Dictum"; ValueData: """{app}\Dictum.exe"""; Tasks: startup

[Run]
Filename: "{app}\Dictum.exe"; Description: "{cm:LaunchProgram,Dictum}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  RemoveUserData: Boolean;

function InitializeSetup(): Boolean;
var
  FindRec: TFindRec;
begin
  Result := True;
  { SPEC section 18.2: the Store build and this one on one PC is unsupported; Dictum warns about it too. }
  if FindFirst(ExpandConstant('{localappdata}\Packages\Kynni.Dictum_*'), FindRec) then
  begin
    FindClose(FindRec);
    SuppressibleMsgBox('The Microsoft Store version of Dictum is also installed on this PC. Running both is not ' +
      'supported: uninstall one of them.', mbInformation, MB_OK, IDOK);
  end;
  { The earlier build was called Dictate. Left running, it holds files Dictum moves across on its first start. }
  if FileExists(ExpandConstant('{localappdata}\Programs\Dictate\Dictate.exe')) then
    SuppressibleMsgBox('The earlier build, Dictate, is installed. Quit it and uninstall it with its own ' +
      'uninstall.ps1 -KeepData before you start Dictum: Dictum then moves Dictate''s settings, models and ' +
      'recordings across on its first start.', mbInformation, MB_OK, IDOK);
end;

#ifdef CheckVcRedist
{ whisper.cpp and llama.cpp need the Microsoft Visual C++ Redistributable (x64) {#MinVcMajor}.{#MinVcMinor} or later
  (docs\DECISIONS.md, "Visual C++ runtime"); its version is in this key ("Redistribute Visual C++ files", Microsoft
  Learn, read 17/10/2026). Setup is per user and cannot install it, so it offers Microsoft's download instead. }
const
  VcRuntimeKey = 'SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64';
  VcRedistUrl = 'https://aka.ms/vc14/vc_redist.x64.exe';

function VcRuntimeAt(RootKey: Integer): Boolean;
var
  Installed, Major, Minor: Cardinal;
begin
  Result := RegQueryDWordValue(RootKey, VcRuntimeKey, 'Installed', Installed) and (Installed = 1) and
    RegQueryDWordValue(RootKey, VcRuntimeKey, 'Major', Major) and
    RegQueryDWordValue(RootKey, VcRuntimeKey, 'Minor', Minor) and
    ((Major > {#MinVcMajor}) or ((Major = {#MinVcMajor}) and (Minor >= {#MinVcMinor})));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and not (VcRuntimeAt(HKLM64) or VcRuntimeAt(HKLM32)) then
  begin
    Log('The Microsoft Visual C++ Redistributable (x64) {#MinVcMajor}.{#MinVcMinor} or later is not installed.');
    if SuppressibleMsgBox('Dictum''s speech recognition and AI clean-up need the Microsoft Visual C++ ' +
      'Redistributable (x64), version {#MinVcMajor}.{#MinVcMinor} or later, and this PC does not have it. Installing it ' +
      'needs administrator rights.' + #13#10#13#10 + 'Download it from Microsoft now? Run the downloaded ' +
      'vc_redist.x64.exe, then start Dictum.' + #13#10#13#10 + VcRedistUrl, mbConfirmation, MB_YESNO, IDNO) = IDYES then
      ShellExecAsOriginalUser('open', VcRedistUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;
#endif

function UserDataFolders(): String;
begin
  Result := ExpandConstant('{localappdata}\Dictum') + #13#10 + ExpandConstant('{userappdata}\Dictum');
  if FileExists(ExpandConstant('{app}\.env')) then
    Result := Result + #13#10 + ExpandConstant('{app}\.env');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Value: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Ask before anything is removed; a silent uninstall (Intune, scripts) keeps the user's data. }
    RemoveUserData := False;
    if not UninstallSilent() and (DirExists(ExpandConstant('{localappdata}\Dictum')) or
      DirExists(ExpandConstant('{userappdata}\Dictum')) or FileExists(ExpandConstant('{app}\.env'))) then
      RemoveUserData := MsgBox('Also delete Dictum''s models, logs, calibration recordings, settings, dictionary, ' +
        'corrections and API keys (.env)?' + #13#10#13#10 + UserDataFolders() + #13#10#13#10 +
        'Choose No to keep them for a later install. Folders you chose yourself in Settings are not touched.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;

    { Launch at login: only the value that starts this copy, as Dictum and uninstall.ps1 do. }
    if RegQueryStringValue(HKCU, RunKey, 'Dictum', Value) then
      if CompareText(RemoveQuotes(Trim(Value)), ExpandConstant('{app}\Dictum.exe')) = 0 then
        RegDeleteValue(HKCU, RunKey, 'Dictum');
  end;

  if (CurUninstallStep = usPostUninstall) and RemoveUserData then
  begin
    DelTree(ExpandConstant('{localappdata}\Dictum'), True, True, True);
    DelTree(ExpandConstant('{userappdata}\Dictum'), True, True, True);
    DeleteFile(ExpandConstant('{app}\.env'));
    RemoveDir(ExpandConstant('{app}'));
  end;
end;
