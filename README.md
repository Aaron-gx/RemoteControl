# 远程控制软件（单会话共享 + 双光标方案）

> **架构唯一真源**：产品形态只有一种 —— **共享模式**（管理员已登录账户 + 虚拟外屏 + 双光标），
> 详见 `docs/架构-单会话共享与双光标.md`。
> 早期策划的「RDP 多会话 + 虚拟副屏」独立会话方案**已废弃**（`cehua.txt` 为策划初稿，仅历史参考）。

一台 Windows 电脑上，**本机用户与远程用户各用一块屏幕、同时操作、互不干扰**的远程控制软件。
已按客户需求定稿为共享模式，完成真机端到端验收。

---

## 0. 架构定稿（2026-09-22）

- 被控端在**管理员已登录的那个账户**里，额外挂一块**虚拟外屏**；远端在这块屏上干活，本机用户在物理屏上干活。
- 两边是**同一个账户、同一份 profile、同一个微信实例**，窗口可在物理屏与外屏之间拖。
- **光标**：本机用户永远独占真实系统光标；远端默认原生光标，本机用户在用时自动降级为后台定向注入。
- 旧的多会话方案（`UseRdpSession=true` / RemoteWorker 用户 / RDPWrap）已标 `[Obsolete]` 废弃，
  仅经 `AllowLegacyRdpSession=true` 逃生门保留兼容，**不参与验收**。

---

## 1. 交付物总览

```
E:\Learn\MeProject\2026.9.20-uu\RemoteControl\
├── src\
│   ├── Agent\                      # 被控端（C# .NET 8）
│   │   ├── Agent.Common\           #   协议 / IPC / 共享内存 / Win32 互操作（共用库）
│   │   ├── Agent.Coordinator\      #   Session 0 主进程：托盘 · RDP 会话 · 虚拟屏 · WebSocket · IPC 服务端
│   │   └── Agent.Worker\           #   会话内进程：WGC 采集 · H.264 编码 · 键鼠注入 · 窗口 · 剪贴板
│   ├── Viewer\                     # 主控端（WPF）：软件列表 · 远程画面 · 键鼠捕获 · 剪贴板/文件
│   ├── Server\                     # 香港中继服务器（Go）：WebSocket 中继 + 文件暂存
│   └── Tests\
│       ├── Agent.Common.Tests\     # 单元测试（36 项）
│       └── E2E\                    # 端到端验收工具（无头主控端，26 项断言）
├── scripts\
│   ├── build-all.ps1               # 一键构建全部产物 → build\
│   ├── install-agent.ps1           # 被控端一键安装（用户/RDP/RDPWrap/VDD/自启）
│   ├── create-remote-user.ps1      # 创建 RemoteWorker 会话用户
│   └── deploy-quick.ps1            # 迭代用：只更新程序文件（保留配置/日志）
├── drivers\                        # 第三方驱动与工具（已下载）
│   ├── RDPWrap\                    #   RDP Wrapper v1.6.2 + 社区维护的 rdpwrap.ini
│   └── VirtualDisplayDriver\       #   Virtual Display Driver 25.7.23 + nefcon
├── third_party\ffmpeg\ffmpeg.exe   # H.264 编解码（ffmpeg 7.1 essentials）
├── build\                          # 构建产物（agent / viewer / server）
└── docs\                           # 验收报告等
```

部署位置（本机已装好）：

| 组件 | 位置 | 启动方式 |
|------|------|----------|
| 被控端 Agent | `E:\RemoteControl\agent` | 计划任务 `RemoteControlAgent`（登录时以最高权限自启），也可直接双击 `Agent.Coordinator.exe` |
| 主控端 Viewer | `E:\RemoteControl\viewer` | 双击 `Viewer.exe` |
| 中继服务器 | `E:\RemoteControl\server` | 已注册开机自启（计划任务 `RemoteControlRelay`），手动起用 `start-server.cmd` |
| 运行日志 | `E:\RemoteControl\agent\logs`、`E:\RemoteControl\viewer\logs` | — |

