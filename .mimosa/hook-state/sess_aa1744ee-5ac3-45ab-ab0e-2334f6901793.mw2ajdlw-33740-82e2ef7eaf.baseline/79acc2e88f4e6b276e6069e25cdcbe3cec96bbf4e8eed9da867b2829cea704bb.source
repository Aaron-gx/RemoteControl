using System.Diagnostics;
using System.Net.Http;
using Agent.Common;

namespace E2E;

/// <summary>
/// 端到端验收：以"无头主控端"身份连接中继，逐项验证策划 §十一 的手动验证清单。
/// 用法：
///   E2E.exe --server ws://127.0.0.1:8080/ws --agent ID --dir E:\RemoteControl\e2e [--scenario all|connect|video|software|input|clipboard|file|reconnect]
/// </summary>
internal static class Program
{
    private static int _pass, _fail;
    private static readonly List<string> Failures = new();

    private static async Task<int> Main(string[] args)
    {
        var server = Arg(args, "--server") ?? "ws://127.0.0.1:8080/ws";
        var agent = Arg(args, "--agent") ?? "";
        var dir = Arg(args, "--dir") ?? @"E:\RemoteControl\e2e";
        var scenario = (Arg(args, "--scenario") ?? "all").ToLowerInvariant();
        var ffmpeg = Arg(args, "--ffmpeg") ?? FindFfmpeg();
        var token = Arg(args, "--token") ?? "";

        Directory.CreateDirectory(dir);
        var log = new Logger("e2e", baseDir: dir, minLevel: LogLevel.Info, echoConsole: false);
        Console.WriteLine($"端到端验收  服务器={server}  被控端={agent}  证据目录={dir}");
        Console.WriteLine($"ffmpeg={ffmpeg}\n");

        if (string.IsNullOrEmpty(agent) && scenario != "bench")
        {
            // 自动发现：从 /api/agents 取第一个
            agent = await DiscoverAgentAsync(server, token);
            if (string.IsNullOrEmpty(agent))
            {
                Console.WriteLine("FAIL: 没有在线被控端（/api/agents 为空）");
                return 1;
            }
            Console.WriteLine($"自动发现被控端：{agent}\n");
        }

        using var harness = new ViewerHarness(server, agent, ffmpeg, log) { Token = token };
        harness.RequestStreamReset += () =>
            harness.Queue(new ProtocolMessage(MessageType.StreamReset, PayloadCodec.Empty()));
        if (scenario != "bench") harness.Start();
        else Console.WriteLine("（bench 场景不建立中继连接）");

        var scenarios = scenario == "all"
            ? new[] { "connect", "software", "video", "clipboard", "input", "file", "direct", "reconnect" }
            : scenario.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var s in scenarios)
        {
            Console.WriteLine($"----- 场景：{s} -----");
            switch (s)
            {
                case "connect": await ScenarioConnect(harness); break;
                case "video": await ScenarioVideo(harness, dir); break;
                case "software": await ScenarioSoftware(harness); break;
                case "input": await ScenarioInput(harness, dir); break;
                case "clipboard": await ScenarioClipboard(harness, dir); break;
                case "file": await ScenarioFile(harness, dir, server); break;
                case "reconnect": await ScenarioReconnect(harness, dir); break;
                case "bench": await ScenarioBench(dir, log); break;
                case "direct": await ScenarioDirect(harness); break;
                default: Console.WriteLine($"未知场景 {s}"); break;
            }
            Console.WriteLine();
        }

