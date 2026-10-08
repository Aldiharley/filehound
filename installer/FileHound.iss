; FileHound installer (Inno Setup 6.2+).
; Installs the framework-dependent build and, when the .NET 10 Desktop Runtime is missing, downloads and installs it
; from Microsoft first. Compile with:  ISCC.exe /DVersion=1.4.0 /DPublishDir=..\publish-fdd installer\FileHound.iss

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\publish-fdd"
#endif
#define AppName "FileHound"
#define Publisher "Aldiharley"
#define RepoUrl "https://github.com/Aldiharley/filehound"
#define RuntimeUrl "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
AppId={{A4F1C2D3-6B7E-4F8A-9C0D-1E2F3A4B5C6D}
AppName={#AppName}
AppVersion={#Version}
AppVerName={#AppName} {#Version}
AppPublisher={#Publisher}
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayIcon={app}\FileHound.exe
UninstallDisplayName={#AppName}
OutputDir=..\release
OutputBaseFilename=FileHound-Setup-v{#Version}
SetupIconFile=..\src\FileHound.App\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Per-user by default: no UAC for FileHound itself, and the Run key / %LOCALAPPDATA% cleanup then belong to the user who
; installed it. The .NET runtime installer elevates on its own when it is actually needed. "All users" stays available
; in the dialog for shared PCs (its Run key then targets the elevating account, which is what UsedUserAreasWarning is about).
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsedUserAreasWarning=no
; A running FileHound holds its files: close it first (the tray app exits cleanly on WM_CLOSE) and restart it afterwards.
CloseApplications=yes
CloseApplicationsFilter=FileHound.exe
RestartApplications=no
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
VersionInfoVersion={#Version}
VersionInfoDescription={#AppName} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start FileHound with Windows (keeps the search hotkey ready)"; GroupDescription: "Startup:"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\FileHound.exe"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\FileHound.exe"; Tasks: desktopicon

[Registry]
; The app's own Settings → Start with Windows writes the same value, so both stay in step.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "FileHound"; ValueData: """{app}\FileHound.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\FileHound.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Index snapshots and logs are rebuilt on demand; settings and deletion logs are left for a reinstall unless the user removes the folder.
Type: filesandordirs; Name: "{localappdata}\FileHound\index"
Type: filesandordirs; Name: "{localappdata}\FileHound\logs"

[Code]
var
  RuntimeNeeded: Boolean;
  DownloadPage: TDownloadWizardPage;

// The .NET Desktop Runtime registers every installed version under the 32-bit registry view
// (HKLM\SOFTWARE\WOW6432Node\dotnet\... on a 64-bit OS), so read HKLM32 explicitly; any 10.x satisfies a
// net10.0-windows app. The shared-framework folder is the fallback in case the registry entry is missing.
function DesktopRuntime10Installed(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  FindRec: TFindRec;
begin
  Result := False;
  if RegGetValueNames(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos('10.', Names[I]) = 1 then
      begin
        Result := True;
        exit;
      end;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  if Progress = ProgressMax then Log(Format('Downloaded %s (%d bytes)', [FileName, ProgressMax]));
  Result := True;
end;

procedure InitializeWizard;
begin
  RuntimeNeeded := not DesktopRuntime10Installed();
  Log(Format('.NET 10 Desktop Runtime present: %d', [Ord(not RuntimeNeeded)]));
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading the .NET 10 Desktop Runtime from Microsoft…', @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
  Installer: String;
begin
  Result := True;
  if (CurPageID <> wpReady) or (not RuntimeNeeded) then exit;

  // Download Microsoft's runtime installer (the aka.ms link always points at the latest 10.0.x patch).
  DownloadPage.Clear;
  DownloadPage.Add('{#RuntimeUrl}', 'windowsdesktop-runtime-10-win-x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if DownloadPage.AbortedByUser then
        Log('Runtime download cancelled by the user')
      else
        SuppressibleMsgBox('FileHound needs the .NET 10 Desktop Runtime, and it could not be downloaded:' + #13#10 + AddPeriod(GetExceptionMessage) + #13#10#13#10 +
          'Install it from ' + '{#RuntimeUrl}' + ' and run this setup again.', mbCriticalError, MB_OK, IDOK);
      Result := False;
      exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  Installer := ExpandConstant('{tmp}\windowsdesktop-runtime-10-win-x64.exe');
  WizardForm.StatusLabel.Caption := 'Installing the .NET 10 Desktop Runtime…';
  if not Exec(Installer, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    SuppressibleMsgBox('The .NET 10 Desktop Runtime installer could not be started (error ' + IntToStr(ResultCode) + ').', mbCriticalError, MB_OK, IDOK);
    Result := False;
    exit;
  end;
  // 0 = installed, 1641/3010 = installed but a reboot is pending (the app still runs), 1638 = a newer version exists.
  if (ResultCode <> 0) and (ResultCode <> 1641) and (ResultCode <> 3010) and (ResultCode <> 1638) then
  begin
    SuppressibleMsgBox('The .NET 10 Desktop Runtime did not install (exit code ' + IntToStr(ResultCode) + ').' + #13#10 +
      'Install it from ' + '{#RuntimeUrl}' + ' and run this setup again.', mbCriticalError, MB_OK, IDOK);
    Result := False;
    exit;
  end;
  RuntimeNeeded := not DesktopRuntime10Installed();
  Log(Format('.NET 10 Desktop Runtime present after install: %d', [Ord(not RuntimeNeeded)]));
end;

// Uninstall: stop only the copy being removed (matched by executable path), never a portable or development FileHound.
procedure StopInstalledFileHound();
var
  Locator, Service, Processes, Process: Variant;
  Exe: String;
  I: Integer;
begin
  Exe := ExpandConstant('{app}\FileHound.exe');
  try
    Locator := CreateOleObject('WbemScripting.SWbemLocator');
    Service := Locator.ConnectServer('', 'root\cimv2');
    Processes := Service.ExecQuery('SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name = ''FileHound.exe''');
    for I := 0 to Processes.Count - 1 do
    begin
      Process := Processes.ItemIndex(I);
      // ExecutablePath is Null for processes this account may not inspect (an elevated FileHound, for instance).
      if (not VarIsNull(Process.ExecutablePath)) and (CompareText(Process.ExecutablePath, Exe) = 0) then
      begin
        Log(Format('Stopping installed FileHound (pid %d)', [Process.ProcessId]));
        Process.Terminate(0);
      end;
    end;
    Sleep(1000);
  except
    Log('Could not enumerate FileHound processes: ' + GetExceptionMessage);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then StopInstalledFileHound();
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := '';
  if RuntimeNeeded then
    Result := 'Prerequisite:' + NewLine + Space + '.NET 10 Desktop Runtime (downloaded from Microsoft, about 60 MB)' + NewLine + NewLine;
  if MemoDirInfo <> '' then Result := Result + MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then Result := Result + MemoTasksInfo + NewLine;
end;
