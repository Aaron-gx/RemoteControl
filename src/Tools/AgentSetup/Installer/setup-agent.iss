; ============================================================================
;  远程控制 · 被控端 —— Inno Setup 安装脚本
;
;  和之前自制安装器（WPF 的 AgentSetup）的区别：
;   · 标准 Windows 安装向导（欢迎 → 安装位置 → 附加任务 → 连接设置 → 安装 → 完成）
;   · 装进 Program Files，注册到「应用和功能」（可正常卸载，带卸载器）
;   · 开始菜单快捷方式 + 卸载入口
;   · LZMA2 固实压缩，安装包比自制版小
;   · 支持 /SILENT、/VERYSILENT 等标准无人值守参数
;
;  文件部署由 Inno 负责；建远程用户 / RDPWrap / 虚拟屏 / 计划任务 / 防火墙 / Defender
;  这些系统级步骤仍交给经过验收的 scripts\install-agent.ps1（-NoDeploy 模式，
;  只做系统配置、不再复制文件），逻辑一行没改，风险最低。
;
;  编译：scripts\build-setup-agent.ps1  （ISCC.exe 在 E:\tools\innosetup）
; ============================================================================

#define AppName        "远程控制 · 被控端"
#define AppShortName   "远程控制被控端"
#define AppVersion     "1.0.0"
#define AppPublisher   "远程控制"
#define AppExeName     "Agent.Coordinator.exe"
; 注意：命令行里**不能**出现 "-Token -" 这种写法 —— PowerShell 会把 "-" 当参数名，
; 整个脚本都不会被执行（静默 exit 1/2，没有输出，极难查）。所以令牌为空时干脆不带 -Token。

; 默认中继地址：没有预置服务器时用这个（可在向导里改）
#ifndef RelayServer
  #define RelayServer "202.60.232.209:8080"
#endif

; 仓库根目录（绝对路径）。构建脚本用 /DRepoRoot=... 传进来；
; Inno 对 Source 里 "..\..\.." 这种相对路径匹配不到文件，所以统一走绝对路径。
#ifndef RepoRoot
  #define RepoRoot SourcePath + "..\..\.."
#endif

; 预置中继令牌（可选）：构建脚本 /DDefaultToken=... 或安装包同目录的 setup-defaults.ini。
; 有了它，客户拿到的安装包打开就是填好的，不用再抄令牌。
#ifndef DefaultToken
  #define DefaultToken ""
#endif

