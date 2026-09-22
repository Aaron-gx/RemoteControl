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
        RefreshVirtualDesktop();
        _log.Info($"采集目标：{target}，输入换算基准=({_captureLeft},{_captureTop})");
    }

    /// <summary>把"画面像素坐标"标准化为 0..65535 的虚拟桌面绝对坐标</summary>
    private (int nx, int ny) Normalize(int x, int y)
    {
        if (_vsWidth <= 1 || _vsHeight <= 1) RefreshVirtualDesktop();
        int px = Math.Clamp(_captureLeft + x, _vsLeft, _vsLeft + Math.Max(0, _vsWidth - 1));
        int py = Math.Clamp(_captureTop + y, _vsTop, _vsTop + Math.Max(0, _vsHeight - 1));
        int nx = (int)Math.Round((px - _vsLeft) * 65535.0 / Math.Max(1, _vsWidth - 1));
        int ny = (int)Math.Round((py - _vsTop) * 65535.0 / Math.Max(1, _vsHeight - 1));
        return (nx, ny);
    }

    public void MouseMove(int x, int y)
    {
        var (nx, ny) = Normalize(x, y);
        SendMouse(nx, ny, 0,
            NativeApi.MOUSEEVENTF_MOVE | NativeApi.MOUSEEVENTF_ABSOLUTE | NativeApi.MOUSEEVENTF_VIRTUALDESK);
    }

    public void MouseButton(int x, int y, byte button, bool up)
    {
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

    public void MouseWheel(int x, int y, short delta)
    {
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
