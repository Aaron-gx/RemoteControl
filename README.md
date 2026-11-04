# 远程控制软件（单会话共享 + 双光标方案）

> 🤖 **AI 助手 / 开发者请先读 [`AGENTS.md`](AGENTS.md)** —— 5 分钟项目全貌：目录地图、每文件用途、铁律、真机验收流程。

> **架构唯一真源**：产品形态只有一种 —— **共享模式**（管理员已登录账户 + 虚拟外屏 + 双光标），
> 详见 `docs/架构-单会话共享与双光标.md`。
> 早期策划的「RDP 多会话 + 虚拟副屏」独立会话方案**已废弃**（`cehua.txt` 为策划初稿，仅历史参考）。

一台 Windows 电脑上，**本机用户与远程用户各用一块屏幕、同时操作、互不干扰**的远程控制软件。
已按客户需求定稿为共享模式，完成真机端到端验收，支持**热更新**自动升级。

**当前版本：被控端 1.6.0 / 主控端 1.2.8**（2026-10-14：**对标 UU 三项升级**——编码器能力缓存重构（键含显卡/编码族/ffmpeg、硬编落盘 config/encoder-cache.json、软编不落盘、失败 120s 重试、同族并行探测）；UDP 通道 KCP 可靠路径（UU dckcp 实测参数 nc=1/wnd256/5ms/minrto10）+ RS-FEC 视频抗丢（GF256 Cauchy 16 片一组、r 随丢包自适应 1~6，双端 Cap 协商、旧主控端零影响）；dda 显卡直通升为 auto 默认（DXGI 设备名精确定位、编码器失败透明回退 WGC、停滞看护））（2026-10-11 补充：**会话自愈**【SYSTEM 会话实例启动时发现控制台有登录用户（锁屏也算）→ 自动迁移到用户会话，治"看门狗/热更新把实例永久卡在 session 0、远控变废"的事故——迁移前补写报平安标记，不触发热更新回滚，10 分钟节流防循环】；第三批 1.5.25：NV12 管道【WGC 采集侧就地转 NV12，管道流量 250→93MB/s 省 62%，BT.601 同色彩】、诊断通道【0x56/0x57/0x58 协调器直答 + `_rcp.py diag`，引擎卡死也能拿日志】、视频丢包恢复【XOR 校验片 + NACK 重传】）

---

## 0. 架构定稿（2026-09-22，2026-10 进程合并）

- 被控端是**单进程**（`Agent.Coordinator.exe`）：托盘 + 采集引擎（原 Agent.Worker 已并入，STA 线程承载）。
- 远程操作的就是被控端**管理员已登录的那个账户**的桌面 + 一块**虚拟外屏**。
- 两边是**同一个账户、同一份 profile、同一个微信实例**，窗口可在物理屏与外屏之间拖。
- **光标**：本机用户永远独占真实系统光标；远端默认原生光标，本机用户在用时自动降级为后台定向注入。
- **热更新**：被控端全自动（检查→验签→下载→空闲→应用→回滚）；主控端只提示下载。详见 §6。

---

## 1. 交付物总览

```
RemoteControl\
├── src\
│   ├── Agent\                      # 被控端（C# .NET 8，单进程）
│   │   ├── Agent.Common\           #   协议 / 更新签名 / 进程内桥 / Win32 互操作
│   │   ├── Agent.Coordinator\      #   唯一 exe：托盘·虚拟屏·WebSocket·直连·授权·热更新·软件列表
│   │   └── Agent.Worker\           #   类库（采集引擎）：WGC·H.264·键鼠·窗口·剪贴板（无独立 exe）
│   ├── Viewer\                     # 主控端（WPF）：软件列表·画面·键鼠·剪贴板·更新提示
│   ├── Server\                     # 中继服务器（Go）：WebSocket·文件暂存·授权·热更新分发
│   └── Tests\
│       ├── Agent.Common.Tests\     # 单元测试（80 项）
│       └── E2E\                    # 端到端验收工具
├── scripts\
│   ├── build-all.ps1               # 一键构建 → build\
│   ├── build-setup-agent.ps1       # 被控端安装包（Inno Setup）
│   ├── build-setup-viewer.ps1      # 主控端安装包
│   ├── install-agent.ps1           # 被控端部署脚本
│   ├── _publish_update.py          # 热更新发布（命令行版）
│   └── _rcp.py                     # 远程诊断工具（查版本/抓日志/帧级测闪/远程部署）
├── license\                        # 厂商密钥（private.pem + public.txt，gitignore）
├── drivers\                        # Virtual Display Driver + nefcon（RDPWrap 已不随包）
├── third_party\ffmpeg\ffmpeg.exe   # H.264 编解码
├── build\                          # 构建产物
│   ├── setup-agent\虚拟网服务-安装程序.exe     # 被控端安装包（含口令预置）
│   ├── setup-viewer\虚拟网服务客户端-安装程序.exe # 主控端安装包
│   ├── 虚拟网服务-安装包.zip                    # 被控端分发整包
│   ├── 虚拟网服务客户端-安装包.zip              # 主控端分发整包
│   └── server\rcserver-linux-amd64             # 中继服务器（Linux 二进制）
└── docs\
```

