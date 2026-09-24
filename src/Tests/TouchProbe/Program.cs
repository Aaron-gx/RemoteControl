using System.Runtime.InteropServices;
using System.Text;

namespace TouchProbe;

/// <summary>
/// 触摸指针注入探针 —— 回答一个问题：**能不能在不碰系统光标的前提下，在指定坐标产生一次"真正的点击"？**
///
/// 【为什么值得单独验一次】
/// 我们现在最大的结构性问题：一个 Windows 会话只有一只系统光标，而"远端点击"是靠"把这只光标挪过去
/// 再按键"实现的 —— 于是任何第三方的鼠标控制（本机物理鼠标、UU远程/ToDesk 这类远控、厂商鼠标驱动）
/// 都会和它抢，现场表现就是"能滑动点不了、点击跑到主屏"（详见 CursorArbiter / MouseRouter 的注释）。
///
/// Windows 10 1809+ 提供了一条**不需要驱动、也不需要碰光标**的路：
///   CreateSyntheticPointerDevice(PT_TOUCH) + InjectSyntheticPointerInput()
/// 注入的是"触摸指针"事件：
///   · 它有自己的指针身份，**不会移动系统光标**（本机用户的鼠标完全不受影响）；
///   · 支持多点（可以想象成"远端的另一只手指/另一只鼠标"）；
///   · 对支持 WM_POINTER 的程序（Chromium/Electron/WinUI/Flutter）是原生指针事件；
///   · 对老的 Win32 程序，Windows 会**自动把触摸提升为鼠标消息**（本探针就是要验证这条）。
///
/// 【安全性】只对自己窗口上的按钮/编辑框注入，绝不点桌面上的其它程序；不移动光标。
/// </summary>
internal static class Program
{
    // ---------------------------------------------------------------- P/Invoke

