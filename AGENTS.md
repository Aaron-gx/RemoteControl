# AGENTS.md — AI 助手 / 新人 5 分钟上手地图

> 这个文件是给 AI（和新人）的第一入口：先读这份，再按需要跳源码。目标是**不用通读代码就能干活**。
> 当前版本：**v1.5.1**（版本号在 `src/Agent/Agent.Worker/Program.cs` 的 `Version` 常量）。

## 0. 这是什么项目

Windows 远程控制软件，三部分（对客户一律叫"**虚拟服务器**"，产品文案/安装包/进程名里**绝不出现"远程控制"字样**）：

| 角色 | 技术 | 位置 |
|---|---|---|
| 被控端 Agent | .NET 8 (WinForms+WGC)，**单进程** Agent.Coordinator.exe | `src/Agent/` |
| 主控端 Viewer | .NET 8 WPF | `src/Viewer/` |
| 中继服务器 | Go（WebSocket 转发 + HTTP API） | `src/Server/` |

核心玩法：被控端装一块 **IDD 虚拟显示器**（远端看到的"副屏"），远端键鼠注入进去、窗口自动搬上去；本机用户照常用物理主屏——**双用户共享一台机器**。与 UU远程（独占式）的差异与逆向分析见 `../uu-reverse/README.md`。

## 1. 铁律（违反会出事故，先记住再动手）

1. **被控端绝不能在本机跑/测**——窗口搬移、键鼠注入会劫持你自己的电脑。被控端只部署到远程真机验收。**输入类实验更不能本机 SendInput**（2026-10-09 险些把测试键打进用户聊天窗口）。主控端、LicenseKeygen、CrashDumpProbe 可以本机跑。
2. **下载的东西不放 C 盘**；本机有 Clash 代理 `127.0.0.1:7897`。
3. **改完必须真机验收再交付**（验收工具见 §5）；**输入相关改动必须覆盖装了 UU/GameViewer 的机器**（仲裁器只在那种机器上武装，bug 只在那种机器上现形）。
4. 工具链在 `D:\tools\`（dotnet/go/ffmpeg/innosetup/7zip），不在 PATH 里，脚本用 `-ToolsRoot D:\tools`。
5. 热更新包必须用 `license/private.pem` 签名（`scripts/_publish_update.py` 自动做）；私钥/令牌不入 git（见 .gitignore）。
6. 日志/注释/提交信息用中文；提交格式 `vX.Y.Z: 改动一句话`。
7. **等被控端自动升级时别频繁 status/grablog**——查询也算"主控端连接"，会一直重置"空闲 5 分钟才应用"的计时；≥7 分钟查一次。

## 2. 一屏架构（数据流）

```
主控端 Viewer.exe（WPF）
  ├─ WebSocketService ──ws──▶ 中继 :18080（Go）──ws──▶ Agent.Coordinator（被控端）
  │      键鼠/剪贴板/软件列表消息(0x02..0x21)                    │
  │      ◀── 视频帧(0x01)/状态(0x41)/剪贴板(0x20) ──────────────┤
  └─ H264StreamDecoder(ffmpeg解码子进程, 最新帧胜出) ◀──────────┤
                                                                │
Agent.Coordinator（单进程，两半）：                              │
  ├─ CoordinatorApp(Program.cs)  托盘/中继WS/授权/热更新          │
  └─ WorkerHost 线程 ──WorkerBridge(进程内Channel)──▶ WorkerContext
        ├─ WGC 采集虚拟屏 → ffmpeg(h264_qsv) → 中继               │
        ├─ InputInjector(SendInput+扫描码) ← 远端键鼠             │
        ├─ WindowManager(窗口三级认领+搬移到副屏+零闪)             │
        ├─ CursorArbiter(光标仲裁/被动监听) + MouseRouter(双路径)  │
        └─ ClipboardWatcher(剪贴板同步) / ReclaimOrphan(断线回收)  │