---

## 2. 架构

默认是**共享模式**（`UseRdpSession=false`）：远程操作的就是被控端**当前已登录用户的桌面 + 一块虚拟外屏**。

```
主控端 Viewer.exe ──WebSocket──> 中继服务器(:8080) ──WebSocket──> Agent Coordinator（被控端已登录会话内，管理员）
                                                                        │ Named Pipe（指令）+ 共享内存（帧）
                                                                        ▼
                                                          Agent Worker（同一会话内）
                                                                        │ Windows.Graphics.Capture + ffmpeg(libx264)
                                                                        ▼
                                                本机已登录桌面 + 虚拟外屏（默认采集外屏 1920×1080）
```

- 远程看到/操作的是**同一个账户**的桌面，软件与数据跟本机用户是同一份，窗口可在物理屏与外屏之间拖
- 代价：一套鼠标键盘两边共用；被控端必须有人登录着（锁屏/UAC 期间看不到画面，主控端会写明原因）

另一条路是**独立会话模式**（`UseRdpSession=true`，安装时勾「独立会话模式」）：
新建 `RemoteWorker` 用户 + RDP 回环会话（`127.0.0.2`）+ RDPWrap 并发会话，远程与本机各有各的桌面/光标/输入队列，
**互不干扰**、也能无人值守；代价是数据不共享（不同用户 profile）。

> 为什么默认改成共享模式：产品定位是"**管理员用户增加一个外屏**"——远程要用的是本机同一个账户里的
> 微信/文件/软件，而不是另起一个用户从头配一套。两种模式在原生 Windows 上不能兼得（同一桌面只有一套光标）。

---

## 3. 快速开始

### 3.1 被控端

**给客户/新机器装：用安装包**（标准 Windows 安装程序，Inno Setup 打包）

```powershell
scripts\build-setup-agent.ps1     # 产物：build\setup-agent\远程控制被控端-安装程序.exe（约 83 MB）
```

把这一个 exe 拷到目标电脑双击即可：标准向导（欢迎 → 安装位置 → 附加任务 → 连接设置 → 安装 → 完成），
默认装到 `C:\Program Files\远程控制被控端`；装完在**设置 → 应用 → 已安装的应用**里能看到并正常卸载，
开始菜单也带卸载入口。无人值守时静默安装：

```powershell
# 全部默认（共享模式 + 虚拟外屏 + Defender 排除项，令牌自动生成）
.\远程控制被控端-安装程序.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

# 指定安装目录，并改用独立会话模式、跳过虚拟外屏与 Defender 排除项
.\远程控制被控端-安装程序.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART `
    /DIR="D:\RemoteControl\agent" /TASKS=multisession,novdd,nodefender
```

想在分发时就把令牌预置好（客户不用填）：在安装包同目录放一个 `setup-defaults.ini`：

```ini
[relay]
token=你的中继令牌
```

**本机重装/调试：直接跑脚本**（不走安装程序，改完立即生效）

```powershell
# 以管理员身份
powershell -ExecutionPolicy Bypass -File RemoteControl\scripts\install-agent.ps1 `
    -ServerUrl "ws://127.0.0.1:8080/ws" -InstallDir "E:\RemoteControl\agent"
```

脚本做的事（幂等，默认共享模式）：部署程序 → 安装 Virtual Display Driver（虚拟外屏）→ Defender 排除项 →
写 `config\agent.json` → 注册"**登录时**以管理员身份启动"的计划任务 → 输出验收摘要。
**不建用户、不开 3389、不装 RDPWrap**（共享模式不需要）。

要旧的独立会话模式（无人值守、互不干扰）加开关：
```powershell
powershell -ExecutionPolicy Bypass -File RemoteControl\scripts\install-agent.ps1 `
    -MultiSession -ServerUrl "ws://127.0.0.1:8080/ws" -InstallDir "E:\RemoteControl\agent"
