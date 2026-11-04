using System.Drawing;
using System.Drawing.Imaging;
using Agent.Common;
using Agent.Worker;

namespace CaptureProbe;

/// <summary>
/// 采集探针：现场"切到全部屏幕就黑屏"的两个可疑点，各验一次。
///
///   ① GDI 能不能一次抓住整个虚拟桌面（主屏 + 虚拟外屏合成一块画布）？
///      抓不到的话，被控端上传的整幅画面就是黑的 —— 这是"切过去就黑"的第一嫌疑。
///   ② ffmpeg 编码器在画布尺寸下能不能启动？
///      代码里给 libx264 写死了 -level 4.0，而它的上限约 2048x2048；
///      4480x1440（现场的分辨率）远超上限，x264 会直接拒绝启动 → 编码器反复重启 → 全程黑屏。
///
/// 只读屏幕、只写临时目录的 png，不改系统任何东西。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var outDir = Path.Combine(Path.GetTempPath(), "capture-probe");
        Directory.CreateDirectory(outDir);
        Console.WriteLine($"=== 采集/编码探针（输出目录 {outDir}）===");

        var log = new Logger("captureprobe", Path.Combine(Path.GetTempPath(), "captureprobe"), LogLevel.Info, echoConsole: false);

        var monitors = DisplayHelper.GetMonitors();
        foreach (var m in monitors) Console.WriteLine($"  屏：{m}");
        var virt = DisplayHelper.PickCaptureTarget();
        var (vl, vt, vw, vh) = DisplayHelper.GetVirtualDesktopBounds();
        Console.WriteLine($"  虚拟外屏={virt}");
        Console.WriteLine($"  虚拟桌面=({vl},{vt}) {vw}x{vh}");

        int problems = 0;

        // ---------------- ① 两种目标的 GDI 采集
        problems += Grab(log, virt, "单屏-虚拟外屏", outDir);
        var canvas = new DisplayInfo("ALL-SCREENS", vl, vt, vw, vh, virt?.IsPrimary ?? false, "全部屏幕（虚拟桌面画布）");
        problems += Grab(log, canvas, "画布-全部屏幕", outDir);

        // ---------------- ①b 拼接式画布（被控端"全部屏幕"用的就是它）
        Console.WriteLine("--- 拼接式画布（每块屏各采 + 拼）---");
        try
        {
            using var comp = new CompositeFrameSource(canvas, monitors, "auto", log);
            using var ms = new MemoryStream();
            if (!comp.Capture(ms, out var cerr))
            {
                Console.WriteLine($"  [FAIL] 拼接画布：采集失败 {cerr}");
                problems++;
            }
            else
            {
                var bytes = ms.ToArray();
                long sum = 0; int samples = 0;
                for (int i = 0; i + 4 <= bytes.Length; i += 4 * 97) { sum += bytes[i] + bytes[i + 1] + bytes[i + 2]; samples++; }
                double avg = samples == 0 ? 0 : sum / (double)samples / 3.0;
                using var bmp = new Bitmap(comp.Width, comp.Height, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, comp.Width, comp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, Math.Min(bytes.Length, comp.Width * comp.Height * 4));
                bmp.UnlockBits(data);
                var thumb = new Bitmap(bmp, new Size(960, Math.Min(960 * comp.Height / Math.Max(1, comp.Width), comp.Height)));
                var path = Path.Combine(outDir, "拼接画布.png");
                thumb.Save(path, ImageFormat.Png);
                thumb.Dispose();
                bool black = avg < 3;
                Console.WriteLine($"  [{(black ? "FAIL" : "PASS")}] 拼接画布：{comp.Width}x{comp.Height} 后端={comp.Backend} " +
                                  $"平均亮度 {avg:F1}{(black ? "（全黑）" : "")} → {path}");
                if (black) problems++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] 拼接画布：{ex.GetType().Name}: {ex.Message}");
            problems++;
        }

        // ---------------- ② 编码器：画布尺寸 vs 现场尺寸
        Console.WriteLine("\n--- 编码器启动测试 ---");
        problems += TryEncoder(log, vw, vh, $"{vw}x{vh}（当前画布）");
        if (vw * vh > 2048 * 2048) problems += TryEncoder(log, 2048, 2048, "2048x2048（另一种大尺寸）");
        problems += TryEncoder(log, 1920, 1080, "1920x1080（单屏，已知可用）");

        Console.WriteLine(problems == 0
            ? "\n➜ 两项都没问题：黑屏的原因在别处"
            : $"\n➜ 有 {problems} 项失败，就是它们导致的");
        Environment.Exit(0);
        return problems == 0 ? 0 : 1;
    }

    /// <summary>用真实的 GDI 采集抓一帧，存成缩略 png，并统计"是不是全黑"</summary>
    private static int Grab(Logger log, DisplayInfo? target, string name, string outDir)
    {
        if (target == null) { Console.WriteLine($"  [SKIP] {name}：没有这个目标"); return 0; }
        try
        {
            using var src = FrameSourceFactory.Create("gdi", target, log);
            using var ms = new MemoryStream();
            if (!src.Capture(ms, out var err))
            {
                Console.WriteLine($"  [FAIL] {name}：采集失败 {err}");
                return 1;
            }
            var bytes = ms.ToArray();
            int w = src.Width, h = src.Height;
            // 统计亮度：全黑的话说明抓到的内容不可用
            long sum = 0; int samples = 0;
            for (int i = 0; i + 4 <= bytes.Length; i += 4 * 97) { sum += bytes[i] + bytes[i + 1] + bytes[i + 2]; samples++; }
            double avg = samples == 0 ? 0 : sum / (double)samples / 3.0;

            using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, data.Scan0, Math.Min(bytes.Length, w * h * 4));
            bmp.UnlockBits(data);
            var thumb = new Bitmap(bmp, new Size(Math.Min(960, w), Math.Min(960 * h / Math.Max(1, w), h)));
            var path = Path.Combine(outDir, $"{name}.png");
            thumb.Save(path, ImageFormat.Png);
            thumb.Dispose();

            bool black = avg < 3;
            Console.WriteLine($"  [{(black ? "FAIL" : "PASS")}] {name}：{w}x{h} 平均亮度 {avg:F1}" +
                              $"{(black ? "（全黑：这块内容抓不到）" : "")} → {path}");
            return black ? 1 : 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] {name}：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>按真实参数起一个 ffmpeg 编码器，喂黑帧，看它能不能出码流</summary>
    private static int TryEncoder(Logger log, int w, int h, string label)
    {
        try
        {
            var ff = FfmpegLocator.Resolve("");
            if (string.IsNullOrEmpty(ff))
            {
                Console.WriteLine("  [SKIP] 找不到 ffmpeg.exe");
                return 0;
            }
            long chunks = 0;
            using var enc = new FfmpegEncoder(ff, "libx264", w, h, 15, 2000, log);
            enc.Chunk += (_, _) => Interlocked.Increment(ref chunks);
            if (!enc.Start())
            {
                Console.WriteLine($"  [FAIL] {label}：编码器启动失败 → {enc.LastError}");
                return 1;
            }
            var frame = new byte[w * h * 4];
            for (int i = 0; i < 30; i++)
            {
                if (!enc.WriteFrame(frame)) break;
                Thread.Sleep(20);
            }
            Thread.Sleep(400);
            bool ok = Interlocked.Read(ref chunks) > 0;
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {label}：编码器启动 {(ok ? "成功" : "失败")}，" +
                              $"喂 30 帧得到 {chunks} 个码流块" +
                              (ok ? "" : $" 最后错误：{Truncate(enc.LastError, 200)}"));
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] {label}：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static string Truncate(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n]);
}