        Console.WriteLine("================ 结果 ================");
        Console.WriteLine($"通过 {_pass} 项，失败 {_fail} 项");
        foreach (var f in Failures) Console.WriteLine($"  FAIL: {f}");
        log.Info($"E2E 结束：通过 {_pass} 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 场景

    private static async Task ScenarioConnect(ViewerHarness h)
    {
        Check("连接到中继", await WaitAsync(() => h.IsConnected, 15000), "15s 内未连上中继");
        Check("收到软件列表", await WaitAsync(() => h.Software.Count > 0, 30000),
            "30s 内没有收到 0x10 软件列表");
        Console.WriteLine($"    软件数量：{h.Software.Count}");
        foreach (var s in h.Software.Take(8))
            Console.WriteLine($"      {(s.IsRunning ? "🔵" : "⚪")} {s.Name}  {s.ExePath}");
        Check("软件项含可执行路径", h.Software.All(s => !string.IsNullOrEmpty(s.ExePath)),
            "有软件项缺少 exePath");
        Check("软件项带图标(base64)", h.Software.Any(s => !string.IsNullOrEmpty(s.Icon)),
            "没有一项带图标（ReportSoftwareIcons 是否关闭？）");
        Check("无被控端错误", h.Errors.Count == 0, string.Join("; ", h.Errors));
    }

    private static async Task ScenarioVideo(ViewerHarness h, string dir)
    {
        Check("收到显示器信息", await WaitAsync(() => h.Monitor != null, 30000),
            "30s 内没有收到 0x14 显示器信息");
        Console.WriteLine($"    {h.Monitor?.Width}x{h.Monitor?.Height} (count={h.Monitor?.Count})");
        Check("收到视频码流", await WaitAsync(() => h.VideoBytes > 50_000, 30000),
            $"30s 内视频数据不足（{h.VideoBytes} 字节）");
        Check("H.264 解码出画面帧", await WaitAsync(() => h.DecodedFrames > 5, 30000),
            $"30s 内只解出 {h.DecodedFrames} 帧");

        // 存一帧作为验收证据
        var png = Path.Combine(dir, "video_frame.png");
        await Task.Delay(1500);
        if (h.TryGetFrameSnapshot(out var frame, out int w, out int hh))
        {
            var raw = Path.Combine(dir, "video_frame.bgra");
            File.WriteAllBytes(raw, frame);
            if (h.SaveFrameAsPng(raw, png, w, hh))
                Console.WriteLine($"    已保存画面证据：{png}");
        }
        Check("画面证据文件已生成", File.Exists(png), "未能保存 PNG 证据");

        // 逐秒监控 10 秒（作为验收证据：码率/帧率是否稳定）
        long prevBytes = h.VideoBytes, prevFrames = h.DecodedFrames;
        var swTotal = Stopwatch.StartNew();
        long framesAtStart = h.DecodedFrames;
        Console.WriteLine("    秒 | 收到KB | 块 | 解码帧 | 本秒fps");
        for (int i = 1; i <= 10; i++)
        {
            await Task.Delay(1000);
            long db = h.VideoBytes - prevBytes, df = h.DecodedFrames - prevFrames;
            prevBytes = h.VideoBytes; prevFrames = h.DecodedFrames;
            Console.WriteLine($"    {i,2} | {db / 1024,6} | {h.VideoChunks,4} | {h.DecodedFrames,6} | {df,3}");
        }
        // 跨公网时 TCP 呈"成簇到达"，逐秒值噪声很大；有意义的是整段平均帧率
        double avgFps = (h.DecodedFrames - framesAtStart) / swTotal.Elapsed.TotalSeconds;
        Console.WriteLine($"    平均解码帧率：{avgFps:F1} fps，累计 {h.VideoBytes / 1024}KB / {h.VideoChunks} 块，" +
                          $"丢弃 {h.DroppedChunks} 块，用时 {swTotal.Elapsed.TotalSeconds:F1}s");
        Check("平均帧率 ≥ 10fps（跨公网按 10 秒均值判定）", avgFps >= 10, $"实测 {avgFps:F1} fps");
        Check("解码管线无积压丢块", h.DroppedChunks == 0, $"丢弃 {h.DroppedChunks} 块（网络读循环或解码器被阻塞）");
        Check("被控端上报采集帧率", h.LastStatus != null && h.LastStatus.CaptureFps > 0,
            "没有收到 WorkerStatus");
        if (h.LastStatus != null)
            Console.WriteLine($"    被控端：后端={h.LastStatus.Backend} 采集={h.LastStatus.CaptureFps}fps " +
                              $"编码字节={h.LastStatus.BytesSent / 1024}KB");
    }

    private static async Task ScenarioSoftware(ViewerHarness h)
    {
        var target = h.Software.FirstOrDefault(s => s.Name.Contains("记事本"))
                     ?? h.Software.FirstOrDefault(s => s.ExePath.EndsWith("notepad.exe", StringComparison.OrdinalIgnoreCase));
        if (target == null)
        {
            Check("找到记事本", false, "软件列表里没有记事本");
            return;
        }
        Console.WriteLine($"    目标：{target.Name} {target.ExePath}");

        h.SoftwareStates.Clear();
        h.Queue(new ProtocolMessage(MessageType.OpenSoftware, PayloadCodec.EncodeText(target.ExePath)));
        Check("打开软件：被控端报告已运行",
            await WaitAsync(() => h.SoftwareStates.Any(s =>
                s.IsRunning && s.ExePath.Equals(target.ExePath, StringComparison.OrdinalIgnoreCase)), 30000),
            "30s 内没有收到 IsRunning=true 的状态上报");

        var st = h.SoftwareStates.FirstOrDefault(s => s.IsRunning &&
            s.ExePath.Equals(target.ExePath, StringComparison.OrdinalIgnoreCase));
        Console.WriteLine($"    pid={st?.Pid}");

        if (st is { Pid: > 0 })
        {
            h.SoftwareStates.Clear();
            h.Queue(new ProtocolMessage(MessageType.CloseSoftware, PayloadCodec.EncodeCloseSoftware(st.Pid)));
            Check("关闭软件：被控端报告已退出",
                await WaitAsync(() => h.SoftwareStates.Any(s => !s.IsRunning), 30000),
                "30s 内没有收到 IsRunning=false 的状态上报");
        }
    }

    private static async Task ScenarioClipboard(ViewerHarness h, string dir)
    {
        // 1) 远程 → 本地：在远程会话里用 clip.exe 设置剪贴板
        const string marker = "RC_CLIP_MARKER_20260920";
        h.ClipboardTexts.Clear();
        var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var args = $"/c echo {marker}|clip";
        h.Queue(new ProtocolMessage(MessageType.OpenSoftware,
            JsonCodec.Encode(new { exePath = cmdExe, args })));
        Check("远程剪贴板文本上报到主控端（远程 Ctrl+C 场景）",
            await WaitAsync(() => h.ClipboardTexts.Any(t => t.Contains(marker)), 40000),
            $"40s 内没有收到含 {marker} 的剪贴板文本");
        if (h.ClipboardTexts.Count > 0)
            Console.WriteLine($"    收到文本：{Truncate(h.ClipboardTexts[0], 80)}");

        // 2) 本地 → 远程：主控端下发文本，远程会话读取剪贴板落盘验证
        const string reverse = "RC_CLIP_REVERSE_20260920";
        var outFile = Path.Combine(dir, "clip_reverse.txt");
        if (File.Exists(outFile)) File.Delete(outFile);
        h.Queue(new ProtocolMessage(MessageType.ClipboardText, PayloadCodec.EncodeText(reverse)));
        await Task.Delay(1500);
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var psArgs = $"-NoProfile -Command \"Get-Clipboard | Out-File -Encoding utf8 '{outFile}'\"";
        h.Queue(new ProtocolMessage(MessageType.OpenSoftware,
            JsonCodec.Encode(new { exePath = ps, args = psArgs })));
        bool wrote = await WaitAsync(() =>
        {
            try { return File.Exists(outFile) && File.ReadAllText(outFile).Contains(reverse); }
            catch { return false; }
        }, 40000);
        Check("主控端剪贴板同步到远程会话（本地 Ctrl+C → 远程 Ctrl+V）", wrote,
            $"40s 内 {outFile} 未出现或内容不符");
        if (wrote) Console.WriteLine($"    远程侧读到的剪贴板：{File.ReadAllText(outFile).Trim()}");
    }

    private static async Task ScenarioInput(ViewerHarness h, string dir)
    {
        var markerFile = Path.Combine(dir, "input_marker.txt");
        if (File.Exists(markerFile)) File.Delete(markerFile);

        // 在远程会话打开一个交互式 cmd，并把工作目录切到证据目录
        var cmdExe = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        h.Queue(new ProtocolMessage(MessageType.OpenSoftware,
            JsonCodec.Encode(new { exePath = cmdExe, args = $"/k cd /d {dir}" })));
        await Task.Delay(9000);   // 等窗口出现、被 Worker 置为前台（Worker 侧最多等 5s 找窗口，留足余量）

        // 注入按键： echo RC_INPUT_OK>input_marker.txt <Enter>
        const string text = "echo RC_INPUT_OK>input_marker.txt";
        Console.WriteLine($"    注入按键：{text}");
        foreach (var ch in text)
        {
            if (!TryCharToVk(ch, out ushort vk, out bool shift))
            {
                Check($"按键映射 {ch}", false, "缺少映射");
                continue;
            }
            if (shift)
            {
                h.Queue(new ProtocolMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey(0x10, false)));
                h.Queue(new ProtocolMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey(vk, false)));
                h.Queue(new ProtocolMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey(vk, true)));
                h.Queue(new ProtocolMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey(0x10, true)));
            }
            else
            {
                h.Queue(new ProtocolMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey(vk, false)));
                h.Queue(new ProtocolMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey(vk, true)));
            }
            await Task.Delay(25);
        }
        h.Queue(new ProtocolMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey(0x0D, false)));
        h.Queue(new ProtocolMessage(MessageType.KeyEvent, PayloadCodec.EncodeKey(0x0D, true)));

        bool ok = await WaitAsync(() =>
        {
            try { return File.Exists(markerFile) && File.ReadAllText(markerFile).Contains("RC_INPUT_OK"); }
            catch { return false; }
        }, 30000);
        Check("键鼠注入生效（远程 cmd 执行了被注入的命令）", ok,
            $"30s 内 {markerFile} 未生成");
        if (ok) Console.WriteLine($"    证据文件：{markerFile} = {File.ReadAllText(markerFile).Trim()}");

        // 鼠标：移动到 (100,100) 后点击，验证不抛错（光标位置由被控端日志体现）
        h.Queue(new ProtocolMessage(MessageType.MouseMove, PayloadCodec.EncodeMouseMove(100, 100)));
        await Task.Delay(200);
        h.Queue(new ProtocolMessage(MessageType.MouseButton, PayloadCodec.EncodeMouseButton(100, 100, 0, false)));
        await Task.Delay(80);
        h.Queue(new ProtocolMessage(MessageType.MouseButton, PayloadCodec.EncodeMouseButton(100, 100, 0, true)));
        Check("鼠标事件已发送且无被控端错误", h.Errors.Count == 0, string.Join("; ", h.Errors));
    }

    private static async Task ScenarioFile(ViewerHarness h, string dir, string server)
    {
        var srcName = "remote_payload.txt";
        const string content = "RC_FILE_PAYLOAD_20260920";
        var remoteSrc = Path.Combine(dir, srcName);
        var localDst = Path.Combine(dir, "downloaded_" + srcName);
        if (File.Exists(localDst)) File.Delete(localDst);

        // 在远程会话：写文件 → 复制到剪贴板（CF_HDROP）
        var ps = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = $"Set-Content -Path '{remoteSrc}' -Value '{content}' -Encoding utf8; Set-Clipboard -Path '{remoteSrc}'";
        h.Queue(new ProtocolMessage(MessageType.OpenSoftware,
            JsonCodec.Encode(new { exePath = ps, args = $"-NoProfile -Command \"{script}\"" })));

        Check("远程文件复制被检测并上传（收到 0x21 文件通知）",
            await WaitAsync(() => h.ClipboardFiles.Count > 0, 60000),
            "60s 内没有收到剪贴板文件通知");
        if (h.ClipboardFiles.Count == 0) return;

        var info = h.ClipboardFiles[0];
        Console.WriteLine($"    文件通知：{info.FileName} ({info.FileSize} 字节) fileId={info.FileId}");
        Check("文件名正确", info.FileName.Contains("remote_payload"), $"文件名={info.FileName}");
        Check("文件大小合理", info.FileSize > 0, $"size={info.FileSize}");

        // 主控端下载
        var baseUrl = server.Replace("ws://", "http://").Replace("wss://", "https://");
        baseUrl = baseUrl[..baseUrl.IndexOf("/ws", StringComparison.Ordinal)];
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var t = h.Token;
            var q = string.IsNullOrEmpty(t) ? "" : $"?token={Uri.EscapeDataString(t)}";
            var bytes = await http.GetByteArrayAsync($"{baseUrl}/api/download/{info.FileId}{q}");
            File.WriteAllBytes(localDst, bytes);
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            Check("下载内容与远程文件一致", text.Contains(content), $"下载内容={Truncate(text, 80)}");
            Console.WriteLine($"    已下载到 {localDst}");
        }
        catch (Exception ex)
        {
            Check("文件下载成功", false, ex.Message);
        }
    }

    private static async Task ScenarioReconnect(ViewerHarness h, string dir)
    {
        var before = h.Software.Count;
        h.Stop();
        await Task.Delay(2000);
        Check("断开后未连接", !h.IsConnected, "Stop() 后仍然连接");
        h.Start();
        Check("自动重连成功", await WaitAsync(() => h.IsConnected, 30000), "30s 内未重连");
        Check("重连后重新收到软件列表", await WaitAsync(() => h.Software.Count > 0, 30000),
            $"重连后没有软件列表（断开前 {before} 项）");
        Check("重连后画面恢复", await WaitAsync(() => h.DecodedFrames > 0, 30000), "重连后无解码帧");
        Console.WriteLine($"    重连后：软件 {h.Software.Count} 项，累计解码 {h.DecodedFrames} 帧");
    }

    /// <summary>
    /// P2P 直连验收：
    /// 1) 被控端是否上报了直连候选；2) 能否直连成功；
    /// 3) 直连后视频是否真的走直连、且**中继侧不再有视频流量**（这才是 P2P 成立的证据）。
    /// </summary>
    private static async Task ScenarioDirect(ViewerHarness h)
    {
        // 先确保中继链路已经出画面（作为对照）
        Check("中继链路已收到视频（对照组）", await WaitAsync(() => h.RelayVideoBytes > 20_000, 30000),
            $"中继视频字节 {h.RelayVideoBytes}");
        Check("被控端上报了直连候选", await WaitAsync(() => h.DirectCandidates.Count > 0, 30000),
            "30s 内没有收到 0x50 直连候选");
        Console.WriteLine($"    候选地址：{string.Join(", ", h.DirectCandidates)}");

        var ok = await h.TryDirectAsync(TimeSpan.FromSeconds(3));
        Check("P2P 直连建立成功", ok, $"直连失败：{h.DirectStatus}");
        if (!ok)
        {
            Console.WriteLine("    （直连不可用时继续用中继，属预期回落路径）");
            return;
        }
        Console.WriteLine($"    直连端点：{h.Direct?.ActiveEndpoint}");

        // ---- 证据 A（确定性）：被控端自己上报的路由计数 ----
        // 判据不依赖墙钟窗口：只要被控端自己说"直连处于激活状态"（directActive=true），
        // 那两条状态之间中继视频块就必须**一块都不涨**。
        // （这样做同时避开了两个坑：状态报文也走中继、直连刚建立/刚断开时会抖动。）
        int pairs = 0, grew = 0;
        long relayFirst = -1, relayLast = -1;
        var swDirect = Stopwatch.StartNew();
        while (swDirect.Elapsed < TimeSpan.FromSeconds(12))
        {
            var s1 = h.LastStatus;
            await Task.Delay(1200);
            var s2 = h.LastStatus;
            if (s1 == null || s2 == null || ReferenceEquals(s1, s2)) continue;
            if (!s1.DirectActive || !s2.DirectActive) continue;   // 只看"两边都处于直连激活"的状态对
            pairs++;
            if (relayFirst < 0) relayFirst = s1.RelayChunks;
            relayLast = s2.RelayChunks;
            if (s2.RelayChunks != s1.RelayChunks)
            {
                grew++;
                Console.WriteLine($"    直连激活期间中继块仍在涨：{s1.RelayChunks} → {s2.RelayChunks}");
            }
        }
        Console.WriteLine($"    被控端上报：采样 {pairs} 组直连期状态，中继视频块 {relayFirst} → {relayLast}");
        Check("被控端确认直连已生效（收到直连激活状态）", pairs > 0, "12s 内没有收到 directActive=true 的状态上报");
        Check("被控端确认中继侧视频已停（P2P 成立的关键证据）", pairs > 0 && grew == 0,
            $"{grew} 组状态里中继视频块仍在增长");

        // ---- 证据 B（观察）：主控端侧直连确实在收视频 ----
        long directBase = h.DirectVideoBytes;
        await Task.Delay(3000);
        long directGrowth = h.DirectVideoBytes - directBase;
        Console.WriteLine($"    主控端侧 3 秒内直连收到 {directGrowth / 1024}KB（中继侧另计 {h.RelayVideoBytes / 1024}KB 累计）");
        Check("视频经直连传输", directGrowth > 20_000, $"直连 3 秒只收到 {directGrowth} 字节");

        long decodedBefore = h.DecodedFrames;
        Check("直连期间画面仍在解码", await WaitAsync(() => h.DecodedFrames > decodedBefore, 10000),
            "直连期间没有解码出画面");
    }

    /// <summary>
    /// 解码管线吞吐基准：把一个既有的 H.264 文件按 16KB 分块喂进主控端解码管线，
    /// 测量纯解码+取帧吞吐，用来区分"网络/编码侧"与"解码侧"的瓶颈。
    /// </summary>
    private static async Task ScenarioBench(string dir, Logger log)
    {
        var ffmpeg = FindFfmpeg();
        var file = Path.Combine(dir, "bench.h264");
        if (!File.Exists(file))
        {
            Console.WriteLine($"    生成基准码流 {file} …");
            var psi = new ProcessStartInfo(ffmpeg,
                "-hide_banner -loglevel error -y -f lavfi -i testsrc=size=1920x1080:rate=30 -t 10 " +
                "-c:v libx264 -preset ultrafast -tune zerolatency -g 60 -b:v 4000k -pix_fmt yuv420p " +
                $"\"{file}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(120000);
        }
        var data = File.ReadAllBytes(file);
        Console.WriteLine($"    码流大小 {data.Length / 1024}KB");

        using var dec = new H264StreamDecoder(ffmpeg, log);
        Check("基准：解码器启动", dec.Configure(1920, 1080), "Configure 失败");

        // 1) 全速喂入
        var sw = Stopwatch.StartNew();
        const int chunk = 16 * 1024;
        for (int off = 0; off < data.Length; off += chunk)
            dec.Feed(data, off, Math.Min(chunk, data.Length - off));
        double feedMs = sw.Elapsed.TotalMilliseconds;
        await Task.Delay(1500);
        long frames = dec.DecodedFrames;
        Console.WriteLine($"    全速喂入 {data.Length / 1024}KB 用时 {feedMs:F0}ms，" +
                          $"解码 {frames} 帧（{frames / 10.0:F1} fps 等效能 力），丢弃 {dec.DroppedChunks} 块");

        // 2) 按 30fps 实时喂入，测可持续吞吐
        dec.Reset("基准第二段");
        await Task.Delay(300);
        long before = dec.DecodedFrames;
        sw.Restart();
        int feedFps = 0;
        var swFeed = Stopwatch.StartNew();
        for (int off = 0; off < data.Length; off += chunk)
        {
            dec.Feed(data, off, Math.Min(chunk, data.Length - off));
            feedFps++;
            await Task.Delay(33);   // 约 30fps 的节奏（每块 ≈ 1 帧）
        }
        await Task.Delay(1500);
        long decoded = dec.DecodedFrames - before;
        double secs = swFeed.Elapsed.TotalSeconds;
        Console.WriteLine($"    实时喂入 {feedFps} 块 / {secs:F1}s，解码 {decoded} 帧 → {decoded / secs:F1} fps，" +
                          $"丢弃 {dec.DroppedChunks} 块");
        Check("解码吞吐 ≥ 20fps（1080p）", decoded / secs >= 20, $"实测 {decoded / secs:F1} fps");
    }

    // ---------------------------------------------------------------- 工具

    private static async Task<bool> WaitAsync(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            try { if (cond()) return true; } catch { }
            await Task.Delay(150);
        }
        try { return cond(); } catch { return false; }
    }

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"  [PASS] {name}");
        }
        else
        {
            _fail++;
            Failures.Add($"{name} —— {detail}");
            Console.WriteLine($"  [FAIL] {name}  {detail}");
        }
    }

    private static string? Arg(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        var pref = args.FirstOrDefault(a => a.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
        return pref?[(name.Length + 1)..];
    }

    private static string FindFfmpeg()
    {
        var f = FfmpegLocator.Resolve("", AppContext.BaseDirectory);
        return f ?? "ffmpeg.exe";
    }

    private static async Task<string> DiscoverAgentAsync(string server, string token)
    {
        try
        {
            var baseUrl = server.Replace("ws://", "http://").Replace("wss://", "https://");
            var idx = baseUrl.IndexOf("/ws", StringComparison.Ordinal);
            if (idx > 0) baseUrl = baseUrl[..idx];
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var q = string.IsNullOrEmpty(token) ? "" : $"?token={Uri.EscapeDataString(token)}";
            var text = await http.GetStringAsync($"{baseUrl}/api/agents{q}");
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("agents", out var arr))
                foreach (var a in arr.EnumerateArray())
                    if (a.TryGetProperty("id", out var id)) return id.GetString() ?? "";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"/api/agents 查询失败：{ex.Message}");
        }
        return "";
    }

    private static bool TryCharToVk(char c, out ushort vk, out bool shift)
    {
        shift = false;
        vk = 0;
        if (c >= 'a' && c <= 'z') { vk = (ushort)(c - 32); return true; }
        if (c >= 'A' && c <= 'Z') { vk = c; shift = true; return true; }
        if (c >= '0' && c <= '9') { vk = c; return true; }
        switch (c)
        {
            case ' ': vk = 0x20; return true;
            case '.': vk = 0xBE; return true;
            case ',': vk = 0xBC; return true;
            case '-': vk = 0xBD; return true;
            case '_': vk = 0xBD; shift = true; return true;
            case '/': vk = 0xBF; return true;
            case '\\': vk = 0xDC; return true;
            case ':': vk = 0xBA; shift = true; return true;
            case ';': vk = 0xBA; return true;
            case '>': vk = 0xBE; shift = true; return true;
            case '<': vk = 0xBC; shift = true; return true;
            case '=': vk = 0xBB; return true;
            case '|': vk = 0xDC; shift = true; return true;
            case '"': vk = 0xDE; shift = true; return true;
            case '\'': vk = 0xDE; return true;
            case '!': vk = 0x31; shift = true; return true;
            case '@': vk = 0x32; shift = true; return true;
            case '#': vk = 0x33; shift = true; return true;
            case '$': vk = 0x34; shift = true; return true;
            case '%': vk = 0x35; shift = true; return true;
            case '^': vk = 0x36; shift = true; return true;
            case '&': vk = 0x37; shift = true; return true;
            case '*': vk = 0x38; shift = true; return true;
            case '(': vk = 0x39; shift = true; return true;
            case ')': vk = 0x30; shift = true; return true;
            case '+': vk = 0xBB; shift = true; return true;
            default: return false;
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n];
}