---

## 2. 架构

```
主控端 Viewer.exe ──WebSocket──> 中继服务器(:18080) ──WebSocket──> Agent.Coordinator.exe（被控端单进程）
                                                                       │
                                                                       ├─ worker-engine 线程（STA）
                                                                       │    WGC 采集 + ffmpeg(H.264)
                                                                       │    │ WorkerBridge（进程内通道）
                                                                       │    ▼
                                                                       │    虚拟外屏 1920×1080
                                                                       │
                                                                       ├─ Updater（热更新：检查→验签→下载→空闲→应用→回滚）
                                                                       └─ DirectLinkServer（P2P 直连）
```

---

## 3. 快速开始

### 3.1 被控端

**给客户：发安装包**（`build\虚拟网服务-安装包.zip`，口令已预置，双击装完即上线）。

**本机调试：**
```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-agent.ps1 -ServerUrl "ws://<中继地址>:18080/ws"
```

### 3.2 中继服务器

```bash
# 部署到 Linux（一键脚本见 docs/部署-香港服务器.md）
./rcserver-linux-amd64 -addr :18080 -data /var/lib/rcserver
```

- 令牌：systemd 环境变量 `RC_TOKEN`（业务）+ `RC_ADMIN_TOKEN`（管理，与 RC_TOKEN 不同）
- 热更新管理页：`http://<服务器>:18080/admin/update?token=<管理令牌>`

### 3.3 主控端

双击 `Viewer.exe`：自动填好服务器地址与口令（`viewer-defaults.txt` 预置）→ 点「刷新」→ 选被控端 → 点「连接」。

- 左侧软件列表**右键**：「打开 / 关闭 / 搬到副屏」；置顶固定项**「此电脑」**打开后窗口直接在副屏显示
- 右侧画面：鼠标/键盘直接操作远程电脑；远程复制文件/文件夹后在画面内 **Ctrl+V** 即下载到本地
- 主控端自身：连接时自动查更新，有新版时状态栏显示"⬆ 有新版本"，点击下载

---

## 4. 配置说明

### 被控端 `config\agent.json`

| 字段 | 默认 | 说明 |
|------|------|------|
| `ServerUrl` | `ws://127.0.0.1:8080/ws` | 中继服务器地址 |
| `AgentId` | 自动（机器名+哈希） | 被控端标识 |
| `AgentToken` | — | 中继共享令牌（与服务器 `RC_TOKEN` 一致） |
| `EnableVirtualDisplay` | `true` | 虚拟外屏（共享模式下就是"远程那块屏"） |
| `ShowTrayIcon` | `false` | 右下角托盘图标（客户要求隐藏；需要直连开关/退出时改 `true`） |
| `AutoUpdateEnabled` | `true` | 热更新开关 |
| `UpdateCheckIntervalHours` | `6` | 检查更新的间隔（小时） |
| `FrameRate` / `BitrateKbps` | `30` / `4000` | 视频帧率 / 码率 |
| `VideoCodec` | `auto` | `auto`=协商后优先 H.265（主控端能解+本机有硬编才启用）；`h264`/`h265` 强制 |
| `Encoder` | `auto` | 自动探测 nvenc→qsv→amf→libx264（H.265 时用 hevc_* 家族） |
| `CaptureBackend` | `auto` | `auto`/`wgc`/`gdi`/`dda`（1.6.0 起 **auto 第一优先尝试 dda 显卡直通**：ffmpeg 用 ddagrab 在显卡上采集+编码帧不过 CPU，N 卡 nvenc/A 卡 amf；不可用（如 Intel 核显）自动落 WGC；dda 失败透明回退 WGC，显式 wgc/gdi 仍是逃生开关） |
| `UdpQualitySwitchEnabled` | `true` | UDP 直连丢包监控：持续超标自动切中继、恢复自动切回 |
| `UdpPunchEnabled` | `false` | **UDP 打洞直连总开关**（1.5.29 起默认关闭）：同局域网环境曾出现"被控端→主控端"单向黑洞（视频分片/输入消息全丢而小包通畅），根因未查明前统一走 TCP（直连/中继）；查明后可开回 |
| `PixelPipeline` | `bgra` | `nv12`=WGC 采集侧转 NV12（管道流量省 62%）——真机曾现色度损坏（1.5.28 回退），修复前 opt-in |
| `CursorArbitrationEnabled` | `true` | 光标仲裁（本机用户独占真实光标） |

