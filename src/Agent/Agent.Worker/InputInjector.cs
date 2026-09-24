using System.Runtime.InteropServices;
using Agent.Common;

namespace Agent.Worker;

/// <summary>
/// 远程键鼠注入（在 Session 1 内执行 SendInput，只影响本会话的光标/输入队列）
/// </summary>
public sealed class InputInjector
{
    private readonly Logger _log;
    private int _vsLeft, _vsTop, _vsWidth, _vsHeight;
    private int _captureLeft, _captureTop, _captureWidth, _captureHeight;

    public InputInjector(Logger log)
    {
        _log = log;
        RefreshVirtualDesktop();
    }

    /// <summary>被采集屏在虚拟桌面里的左上角（= 远端画面 (0,0) 对应的桌面坐标）</summary>
    public (int Left, int Top) CaptureOrigin => (_captureLeft, _captureTop);
    /// <summary>被采集屏的尺寸</summary>
    public (int Width, int Height) CaptureSize => (_captureWidth, _captureHeight);
    /// <summary>被采集屏是不是虚拟外屏</summary>
    public bool CaptureIsVirtual { get; private set; }

    public void RefreshVirtualDesktop()
    {
        var (l, t, w, h) = DisplayHelper.GetVirtualDesktopBounds();
        _vsLeft = l; _vsTop = t; _vsWidth = w; _vsHeight = h;
        _log.Info($"输入映射虚拟桌面：({l},{t}) {w}x{h}");
    }

    /// <summary>设置被采集显示器在虚拟桌面中的位置，用于把画面坐标换算成桌面坐标</summary>
    public void SetCaptureTarget(DisplayInfo target)
    {
        _captureLeft = target.Left;
        _captureTop = target.Top;
        _captureWidth = target.Width;
        _captureHeight = target.Height;
        CaptureIsVirtual = target.IsVirtual;
        RefreshVirtualDesktop();
        _log.Info($"采集目标：{target}，输入换算基准=({_captureLeft},{_captureTop})");
    }

    /// <summary>把"画面像素坐标"换算成虚拟桌面坐标（不归一化）</summary>
    private (int X, int Y) ToDesktop(int x, int y)
    {
        if (_vsWidth <= 1 || _vsHeight <= 1) RefreshVirtualDesktop();
        return (Math.Clamp(_captureLeft + x, _vsLeft, _vsLeft + Math.Max(0, _vsWidth - 1)),
                Math.Clamp(_captureTop + y, _vsTop, _vsTop + Math.Max(0, _vsHeight - 1)));
    }

    /// <summary>把"画面像素坐标"标准化为 0..65535 的虚拟桌面绝对坐标</summary>
    private (int nx, int ny) Normalize(int x, int y)
    {
        var (px, py) = ToDesktop(x, y);
        int nx = (int)Math.Round((px - _vsLeft) * 65535.0 / Math.Max(1, _vsWidth - 1));
        int ny = (int)Math.Round((py - _vsTop) * 65535.0 / Math.Max(1, _vsHeight - 1));
        return (nx, ny);
    }

    /// <summary>
    /// 降级形态：移动事件被别的程序截走，但 SetCursorPos 可用。
    /// 打开后所有操作都变成「先把光标摆到位置（SetCursorPos，不走输入事件链）+ 只发按键/滚轮事件（不带坐标）」。
    /// 见 <see cref="MouseRouter.ProbeCursor"/> 里判定它的地方。
    /// </summary>
    public bool AssistedCursor { get; set; }

    public void MouseMove(int x, int y)
    {
        var (nx, ny) = Normalize(x, y);
        if (AssistedCursor)
        {
            var (tx, ty) = ToDesktop(x, y);
            NativeApi.SetCursorPos(tx, ty);
            return;
        }
        SendMouse(nx, ny, 0,
            NativeApi.MOUSEEVENTF_MOVE | NativeApi.MOUSEEVENTF_ABSOLUTE | NativeApi.MOUSEEVENTF_VIRTUALDESK);
    }

