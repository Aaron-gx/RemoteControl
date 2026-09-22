using Agent.Common;

namespace BgInputProbe;

/// <summary>
/// 后台定向注入探针。
///
/// 验证目标（这是"两个光标互不干扰"方案能不能成立的关键前提）：
///   1. 能不能算准"某个屏幕坐标下面是哪个窗口"（HitTest）；
///   2. 在**目标窗口不是前台窗口**的情况下，把 WM_MOUSEMOVE/WM_LBUTTONDOWN/WM_LBUTTONUP
///      投递过去，控件会不会真的响应（按钮 Click 触发）；
///   3. 往非前台的编辑框投递 WM_CHAR，文字会不会真的进去。
///
/// 安全性：本程序只对自己创建的窗口投递，且投递前校验命中窗口属于本进程；
/// 全程不调用 SetCursorPos / SendInput，不会影响桌面上任何其它程序。
/// </summary>
internal static class Program
{
    private static readonly List<string> Results = new();
    private static Form? _target;
    private static Form? _other;
    private static Button? _button;
    private static TextBox? _text;
    private static int _clicks;
    private static int _step;
    private static System.Windows.Forms.Timer? _timer;
    private static System.Windows.Forms.Timer? _watchdog;
    private static bool _finished;

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== 后台定向注入探针（只操作本程序自己创建的窗口）===");
        Console.WriteLine($"进程 pid={Environment.ProcessId}，DPI 模式={Application.HighDpiMode}");

        _target = new Form
        {
            Text = "BgInputProbe 目标窗口",
            Width = 460, Height = 280,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(140, 140),
            TopMost = true,
        };
        _button = new Button { Text = "目标按钮", Left = 40, Top = 40, Width = 170, Height = 46 };
        _button.Click += (_, _) => Interlocked.Increment(ref _clicks);
        _text = new TextBox { Left = 40, Top = 120, Width = 320 };
        _target.Controls.Add(_button);
        _target.Controls.Add(_text);

        // 另一个窗口用来"抢走前台"，以构造"目标窗口不是前台窗口"这一关键场景
        _other = new Form
        {
            Text = "BgInputProbe 占位窗口（占着前台）",
            Width = 380, Height = 170,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(660, 140),
            TopMost = true,
        };
        _other.Controls.Add(new Button { Text = "只是拿个焦点", Left = 20, Top = 20, Width = 210 });

        _target.Show();
        _other.Show();
        _other.Activate();
        Application.DoEvents();
        Console.WriteLine($"目标窗口 hwnd=0x{_target.Handle.ToInt64():X}，当前前台窗口=0x{NativeApi.GetForegroundWindow().ToInt64():X}");

        _timer = new System.Windows.Forms.Timer { Interval = 150 };
        _timer.Tick += (_, _) => Step();
        _timer.Start();

        // 兜底：8 秒还没跑完就判定失败退出，别把窗口留在别人桌面上
        _watchdog = new System.Windows.Forms.Timer { Interval = 8000 };
        _watchdog.Tick += (_, _) =>
        {
            Report(false, "超时：探针没有在 8 秒内完成");
            Finish();
        };
        _watchdog.Start();

        Application.Run();

        foreach (var r in Results) Console.WriteLine(r);
        int failed = Results.Count(r => r.StartsWith("[FAIL]"));
        Console.WriteLine(failed == 0 ? "==> 探针结论：后台定向注入对标准控件可用" : $"==> 探针结论：{failed} 项失败");
        return failed == 0 ? 0 : 1;
    }

    private static void Step()
    {
        _step++;
        switch (_step)
        {
            case 1:
                ClickButton();
                break;
            case 2:
                Report(_clicks == 1, $"后台注入点击按钮（目标窗口非前台）：按钮 Click 触发 {_clicks} 次（期望 1）");
                TypeIntoTextBox();
                break;
            case 3:
                var got = _text?.Text ?? "";
                Report(got.Contains('A'), $"后台注入输入字符：文本框内容 = \"{got}\"（期望含字母 A）");
                Finish();
                break;
        }
    }

    private static void ClickButton()
    {
        var b = _button!;
        var pt = b.PointToScreen(new Point(b.Width / 2, b.Height / 2));
        Console.WriteLine($"目标按钮中心（屏幕坐标）= ({pt.X},{pt.Y})");

        var hit = BackgroundInput.HitTest(pt.X, pt.Y);
        NativeApi.GetWindowThreadProcessId(hit, out int pid);
        Console.WriteLine($"HitTest → hwnd=0x{hit.ToInt64():X} class=\"{NativeApi.GetClassName(hit)}\" pid={pid}");

        if (pid != Environment.ProcessId)
        {
            // 万一被别的窗口盖住了：宁可判失败也绝不往别人的窗口投事件
            Report(false, $"HitTest 命中的不是本进程窗口（pid={pid}），出于安全中止本次投递");
            Finish();
            return;
        }

        Report(hit == b.Handle, $"HitTest 命中按钮自身（期望 hwnd=0x{b.Handle.ToInt64():X}，实得 0x{hit.ToInt64():X}）");

        bool m = BackgroundInput.PostMove(hit, pt.X, pt.Y, 0);
        bool d = BackgroundInput.PostButton(hit, pt.X, pt.Y, 0, false, 0);
        bool u = BackgroundInput.PostButton(hit, pt.X, pt.Y, 0, true, NativeApi.MK_LBUTTON);
        Report(m && d && u, $"投递 WM_MOUSEMOVE/LBUTTONDOWN/LBUTTONUP（move={m} down={d} up={u}）");
    }

    private static void TypeIntoTextBox()
    {
        var t = _text!;
        var pt = t.PointToScreen(new Point(40, t.Height / 2));
        var hit = BackgroundInput.HitTest(pt.X, pt.Y);
        NativeApi.GetWindowThreadProcessId(hit, out int pid);
        if (pid != Environment.ProcessId)
        {
            Report(false, "文本框 HitTest 命中了别的进程窗口，出于安全中止");
            return;
        }
        Report(hit == t.Handle, $"HitTest 命中文本框自身（期望 hwnd=0x{t.Handle.ToInt64():X}，实得 0x{hit.ToInt64():X}）");

        bool d = BackgroundInput.PostButton(hit, pt.X, pt.Y, 0, false, 0);
        bool u = BackgroundInput.PostButton(hit, pt.X, pt.Y, 0, true, NativeApi.MK_LBUTTON);
        bool c = BackgroundInput.PostChar(hit, 'A');
        Report(d && u && c, $"投递点击+WM_CHAR（down={d} up={u} char={c}）");
    }

    private static void Report(bool ok, string what) => Results.Add($"{(ok ? "[PASS]" : "[FAIL]")} {what}");

    private static void Finish()
    {
        if (_finished) return;
        _finished = true;
        try { _timer?.Stop(); _watchdog?.Stop(); } catch { }
        try { _target?.Close(); } catch { }
        try { _other?.Close(); } catch { }
        Application.ExitThread();
    }
}