### 主控端 `config\viewer.json`

`ServerUrl`、`TargetAgentId`、`AgentToken`、`DownloadDir`、`AutoConnect`、`ClipboardSyncEnabled`。

---

## 5. 通信协议

二进制帧：`[1B 类型][4B 大端长度][负载]`

| 类型 | 名称 | 负载 |
|------|------|------|
| 0x01 | 视频帧 | `[8B 时间戳][N 字节 H.264/H.265]` |
| 0x02/0x03/0x04 | 鼠标移动/按键/滚轮 | 坐标+按键 |
| 0x05 | 键盘 | `[2B vkCode][1B 抬落][1B 扩展]` |
| 0x10 / 0x16 | 软件列表 / 运行状态 | JSON |
| 0x11 / 0x12 | 打开 / 关闭软件 | UTF-8 路径（兼容 `{"exePath","args"}`）/ `[4B pid]` |
| 0x13 | 重建码流 | 被控端→主控端带 `[1B 编码]`（0=H.264，1=H.265；空=H.264，旧版兼容）；主控端→被控端为空 |
| 0x14 | 显示器信息 | JSON |
| 0x15 | 请求软件列表 | 空 |
| 0x20 / 0x21 | 剪贴板文本 / 文件(含文件夹)通知 | UTF-8 / JSON（`IsFolder`+`FolderName`） |
| 0x30 | 心跳 | `[8B 时间戳]` |
| 0x53 | 能力协商（主控端→被控端） | JSON `{capVer, h265Decode}`：双端都支持才启用 H.265（对标 UU 的 min/AND 协商语义） |
| 0x56 / 0x57 | 诊断请求 / 报告 | 空 / JSON DiagnosticsInfo（引擎活性+链路+两份日志尾部；协调器直答，引擎卡死可用） |
| 0x58 | 重启采集引擎 | 空（协调器直答：转储引擎栈+WM_QUIT，看门狗拉起新引擎） |
| 0x61 | 画面范围切换 | `[1B CaptureMode]` |
| 0xFE / 0xFF | 错误 / 断开 | UTF-8 / 空 |

---

## 6. 热更新（自动更新）

### 被控端（全自动）

```
每 15min + 启动后 30s → GET /api/update/manifest → ECDSA 验签 → 版本比较
  → 下载 zip → sha256+大小双重比对 → 等连续 5 分钟无主控端连接
  → 分离脚本：停旧→备份→覆盖→启新 → 45s 内报平安 / 90s 未报自动回滚
```

- 更新码前缀 `RCU1`（与授权码 `RC1` 同密钥不同前缀，互不通用）
- 状态远程可观测：主控端状态栏 / `_rcp.py status` 的 `UpdateState` / `UpdateStaged`

### 主控端（只提示）

连接时查同一接口 → 验签+比版本 → 有新版时状态栏"⬆ 有新版本 vX，点击下载" → 浏览器打开。

### 发布流程

**LicenseKeygen「更新签名」页**（推荐）：
1. 选 zip 包 + 填版本号 + 填灰度比例（建议先 10%）
2. 点「一键发布」（自动算哈希 + 签名 + 传包 + 存清单）

**命令行**：`python scripts\_publish_update.py <zip> <版本号> [说明] [灰度%]`

---

## 7. 关键实现细节

