using System.Drawing;
using Agent.Common;

namespace Agent.Coordinator;

/// <summary>
/// 系统托盘（策划 §2.1：双击静默启动只显示托盘图标，右键唯一菜单项「退出」）
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _ni;
    private readonly Logger _log;
    private Icon _icon;

    public event Action? ExitRequested;

    public TrayIcon(Logger log, string tooltip = "远程控制 Agent")
    {
        _log = log;
        _icon = BuildIcon();
        var menu = new System.Windows.Forms.ContextMenuStrip();
        var exit = new System.Windows.Forms.ToolStripMenuItem("退出");
        exit.Click += (_, _) =>
        {
            _log.Info("用户选择退出（托盘右键 → 退出）");
            ExitRequested?.Invoke();
        };
        menu.Items.Add(exit);

        _ni = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icon,
            Text = tooltip,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _ni.DoubleClick += (_, _) => { /* 双击不做任何事（无窗口） */ };
        _log.Info("托盘图标已创建");
    }

    public void SetTooltip(string text)
    {
        try
        {
            _ni.Text = text.Length > 63 ? text[..63] : text;
        }
        catch { }
    }

    public void ShowBalloon(string title, string text)
    {
        try
        {
            _ni.BalloonTipTitle = title;
            _ni.BalloonTipText = text;
            _ni.ShowBalloonTip(3000);
        }
        catch { }
    }

    /// <summary>程序内绘制一个简单的托盘图标，避免依赖外部 .ico 资源</summary>
    private static Icon BuildIcon()
    {
        var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(30, 110, 200));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var fg = new SolidBrush(Color.White);
            g.FillRectangle(fg, 8, 9, 16, 10);       // 屏幕
            g.FillRectangle(fg, 14, 20, 4, 4);       // 支架
            g.FillRectangle(fg, 10, 24, 12, 2);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        try { _ni.Visible = false; _ni.Dispose(); } catch { }
        try { _icon.Dispose(); } catch { }
    }
}
