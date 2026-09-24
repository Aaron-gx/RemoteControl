; ============================================================================
;  远程控制 · 主控端 —— Inno Setup 安装脚本
;
;  与被控端安装程序（setup-agent.iss）同一套形态：
;   · 标准 Windows 安装向导（欢迎 → 安装位置 → 附加任务 → 连接设置 → 安装 → 完成）
;   · 装进 Program Files，注册到「应用和功能」（可正常卸载，带卸载器）
;   · 开始菜单快捷方式 + 卸载入口
;   · 服务器地址与连接口令**内嵌**在安装程序里：客户只拷这一个 exe，装完双击即用
;   · 支持 /SILENT、/VERYSILENT 等标准无人值守参数
;
;  为什么主控端也要安装程序：以前是发一个 zip，让客户"解压到某个目录再双击 Viewer.exe"——
;  解压到哪、要不要建快捷方式、以后怎么卸载全靠人记；换成安装程序后与客户心里的
;  "装软件"完全一致（应用和功能里能卸载、开始菜单能找到）。
;
;  编译：scripts\build-setup-viewer.ps1  （ISCC.exe 在 E:\tools\innosetup）
; ============================================================================

#define AppName        "远程控制 · 主控端"
#define AppShortName   "远程控制主控端"
#define AppVersion     "1.0.0"
#define AppPublisher   "远程控制"
#define AppExeName     "Viewer.exe"

; 默认中继地址：没有预置值时用这个（向导里可改）
#ifndef ServerUrl
  #define ServerUrl "156.225.30.231:18080"
#endif

; 预置连接口令（构建脚本 /DDefaultToken=... 或安装包同目录的 setup-defaults.ini）
#ifndef DefaultToken
  #define DefaultToken ""
#endif

; 仓库根目录（绝对路径）：Inno 对 Source 里 "..\..\.." 这种相对路径匹配不到文件
#ifndef RepoRoot
  #define RepoRoot SourcePath + "..\..\.."
#endif