```

消息类型号看 `src/Agent/Agent.Common/Protocol.cs`（MessageType 枚举 0x01..0x45）。

## 3. 目录与每个文件的用途

### src/Agent/Agent.Common（两端共用库）
| 文件 | 用途 |
|---|---|
| Protocol.cs | 消息类型 + 编解码（帧格式：1B类型+4B长度BE+负载） |
| AppConfig.cs | agent.json 配置模型（中继地址/口令/采集/热更新周期等） |
| Logger.cs | 按天分文件日志（worker-/coordinator-*.log） |
| NativeApi*.cs | P/Invoke 分文件：窗口/钩子(Hook)/输入(Input)/GDI显示/Launch进程 |
| DisplayHelper.cs | 显示器枚举/挑虚拟屏/切分辨率/**相邻归位**(EnsureVirtualAdjacent) + DisplayLayoutSnapshot(布局快照+医生) |
| WorkerBridge.cs | Coordinator↔Worker 进程内消息通道（2026-10 进程合并后替代命名管道） |
| H264StreamDecoder.cs | ffmpeg 解码子进程封装：非阻塞喂流+最新帧胜出（主控端共用） |
| InputInjector 相关 | BackgroundInput.cs=后台定向注入(PostMessage) |
| LicenseCodec/LicenseGate | ECDSA P-256 激活码验签（格式 RC1.*，与 Go 侧互验） |
| UpdateCodec.cs | 热更新清单验签（格式 RCU1.*，同一把密钥） |
| CrashDumper.cs | 崩溃 minidump（logs\crash\，保留5份，重启时报告） |
| FolderTransfer.cs / FileUploader 由 Coordinator 用 | 文件夹复制（跳过被锁文件）/上传 |

### src/Agent/Agent.Worker（采集引擎，被 WorkerHost 线程承载）
| 文件 | 用途 |
|---|---|
| Program.cs | WorkerContext：STA 引擎线程、采集循环、**所有消息处理**（键鼠门控在 KeyEvent case）、SendMonitorInfo、孤儿回收、拓扑巡检 |
| WindowManager.cs | 最大最核心：三级认领(pid/同名/新进程)+闸门、零闪搬移(SW_HIDE+cloak)、OpenThisPC、Win+R 会话、搬移/回收、ForceActivateWindow、IME 英文化 |
| InputInjector.cs | SendInput 注入：鼠标(带InjectTag)+键盘(**扫描码注入**，绕中文输入法) |
| MouseRouter.cs | 鼠标双路径路由：真实光标 ⇄ 后台定向注入；光标被抢探测(CursorContested) |
| CursorArbiter.cs | 光标仲裁（本机用户动手→光标还给他）；被动监听模式；**人手判定**(IsSoftwareInjection) |
| WgcFrameSource.cs | WGC 采集（IsBorderRequired=false 反射去黄框） |
| ClipboardWatcher.cs | 剪贴板监听/写回（文本+文件） |

### src/Agent/Agent.Coordinator（被控端外壳）
| 文件 | 用途 |
|---|---|
| Program.cs | 入口：互斥单实例+CrashDumper+CoordinatorApp（托盘/中继WS/授权/软件列表/文件收发） |
| WorkerHost.cs | 承载 WorkerContext 的 STA 线程（钩子宿主） |
| Updater.cs | 热更新闭环：查→验签→暂存→空闲5分钟→应用→90秒报平安→回滚 |
| FileUploader.cs / TrayIcon.cs | 上传 / 托盘图标 |

### src/Viewer（主控端 WPF）
| 文件 | 用途 |
|---|---|
| MainWindow.xaml.cs | 主窗口：连接/软列表/剪贴板/渲染循环(CompositionTarget+TryTakeFrame) |
| Controls/RemoteScreenControl.cs | 远程画面控件：键鼠捕获→事件、只读区域拦截、坐标映射 |
| Services/WebSocketService.cs | 中继 WS：收发泵、发送队列、直连候选 |
| Services/VideoDecoder.cs | = H264StreamDecoder 换名 |
| Services/{ViewerConfig,FileDownloader,HotkeyCapture}.cs | 配置/下载/本地热键 |

### src/Server（Go 中继）
| 文件 | 用途 |
|---|---|
| main.go/relay.go | 入口 + WS 转发（/ws?role=agent|viewer）、/api/agents 在线列表 |
| update.go | 热更新清单/包分发（灰度%） |
| license.go/revoke.go | 激活码验签/吊销 |
| filestore.go | /api/upload /api/download（剪贴板文件/更新包） |
| admin.go | 管理 API（admin token） |

### src/Tools
| 项目 | 用途 |
|---|---|
| LicenseKeygen | 厂商上位机：激活码签发 + 热更新签名页 |
| LicenseCli | 命令行签发 |
| AgentSetup/Installer | 两个 Inno 安装脚本（setup-agent.iss / setup-viewer.iss，AppName=虚拟服务器） |
| CrashDumpProbe | CrashDumper 本地验收小工具（可本机跑） |

### 其它目录
`scripts/`（构建/发布/真机诊断，见§5）· `docs/`（专题文档，见§6）· `build/`（产物，gitignore）· `license/`（私钥，gitignore）· `config/relay-token.txt`（中继口令）· `third_party/`（ffmpeg）· `drivers/`（VDD 虚拟屏驱动，gitignore）

## 4. 最容易踩坑的机制（改之前先懂）

1. **窗口搬移三级认领**（WindowManager.TryCatchWindow）：①精确pid=远端开的 ②同名进程 ③新进程——级数越靠后闸门越严（本机用户 1.2s 内动过键鼠就不搬），防止把本机用户开的软件搬到副屏；`justOpened`（打开指令后 4 秒）豁免闸门。
2. **键盘链路**（v1.4.6~1.5.13 打磨完）：KeyEvent → 本机用户 **300ms** 内动手则丢（防打进他的窗口；"物理人手"判定：键盘用 `LLKHF_INJECTED=0x10`、鼠标用 `LLMHF_INJECTED=0x1`，**不是一个位，用错就自己拦自己**——v1.5.6 事故）→ EnsureClaimedForeground → **扫描码 SendInput**（纯 VK 会被微软拼音吃 Ctrl）→ 认领过的窗口自动 **IME 英文直通**（中文输入走剪贴板粘贴）→ **Ctrl+A/C 双路注入**（扫描码+VK 各一次，幂等键专属；Win11 新记事本扫描码组合丢失、老 IME 吃 VK 组合，两条路各救一头）→ **Ctrl+C 另补 Ctrl+Insert 第三通道**（新记事本唯 C 不通的怪癖）。V/Z/S 等非幂等键**绝不双发**（粘贴/撤销两次会出事）。
3. **光标**：一会话一光标。本机用户空闲→远端用真实光标；用户动手/光标被 GameViewer 类软件抢→切后台定向注入（PostMessage）。我们注入都带 `InjectTag`，`IsSoftwareInjected(Kb)` 区分"人手/软件注入"（别把同机远控的注入当成人在打字——仲裁器只在检测到同类远控时武装，没装 UU 的机器永远复现不了这类 bug，**测试必须覆盖装了 UU 的机器**）。
4. **热更新闭环**：`_publish_update.py`（签名+上传+manifest）→ 被控端每 **15 分钟**检查 → 暂存 → **无主控端连接 5 分钟**才应用（绝不断正在用的人）→ 90 秒报平安失败自动回滚。诊断期可手动 `_rcp.py deploy`。
5. **虚拟屏布局**：物理屏 0,0 起，虚拟屏须在其右侧紧贴（重叠=窗口半边显示在主屏）。启动 EnsureVirtualAdjacent 归位 + 2 秒巡检 + 布局快照 `config/display-layout.json` + 布局医生日志。注意：某些机器虚拟屏位置被驱动锁死，ChangeDisplaySettingsEx 会"假成功"——代码里有核实+指数退避。
6. **无人观看暂停**（v1.5.8）：所有主控端断开 ≥10 秒 → 暂停采集与编码（ffmpeg 空转等输入近零开销，拓扑自愈照跑）；BitrateHint 或码流重建请求一到立即恢复。被控端 7×24 挂在客户机上不能空烧。
7. **编码器自适应**（v1.5.8）：候选 nvenc→qsv→amf→libx264，**按真实分辨率/帧率探测**（64x64 小图探测会让半残驱动蒙混过关→实跑反复重建码流="换台电脑特别卡"）；探测缓存按分辨率区分；运行期硬件编码器反复失败自动 ForceSoftware 降级。
8. **剪贴板监听序号纪律**（v1.5.11）：`GetClipboardSequenceNumber` 只在**成功读到内容后**才提交 `_lastSequence`；被占用重试 5 次全失败则 600ms 后主动补读（Windows 不会为"没读走"的内容再发 WM_CLIPBOARDUPDATE）——先提交后读=这次复制永久丢失。

## 5. 真机验收工具（scripts/_rcp.py）

连真机做诊断/部署（连接信息在文件顶部常量；机器：`WIN-CEQ12K8EJM9-D625DA31`=开发测试机，`WIN-JOGG0C60202-9567957E`=用户试用机【VDD 已坏待重装驱动，画面停顿与此有关】，`WIN-VSAQ3MBL5MA-55BC6EC8`=朋友机【Win11 26H2 新记事本，输入法怪癖多，是好试金石】，`WIN-OG1HLKFEOE9-ECB5FE5A`=第二台朋友机）：

```bash
export RC_AGENT=WIN-VSAQ3MBL5MA-55BC6EC8   # 或 WIN-CEQ 等；不设则默认测试机
python scripts/_rcp.py status     # 版本/FPS/输入路径/更新状态；RC_FULLMON=1 看完整显示拓扑
python scripts/_rcp.py grablog    # 拉两端日志尾（经剪贴板通道）；RC_TAIL=600 拉更多行
python scripts/_rcp.py thispc     # 远程打开"此电脑"（测窗口搬移零闪）
python scripts/_rcp.py typetest   # 键盘端到端：打字→Ctrl+C→剪贴板回传→Ctrl+V 粘贴回
python scripts/_rcp.py kbtest5    # 视频流取证版键盘测试（含选区高亮判定）
python scripts/_rcp.py capture out.h264 15   # 抓 15 秒视频流，本地 ffmpeg 解码看画面
python scripts/_rcp.py deploy <fileId> <字节数>   # 手动热更（先 /api/upload 拿 fileId）
python scripts/_rcp.py run "<cmd>"  # 在被控端跑命令（输出走文件+clip 两步回传更稳）
```

⚠ **status/grablog 也是"主控端连接"**：会重置被控端"空闲 5 分钟才应用更新"的计时——等升级时要拉长间隔（≥7 分钟查一次），别越查越升不上去。

⚠ **绝不在开发机本地注入键鼠做测试**（2026-10-09 险些把测试键打进用户聊天窗口）：输入类实验一律走真机远程（ztest*.py 模式：aiohttp 连 WS 按协议注入 + `capture` 抓帧看结果 + CLIPNOW 探针读远端剪贴板——把结果 Add-Content 进 `logs\worker-9999.log` 再 grablog，时间戳要带上防读到旧行）。

构建/发布：`scripts/build-all.ps1 -ToolsRoot D:\tools` → `scripts/_publish_update.py build/agent-X.Y.Z.zip X.Y.Z "说明" 100` → 安装包 `build-setup-agent/viewer.ps1 -Token <口令>`（口令内嵌+setup-defaults.ini 双保险）。

⚠ **热更包必须带齐依赖 DLL**（2026-10-09 事故）：包内容是 `Agent.Coordinator.dll + Agent.Worker.dll + Agent.Common.dll + Microsoft.Windows.SDK.NET.dll + WinRT.Runtime.dll` 共 5 个。TFM 从 19041 升到 22621 后只带 3 个业务 DLL → 客户机上旧版 SDK 投影 DLL 加载失败 → WGC 采集静默回退 GDI（变卡）。**凡是动 TFM / NuGet 依赖 / SDK 版本，必须 diff publish 输出的 DLL 清单再定 zip 内容**；发版前用旧安装目录模拟"部分覆盖"验证。另外：**版本号改动必须走完整 publish**（只 build 不 publish 的话 zip 里还是旧版本号 DLL → 升级死循环）。

⚠ **禁止裸 `dotnet publish -o build/viewer`**（2026-10-08 事故）：build\viewer 是**自包含**发布（免装 .NET 双击即跑）；裸 publish 不清目录，会把框架依赖版 exe/runtimeconfig 盖在自包含运行时上 → 双击弹"必须安装 .NET 8"（开发机上带 DOTNET_ROOT 能跑，极易漏测）。临时验证用 `-o build/viewer-test` 之类独立目录；正式恢复一律带 `-r win-x64 --self-contained true`，或直接跑 build-setup-viewer.ps1（会顺手补 ffmpeg/文档并剔除 config/logs）。本机 dotnet 在 D:\tools\dotnet（非系统路径），本机双击自包含版是唯一可靠回归法。

## 6. 深入文档（按需读）

`docs/架构-单会话共享与双光标.md`（双用户共享的设计极限与取舍）· `docs/使用说明.md` · `docs/运维-授权与防绕过.md` · `docs/部署-香港服务器.md` · `docs/验收报告.md` · `docs/P2P方案.md` · `../uu-reverse/README.md`（UU远程逆向对比）· `README.md`（产品总览）

## 7. 版本速史

1.2.x 进程合并/热更新体系 → 1.3.0 文件夹复制 → 1.4.0 性能（TFM 19041 回退）→ 1.4.1~1.4.5 窗口搬移/键盘/断线回收/跨双屏（虚拟屏相邻归位）→ 1.4.6~1.4.9 键盘三连修（注入误判/窗口焦点/扫描码+IME）→ **1.5.0 崩溃转储+布局快照/医生** → **1.5.1 更新周期 6h→15min** → 1.5.2 ClaimedWindow 补设 → 1.5.3 VDD 自灭 pnputil 自愈 → 1.5.4 Unicode 文本注入通道 → **1.5.5 键盘闸门 800→300ms+来源记录** → **1.5.6 键盘注入位 LLKHF(0x10)≠鼠标位(0x1)，闸门自拦根治**（装 UU 机器"只出第一个字母"）→ **1.5.7 文件夹打包暂存盘自动选（安装盘>源盘>Temp），C 盘保护** → **1.5.8 自适应三件套**（无人观看≥10s 暂停采集编码 / 编码器真实分辨率探测 / 黄框 IsBorderRequired 编译期绑定+TFM 22621）→ **1.5.9 热更包补齐 SDK 投影 DLL（5 个）**（1.5.8 的包不齐导致 WGC 回退 GDI）→ **1.5.10 更新器加固**（下载 tmp+原子改名/同版本哈希去重/应用脚本 Wait-Process+解压重试×3/回滚含 SDK DLL）→ **1.5.11 剪贴板序号读后才提交+600ms 补读；防闪武装期 25s→60s+钩子线程最高优先级** → **1.5.12 Ctrl+A/C 双路注入**（扫描码+VK，幂等键专属）→ **1.5.13 Ctrl+C 补 Ctrl+Insert 第三通道**（Win11 新记事本唯 C 不通）。主控端并行线：viewer 1.2.1（本地剪贴板读写重试 6×60ms/3×50ms，CLIPBRD_E_CANT_OPEN 不再一击即丢）。