```
勾了它才会：创建 `RemoteWorker`（加入 Remote Desktop Users）→ 开启远程桌面与防火墙 → 安装 RDPWrap →
注册"开机启动（SYSTEM）"计划任务。

### 3.2 中继服务器

```powershell
# 本地/内网测试（无鉴权）
E:\RemoteControl\server
cserver.exe -addr :8080 -data E:\RemoteControl\serverdata

# 公网部署（务必带令牌）
E:\RemoteControl\server
cserver.exe -addr :8080 -data E:\RemoteControl\serverdata -token <32位令牌>
```
- `GET /healthz` 健康检查（无需令牌）；`GET /api/agents?token=...` 在线被控端列表
- 剪贴板文件暂存 10 分钟自动清理（数据目录别放日志，否则会被当暂存文件清掉）
- **公网必须启用令牌**（`-token` 或环境变量 `RC_TOKEN`），否则任何人猜到被控端 ID 就能连上被控端
- 跨网络正式使用：见 `docs\部署-香港服务器.md`（systemd + 防火墙 + 两端配置片段）
- 想要更低延迟（直连优先、失败回落中继）：见 `docs\P2P方案.md`

### 3.3 主控端

双击 `E:\RemoteControl\viewer\Viewer.exe`：填服务器地址 → 点「刷新」列出在线被控端 → 选一个 → 点「连接」。
左侧软件列表**右键**可「打开 / 关闭(退出)」；右侧画面区域内可直接操作远程电脑（鼠标/键盘）。
远程复制文件后在画面内按 **Ctrl+V** 即下载到本地。

---

## 4. 配置说明

### 被控端 `E:\RemoteControl\agent\config\agent.json`

| 字段 | 说明 |
|------|------|
| `ServerUrl` / `FileServerUrl` | 中继服务器 WebSocket / HTTP 地址 |
| `AgentId` | 被控端标识（主控端用它选择目标） |
| `AgentToken` | 中继共享令牌（服务器启用鉴权时必填，主控端要填同一个） |
| `WorkerUser` / `WorkerPassword` | 远程会话专用本地用户及其密码 |
| `RdpLoopbackHost` | RDP 回环地址，**必须是 `127.0.0.2`**（`127.0.0.1` 会被 mstsc 拦截） |
| `UseRdpSession` | **`false`=共享模式（默认）**：采集/注入本机已登录用户的桌面（+ 外屏）；`true`=独立会话模式：新建 RemoteWorker 走 RDP 回环会话，两边互不干扰 |
| `EnableVirtualDisplay` | **默认 `true`**：启用虚拟外屏（共享模式下它就是"远程那块屏"；关掉则只采集物理主屏） |
| `FrameRate` / `BitrateKbps` | 视频帧率 / 码率 |
| `Encoder` | `auto`（自动探测 nvenc→qsv→amf→libx264）/ 指定编码器 |
| `CaptureBackend` | `auto`（先试 WGC，失败退 GDI）/ `wgc` / `gdi` |
| `RdpConnectTimeoutSec` | 等待 RDP 会话建立的最长秒数（首次登录建配置文件较慢，默认 180） |
| `LogLevel` | `Debug`/`Info`/`Warn`/`Error` |

### 主控端 `E:\RemoteControl\viewer\config\viewer.json`

`ServerUrl`、`TargetAgentId`、`DownloadDir`（默认优先非系统盘）、`AutoConnect`、`ClipboardSyncEnabled`。

---

## 5. 通信协议（与策划 §7 一致）

二进制帧：`[1B 类型][4B 大端长度][负载]`

| 类型 | 名称 | 负载 |
|------|------|------|
| 0x01 | 视频帧 | `[8B 时间戳][N 字节 H.264 码流]` |
| 0x02/0x03/0x04 | 鼠标移动/按键/滚轮 | `[2B x][2B y]([1B 按键][1B 抬落] / [2B 增量])` |
| 0x05 | 键盘 | `[2B vkCode][1B 抬落][1B 扩展标志]` |
| 0x10 / 0x16 | 软件列表 / 运行状态 | JSON |
| 0x11 / 0x12 | 打开 / 关闭软件 | UTF-8 exePath（也兼容 `{"exePath","args"}`）/ `[4B pid]` |
| 0x13 | 重建码流 | 空（双向：任一端重建编解码器时通知对端） |
| 0x14 | 显示器信息 | JSON `{left,top,width,height,dpi,count}` |
| 0x15 | 请求软件列表 | 空 |
| 0x20 / 0x21 / 0x22 | 剪贴板文本 / 文件通知 / 文件块 | UTF-8 / JSON / `[16B fileId][4B offset][data]` |
| 0x30 | 心跳 | `[8B 时间戳]`（0=保活；非 0=请求回声，用于测 RTT） |
| 0x40~0x43 | IPC 专用（就绪/状态/停止/文件投递） | JSON |
| 0xFE / 0xFF | 错误 / 断开 | UTF-8 / 空 |

---

## 6. 关键实现细节（踩过的坑，已修复）

1. **跨会话内核对象必须带"低完整性标签"**：管理员（High 完整性）创建的共享内存/命名管道会被强制完整性标签拦住，
   普通用户（Medium）即便 DACL 有权限也打不开。安全描述符需为
   `D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;AU)S:(ML;;NW;;;LW)`，且创建进程需启用 `SeSecurityPrivilege`。
2. **`CreateFileMappingW`/`OpenFileMappingW` 必须指定 `CharSet.Unicode`**：默认 Ansi 会把名字按单字节传给 W 版本，
   对象名被搞乱并落进"会话命名空间"，表现为**同会话可用、跨会话报"找不到文件"**。
3. **RDPWrap 匹配的是 `termsrv.dll` 的定长版本号**（本机 `10.0.26100.8972`），不是系统 UBR（26100.9168）。
   社区 ini 有精确条目即可用；`RDPPatchSupport` 判定必须读 DLL 版本。
4. **RDP 会话"已断开"时没有显示表面**：此时会话内 GDI BitBlt/WGC/DesktopDuplication 全部失败
   （拒绝访问/参数错误），因此会话必须保持 Active。
5. **RDP 客户端窗口不能最小化**：最小化会让服务端会话进入"无显示"状态（光标恒 0,0、无前台窗口），
   远程键鼠注入失效。正确做法是**连上后 SW_HIDE 隐藏**（策划要求的"自动隐藏"正好符合）。
6. **控制台窗口（cmd/powershell）的主窗口句柄为 0**：窗口属于 `conhost.exe`，需
   `AttachConsole(pid)` + `GetConsoleWindow()` 才能找到并把窗口置前台，否则注入的键盘落不到它上面。
7. **建立会话要有耐心**：首次登录要建用户配置文件，40~60 秒属正常；对"已断开"的会话重连会卡在
   登录界面，必须**注销后重建**。协调器带观察窗口与自动重建。
8. **实时视频管线里网络接收循环绝不能阻塞**：解码器积压时一旦阻塞接收，中继服务器会因写超时踢连接。
   解码端采用非阻塞入队 + 积压丢块 + 请求对端重建码流（H.264 丢块必须等新 IDR）。
9. **主控端必须主动上报"在线活跃"**：中继是透传的，Agent 无法从"自己连上服务器"推断主控端在场，
   判定依据是"最近 6 秒内收到过入站消息"（心跳也算）。

---

## 7. 验收结果

| 验收项 | 结果 |
|--------|------|
| 单元测试（协议/IPC/共享内存） | **36/36 通过** |
| 中继服务器测试（Go） | **28/28 通过**（含多 Viewer、断线保留 Agent、中文文件名上传下载） |
| 端到端验收（RDP 双会话模式） | **26/26 通过** |
| 端到端验收（单会话降级模式） | **26/26 通过** |
| RDP 并发会话 | 本地 console 会话 + RemoteWorker RDP 会话**同时 Active** |
| 主控端 GUI | 软件列表 53 项带图标、远程画面实时显示、状态栏延迟/分辨率/FPS/码率、右键菜单、鼠标坐标映射精确 |

端到端覆盖：连接/软件列表（图标+运行状态）、打开/关闭软件、视频（WGC 采集 → H.264 → 中继 → 解码 20~40fps 无丢块）、
**远程剪贴板文本双向**、**键鼠注入**（远程 cmd 真实执行了注入的命令）、**文件传输**（远程复制→上传→主控端 Ctrl+V 下载，内容一致）、
断线自动重连（画面自动恢复）。

详见 `docs\验收报告.md`（含逐条证据与截图）。

---

## 8. 运维：启停、自启与排障

```powershell
# —— 被控端 ——
# 启动（已在运行则静默退出，避免双实例）
Start-Process 'E:\RemoteControl\agent\Agent.Coordinator.exe' -WorkingDirectory 'E:\RemoteControl\agent'
# 停止：托盘图标右键 → 退出（会结束 Worker 并注销 RDP 会话）
Get-Process Agent.Coordinator,Agent.Worker,ffmpeg | Stop-Process -Force   # 强制停止（下次登录/手动启动恢复）
Get-ScheduledTask RemoteControlAgent | Disable-ScheduledTask              # 关闭开机自启
Get-ScheduledTask RemoteControlAgent | Enable-ScheduledTask               # 恢复开机自启