[Setup]
AppId={{C41F7B93-2D68-4E5A-9A17-6B3C8D2E4F55}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppShortName}
DisableProgramGroupPage=yes
DisableWelcomePage=no
OutputDir={#RepoRoot}\build\setup-viewer
OutputBaseFilename={#AppShortName}-安装程序
SetupIconFile={#RepoRoot}\src\Assets\viewer.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
ShowLanguageDialog=no
; 正在运行的主控端会锁住 Viewer.exe / ffmpeg.exe —— Inno 自己处理会弹"请关闭程序"，
; 这里关掉它，改由 [Code] 的 PrepareToInstall 显式 taskkill（与被控端一致的做法）
CloseApplications=no
RestartApplications=no
AllowNoIcons=yes
DisableDirPage=no
Uninstallable=yes

[Languages]
Name: "cn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
cn.RelayPageTitle=连接设置
cn.RelayPageDesc=主控端用这份设置连到中继服务器；不确定就保持默认。
cn.RelayServerLabel=中继服务器地址（IP:端口）
cn.TokenLabel=连接口令
cn.TokenDefault=留空则只能在「未启用鉴权」的中继上连接
cn.TokenEmptyWarn=连接口令为空。%n%n如果中继服务器启用了鉴权（服务端配置了 RC_TOKEN），主控端将无法连接、也列不出在线被控端。%n%n口令必须与服务端的 RC_TOKEN 完全一致。%n%n确实要继续（例如内网测试、中继未启用鉴权）吗？
cn.TokenEmptyAbort=请返回上一步把「连接口令」填好再安装
cn.TaskGroupDesc=选择要一起启用的功能；默认项适合绝大多数电脑。
cn.TaskNoDefender=跳过 Defender 排除项
cn.TaskNoDefenderHint=默认会为主控端程序目录加排除项，避免程序被误杀。
cn.TaskDesktopIcon=创建桌面快捷方式
cn.TaskRunApp=安装完成后启动主控端
cn.Configured=已写入预置的服务器与连接口令。

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:TaskGroupDesc}"; Flags: unchecked
Name: "nodefender";  Description: "{cm:TaskNoDefender}";  GroupDescription: "{cm:TaskGroupDesc}"
Name: "runapp";      Description: "{cm:TaskRunApp}";      GroupDescription: "{cm:TaskGroupDesc}"

[Files]
; 主控端程序 / ffmpeg / 使用说明（构建脚本先补齐到 build\viewer）。
; 刻意排除 config 与 logs：那是开发机/上次运行留下的东西，不该进安装包，
; 更不能覆盖用户已有的配置（预置值由安装程序在装完后写 viewer-defaults.txt）。
Source: "{#RepoRoot}\build\viewer\*"; DestDir: "{app}"; \
  Excludes: "config\*,logs\*"; \
  Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
; 程序自己要在这些目录里写日志/配置，而 Program Files 默认只有管理员可写 ——
; 不显式授权的话，普通用户运行时会写不进去（实测：装到 C:\Program Files 后一启动就弹
; "Access to the path ...\logs is denied"）。users-modify = 所有用户可修改。
Name: "{app}\config"; Permissions: users-modify
Name: "{app}\logs";   Permissions: users-modify

[Icons]
Name: "{group}\卸载 {#AppShortName}"; Filename: "{uninstallexe}"
Name: "{group}\{#AppShortName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppShortName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Defender 排除项（勾了就加，避免自包含的 exe 被误杀）
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Add-MpPreference -ExclusionPath '{app}' -ErrorAction SilentlyContinue"""; \
  Flags: runhidden waituntilterminated; Tasks: nodefender; StatusMsg: "正在添加 Defender 排除项…"
; 装完直接可用（静默安装时不弹窗启动，交给部署方决定）
Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; \
  Description: "{cm:TaskRunApp}"; Flags: nowait postinstall skipifsilent; Tasks: runapp

[UninstallRun]
; 先结束正在运行的主控端，否则卸载程序删不掉被占用的文件
Filename: "taskkill.exe"; Parameters: "/f /im {#AppExeName}"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RcViewerKill"
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Remove-MpPreference -ExclusionPath '{app}' -ErrorAction SilentlyContinue"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "RcViewerDefender"

[Code]
var
  RelayPage: TInputQueryWizardPage;
  ImportedToken: string;

{ ---- 预置值：优先用安装包同目录的 setup-defaults.ini，其次是构建时嵌进去的默认值 ---- }
procedure ImportDefaults;
var
  Ini: string;
begin
  if '{#DefaultToken}' <> '' then
    ImportedToken := '{#DefaultToken}';
  Ini := ExpandConstant('{src}\setup-defaults.ini');
  if FileExists(Ini) then
    ImportedToken := GetIniString('relay', 'token', ImportedToken, Ini);
end;

{ ---- 装之前先停掉正在运行的主控端 ----
  为什么必须在**拷文件之前**做：正在运行的 Viewer.exe 与它拉起的 ffmpeg.exe 都锁着文件，
  Inno 盖不掉 —— 升级后新旧文件混在一起，行为诡异（被控端那条路上我们踩过同样的坑）。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  NeedsRestart := False;
  Exec('taskkill.exe', '/f /im {#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/f /im ffmpeg.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(700);   { 给系统一点时间释放文件句柄 }

  { 口令为空 = 中继启用鉴权时必然连不上（主控端会一直"连不上/列不出被控端"）。
    静默安装时没有向导，不能弹框。 }
  if (not WizardSilent) and (Trim(RelayPage.Values[1]) = '') then
  begin
    if MsgBox(ExpandConstant('{cm:TokenEmptyWarn}'), mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := ExpandConstant('{cm:TokenEmptyAbort}');
      Exit;
    end;
  end;

  Result := '';
end;

{ ---- 向导页：连接设置 ---- }
procedure InitializeWizard;
begin
  ImportDefaults;

  RelayPage := CreateInputQueryPage(wpSelectTasks,
    ExpandConstant('{cm:RelayPageTitle}'),
    ExpandConstant('{cm:RelayPageDesc}'),
    '');
  RelayPage.Add(ExpandConstant('{cm:RelayServerLabel}'), False);
  RelayPage.Add(ExpandConstant('{cm:TokenLabel}'), True);
  RelayPage.Values[0] := '{#ServerUrl}';
  if ImportedToken <> '' then
    RelayPage.Values[1] := ImportedToken;
end;

function GetRelayServer(Param: string): string;
begin
  Result := Trim(RelayPage.Values[0]);
  if Result = '' then
    Result := '{#ServerUrl}';
end;

function GetToken(Param: string): string;
begin
  Result := Trim(RelayPage.Values[1]);
end;

{ ---- 文件就位后写预置值 ----
  主控端启动时会把 config\viewer.json 里**为空**的项用这份文件补齐（已有配置优先），
  所以它既保证"装完即用"，又不会覆盖用户自己填过的值。 }
procedure CurStepChanged(CurStep: TSetupStep);
var
  Lines: TArrayOfString;
begin
  if CurStep = ssPostInstall then
  begin
    SetArrayLength(Lines, 3);
    Lines[0] := '# 主控端预置值（安装程序写入）。config\viewer.json 里为空的项会用这里的值补齐。';
    Lines[1] := 'ServerUrl=ws://' + GetRelayServer('') + '/ws';
    Lines[2] := 'Token=' + GetToken('');
    SaveStringsToUTF8File(ExpandConstant('{app}\viewer-defaults.txt'), Lines, False);
  end;
end;

{ 静默安装（/SILENT、/VERYSILENT）时没有向导页，用默认值兜住 }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = RelayPage.ID) and (Trim(RelayPage.Values[0]) = '') then
    RelayPage.Values[0] := '{#ServerUrl}';
end;