    public void MouseButton(int x, int y, byte button, bool up)
    {
        if (AssistedCursor)
        {
            var (tx, ty) = ToDesktop(x, y);
            NativeApi.SetCursorPos(tx, ty);
            ButtonAtCursor(button, up);
            return;
        }
        var (nx, ny) = Normalize(x, y);
        // 先移动，避免按下时坐标不一致
        SendMouse(nx, ny, 0,
            NativeApi.MOUSEEVENTF_MOVE | NativeApi.MOUSEEVENTF_ABSOLUTE | NativeApi.MOUSEEVENTF_VIRTUALDESK);

        uint flag = button switch
        {
            0 => up ? NativeApi.MOUSEEVENTF_LEFTUP : NativeApi.MOUSEEVENTF_LEFTDOWN,
            1 => up ? NativeApi.MOUSEEVENTF_RIGHTUP : NativeApi.MOUSEEVENTF_RIGHTDOWN,
            2 => up ? NativeApi.MOUSEEVENTF_MIDDLEUP : NativeApi.MOUSEEVENTF_MIDDLEDOWN,
            _ => 0,
        };
        if (flag == 0) return;
        SendMouse(nx, ny, 0, flag | NativeApi.MOUSEEVENTF_ABSOLUTE | NativeApi.MOUSEEVENTF_VIRTUALDESK);
    }

    /// <summary>
    /// 点击：把"移动到目标点"和"按下/抬起"放进**同一次 SendInput** 里发出去。
    ///
    /// 【为什么必须合成一次】Windows 规定 dx/dy 只对 MOUSEEVENTF_MOVE 有效 —— 按键事件**不带坐标**，
    /// 它落在"按下那一刻光标所在的位置"。所以"先移动、再按键"拆成两次调用时，中间存在一个空隙：
    /// 本机用户的物理鼠标、另一份被控端/别的远控软件的全局钩子只要在这个空隙里动一下光标，
    /// 这一击就会落到光标当时所在的位置上（现场表现就是"远端在副屏点、结果点到主屏"）。
    /// 合成一次调用后，移动与按键之间没有可被插队的空隙。
    /// </summary>
    public void Click(int x, int y, byte button, bool up)
    {
        // 降级形态（移动事件被抢）：位置只能靠 SetCursorPos 摆，按键单独发
        if (AssistedCursor) { MouseButton(x, y, button, up); return; }

        var (nx, ny) = Normalize(x, y);
        uint flag = button switch
        {
            0 => up ? NativeApi.MOUSEEVENTF_LEFTUP : NativeApi.MOUSEEVENTF_LEFTDOWN,
            1 => up ? NativeApi.MOUSEEVENTF_RIGHTUP : NativeApi.MOUSEEVENTF_RIGHTDOWN,
            2 => up ? NativeApi.MOUSEEVENTF_MIDDLEUP : NativeApi.MOUSEEVENTF_MIDDLEDOWN,
            _ => 0,
        };
        if (flag == 0) { MouseMove(x, y); return; }

        const uint abs = NativeApi.MOUSEEVENTF_ABSOLUTE | NativeApi.MOUSEEVENTF_VIRTUALDESK;
        var inputs = new NativeApi.INPUT[2];
        inputs[0].type = NativeApi.INPUT_MOUSE;
        inputs[0].u.mi = new NativeApi.MOUSEINPUT
        {
            dx = nx, dy = ny, mouseData = 0,
            dwFlags = NativeApi.MOUSEEVENTF_MOVE | abs,
            time = 0, dwExtraInfo = NativeApi.InjectTagPtr,
        };
        inputs[1].type = NativeApi.INPUT_MOUSE;
        inputs[1].u.mi = new NativeApi.MOUSEINPUT
        {
            dx = nx, dy = ny, mouseData = 0,
            dwFlags = flag | abs,
            time = 0, dwExtraInfo = NativeApi.InjectTagPtr,
        };
        uint sent = NativeApi.SendInput(2, inputs, Marshal.SizeOf<NativeApi.INPUT>());
        if (sent == 0)
        {
            // 整批都没进去（UIPI/安全桌面）：退回单发，至少别把这次点击彻底丢掉
            MouseButton(x, y, button, up);
        }
    }