    private const uint PT_TOUCH = 0x00000002;
    private const uint TOUCH_FEEDBACK_DEFAULT = 0x1;
    private const uint POINTER_FLAG_INRANGE = 0x00000002;
    private const uint POINTER_FLAG_INCONTACT = 0x00000004;
    private const uint POINTER_FLAG_DOWN = 0x00010000;
    private const uint POINTER_FLAG_UP = 0x00040000;
    private const uint POINTER_FLAG_UPDATE = 0x00020000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int L, T, R, B; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_INFO
    {
        public uint pointerType;
        public uint pointerId;
        public uint frameId;
        public uint pointerFlags;
        public IntPtr sourceDevice;
        public IntPtr hwndTarget;
        public POINT ptPixelLocation;
        public POINT ptHimetricLocation;
        public POINT ptPixelLocationRaw;
        public POINT ptHimetricLocationRaw;
        public uint dwTime;
        public uint historyCount;
        public int InputData;
        public uint dwKeyStates;
        public ulong PerformanceCount;
        public int ButtonChangeType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTER_TOUCH_INFO
    {
        public POINTER_INFO pointerInfo;
        public uint touchFlags;
        public uint touchMask;
        public RECT rcContact;
        public RECT rcContactRaw;
        public uint orientation;
        public uint pressure;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType, uint maxCount, uint mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool InjectSyntheticPointerInput(IntPtr device, POINTER_TOUCH_INFO[] pointerInfo, uint count);

    [DllImport("user32.dll")]
    private static extern bool DestroySyntheticPointerDevice(IntPtr device);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    // ---------------------------------------------------------------- 状态

    private static Form? _win;
    private static Button? _btn;
    private static TextBox? _text;
    private static int _btnClicks, _btnDown, _btnUp;
    private static readonly List<string> Lines = new();

    [STAThread]
    private static int Main()
    {
        ApplicationConfiguration.Initialize();
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 触摸指针注入探针（只点自己窗口上的控件，不移动光标）===");
        Console.WriteLine($"pid={Environment.ProcessId}  Win10 1809+ 才有的 InjectSyntheticPointerInput");

        // 先看 API 在不在（老系统/被裁剪的系统上可能没有这个导出）
        var device = CreateSyntheticPointerDevice(PT_TOUCH, 1, TOUCH_FEEDBACK_DEFAULT);
        int err = Marshal.GetLastWin32Error();
        if (device == IntPtr.Zero)
        {
            Console.WriteLine($"  [FAIL] CreateSyntheticPointerDevice 失败（Win32={err}）：本机不支持触摸注入，" +
                              "这条路走不通，只能走驱动级虚拟鼠标。");
            return 1;
        }
        Console.WriteLine($"  [PASS] 触摸注入设备已创建（handle={device}）");

        // 自己的窗口：经典 Win32 窗口（WinForms 不是 pointer-aware 的），
        // 于是"触摸→鼠标"的自动提升正好被验证到 —— 这代表绝大多数老程序的行为。
        _win = new Form
        {
            Text = "TouchProbe 目标窗口（探针自建）",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(260, 260),
            ClientSize = new Size(520, 260),
            ShowInTaskbar = false,
        };
        _btn = new Button { Text = "目标按钮", Left = 40, Top = 40, Width = 200, Height = 56 };
        _btn.Click += (_, _) => Interlocked.Increment(ref _btnClicks);
        _btn.MouseDown += (_, _) => Interlocked.Increment(ref _btnDown);
        _btn.MouseUp += (_, _) => Interlocked.Increment(ref _btnUp);
        _text = new TextBox { Left = 40, Top = 130, Width = 420 };
        _win.Controls.Add(_btn);
        _win.Controls.Add(_text);
        _win.Show();
        Application.DoEvents();
        Thread.Sleep(300);
        Application.DoEvents();

        var (bx, by) = ButtonCenter();
        Console.WriteLine($"\n目标按钮中心（屏幕坐标）= ({bx},{by})  窗口位置=({_win.Left},{_win.Top})");
        GetCursorPos(out var before);
        Console.WriteLine($"注入前：光标=({before.X},{before.Y})，前台窗口 pid={FgPid()}");

        // ---------------------------------------------------------- 注入一次"点按"
        Console.WriteLine("\n--- 注入一次触摸点按（down → up）---");
        bool down = Tap(device, bx, by, down: true);
        Thread.Sleep(60);
        bool up = Tap(device, bx, by, down: false);
        for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(15); }

        GetCursorPos(out var after);
        int clicks = _btnClicks, downs = _btnDown, ups = _btnUp;
        Result(clicks > 0, $"[A] 触摸点按能触发「经典 Win32 按钮」的点击：Click={clicks} MouseDown={downs} MouseUp={ups}（期望各 ≥1）");
        Result(Math.Abs(after.X - before.X) <= 1 && Math.Abs(after.Y - before.Y) <= 1,
            $"[B] 注入全程没有移动系统光标：({before.X},{before.Y}) → ({after.X},{after.Y})");
        Result(down && up, $"[C] 注入调用本身返回成功（down={down} up={up}，Win32={Marshal.GetLastWin32Error()}）");

        // ---------------------------------------------------------- 键盘焦点（点击通常会带走焦点）
        Console.WriteLine($"\n注入后：前台窗口 pid={FgPid()}（自己 pid={Environment.ProcessId}）");

        DestroySyntheticPointerDevice(device);
        _win.Close();
        Console.WriteLine("\n=== 结论 ===");
        foreach (var l in Lines) Console.WriteLine(l);
        Console.WriteLine(clicks > 0 && Math.Abs(after.X - before.X) <= 1
            ? "\n➜ 触摸注入可用：远端点击可以完全不碰系统光标（无需驱动、不与任何人抢鼠标）。"
            : "\n➜ 触摸注入在本机不成立（或只对 pointer-aware 程序有效）：远端点击仍需驱动级虚拟鼠标。");
        Environment.Exit(0);
        return 0;
    }

    // ---------------------------------------------------------------- 注入

    /// <summary>在屏幕坐标 (x,y) 注入一次触摸按下/抬起</summary>
    private static bool Tap(IntPtr device, int x, int y, bool down)
    {
        var info = new POINTER_TOUCH_INFO[1];
        info[0].pointerInfo.pointerType = PT_TOUCH;
        info[0].pointerInfo.pointerId = 1;
        info[0].pointerInfo.ptPixelLocation = new POINT { X = x, Y = y };
        info[0].pointerInfo.ptPixelLocationRaw = new POINT { X = x, Y = y };
        info[0].pointerInfo.pointerFlags = down
            ? POINTER_FLAG_DOWN | POINTER_FLAG_INRANGE | POINTER_FLAG_INCONTACT
            : POINTER_FLAG_UP;
        info[0].touchFlags = 0;
        info[0].touchMask = 0;
        info[0].rcContact = new RECT { L = x - 2, T = y - 2, R = x + 2, B = y + 2 };
        info[0].rcContactRaw = info[0].rcContact;
        info[0].orientation = 90;
        info[0].pressure = down ? 32000u : 0u;
        return InjectSyntheticPointerInput(device, info, 1);
    }

    // ---------------------------------------------------------------- 小工具

    private static (int X, int Y) ButtonCenter()
        => (_win!.Left + _btn!.Left + _btn.Width / 2, _win.Top + _btn.Top + _btn.Height / 2);

    private static uint FgPid()
    {
        var h = GetForegroundWindow();
        GetWindowThreadProcessId(h, out uint pid);
        return pid;
    }

    private static void Result(bool ok, string text)
    {
        Console.WriteLine((ok ? "  [PASS] " : "  [FAIL] ") + text);
        Lines.Add((ok ? "[PASS] " : "[FAIL] ") + text);
    }
}
