; NexPane installer - Inno Setup 6 (ARCHITECTURE section 19.3, TODO P1.11 / P7.08).
;
; Compile through eng\installer.ps1, which passes:
;   /DAppVersion=<Directory.Build.props Version>   /DPublishDir=<absolute publish folder>
; Optional define:
;   /DNoBranding  PRE-PHASE-2 PIPELINE VERIFICATION ONLY (eng\installer.ps1 -AllowMissingBranding).
;                 Omits SetupIconFile / WizardImageFile / WizardSmallImageFile. Release builds (P7.08) must not use it.
;
; Decisions: D1 proprietary EULA on the license page; D2 unsigned (no SignTool directive), classic Explorer verbs
; only (Windows 11 shows them under "Show more options"); D3 Index Service task checked by default in admin installs.

#define AppName "NexPane"
#define AppPublisher "NexPane"
#define AppExeName "NexPane.exe"
#define ServiceExeName "NexPane.IndexService.exe"
#define ServiceName "NexPaneIndex"
#define ServiceDisplayName "NexPane Index Service"
#define RepoRoot RemoveBackslashUnlessRoot(ExtractFilePath(RemoveBackslashUnlessRoot(SourcePath)))

#ifndef PublishDir
  #define PublishDir RepoRoot + "\build\publish\win-x64"
#endif

#if !FileExists(PublishDir + "\" + AppExeName)
  #error NexPane.exe not found in the publish folder. Run eng\publish.ps1 first (or pass /DPublishDir=<folder>).
#endif
#if !FileExists(PublishDir + "\" + ServiceExeName)
  #error NexPane.IndexService.exe not found in the publish folder. Run eng\publish.ps1 first.
#endif

#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(PublishDir + "\" + AppExeName)
#endif

#ifndef NoBranding
  #define BrandIcon RepoRoot + "\assets\icons\NexPane.ico"
  #define BrandDir RepoRoot + "\assets\installer"
  #define WizardImages BrandDir + "\wizard-164x314.bmp," + BrandDir + "\wizard-205x393.bmp," + BrandDir + "\wizard-246x471.bmp," + BrandDir + "\wizard-287x550.bmp," + BrandDir + "\wizard-328x628.bmp"
  #define WizardSmallImages BrandDir + "\wizard-small-55.bmp," + BrandDir + "\wizard-small-69.bmp," + BrandDir + "\wizard-small-83.bmp," + BrandDir + "\wizard-small-97.bmp," + BrandDir + "\wizard-small-110.bmp"
  #if !FileExists(BrandIcon)
    #error Branding asset missing: assets\icons\NexPane.ico (Phase 2, P2.04). Pre-Phase-2 pipeline verification only: eng\installer.ps1 -AllowMissingBranding.
  #endif
  #if !FileExists(BrandDir + "\wizard-164x314.bmp") || !FileExists(BrandDir + "\wizard-205x393.bmp") || !FileExists(BrandDir + "\wizard-246x471.bmp") || !FileExists(BrandDir + "\wizard-287x550.bmp") || !FileExists(BrandDir + "\wizard-328x628.bmp")
    #error Branding asset missing: assets\installer\wizard-*.bmp (Phase 2, P2.06). Pre-Phase-2 pipeline verification only: eng\installer.ps1 -AllowMissingBranding.
  #endif
  #if !FileExists(BrandDir + "\wizard-small-55.bmp") || !FileExists(BrandDir + "\wizard-small-69.bmp") || !FileExists(BrandDir + "\wizard-small-83.bmp") || !FileExists(BrandDir + "\wizard-small-97.bmp") || !FileExists(BrandDir + "\wizard-small-110.bmp")
    #error Branding asset missing: assets\installer\wizard-small-*.bmp (Phase 2, P2.06). Pre-Phase-2 pipeline verification only: eng\installer.ps1 -AllowMissingBranding.
  #endif
#endif