    /// <summary>
    /// 只用 SetCursorPos 把光标摆过去（不发任何输入事件），并确认真的摆到了。
    ///
    /// 【为什么要单开一条】SendInput 的移动是"输入事件"，会被别的远控软件/鼠标工具的全局钩子
    /// 截走或"归位"；SetCursorPos 是直接设位置，不走那条事件链 —— 有些机器上它反而是通的。
    /// 通了就用"SetCursorPos + 只发按键"的方式点击（<see cref="ButtonAtCursor"/>），
    /// 这样即使移动事件被抢，点击仍然落在正确的位置上。
    /// </summary>
    public bool TrySetCursorExact(int x, int y, out int actualX, out int actualY, int tolerance = 8)
    {
        int wantX = Math.Clamp(_captureLeft + x, _vsLeft, _vsLeft + Math.Max(0, _vsWidth - 1));
        int wantY = Math.Clamp(_captureTop + y, _vsTop, _vsTop + Math.Max(0, _vsHeight - 1));
        NativeApi.SetCursorPos(wantX, wantY);
        for (int i = 0; i < 4; i++)
        {
            if (NativeApi.GetCursorPos(out var p))
            {
                actualX = p.X; actualY = p.Y;
                if (Math.Abs(actualX - wantX) <= tolerance && Math.Abs(actualY - wantY) <= tolerance) return true;
            }
            Thread.Sleep(4);
        }
        NativeApi.GetCursorPos(out var last);
        actualX = last.X; actualY = last.Y;
        return Math.Abs(actualX - wantX) <= tolerance && Math.Abs(actualY - wantY) <= tolerance;
    }

    /// <summary>
    /// 在当前光标位置按下/抬起（事件里不带坐标、也不带 MOUSEEVENTF_MOVE）——
    /// 配合 <see cref="TrySetCursorExact"/> 使用：位置由 SetCursorPos 摆好，这里只负责按键。
    /// </summary>
    public void ButtonAtCursor(byte button, bool up)
    {
        uint flag = button switch
        {
            0 => up ? NativeApi.MOUSEEVENTF_LEFTUP : NativeApi.MOUSEEVENTF_LEFTDOWN,
            1 => up ? NativeApi.MOUSEEVENTF_RIGHTUP : NativeApi.MOUSEEVENTF_RIGHTDOWN,
            2 => up ? NativeApi.MOUSEEVENTF_MIDDLEUP : NativeApi.MOUSEEVENTF_MIDDLEDOWN,
            _ => 0,
        };
        if (flag == 0) return;
        SendMouse(0, 0, 0, flag);
    }

    /// <summary>
    /// 注入一次移动，然后看光标**真的**到没到目标点。
    ///
    /// 【为什么要问这一句】注入能否生效依赖"系统光标听我们的"，可它并不总是听：
    /// 同一台机器上还有别的远控软件（或另一份旧被控端的全局钩子）在管光标时，
    /// SendInput 会照常返回成功，光标却一动不动或者立刻被"归位"回主屏 —— 于是后面所有点击
    /// 都落在主屏上，而日志里看上去一切正常（现场最难查的就是这一条）。
    /// 探一次就能把这种状态识别出来，交给 <see cref="MouseRouter"/> 改走不依赖光标的那条路。
    /// </summary>
    public bool TryProbeCursor(int x, int y, out int actualX, out int actualY, int tolerance = 24)
    {
        var (nx, ny) = Normalize(x, y);
        SendMouse(nx, ny, 0,
            NativeApi.MOUSEEVENTF_MOVE | NativeApi.MOUSEEVENTF_ABSOLUTE | NativeApi.MOUSEEVENTF_VIRTUALDESK);

        int wantX = _captureLeft + x, wantY = _captureTop + y;
        // 输入队列是异步处理的：轮询几毫秒，别把"还没处理到"误判成"没生效"
        for (int i = 0; i < 6; i++)
        {
            if (NativeApi.GetCursorPos(out var p))
            {
                actualX = p.X; actualY = p.Y;
                if (Math.Abs(actualX - wantX) <= tolerance && Math.Abs(actualY - wantY) <= tolerance) return true;
            }
            Thread.Sleep(4);
        }
        NativeApi.GetCursorPos(out var last);
        actualX = last.X; actualY = last.Y;
        return Math.Abs(actualX - wantX) <= tolerance && Math.Abs(actualY - wantY) <= tolerance;
    }

