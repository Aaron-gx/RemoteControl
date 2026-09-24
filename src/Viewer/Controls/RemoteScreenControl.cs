using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Agent.Common;
using Viewer.Services;

namespace Viewer.Controls;

/// <summary>
/// 远程画面显示 + 键鼠捕获控件（策划 §3.2）：
/// - 画面按等比缩放居中显示
/// - 鼠标进入即开始捕获（移动/按键/滚轮），离开停止
/// - 键盘在控件获得焦点后转发；Alt 组合键交给本机系统
/// </summary>
public sealed class RemoteScreenControl : Image
{
    private WriteableBitmap? _bitmap;
    private int _remoteWidth, _remoteHeight;
    private bool _capturingMouse;
    private readonly Logger _log;

    // ---------------------------------------------------------------- 远端光标（自绘）
    // 位置记录的是"我们最后发给被控端的那个远程画面坐标"。远端光标只在主控端窗口里被画出来，
    // 被控机上不会有任何可见光标 —— 本机用户看不见它，而远端永远看得见自己的指针。
    private double _cursorX = -1, _cursorY = -1;
    /// <summary>物理屏区域（画面坐标）：只显示、不接收远端操作 —— 见 SetScreenLayout</summary>
    private List<ScreenRect> _readOnlyRegions = new();
    /// <summary>画面里所有屏（用来画分隔线与标注）</summary>
    private List<ScreenRect> _screens = new();
    private bool _allScreens;
    /// <summary>叠在画面上的提示文字（"为什么没有画面"），null = 不显示</summary>
    private string? _notice;
    /// <summary>画面布局相关的提示（例如"请求了全部屏幕但被控端没切"），与上面那条互不覆盖</summary>
    private string? _layoutNotice;

