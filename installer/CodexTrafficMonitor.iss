; Compile with build-installer.ps1. No installer step removes the user's history.
#ifndef AppVersion
  #error "AppVersion must be supplied by build-installer.ps1"
#endif
#ifndef PayloadDirectory
  #error "PayloadDirectory must be supplied by build-installer.ps1"
#endif
#ifndef InstallerOutput
  #error "InstallerOutput must be supplied by build-installer.ps1"
#endif
#ifndef IsolatedTest
  #define IsolatedTest 0
#endif
#define ProductName "Codex 流量统计"
#define ProductExe "CodexTrafficMonitor.exe"
#if Int(IsolatedTest) != 0
  #define ProductId "CodexTrafficMonitor.InstallerIsolationTest"
  #define ProductGroup "Codex 流量统计（安装测试）"
  #define FileSuffix "-isolated-test"
#else
  #define ProductId "CodexTrafficMonitor.Desktop"
  #define ProductGroup ProductName
  #define FileSuffix ""
#endif

[Setup]
AppId={#ProductId}
AppName={#ProductName}
AppVersion={#AppVersion}
AppVerName={#ProductName} {#AppVersion}
AppPublisher=Codex Traffic Monitor contributors
DefaultDirName={localappdata}\Programs\CodexTrafficMonitor{#FileSuffix}
DefaultGroupName={#ProductGroup}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern dynamic
WizardSizePercent=110
SetupIconFile=..\src\Assets\app.ico
UninstallDisplayIcon={app}\{#ProductExe}
UninstallDisplayName={#ProductName}
OutputDir={#InstallerOutput}
OutputBaseFilename=CodexTrafficMonitor-Setup-{#AppVersion}-win-x64{#FileSuffix}
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
RestartIfNeededByRun=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#ProductName} 安装程序
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#AppVersion}

[Languages]
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#PayloadDirectory}\{#ProductExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDirectory}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
; The embedded copy can prepare an upgrade before the installed EXE is replaced.
Source: "{#PayloadDirectory}\{#ProductExe}"; Flags: dontcopy

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\{#ProductExe}"; WorkingDir: "{app}"
Name: "{group}\卸载 {#ProductName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#ProductGroup}"; Filename: "{app}\{#ProductExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
#if Int(IsolatedTest) == 0
Filename: "{app}\{#ProductExe}"; Description: "打开 {#ProductName}"; Flags: nowait postinstall skipifsilent runasoriginaluser
#endif

[Code]
var
  MaintenanceState: String;
  PreparationComplete: Boolean;
  FinishMaintenanceError: String;

function Quote(const Value: String): String;
begin
  Result := '"' + Value + '"';
end;

function DataDirectory: String;
begin
  Result := ExpandConstant('{localappdata}\CodexTrafficMonitor');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Chosen, History: String;
begin
  Result := True;
  if CurPageID <> wpSelectDir then Exit;
  Chosen := AddBackslash(ExpandFileName(WizardDirValue));
  History := AddBackslash(ExpandFileName(DataDirectory));
  if CompareText(Copy(Chosen, 1, Length(History)), History) = 0 then
  begin
    MsgBox('请将程序安装到单独的文件夹。此目录用于保存流量历史记录。', mbError, MB_OK);
    Result := False;
  end;
end;

function RunMaintenance(const Executable, Operation: String; var ExitCode: Integer): Boolean;
var
  Arguments: String;
begin
  Arguments := '--maintenance ' + Operation + ' --install-path ' + Quote(ExpandConstant('{app}'));
  if MaintenanceState <> '' then Arguments := Arguments + ' --state ' + Quote(MaintenanceState);
  Result := Exec(Executable, Arguments, '', SW_HIDE, ewWaitUntilTerminated, ExitCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitCode: Integer;
begin
  Result := '';
#if Int(IsolatedTest) == 0
  if PreparationComplete then Exit;
  if MaintenanceState = '' then MaintenanceState := ExpandConstant('{tmp}\codex-traffic-maintenance.json');
  ExtractTemporaryFile('{#ProductExe}');
  if not RunMaintenance(ExpandConstant('{tmp}\{#ProductExe}'), 'prepare', ExitCode) then
  begin
    Result := '无法启动安装检查程序。请检查安全软件是否拦截，然后重试。';
    Exit;
  end;
  if ExitCode <> 0 then
  begin
    Result := '请先从右下角托盘菜单退出 Codex 流量统计，再点击重试。' + #13#10 +
      '安装程序不会强制结束采集进程。检查返回码：' + IntToStr(ExitCode);
    Exit;
  end;
  PreparationComplete := True;
#endif
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ExitCode: Integer;
begin
#if Int(IsolatedTest) == 0
  if CurStep = ssPostInstall then
  begin
    if not RunMaintenance(ExpandConstant('{app}\{#ProductExe}'), 'complete', ExitCode) then
      FinishMaintenanceError := '未能启动自动监控迁移。'
    else if ExitCode <> 0 then
      FinishMaintenanceError := '自动监控迁移未完成，返回码：' + IntToStr(ExitCode) + '。';
    if FinishMaintenanceError <> '' then
    begin
      Log(FinishMaintenanceError);
      if not WizardSilent then
        MsgBox('程序已经安装。' + FinishMaintenanceError + #13#10 +
          '请打开“监控设置”，重新开启自动启动。历史记录已保留。', mbInformation, MB_OK);
    end;
  end;
#endif
end;

function GetCustomSetupExitCode: Integer;
begin
  if FinishMaintenanceError <> '' then Result := 10 else Result := 0;
end;

function InitializeUninstall: Boolean;
var
  ExitCode: Integer;
begin
  Result := True;
#if Int(IsolatedTest) == 0
  MaintenanceState := ExpandConstant('{tmp}\codex-traffic-uninstall.json');
  if not RunMaintenance(ExpandConstant('{app}\{#ProductExe}'), 'uninstall', ExitCode) then
  begin
    if not UninstallSilent then
      MsgBox('无法完成卸载检查，请先退出 Codex 流量统计后重试。', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if ExitCode <> 0 then
  begin
    if not UninstallSilent then
      MsgBox('请先从托盘菜单退出 Codex 流量统计后重试；如已开启自动启动，请先在监控设置中关闭。' + #13#10 +
        '卸载检查返回码：' + IntToStr(ExitCode), mbError, MB_OK);
    Result := False;
  end;
#endif
end;