    public void MouseWheel(int x, int y, short delta)
    {
        if (AssistedCursor)
        {
            // 位置由 SetCursorPos 摆好；滚轮事件本身不带坐标（WM_MOUSEWHEEL 用的是"光标当时的位置"）
            var (tx, ty) = ToDesktop(x, y);
            NativeApi.SetCursorPos(tx, ty);
            SendMouse(0, 0, unchecked((uint)delta), NativeApi.MOUSEEVENTF_WHEEL);
            return;
        }
        var (nx, ny) = Normalize(x, y);
        SendMouse(nx, ny, unchecked((uint)delta),
            NativeApi.MOUSEEVENTF_WHEEL | NativeApi.MOUSEEVENTF_ABSOLUTE | NativeApi.MOUSEEVENTF_VIRTUALDESK);
    }

    private static void SendMouse(int nx, int ny, uint data, uint flags)
    {
        var inputs = new NativeApi.INPUT[1];
        inputs[0].type = NativeApi.INPUT_MOUSE;
        inputs[0].u.mi = new NativeApi.MOUSEINPUT
        {
            dx = nx,
            dy = ny,
            mouseData = data,
            dwFlags = flags,
            time = 0,
            // 打上"这是远端注入"的记号：光标仲裁只归还本机用户的光标，
            // 没有这个记号就分不清"远端注入"和"本机触摸板/厂商驱动用 SendInput 送的输入"
            dwExtraInfo = NativeApi.InjectTagPtr,
        };
        uint sent = NativeApi.SendInput(1, inputs, Marshal.SizeOf<NativeApi.INPUT>());
        if (sent == 0)
        {
            // 失败通常是 UIPI/安全桌面，不刷屏
        }
    }

    private long _lastKeyLogTicks;

    public void Key(ushort vk, bool up, byte flags)
    {
        // 诊断：按键注入时记录当前前台窗口（限流：每 10 秒最多 3 条，避免刷日志）
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastKeyLogTicks);
        if (now - last > 3000 && Interlocked.CompareExchange(ref _lastKeyLogTicks, now, last) == last)
        {
            var fg = NativeApi.GetForegroundWindow();
            if (fg != IntPtr.Zero && NativeApi.GetWindowRect(fg, out var r))
                _log.Info($"注入键盘 vk={vk} 抬落={up}，前台窗口={fg} 区域=({r.Left},{r.Top},{r.Right - r.Left}x{r.Bottom - r.Top})");
            else
                _log.Warn($"注入键盘 vk={vk} 抬落={up}，但当前没有前台窗口（fg={fg}）");
        }
        uint f = 0;
        if (up) f |= NativeApi.KEYEVENTF_KEYUP;
        if ((flags & 1) != 0) f |= NativeApi.KEYEVENTF_EXTENDEDKEY;
        var inputs = new NativeApi.INPUT[1];
        inputs[0].type = NativeApi.INPUT_KEYBOARD;
        inputs[0].u.ki = new NativeApi.KEYBDINPUT
        {
            wVk = vk,
            wScan = 0,
            dwFlags = f,
            time = 0,
            dwExtraInfo = NativeApi.InjectTagPtr,
        };
        NativeApi.SendInput(1, inputs, Marshal.SizeOf<NativeApi.INPUT>());
    }

    /// <summary>直接设置光标位置（GDI 兜底路径，调试用）</summary>
    public void SetCursorAbsolute(int x, int y) => NativeApi.SetCursorPos(x, y);
}
