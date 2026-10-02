; 赴死之旅守护器安装程序（Inno Setup 6）。由 scripts\package.ps1 调用：
;   ISCC.exe /DAppVersion=1.2.3 /DSourceDir=<构建好的程序目录> /O<输出目录> installer\NoReturnGuardian.iss
; 默认按当前用户装到 %LOCALAPPDATA%\Programs\NoReturnGuardian，不需要管理员权限；安装位置可以改，
; 也可以在开头的对话框里选择为所有用户安装。快照、设置和日志在 %LOCALAPPDATA%\NoReturnGuardian，
; 安装、升级和卸载都不碰它们。

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\NoReturnGuardian"
#endif
#ifndef AppUrl
  #define AppUrl ""
#endif
#define AppName "赴死之旅守护器"
#define AppExe "NoReturnGuardian.exe"
; 与 Program.MutexName 相同（不带 Local\ 前缀，同一会话内等价）。
#define GuardianMutex "NoReturnGuardian-2FE084E5-A526-474D-9E78-22F0068C78A1"

[Setup]
; AppId 一经发布不能再改，否则升级会装成第二份。
AppId={{295032DA-7333-4CA6-B405-D53B639C79A9}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=No Return Guardian contributors
#if AppUrl != ""
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
#endif
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\NoReturnGuardian
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename=NoReturnGuardian-{#AppVersion}-setup
SetupIconFile=..\src\NoReturnGuardian.App\assets\guardian.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=no

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; 原生组件整体替换：旧版本留下的脚本、运行时或依赖不能和新版本混用。这里没有玩家数据。
Type: filesandordirs; Name: "{app}\native-recovery"

[Files]
; exe 单列在最前：PrepareToInstall 要先解出它来请旧守护器退出，固实压缩下排在前面解得快。
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\*"; Excludes: "\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; 守护器的“更新”带 /RELAUNCH 静默运行安装程序：装好后在托盘里重新打开它，身份和玩家原来的一样（不提权）。
Filename: "{app}\{#AppExe}"; Parameters: "--minimized"; Flags: nowait runasoriginaluser; Check: RelaunchRequested

[UninstallDelete]
; Python 运行时在安装目录里生成的 __pycache__。
Type: filesandordirs; Name: "{app}\native-recovery"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  WebView2Client = 'Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function GuardianRunning: Boolean;
begin
  Result := CheckForMutexes('{#GuardianMutex}');
end;

function GameRunning: Boolean;
var
  Code: Integer;
begin
  { 与 Core 的 GameProcessProbe 相同的两个进程名。 }
  Result := Exec(ExpandConstant('{cmd}'), ExpandConstant('/c ""{sys}\tasklist.exe" /NH | "{sys}\findstr.exe" /I /B /C:"tlou-ii.exe " /C:"tlou-ii-l.exe " >nul"'),
    '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

{ 像托盘“退出”一样请守护器退出（--exit），最多等十秒。原生恢复进行中时它不会退。
  刚启动的守护器可能还没开始接收请求，等待期间每秒重发一次。 }
function StopGuardian(const Exe: String): Boolean;
var
  Code, I: Integer;
begin
  if GuardianRunning and FileExists(Exe) then
    for I := 0 to 49 do
    begin
      if not GuardianRunning then
        Break;
      if I mod 5 = 0 then
        Exec(Exe, '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code);
      Sleep(200);
    end;
  Result := not GuardianRunning;
end;

function RelaunchRequested: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/RELAUNCH') = 0 then
      Result := True;
end;

function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result := (RegQueryStringValue(HKLM32, 'SOFTWARE\' + WebView2Client, 'pv', Version)
      or RegQueryStringValue(HKCU, 'Software\' + WebView2Client, 'pv', Version))
    and (Version <> '') and (Version <> '0.0.0.0');
end;

function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsDotNetInstalled(net48, 0) then
  begin
    SuppressibleMsgBox('需要 .NET Framework 4.8。Windows 10 1903 及更新版本已自带；请先通过 Windows 更新安装它。',
      mbCriticalError, MB_OK, IDOK);
    Result := False;
  end
  else if not WebView2Installed then
    SuppressibleMsgBox('没有检测到 Microsoft Edge WebView2 运行时，守护器的界面需要它。' + #13#10 +
      'Windows 11 已自带；Windows 10 请从 https://go.microsoft.com/fwlink/p/?LinkId=2124703 安装。',
      mbInformation, MB_OK, IDOK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not GuardianRunning then
    Exit;
  if GameRunning and (SuppressibleMsgBox('游戏正在运行。安装期间守护器会暂时退出，出发自动保存也随之暂停，装好后再打开。'
      + #13#10#13#10 + '现在继续吗？', mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES) <> IDYES) then
  begin
    Result := '已取消安装。可以等这局打完、退出游戏后再安装。';
    Exit;
  end;
  ExtractTemporaryFile('{#AppExe}');
  if not StopGuardian(ExpandConstant('{tmp}\{#AppExe}')) then
    Result := '守护器没有退出：可能正在进行恢复，或者正在运行的是不支持自动退出的旧版本。请等恢复结束，从托盘图标菜单选择“退出”，然后重新运行安装程序。';
end;

function InitializeUninstall: Boolean;
begin
  Result := StopGuardian(ExpandConstant('{app}\{#AppExe}'));
  if not Result then
    MsgBox('守护器没有退出：可能正在进行恢复，或者正在运行的是不支持自动退出的旧版本。请等恢复结束，从托盘图标菜单选择“退出”，然后再卸载。', mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { 只删指向这份安装的开机启动；别处的便携版自己的设置不动。 }
    if RegQueryStringValue(HKCU, RunKey, 'NoReturnGuardian', Command)
        and (Pos(Lowercase(ExpandConstant('{app}\{#AppExe}')), Lowercase(Command)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'NoReturnGuardian');
  end
  else if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    MsgBox('守护器已卸载。快照、设置和日志仍保留在 ' + ExpandConstant('{localappdata}\NoReturnGuardian') +
      '（或你在设置里选的位置），重新安装后可以继续使用；不需要的话可以手动删除。', mbInformation, MB_OK);
end;