[Setup]
; Fixed forever: changing AppId breaks in-place upgrades (ARCHITECTURE section 21).
AppId={{23676795-F60E-4EAF-95C6-C4D81BAD01C1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (C) 2026 NexPane. All rights reserved.
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
DisableWelcomePage=no
DisableDirPage=auto
DisableProgramGroupPage=auto
LicenseFile={#RepoRoot}\LICENSE.txt
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
WizardStyle=modern
#ifndef NoBranding
SetupIconFile={#BrandIcon}
WizardImageFile={#WizardImages}
WizardSmallImageFile={#WizardSmallImages}
#endif
OutputDir={#RepoRoot}\dist
OutputBaseFilename=NexPane-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
english.ContextMenuTask=Add "Open in NexPane" to the Explorer context menu (Windows 11: under "Show more options")
english.IndexServiceTask=Install NexPane Index Service for fast NTFS indexing%nRuns as a background Windows service with administrator rights to read the NTFS change journal. File names are only shown to Windows users who are allowed to list the containing folder. Nothing leaves this computer.
english.ExplorerGroup=Explorer integration:
english.IndexingGroup=Fast indexing:
english.OpenInNexPane=Open in NexPane
english.ServiceDescription=Reads the NTFS change journal so NexPane can index and search files quickly. File names are only returned to users allowed to list the containing folder. Nothing leaves this computer.
english.ServiceSetupFailed=The NexPane Index Service could not be set up (%1 exited with code %2).%n%nNexPane will still work and will index folders with its slower built-in folder walker.
english.RemoveUserData=Do you also want to remove your NexPane user data (index, settings, sessions, downloaded models and GPU runtimes) from%n%1 ?%n%nChoose No to keep it for a later reinstall.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "contextmenu"; Description: "{cm:ContextMenuTask}"; GroupDescription: "{cm:ExplorerGroup}"
; Decision D3: checked by default (no "unchecked" flag); only offered in admin (all users) installs.
Name: "indexservice"; Description: "{cm:IndexServiceTask}"; GroupDescription: "{cm:IndexingGroup}"; Check: IsAdminInstallMode

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\LICENSE.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RepoRoot}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
; Classic Explorer verbs (ARCHITECTURE section 19.3). HKA = HKLM in admin installs, HKCU in per-user installs.
; Removed on uninstall (uninsdeletekey); removed on upgrade when the task is deselected (deletekey + "not contextmenu").
Root: HKA; Subkey: "Software\Classes\Directory\shell\NexPane"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInNexPane}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\NexPane"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\NexPane\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%V"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\NexPane"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInNexPane}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\NexPane"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\NexPane\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%V"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\NexPane"; ValueType: string; ValueName: ""; ValueData: "{cm:OpenInNexPane}"; Flags: uninsdeletekey; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\NexPane"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#AppExeName},0"; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\NexPane\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%V"""; Tasks: contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\shell\NexPane"; Flags: deletekey dontcreatekey; Tasks: not contextmenu
Root: HKA; Subkey: "Software\Classes\Directory\Background\shell\NexPane"; Flags: deletekey dontcreatekey; Tasks: not contextmenu
Root: HKA; Subkey: "Software\Classes\Drive\shell\NexPane"; Flags: deletekey dontcreatekey; Tasks: not contextmenu

[Run]
; The app must never run elevated (UIPI breaks drag/drop with Explorer): launch as the original user.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
// Index Service management (ARCHITECTURE sections 7.5 and 19.3). Done in [Code] rather than [Run]/[UninstallRun]
// so that exit codes are checked, an existing service is reconfigured on upgrade instead of failing "sc create",
// and the service is fully stopped (net stop is synchronous) before files are replaced or removed.

const
  ServiceName = '{#ServiceName}';
  ScErrorServiceAlreadyRunning = 1056;
  NetErrorServiceNotStarted = 2;

function SystemTool(const FileName: String): String;
begin
  Result := ExpandConstant('{sys}\') + FileName;
end;

function RunHidden(const FileName, Params: String; var ResultCode: Integer): Boolean;
begin
  Result := Exec(FileName, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if Result then
    Log(Format('Ran "%s" %s -> exit code %d', [FileName, Params, ResultCode]))
  else
    Log(Format('Could not start "%s" %s -> error %d (%s)', [FileName, Params, ResultCode, SysErrorMessage(ResultCode)]));
end;

function IndexServiceExists: Boolean;
var
  Code: Integer;
begin
  // sc query returns 0 for an installed service (any state) and 1060 when it does not exist.
  Result := RunHidden(SystemTool('sc.exe'), 'query ' + ServiceName, Code) and (Code = 0);
end;

procedure StopIndexService;
var
  Code: Integer;
begin
  if IndexServiceExists then
  begin
    // net stop waits until the service has stopped; exit code 2 means it was not running.
    if RunHidden(SystemTool('net.exe'), 'stop ' + ServiceName, Code) and (Code <> 0) and (Code <> NetErrorServiceNotStarted) then
      Log(Format('net stop %s returned %d; continuing', [ServiceName, Code]));
  end;
end;

procedure RemoveIndexService;
var
  Code: Integer;
begin
  if IndexServiceExists then
  begin
    StopIndexService;
    if RunHidden(SystemTool('sc.exe'), 'delete ' + ServiceName, Code) and (Code <> 0) then
      Log(Format('sc delete %s returned %d', [ServiceName, Code]));
  end;
end;

procedure ReportServiceFailure(const Step: String; Code: Integer);
begin
  SuppressibleMsgBox(FmtMessage(CustomMessage('ServiceSetupFailed'), [Step, IntToStr(Code)]), mbError, MB_OK, IDOK);
end;

procedure InstallOrUpdateIndexService;
var
  Code: Integer;
  Verb, Params: String;
begin
  if IndexServiceExists then
    Verb := 'config'
  else
    Verb := 'create';

  // binPath= "\"C:\...\NexPane.IndexService.exe\"" keeps the quoted path intact for paths with spaces.
  Params := Verb + ' ' + ServiceName +
    ' binPath= "\"' + ExpandConstant('{app}\{#ServiceExeName}') + '\""' +
    ' start= delayed-auto obj= LocalSystem DisplayName= "{#ServiceDisplayName}"';
  if not RunHidden(SystemTool('sc.exe'), Params, Code) or (Code <> 0) then
  begin
    ReportServiceFailure('sc.exe ' + Verb, Code);
    Exit;
  end;

  if not RunHidden(SystemTool('sc.exe'), 'description ' + ServiceName + ' "' + CustomMessage('ServiceDescription') + '"', Code) or (Code <> 0) then
    Log(Format('sc description returned %d (non-fatal)', [Code]));

  if not RunHidden(SystemTool('sc.exe'), 'start ' + ServiceName, Code) or ((Code <> 0) and (Code <> ScErrorServiceAlreadyRunning)) then
    ReportServiceFailure('sc.exe start', Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  // Upgrade: stop the running service so its files can be replaced.
  if IsAdminInstallMode then
    StopIndexService;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and IsAdminInstallMode then
  begin
    if WizardIsTaskSelected('indexservice') then
      InstallOrUpdateIndexService
    else
      // Upgrade with the task deselected: remove a previously installed service.
      RemoveIndexService;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if (CurUninstallStep = usUninstall) and IsAdminInstallMode then
    // Before files are deleted: stop (synchronously) and delete the service.
    RemoveIndexService;

  if CurUninstallStep = usPostUninstall then
  begin
    // User data, including runtimes\ (GPU Whisper packs) and downloaded models. Default answer: No.
    DataDir := ExpandConstant('{localappdata}\NexPane');
    if DirExists(DataDir) then
      if SuppressibleMsgBox(FmtMessage(CustomMessage('RemoveUserData'), [DataDir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
