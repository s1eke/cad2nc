; CAD2NC current-user, offline installer. Compile through build.ps1 or
; scripts/build_installer.ps1 with the pinned Inno Setup 7.1.0 compiler.
#if Ver < EncodeVer(7, 1, 0)
  #error Inno Setup 7.1.0 is required.
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by the build script.
#endif
#ifndef PublishDirectory
  #error PublishDirectory must be supplied by the build script.
#endif
#ifndef InstallerOutputDirectory
  #error InstallerOutputDirectory must be supplied by the build script.
#endif

[Setup]
; Keep this identity unchanged across application versions.
AppId=CAD2NC
AppName=CAD2NC
AppVersion={#AppVersion}
AppVerName=CAD2NC {#AppVersion}
AppPublisher=CAD2NC
VersionInfoVersion={#AppVersion}
VersionInfoProductVersion={#AppVersion}
VersionInfoProductName=CAD2NC
DefaultDirName={localappdata}\Programs\CAD2NC
DefaultGroupName=CAD2NC
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupArchitecture=x64
MinVersion=10.0.19045
UsePreviousAppDir=yes
UsePreviousTasks=yes
UsePreviousGroup=yes
AllowNoIcons=yes
DisableWelcomePage=no
DisableStartupPrompt=yes
AppMutex=Local\CAD2NC.Gui.InstallLock
SetupMutex=Local\CAD2NC.Setup.InstallLock
CloseApplications=no
RestartApplications=no
WizardStyle=modern
SetupIconFile={#PublishDirectory}\Assets\CAD2NC.ico
UninstallDisplayIcon={app}\CAD2NC.exe
UninstallDisplayName=CAD2NC
LicenseFile={#PublishDirectory}\LICENSE
OutputDir={#InstallerOutputDirectory}
OutputBaseFilename=CAD2NC-Setup-{#AppVersion}-x64
Compression=lzma2
SolidCompression=yes
SetupLogging=yes

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Messages]
SetupAppRunningError=CAD2NC 正在运行。%n%n请先保存任务并关闭 CAD2NC，然后单击“确定”继续；单击“取消”退出。
UninstallAppRunningError=卸载程序检测到 CAD2NC 正在运行。%n%n请先保存任务并正常退出 CAD2NC，然后单击“确定”继续卸载；单击“取消”退出卸载程序。
WizardSelectDir=程序放在哪里
SelectDirDesc=通常保持下面的位置即可。
WizardSelectTasks=创建桌面图标
SelectTasksDesc=方便您下次打开 CAD2NC。
SelectTasksLabel2=勾选后，可直接从桌面打开 CAD2NC。
StatusExtractFiles=正在复制程序文件...
StatusCreateDirs=正在准备程序文件夹...
StatusCreateRegistryEntries=正在保存安装信息...
StatusSavingUninstall=正在保存卸载信息...
StatusRunProgram=正在完成，请稍候...

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："

[Files]
; Preserve the complete self-contained runtime, XAML resources and licenses.
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\CAD2NC"; Filename: "{app}\CAD2NC.exe"; WorkingDir: "{app}"; IconFilename: "{app}\CAD2NC.exe"; AppUserModelID: "CAD2NC.Desktop"; Check: not WizardNoIcons
Name: "{userdesktop}\CAD2NC"; Filename: "{app}\CAD2NC.exe"; WorkingDir: "{app}"; IconFilename: "{app}\CAD2NC.exe"; Tasks: desktopicon; AppUserModelID: "CAD2NC.Desktop"; Check: not WizardNoIcons

[Run]
Filename: "{app}\CAD2NC.exe"; WorkingDir: "{app}"; Description: "启动 CAD2NC"; Flags: nowait postinstall skipifsilent runasoriginaluser

; Deliberately no file association registration or user-data deletion:
; %LOCALAPPDATA%\CAD2NC remains available after upgrades and uninstall.

[Code]
// BEGIN INSTALLER MODE LOGIC
// Pure functions are also compiled by scripts/test_installer_modes.ps1.
type
  TInstallMode = (imInstall, imUpdate, imReinstall, imRepairUnknown, imDowngrade);

function TryParseInstallVersion(const Text: String; var Version: Int64): Boolean;
var
  Value, Part: String;
  I, PartCount, Number: Integer;
begin
  Result := False;
  Value := Trim(Text);
  if Value = '' then Exit;
  Part := '';
  PartCount := 1;
  for I := 1 to Length(Value) do begin
    if Value[I] = '.' then begin
      if Part = '' then Exit;
      Number := StrToIntDef(Part, -1);
      if (Number < 0) or (Number > 65535) then Exit;
      Part := '';
      PartCount := PartCount + 1;
      if PartCount > 4 then Exit;
    end else begin
      if (Value[I] < '0') or (Value[I] > '9') then Exit;
      Part := Part + Value[I];
    end;
  end;
  if Part = '' then Exit;
  Number := StrToIntDef(Part, -1);
  if (Number < 0) or (Number > 65535) then Exit;
  Result := StrToVersion(Value, Version);
end;

function SelectInstalledVersion(const RegistryVersion, ExeVersion, DllVersion: String): String;
var
  Parsed: Int64;
begin
  Result := '';
  if TryParseInstallVersion(RegistryVersion, Parsed) then
    Result := Trim(RegistryVersion)
  else if TryParseInstallVersion(ExeVersion, Parsed) then
    Result := Trim(ExeVersion)
  else if TryParseInstallVersion(DllVersion, Parsed) then
    Result := Trim(DllVersion);
end;

function ResolveVersionProbeDirectory(const RegisteredDirectory, RestoredDirectory: String): String;
begin
  Result := Trim(RegisteredDirectory);
  if Result = '' then Result := Trim(RestoredDirectory);
end;

function ResolveInstallMode(const HasInstallation: Boolean;
  const InstalledVersion, PackageVersion: String): TInstallMode;
var
  Previous, Current: Int64;
  Comparison: Integer;
begin
  Result := imInstall;
  if not HasInstallation then Exit;
  Result := imRepairUnknown;
  if not TryParseInstallVersion(InstalledVersion, Previous) then Exit;
  if not TryParseInstallVersion(PackageVersion, Current) then Exit;
  Comparison := ComparePackedVersion(Previous, Current);
  if Comparison < 0 then Result := imUpdate
  else if Comparison = 0 then Result := imReinstall
  else Result := imDowngrade;
end;
// END INSTALLER MODE LOGIC

const
  Cad2NcUninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\CAD2NC_is1';
  DataPreservationText = '您保存的图纸、任务、NC 文件和参数设置都会保留。';

var
  InstallMode: TInstallMode;
  HasPreviousInstallation: Boolean;
  PreviousVersion, PreviousDirectory: String;

procedure DetectInstallation(const FallbackDirectory: String);
var
  RegisteredVersion, ExeVersion, DllVersion: String;
begin
  HasPreviousInstallation := RegKeyExists(HKCU64, Cad2NcUninstallKey);
  PreviousDirectory := '';
  RegisteredVersion := '';
  ExeVersion := '';
  DllVersion := '';
  if HasPreviousInstallation then begin
    RegQueryStringValue(HKCU64, Cad2NcUninstallKey, 'InstallLocation', PreviousDirectory);
    RegQueryStringValue(HKCU64, Cad2NcUninstallKey, 'DisplayVersion', RegisteredVersion);
    PreviousDirectory := ResolveVersionProbeDirectory(PreviousDirectory, FallbackDirectory);
    // An unknown location must not probe a default directory or a relative path.
    if PreviousDirectory <> '' then begin
      GetVersionNumbersString(AddBackslash(PreviousDirectory) + 'CAD2NC.exe', ExeVersion);
      GetVersionNumbersString(AddBackslash(PreviousDirectory) + 'CAD2NC.dll', DllVersion);
    end;
  end;
  PreviousVersion := SelectInstalledVersion(RegisteredVersion, ExeVersion, DllVersion);
  InstallMode := ResolveInstallMode(HasPreviousInstallation, PreviousVersion, '{#AppVersion}');
  Log(Format('CAD2NC mode=%d; previous=%s; package={#AppVersion}; directory=%s', [
    Ord(InstallMode), PreviousVersion, PreviousDirectory]));
end;

function DowngradeMessage: String;
begin
  Result := '电脑上的 CAD2NC ' + PreviousVersion + ' 比此安装包 {#AppVersion} 更新。' + #13#10#13#10 +
    '请使用相同或更新版本的安装包。当前程序和保存的文件未作改动。';
end;

function InitializeSetup: Boolean;
begin
  // Before Inno restores its previous directory, only a recorded location is
  // reliable. A newer leftover EXE in the default directory is unrelated.
  DetectInstallation('');
  Result := InstallMode <> imDowngrade;
  if not Result then
    SuppressibleMsgBox(DowngradeMessage, mbError, MB_OK, IDOK);
end;

function OperationName: String;
begin
  case InstallMode of
    imUpdate: Result := '更新';
    imReinstall, imRepairUnknown: Result := '重新安装';
    imDowngrade: Result := '无法继续';
  else
    Result := '安装';
  end;
end;

function VersionSummary: String;
begin
  case InstallMode of
    imUpdate: Result := '当前版本：' + PreviousVersion + '  →  更新后：{#AppVersion}';
    imReinstall: Result := '电脑上已经安装了 CAD2NC {#AppVersion}。';
    imRepairUnknown: Result := '检测到已有 CAD2NC，但无法读取原版本。';
    imDowngrade: Result := DowngradeMessage;
  else
    Result := '即将安装 CAD2NC {#AppVersion}。';
  end;
end;

procedure InitializeWizard;
var
  ActionButtonWidth: Integer;
  RestoredDirectory: String;
begin
  // Let Inno restore its recorded directory before the fallback version check.
  RestoredDirectory := WizardForm.PrevAppDir;
  if Trim(RestoredDirectory) = '' then RestoredDirectory := WizardDirValue;
  DetectInstallation(RestoredDirectory);
  if HasPreviousInstallation and (Trim(PreviousDirectory) <> '') then
    WizardForm.DirEdit.Text := PreviousDirectory;
  ActionButtonWidth := WizardForm.CalculateButtonWidth(['开始重新安装', '重新安装此版本']);
  if WizardForm.NextButton.Width < ActionButtonWidth then begin
    WizardForm.NextButton.Width := ActionButtonWidth;
    WizardForm.NextButton.Left := WizardForm.CancelButton.Left - ScaleX(8) - ActionButtonWidth;
    WizardForm.BackButton.Left := WizardForm.NextButton.Left - WizardForm.BackButton.Width;
  end;
  WizardForm.Caption := OperationName + ' CAD2NC';
  if InstallMode = imReinstall then
    WizardForm.WelcomeLabel1.Caption := '此版本已经安装'
  else
    WizardForm.WelcomeLabel1.Caption := OperationName + ' CAD2NC';
  WizardForm.WelcomeLabel2.Caption := VersionSummary + #13#10#13#10;
  if InstallMode = imInstall then
    WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption +
      '安装完成后，从桌面图标或开始菜单打开程序。所需组件已包含，无需联网。'
  else if InstallMode <> imDowngrade then begin
    if InstallMode = imReinstall then
      WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption +
        '如果程序无法正常使用，可重新安装此版本，补齐程序文件。' + #13#10#13#10
    else if InstallMode = imRepairUnknown then
      WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption +
        '可以重新安装，补齐程序文件。' + #13#10#13#10;
    WizardForm.WelcomeLabel2.Caption := WizardForm.WelcomeLabel2.Caption +
      DataPreservationText + #13#10#13#10 + '继续前，请保存任务并关闭 CAD2NC。';
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := HasPreviousInstallation and
    ((PageID = wpSelectDir) or (PageID = wpSelectProgramGroup) or (PageID = wpSelectTasks));
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := InstallMode <> imDowngrade;
  if not Result then SuppressibleMsgBox(DowngradeMessage, mbError, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // Recheck immediately before files can change, including silent runs.
  DetectInstallation(WizardDirValue);
  Result := '';
  if InstallMode = imDowngrade then Result := DowngradeMessage;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := VersionSummary + NewLine + NewLine + DataPreservationText;
  if HasPreviousInstallation then
    Result := Result + NewLine + '沿用原来的程序位置和快捷方式设置。'
  else if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
  Result := Result + NewLine + NewLine + '程序位置：' + WizardDirValue;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  ActionCaption: String;
begin
  WizardForm.Caption := OperationName + ' CAD2NC';
  if CurPageID = wpLicense then begin
    WizardForm.PageDescriptionLabel.Caption := '请阅读以下软件许可协议。';
    WizardForm.LicenseLabel1.Caption := '同意协议后即可继续。';
  end else if CurPageID = wpReady then begin
    if InstallMode = imReinstall then ActionCaption := '重新安装此版本'
    else ActionCaption := '开始' + OperationName;
    WizardForm.PageNameLabel.Caption := '准备' + OperationName + ' CAD2NC';
    WizardForm.PageDescriptionLabel.Caption := '确认下面的信息后即可开始。';
    WizardForm.ReadyLabel.Caption := '单击“' + ActionCaption + '”继续。';
    WizardForm.NextButton.Caption := ActionCaption;
  end else if CurPageID = wpPreparing then begin
    WizardForm.PageNameLabel.Caption := '正在准备' + OperationName;
    WizardForm.PageDescriptionLabel.Caption := '正在检查是否可以继续，请稍候。';
  end else if CurPageID = wpInstalling then begin
    WizardForm.PageNameLabel.Caption := '正在' + OperationName + ' CAD2NC';
    WizardForm.PageDescriptionLabel.Caption := '请稍候，完成后即可打开程序。';
  end else if CurPageID = wpFinished then begin
    WizardForm.FinishedHeadingLabel.Caption := 'CAD2NC ' + OperationName + '完成';
    WizardForm.FinishedLabel.Caption := 'CAD2NC {#AppVersion} 已准备好。' + #13#10#13#10 +
      DataPreservationText + #13#10#13#10 + '单击“完成”，即可按下面的选择打开 CAD2NC。';
  end;
end;