| # | 要点 | 详情 |
|---|------|------|
| 1 | 进程合并 | Worker 并入 Coordinator（STA 线程承载），IPC 换进程内 WorkerBridge，删约 2300 行多会话遗留 |
| 2 | 绝对零闪（此电脑） | 三层：CREATE 预定位 + 3ms 热旋转 + DWM cloak；198 帧逐帧比对主屏零变化 |
| 3 | 黄框取消 | WGC `IsBorderRequired=false`（系统在物理屏画的"正在被采集"指示框） |
| 4 | 副屏记忆位置回主屏 | Windows 记住程序上次关在哪个屏 → 本机用户开的窗口落副屏时自动搬回主屏 |
| 5 | 文件夹复制 | 被控端复制文件夹 → zip 上传 → 主控端解压成目录放本机剪贴板 → Ctrl+V 粘出文件夹 |
| 6 | 托盘隐藏 | `ShowTrayIcon` 默认 false（客户要求装完右下角不显示） |
| 7 | 光标仲裁（被同类远控挤掉时保留被动监听） | UU远程等共存时不丢"本机用户在场"判定 |
| 8 | 热更新安全 | 更新码 ECDSA 签名 + sha256 双重比对 + 新版本 90s 未报平安自动回滚 |
| 9 | 灰度发布 | 服务器按 AgentId 哈希 × rolloutPercent 决定哪台机器本轮看到新版本 |
| 10 | H.265 编码协商（2026-10-10） | 主控端连上后上报能力（0x53），被控端"双端都支持才启用"（对标 UU 的 min/AND 语义）：auto 模式还要求本机有 H.265 硬件编码器（软编 libx265 太重不用）；切换时 StreamReset 带编码字节，主控端解码器按 hevc/h264 重建。灰度期间新旧版本互连自动落到 H.264 |
| 11 | 码率升档探测（2026-10-10） | 自适应升档改为"探测式"：升一档 → 10 秒观察窗 → 再拥塞立即回滚 + 指数退避（1→2→5 分钟封顶），连续 5 分钟干净才清退避；帧率从第 2 档起一起降（30fps→22→20→…），档位表修了小码率配置下"降档反而升码率"的单调性 bug |
| 12 | UDP 直连质量监控（2026-10-10） | 保活 ping 无应答率（10s 滑窗、发送侧可测）≥30% 持续 10s → 视频/控制切回中继（直连保活继续探测）；≤10% 持续 30s → 自动切回。拥塞判定同时纳入 ping 丢包；以前只有 3.5s 彻底断流才回落 |

详见 `docs/架构-单会话共享与双光标.md` §9~§12。

---

## 8. 验收结果

| 验收项 | 结果 |
|--------|------|
| 单元测试 | **143/143 通过**（1.6.0：+EncoderCapability 9、+RsFec 10、+UdpKcpFec 9） |
| 中继服务器（Go） | **全绿**（含热更新灰度/穿越防护/形状校验） |
| 主控端侧集成测试 | 列表含「此电脑」置顶 ✔ / 文件夹接收进剪贴板 ✔ |
| 帧级闪烁验证 | **198 帧零变化**（1.2.5 冷路径打开此电脑，主屏绝对零帧） |
| 热更新真机闭环 | 1.2.1→1.2.2 全自主（检查→验签→下载→空闲→应用→报平安）✔ |
| 伪造签名拒绝 | 真机收到伪造清单 → 拒绝并保持不动 ✔ |
| 生产热更闭环 ×6 | 1.5.22→25→27→28→**29→30** 全自主应用（29/30 为 2026-10-11 客户现场事故当日双发，含客户自己机器）✔ |
| 客户同网段环境 | UDP 打洞黑洞（1.5.29 停用）+ H.265 编码器错配（1.5.30 修缓存键）后，视频/点击恢复 ✔ |

---

## 9. 运维

```powershell
# 被控端
Get-Process Agent.Coordinator | Stop-Process -Force    # 停止
Start-Process 'C:\Program Files\虚拟网服务\Agent.Coordinator.exe' -WorkingDirectory 'C:\Program Files\虚拟网服务'

# 查被控端版本与更新状态（远程）
python scripts\_rcp.py status

# 发布热更新
python scripts\_publish_update.py build\agent-X.X.X.zip X.X.X "更新说明" 100

# 中继
Invoke-WebRequest http://<中继地址>:18080/healthz
```

---

## 10. 源码改动后如何重新验证

```powershell
# 全量编译 + 单测
dotnet build src\Agent\Agent.Coordinator\Agent.Coordinator.csproj -c Release
dotnet test src\Tests\Agent.Common.Tests\Agent.Common.Tests.csproj -c Release

# 构建安装包
powershell -ExecutionPolicy Bypass -File scripts\build-setup-agent.ps1 -ToolsRoot D:\tools -Token <口令>

# 发布热更新补丁（3 个 DLL）
dotnet publish src\Agent\Agent.Coordinator\Agent.Coordinator.csproj -c Release -r win-x64 --self-contained false -o build\patch
tar -a -c -f build\agent-X.X.X.zip -C build\patch Agent.Coordinator.dll Agent.Worker.dll Agent.Common.dll
python scripts\_publish_update.py build\agent-X.X.X.zip X.X.X "说明" 100
```