    private static readonly Geometry CursorShape = Geometry.Parse(
        "M 0,0 L 0,16 L 4.6,12.2 L 7.7,19.2 L 10.5,17.8 L 7.4,11 L 12.2,11 Z");
    private static readonly Brush CursorFill = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));
    private static readonly Pen CursorOutline = new(new SolidColorBrush(Colors.White), 1.4);

    /// <summary>在物理屏（只读区域）上点了某个位置：请被控端把那个窗口搬到虚拟外屏</summary>
    public event Action<int, int>? RemotePullWindow;
    public event Action<int, int>? RemoteMouseMove;
    public event Action<int, int, byte, bool>? RemoteMouseButton;
    public event Action<int, int, short>? RemoteMouseWheel;
    public event Action<ushort, bool, byte>? RemoteKey;

    public RemoteScreenControl()
    {
        Stretch = Stretch.Uniform;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        _log = Logger.Current ?? new Logger("viewer");
        MouseEnter += (_, _) => { _capturingMouse = true; Focus(); };
        MouseLeave += (_, _) => { _capturingMouse = false; };
        Loaded += (_, _) => Focus();
    }

    public int RemoteWidth => _remoteWidth;
    public int RemoteHeight => _remoteHeight;

    /// <summary>更新一帧（BGRA）</summary>
    public void UpdateFrame(byte[] bgra, int width, int height)
    {
        if (width != _remoteWidth || height != _remoteHeight || _bitmap == null)
        {
            _remoteWidth = width;
            _remoteHeight = height;
            _bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            Source = _bitmap;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
    }

    /// <summary>显示占位提示（无画面时）</summary>
    public void ShowPlaceholder(string text)
    {
        _bitmap = null;
        Source = null;
        PlaceholderText = text;
        InvalidateVisual();
    }

    public string PlaceholderText { get; private set; } = "未连接";

    /// <summary>
    /// 设置画面里的屏幕布局（来自被控端 MonitorInfo.Screens）。
    /// "全部屏幕"模式下，物理屏那几块是**只读**的：远端看得到（知道本机用户在干什么、窗口在哪），
    /// 但点/滚都不送过去 —— 否则要么抢了本机用户的光标、要么把点击送到他正看着的屏上。
    /// </summary>
    public void SetScreenLayout(List<ScreenRect>? screens, bool allScreens)
    {
        // "只显示主屏"这一档：那块屏也是只读的（点击＝把窗口搬到副屏，见 RemotePullWindow）
        _allScreens = true;
        _screens = screens ?? new List<ScreenRect>();
        _readOnlyRegions = _screens.Where(s => !s.IsVirtual).ToList();
        InvalidateVisual();
    }

    /// <summary>物理屏那块是否只读（false = 可直接操作，见 ViewerConfig.PhysicalScreenReadOnly）</summary>
    public bool ReadOnlyPhysicalScreens { get; set; }

    /// <summary>这个画面坐标是不是落在只读区域（物理屏）里</summary>
    private bool InReadOnlyRegion(int rx, int ry)
        => ReadOnlyPhysicalScreens &&
           _readOnlyRegions.Any(r => rx >= r.Left && rx < r.Left + r.Width && ry >= r.Top && ry < r.Top + r.Height);

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        // 这里刻意**不画**任何屏框/标注/遮罩：两块屏可能是同一张壁纸，曾经画过黄框（物理屏）
        // 与蓝框（虚拟外屏）来区分，但用户明确要求"不要花黄线"，所以整套绘制已删除。
        // 想知道当前显示哪几块屏，看状态栏的「分辨率: xxx（全部屏幕）」。
        if (Source == null && !string.IsNullOrEmpty(PlaceholderText))
        {
            var ft = new FormattedText(PlaceholderText, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), 16,
                new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
        }
        DrawRemoteCursor(dc);
        DrawLayoutNotice(dc);
        DrawNotice(dc);
    }

    /// <summary>
    /// 在画面上叠一条提示（例如"为什么一直没有画面"）。null = 不显示。
    /// 为什么不复用 ShowPlaceholder：那条路只在"从来没有过画面"时才会画出来，
    /// 一旦有了一帧（哪怕是黑帧）提示就消失了 —— 而"有黑帧但一直不动"正是最需要说明的场景。
    /// </summary>
    public void SetNotice(string? text)
    {
        if (_notice == text) return;
        _notice = text;
        InvalidateVisual();
    }

    /// <summary>布局类提示（"被控端还是旧版本"这类）：跟"没画面的原因"分开存，谁也不盖谁</summary>
    public void SetLayoutNotice(string? text)
    {
        if (_layoutNotice == text) return;
        _layoutNotice = text;
        InvalidateVisual();
    }

    private void DrawNotice(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(_notice)) return;
        DrawNoticeCore(dc, _notice!);
    }

    /// <summary>布局提示单独画一条（优先显示在"没画面"那条上面）</summary>
    private void DrawLayoutNotice(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(_layoutNotice)) return;
        DrawNoticeCore(dc, _layoutNotice!);
    }

    private void DrawNoticeCore(DrawingContext dc, string text)
    {
        if (ActualWidth < 120 || ActualHeight < 60) return;
        var ft = new FormattedText(text,
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), 14, System.Windows.Media.Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(120, ActualWidth - 80),
            MaxLineCount = 3,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        double w = Math.Min(ft.Width + 28, ActualWidth - 24);
        double h = ft.Height + 18;
        double x = (ActualWidth - w) / 2;
        double y = ActualHeight - h - 20;
        var bg = new SolidColorBrush(Color.FromArgb(0xD8, 0x1C, 0x1C, 0x1C));
        var border = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xB0, 0x40)), 1);
        dc.DrawRoundedRectangle(bg, border, new Rect(x, y, w, h), 6, 6);
        dc.DrawText(ft, new Point(x + 14, y + 9));
    }

    /// <summary>
    /// 自绘远端光标。用矢量箭头而不是让被控端把系统光标烤进画面，有两个好处：
    /// (1) 本机用户的屏幕上永远不会出现远端光标（它只存在于这个窗口里）；
    /// (2) 无论被控端的系统光标此刻在哪块屏上 —— 尤其"后台定向注入"模式下它压根不在外屏上 ——
    ///     远端都能看见自己的指针落在哪。
    /// </summary>
    private void DrawRemoteCursor(DrawingContext dc)
    {
        if (Source == null || _cursorX < 0 || _cursorY < 0) return;
        if (_remoteWidth <= 0 || _remoteHeight <= 0) return;
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        double scale = Math.Min(ActualWidth / _remoteWidth, ActualHeight / _remoteHeight);
        double dw = _remoteWidth * scale, dh = _remoteHeight * scale;
        double ox = (ActualWidth - dw) / 2, oy = (ActualHeight - dh) / 2;
        double px = ox + _cursorX * scale, py = oy + _cursorY * scale;
        if (px < -24 || py < -24 || px > ActualWidth + 24 || py > ActualHeight + 24) return;
        dc.PushTransform(new TranslateTransform(px, py));
        dc.DrawGeometry(CursorFill, CursorOutline, CursorShape);
        dc.Pop();
    }

    /// <summary>记录远端光标位置（= 刚发给被控端的画面坐标）并重绘</summary>
    private void SetCursor(int x, int y)
    {
        if (_cursorX == x && _cursorY == y) return;
        _cursorX = x;
        _cursorY = y;
        InvalidateVisual();
    }

    // ---------------------------------------------------------------- 坐标映射

    /// <summary>控件坐标 → 远程画面像素坐标</summary>
    public bool TryMapPoint(Point p, out int rx, out int ry)
    {
        rx = ry = 0;
        if (_remoteWidth <= 0 || _remoteHeight <= 0) return false;
        if (ActualWidth <= 0 || ActualHeight <= 0) return false;
        double scale = Math.Min(ActualWidth / _remoteWidth, ActualHeight / _remoteHeight);
        double dw = _remoteWidth * scale, dh = _remoteHeight * scale;
        double ox = (ActualWidth - dw) / 2, oy = (ActualHeight - dh) / 2;
        if (p.X < ox || p.Y < oy || p.X > ox + dw || p.Y > oy + dh) return false;
        rx = (int)Math.Round((p.X - ox) / scale);
        ry = (int)Math.Round((p.Y - oy) / scale);
        rx = Math.Clamp(rx, 0, _remoteWidth - 1);
        ry = Math.Clamp(ry, 0, _remoteHeight - 1);
        return true;
    }

    // ---------------------------------------------------------------- 鼠标

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_capturingMouse && e.LeftButton == MouseButtonState.Released &&
            e.RightButton == MouseButtonState.Released) return;
        var p = e.GetPosition(this);
        if (!TryMapPoint(p, out int x, out int y)) return;
        SetCursor(x, y);
        RemoteMouseMove?.Invoke(x, y);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        CaptureMouse();
        var p = e.GetPosition(this);
        if (!TryMapPoint(p, out int x, out int y)) return;
        SetCursor(x, y);
        // 物理屏区域：不直接把点击送过去（那会打扰本机用户），而是"把这里点的那个窗口搬到副屏"。
        // 这样主控端看到主屏上有什么，就能把它拉过来操作 —— 而不是让被控端自动乱搬窗口。
        if (InReadOnlyRegion(x, y)) { RemotePullWindow?.Invoke(x, y); return; }
        RemoteMouseButton?.Invoke(x, y, ButtonIndex(e.ChangedButton), false);
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        ReleaseMouseCapture();
        var p = e.GetPosition(this);
        if (!TryMapPoint(p, out int x, out int y)) return;
        SetCursor(x, y);
        RemoteMouseButton?.Invoke(x, y, ButtonIndex(e.ChangedButton), true);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        if (!TryMapPoint(p, out int x, out int y)) return;
        SetCursor(x, y);
        RemoteMouseWheel?.Invoke(x, y, (short)Math.Clamp(e.Delta, short.MinValue, short.MaxValue));
        e.Handled = true;
    }

    private static byte ButtonIndex(MouseButton b) => b switch
    {
        MouseButton.Left => 0,
        MouseButton.Right => 1,
        MouseButton.Middle => 2,
        _ => 0,
    };

    // ---------------------------------------------------------------- 键盘

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        HandleKey(e, false);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        HandleKey(e, true);
    }

    private void HandleKey(KeyEventArgs e, bool up)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftAlt or Key.RightAlt or Key.System)
        {
            // Alt 组合键交给本机（避免被系统菜单截获）
            return;
        }
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) return;
        byte flags = IsExtendedKey(key) ? (byte)1 : (byte)0;
        RemoteKey?.Invoke((ushort)vk, up, flags);
        e.Handled = true;
    }

    private static bool IsExtendedKey(Key key) => key switch
    {
        Key.RightCtrl or Key.RightAlt or Key.Insert or Key.Delete or Key.Home or Key.End
            or Key.Prior or Key.Next or Key.Up or Key.Down or Key.Left or Key.Right
            or Key.NumLock or Key.Divide or Key.RWin or Key.Apps or Key.PrintScreen => true,
        _ => false,
    };
}