# —— 中继服务器 ——
Start-Process 'E:\RemoteControl\server
cserver.exe' -ArgumentList '-addr',':8080','-data','E:\RemoteControl\serverdata'
Invoke-WebRequest http://127.0.0.1:8080/healthz      # ok
Invoke-WebRequest http://127.0.0.1:8080/api/agents   # 在线被控端

# —— 日志 ——
#   E:\RemoteControl\agent\logs\coordinator-YYYYMMDD.log   被控端主进程（会话/看护/中继/诊断）
#   E:\RemoteControl\agent\logs\worker-YYYYMMDD.log        采集/编码/注入/剪贴板
#   E:\RemoteControl\viewer\logs\viewer-YYYYMMDD.log       主控端
```

**常见问题**

| 现象 | 处理 |
|------|------|
| 主控端显示"被控端不在线" | 中继未启动或 Agent 未运行；查 `api/agents` 与 `coordinator` 日志 |
| 画面黑屏 / 采集失败（拒绝访问） | RDP 会话掉到"已断开"状态（如 mstsc 被关掉）。看护会在 20s 内自动重建；也可手动 `Get-Process mstsc` 确认 |
| 远程键鼠无反应 | 确认 mstsc 未被最小化（最小化会让会话失去显示表面）。协调器每 20s 自动纠正为"隐藏但未最小化" |
| RDPWrap 失效（3389 未监听） | 系统更新换了 termsrv.dll → 用新版 `rdpwrap.ini` 覆盖 `C:\Program Files\RDP Wrapper
dpwrap.ini`，然后 `Restart-Service TermService` |
| 想彻底卸载 | `scripts\create-remote-user.ps1` 反向操作 + `RDPW_Uninstaller.exe` + `nefconw.exe remove "Root\MttVDD"`；注册表备份在 `E:\RemoteControl\backup\` |

---

## 9. 源码改动后如何重新验证

```powershell
# 只改代码（不动驱动/用户/计划任务）：发布 + 同步 + 等 Worker 重启
powershell -ExecutionPolicy Bypass -File RemoteControl\scripts\deploy-quick.ps1

# 端到端验收（26 项，证据写入 E:\RemoteControl\e2e）
E:\RemoteControl\build\tests\E2E.exe --server ws://127.0.0.1:8080/ws --dir E:\RemoteControl\e2e `
    --ffmpeg <repo>\third_party\ffmpeg\ffmpeg.exe --scenario all
```