[Setup]
AppId={{8F3A8C25-6D4E-4C93-9E1B-2A4F5B7C8D01}
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
OutputDir={#RepoRoot}\build\setup-agent
OutputBaseFilename={#AppShortName}-安装程序
SetupIconFile={#RepoRoot}\src\Assets\agent.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
WizardStyle=modern
ShowLanguageDialog=no
CloseApplications=no
RestartApplications=no
AllowNoIcons=yes
DisableDirPage=no
; 卸载时提示：RDPWrap / 虚拟屏 / RemoteWorker 用户是系统级改动，默认保留（见 uninstall-agent.ps1）
Uninstallable=yes

[Languages]
Name: "cn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[CustomMessages]
cn.RelayPageTitle=连接设置
cn.RelayPageDesc=这些设置会写进被控端配置；不确定就保持默认。
cn.RelayServerLabel=中继服务器地址（IP:端口）
cn.TokenLabel=连接口令
cn.TokenDefault=留空则只能在「未启用鉴权」的中继上使用
cn.TokenEmptyWarn=连接口令为空。%n%n如果中继服务器启用了鉴权（服务端配置了 RC_TOKEN），被控端会一直显示「未连接服务器」，主控端列表里也看不到这台机器。%n%n口令必须与服务端的 RC_TOKEN 完全一致。%n%n确实要继续（例如内网测试、中继未启用鉴权）吗？
cn.TokenEmptyAbort=请返回上一步把「连接口令」填好再安装
cn.TaskGroupDesc=选择要一起启用的功能；默认项适合绝大多数机器。
; 曾经这里还有一个「独立会话模式」勾选项（新建 RemoteWorker 用户 + RDP 回环会话）。
; 那个模式与本产品的形态相冲突：它是**另一个账户、另一份 profile**，微信/浏览器/文件都跟
; 本机管理员不共享，install-agent.ps1 现在也会直接拒绝它。所以向导里不再提供这个选项。
; 确实要跑旧方案，用命令行显式给两个开关：
;   install-agent.ps1 -MultiSession -IUnderstandLegacyMultiSession
cn.TaskNoDefender=跳过 Defender 排除项
cn.TaskNoDefenderHint=默认会为被控端程序目录加排除项，避免被误杀。
cn.TaskNoVdd=跳过虚拟外屏
cn.TaskNoVddHint=共享模式下远程的那块扩展屏；不装的话远程只能看到本机桌面。
cn.TaskDesktopIcon=创建桌面快捷方式
cn.Configuring=正在配置系统（安装会话组件、驱动、开机自启）…
cn.ConfigFailed=系统配置步骤失败，安装未完成。%n%n请把下面的日志发给技术支持：%n%1
cn.ConfigTimeout=系统配置步骤超时（可能卡在驱动安装）。%n%n日志：%1
cn.RunAgent=安装完成后启动被控端

[Tasks]
Name: "desktopicon"; Description: "{cm:TaskDesktopIcon}"; GroupDescription: "{cm:TaskGroupDesc}"; Flags: unchecked
Name: "nodefender"; Description: "{cm:TaskNoDefender}"; GroupDescription: "{cm:TaskGroupDesc}"
Name: "novdd"; Description: "{cm:TaskNoVdd}"; GroupDescription: "{cm:TaskGroupDesc}"

[Files]
; 被控端程序 / ffmpeg / 驱动（构建脚本先把 ffmpeg 与驱动补齐到 build\agent）
Source: "{#RepoRoot}\build\agent\*"; DestDir: "{app}"; Flags: ignoreversion restartreplace recursesubdirs createallsubdirs
; 安装/卸载脚本放在 app\installer 下：安装时与卸载时都要用（三个脚本必须在一起）
Source: "{#RepoRoot}\scripts\install-agent.ps1";     DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#RepoRoot}\scripts\create-remote-user.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion
Source: "{#RepoRoot}\scripts\uninstall-agent.ps1";   DestDir: "{app}\installer"; Flags: ignoreversion

[Dirs]
; 程序自己要在这些目录里写日志/配置，而 Program Files 默认只有管理员可写 ——
; 不显式授权的话，普通用户运行时会写不进去（实测：装到 C:\Program Files 后一启动就弹
; "Access to the path ...\logs is denied"）。users-modify = 所有用户可修改。
Name: "{app}\config"; Permissions: users-modify
Name: "{app}\logs";   Permissions: users-modify

[Icons]
Name: "{group}\卸载 {#AppShortName}"; Filename: "{uninstallexe}"
Name: "{group}\{#AppShortName}（手动启动）"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppShortName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; 系统级配置：交给 install-agent.ps1（-NoDeploy：文件已由 Inno 铺好，这里只做系统改动）
; 参数交给 BuildCommandLine 拼：令牌为空时不带 -Token 参数——
; "-Token -" 会让 PowerShell 连脚本都不执行（静默失败，只留一个退出码）
Filename: "powershell.exe"; \
  Parameters: "{code:BuildCommandLine}"; \
  StatusMsg: "{cm:Configuring}"; Flags: runhidden waituntilterminated

[UninstallRun]
; 卸载前的清理：停计划任务/进程、删 Defender 排除与防火墙规则（RDPWrap/虚拟屏/用户默认保留）
Filename: "powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\uninstall-agent.ps1"" -InstallDir ""{app}"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "RcAgentCleanup"

[Code]
var
  RelayPage: TInputQueryWizardPage;
  ImportedToken: string;

{ ---- 预置令牌：优先用安装包同目录的 setup-defaults.ini，其次是构建时嵌进去的默认值 ---- }
procedure ImportDefaults;
var
  Ini: string;
begin
  if '{#DefaultToken}' <> '' then
    ImportedToken := '{#DefaultToken}';
  Ini := ExpandConstant('{src}\setup-defaults.ini');
  if FileExists(Ini) then
  begin
    ImportedToken := GetIniString('relay', 'token', ImportedToken, Ini);
  end;
end;

{ ---- 装之前先停掉正在运行的被控端 ----
  为什么必须在**拷文件之前**做：正在运行的 Agent.Coordinator.exe / Agent.Worker.exe /
  Agent.Common.dll 是锁住的，Inno 盖不掉 —— 升级后新旧文件混在一起跑，
  表现就是"行为诡异、鼠标点不到、副屏点击跑到主屏去"（实测踩到）。
  CloseApplications=no 意味着 Inno 不会替我们做这件事，所以这里显式来一遍。 }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  NeedsRestart := False;
  Exec('taskkill.exe', '/f /im Agent.Coordinator.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec('taskkill.exe', '/f /im Agent.Worker.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(900);   { 给系统一点时间释放文件句柄 }

  { 口令为空 = 中继启用鉴权时必然连不上。以前这里什么都不说、安装脚本还随机生成一个令牌，
    结果是被控端装完一直「不在线」，界面上又没有任何线索（实测踩到）。
    静默安装（/VERYSILENT）时没有向导，不能弹框，只在日志里留痕。 }
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
var
  DefaultServer: string;
begin
  ImportDefaults;

  RelayPage := CreateInputQueryPage(wpSelectTasks,
    ExpandConstant('{cm:RelayPageTitle}'),
    ExpandConstant('{cm:RelayPageDesc}'),
    '');
  RelayPage.Add(ExpandConstant('{cm:RelayServerLabel}'), False);
  RelayPage.Add(ExpandConstant('{cm:TokenLabel}'), True);
  RelayPage.Values[0] := '{#RelayServer}';
  if ImportedToken <> '' then
    RelayPage.Values[1] := ImportedToken;
end;

function GetRelayServer(Param: string): string;
begin
  Result := Trim(RelayPage.Values[0]);
  if Result = '' then
    Result := '{#RelayServer}';
end;

function GetToken(Param: string): string;
begin
  Result := Trim(RelayPage.Values[1]);
end;

{ 把勾选状态翻成 install-agent.ps1 的开关 }
function GetModeFlags(Param: string): string;
begin
  Result := '';
  { 只出共享模式：不再传 -MultiSession —— 它会被 install-agent.ps1 拒绝（exit 2），
    而且那个模式会新建账户、数据不共享，正是本产品要避免的。旧方案请命令行显式加两个开关。 }
  if WizardIsTaskSelected('nodefender')   then Result := Result + ' -SkipDefender';
  if WizardIsTaskSelected('novdd')        then Result := Result + ' -SkipVdd';
end;

{ 命令行拼装：令牌为空时整段省略，避免 "-Token -" 把 PowerShell 的脚本执行打掉；
  其余参数一律带引号（路径里有空格或中文也安全）。 }
function BuildCommandLine(Param: string): string;
var
  Token: string;
  AppDir: string;
begin
  Token := GetToken('');
  { 安装目录这类常量在 [Code] 里不会自动展开，必须显式 ExpandConstant（大括号是注释，不能写进注释里） }
  AppDir := ExpandConstant('{app}');
  Result := '-NoProfile -ExecutionPolicy Bypass -File "' + AppDir + '\installer\install-agent.ps1"' +
            ' -InstallDir "' + AppDir + '" -ServerUrl "ws://' + GetRelayServer('') + '/ws"';
  if Token <> '' then
    Result := Result + ' -Token "' + Token + '"';
  Result := Result + GetModeFlags('');
end;

{ 静默安装（/SILENT、/VERYSILENT）时没有向导页，用默认值兜住 }
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = RelayPage.ID) and (Trim(RelayPage.Values[0]) = '') then
    RelayPage.Values[0] := '{#RelayServer}';

  { 勾了"跳过虚拟外屏"是个代价很大的选择：远程就只能看到并操作本机主屏，
    本机用户还会看见远端光标在动。实测有客户手滑勾了，表现就是"副屏点了没反应、
    点击跑到主屏"。这里加一道确认。 }
  if (CurPageID = wpSelectTasks) and WizardIsTaskSelected('novdd') then
  begin
    if MsgBox('你勾选了「跳过虚拟外屏」。' + #13#10 + #13#10 +
              '那将不再安装/管理远程专用的那块扩展屏，远程只能看到并操作本机主屏，' + #13#10 +
              '而且本机用户会看见远端的光标在动。' + #13#10 + #13#10 +
              '确实要这样吗？（推荐选「否」，保持勾选为空）',
              mbConfirmation, MB_YESNO) = IDNO then
    begin
      Result := False;   { 拦下这一页，让用户把勾去掉 }
      Exit;
    end;
  end;
end;
