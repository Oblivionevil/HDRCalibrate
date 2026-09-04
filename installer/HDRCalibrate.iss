#ifndef AppVersion
  #error AppVersion must be supplied by tools/Build-Installer.ps1
#endif

#ifndef VersionInfoVersion
  #error VersionInfoVersion must be supplied by tools/Build-Installer.ps1
#endif

#ifndef PayloadDir
  #error PayloadDir must be supplied by tools/Build-Installer.ps1
#endif

#ifndef SupportDir
  #error SupportDir must be supplied by tools/Build-Installer.ps1
#endif

#ifndef AppIconFile
  #error AppIconFile must be supplied by tools/Build-Installer.ps1
#endif

#define AppName "HDR Calibrate"
#define AppExeName "HDRCalibrate.exe"
#define SetupSupportExeName "HDRCalibrate.SetupSupport.exe"

[Setup]
AppId={{6D421644-5F94-4C3D-8F1D-872DB1D5778B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#VersionInfoVersion}
VersionInfoProductVersion={#VersionInfoVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Installer
SetupIconFile={#AppIconFile}
DefaultDirName={localappdata}\Programs\HDR Calibrate
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
SetupArchitecture=x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
CloseApplications=no
RestartApplications=no
SetupLogging=yes
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
OutputBaseFilename=HDRCalibrate-Setup
SetupMutex=HDRCalibrate.Setup.6D4216445F944C3D8F1D872DB1D5778B

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"

[CustomMessages]
english.DesktopTask=Create a desktop shortcut
english.AutoStartTask=Start with Windows (in the notification area)
english.DotNetMissing=Microsoft .NET Framework 4.8.1 or later is required. Install it and run Setup again.
english.StopStartFailed=The running HDR Calibrate instance could not be contacted. Setup did not change any application files.
english.StopTimedOut=HDR Calibrate did not close within the allowed time. Close it manually and try again. No process was force-terminated.
english.StopBusy=HDR Calibrate is still running. Close it manually and try again. No process was force-terminated.
english.StopFailed=HDR Calibrate could not be closed cleanly (exit code %1). Setup did not change any application files.
english.SupportMissing=The installed setup support component is missing. HDR Calibrate cannot be uninstalled safely while its running state is unknown.
german.DesktopTask=Desktop-Verknüpfung erstellen
german.AutoStartTask=Mit Windows starten (im Tray)
german.DotNetMissing=Microsoft .NET Framework 4.8.1 oder neuer wird benötigt. Installieren Sie es und starten Sie das Setup erneut.
german.StopStartFailed=Die laufende HDR-Calibrate-Instanz konnte nicht angesprochen werden. Das Setup hat keine Anwendungsdateien geändert.
german.StopTimedOut=HDR Calibrate wurde nicht rechtzeitig beendet. Beenden Sie die Anwendung manuell und versuchen Sie es erneut. Es wurde kein Prozess erzwungen beendet.
german.StopBusy=HDR Calibrate wird noch ausgeführt. Beenden Sie die Anwendung manuell und versuchen Sie es erneut. Es wurde kein Prozess erzwungen beendet.
german.StopFailed=HDR Calibrate konnte nicht sauber beendet werden (Exitcode %1). Das Setup hat keine Anwendungsdateien geändert.
german.SupportMissing=Die installierte Setup-Unterstützung fehlt. HDR Calibrate kann nicht sicher deinstalliert werden, solange sein Laufzeitstatus unbekannt ist.

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopTask}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStartTask}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\HDRCalibrate.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\HDRCalibrate.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\HDRCalibrate.Dwm.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\HDRCalibrate.DwmHelper.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\HDRCalibrate.DwmHelper.exe.config"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SupportDir}\HDRCalibrate.SetupSupport.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SupportDir}\HDRCalibrate.SetupSupport.exe.config"; DestDir: "{app}"; Flags: ignoreversion

; The new app binary is staged under another name so upgrades can request a
; graceful shutdown before any installed file is replaced. These entries are
; embedded in Setup but are never copied to the installation directory.
Source: "{#PayloadDir}\HDRCalibrate.exe"; DestName: "HDRCalibrate.Stopper.exe"; Flags: dontcopy
Source: "{#PayloadDir}\HDRCalibrate.exe.config"; DestName: "HDRCalibrate.Stopper.exe.config"; Flags: dontcopy
Source: "{#PayloadDir}\HDRCalibrate.Dwm.dll"; DestName: "HDRCalibrate.Dwm.dll"; Flags: dontcopy
Source: "{#SupportDir}\HDRCalibrate.SetupSupport.exe"; DestName: "HDRCalibrate.SetupSupport.tmp.exe"; Flags: dontcopy
Source: "{#SupportDir}\HDRCalibrate.SetupSupport.exe.config"; DestName: "HDRCalibrate.SetupSupport.tmp.exe.config"; Flags: dontcopy

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; IconIndex: 0
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; IconFilename: "{app}\{#AppExeName}"; IconIndex: 0; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "HDRCalibrate"; ValueData: """{app}\{#AppExeName}"" --tray --startup"; Tasks: autostart

[Code]
const
  DotNet481Release = 533320;
  ExitCodeAppTimeout = 5;
  ExitCodeBusy = 170;
  ExitCodeTimeout = 1460;

function IsDotNet481OrLaterInstalled: Boolean;
var
  Release: Cardinal;
begin
  Result :=
    RegQueryDWordValue(
      HKLM64,
      'SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full',
      'Release',
      Release) and
    (Release >= DotNet481Release);
end;

function InitializeSetup: Boolean;
begin
  Result := IsDotNet481OrLaterInstalled;
  if not Result then
    MsgBox(CustomMessage('DotNetMissing'), mbError, MB_OK);
end;

function RunExitExisting(
  const SupportExe: String;
  const CommandExe: String;
  const TargetExe: String;
  var ResultCode: Integer): Boolean;
var
  Parameters: String;
begin
  Parameters := AddQuotes(CommandExe) + ' ' + AddQuotes(TargetExe);
  Result :=
    Exec(
      SupportExe,
      Parameters,
      ExtractFileDir(CommandExe),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);
end;

function StopFailureMessage(const Started: Boolean; const ResultCode: Integer): String;
begin
  if not Started then
    Result := CustomMessage('StopStartFailed')
  else if (ResultCode = ExitCodeAppTimeout) or (ResultCode = ExitCodeTimeout) then
    Result := CustomMessage('StopTimedOut')
  else if ResultCode = ExitCodeBusy then
    Result := CustomMessage('StopBusy')
  else
    Result := Format(CustomMessage('StopFailed'), [IntToStr(ResultCode)]);
end;

function OwnRunValue: String;
begin
  Result := AddQuotes(ExpandConstant('{app}\{#AppExeName}')) + ' --tray --startup';
end;

function LegacyOwnRunValue: String;
begin
  Result := AddQuotes(ExpandConstant('{app}\{#AppExeName}')) + ' --tray';
end;

function IsOwnRunValueRegistered: Boolean;
var
  ExistingValue: String;
begin
  Result :=
    RegQueryStringValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'HDRCalibrate',
      ExistingValue) and
    ((CompareText(ExistingValue, OwnRunValue) = 0) or
     (CompareText(ExistingValue, LegacyOwnRunValue) = 0));
end;

procedure DeleteOwnRunValue;
begin
  if IsOwnRunValueRegistered then
    RegDeleteValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'HDRCalibrate');
end;

var
  AutostartTaskSynchronized: Boolean;

function HasExplicitTaskSelection: Boolean;
var
  Index: Integer;
  Parameter: String;
begin
  Result := False;
  for Index := 1 to ParamCount do
  begin
    Parameter := ParamStr(Index);
    if
      (CompareText(Copy(Parameter, 1, Length('/TASKS=')), '/TASKS=') = 0) or
      (CompareText(Copy(Parameter, 1, Length('/MERGETASKS=')), '/MERGETASKS=') = 0)
    then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

procedure SynchronizeAutostartTask;
begin
  if AutostartTaskSynchronized then
    Exit;

  AutostartTaskSynchronized := True;
  { On upgrades, reflect the app's current switch instead of reviving a task
    choice saved by an older Setup run. Explicit /TASKS or /MERGETASKS command
    line selections, including an empty /TASKS= value, still take precedence. }
  if
    FileExists(ExpandConstant('{app}\{#AppExeName}')) and
    (not HasExplicitTaskSelection)
  then
  begin
    if IsOwnRunValueRegistered then
      WizardSelectTasks('autostart')
    else
      WizardSelectTasks('!autostart');
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpSelectTasks then
    SynchronizeAutostartTask;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  SupportExe: String;
  CommandExe: String;
  TargetExe: String;
  Started: Boolean;
  ResultCode: Integer;
begin
  Result := '';
  { CurPageChanged is not called for hidden pages during silent Setup. Keep
    upgrades from restoring a stale saved autostart task in that mode too. }
  SynchronizeAutostartTask;
  TargetExe := ExpandConstant('{app}\{#AppExeName}');

  { A fresh install has no installed instance whose files could be replaced. }
  if not FileExists(TargetExe) then
    Exit;

  try
    ExtractTemporaryFile('HDRCalibrate.SetupSupport.tmp.exe');
    ExtractTemporaryFile('HDRCalibrate.SetupSupport.tmp.exe.config');
    ExtractTemporaryFile('HDRCalibrate.Stopper.exe');
    ExtractTemporaryFile('HDRCalibrate.Stopper.exe.config');
    ExtractTemporaryFile('HDRCalibrate.Dwm.dll');
  except
    Result := CustomMessage('StopStartFailed');
    Exit;
  end;

  SupportExe := ExpandConstant('{tmp}\HDRCalibrate.SetupSupport.tmp.exe');
  CommandExe := ExpandConstant('{tmp}\HDRCalibrate.Stopper.exe');
  ResultCode := 0;
  Started := RunExitExisting(SupportExe, CommandExe, TargetExe, ResultCode);
  if (not Started) or (ResultCode <> 0) then
    Result := StopFailureMessage(Started, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  { On an upgrade, explicitly remove a formerly selected startup value when
    the user now deselects the task. No other value or Run key is touched. }
  if (CurStep = ssPostInstall) and (not WizardIsTaskSelected('autostart')) then
    DeleteOwnRunValue;
end;

function InitializeUninstall: Boolean;
var
  SupportExe: String;
  TargetExe: String;
  Started: Boolean;
  ResultCode: Integer;
begin
  Result := False;
  TargetExe := ExpandConstant('{app}\{#AppExeName}');

  if not FileExists(TargetExe) then
  begin
    Result := True;
    Exit;
  end;

  SupportExe := ExpandConstant('{app}\{#SetupSupportExeName}');
  if not FileExists(SupportExe) then
  begin
    MsgBox(CustomMessage('SupportMissing'), mbError, MB_OK);
    Exit;
  end;

  ResultCode := 0;
  Started := RunExitExisting(SupportExe, TargetExe, TargetExe, ResultCode);
  if (not Started) or (ResultCode <> 0) then
  begin
    MsgBox(StopFailureMessage(Started, ResultCode), mbError, MB_OK);
    Exit;
  end;

  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { The app may have enabled startup after installation, independently of the
    original Setup task. Remove only the exact command owned by this install. }
  if CurUninstallStep = usUninstall then
    DeleteOwnRunValue;
end;
