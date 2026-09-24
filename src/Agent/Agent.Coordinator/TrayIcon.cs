using System.Drawing;
using Agent.Common;

namespace Agent.Coordinator;

/// <summary>
/// 托盘菜单要用的回调：托盘只负责显示与转发点击，具体做什么由协调器决定
/// （这样托盘不用直接持有 DirectLinkServer / AgentConfig 这些对象）。
/// </summary>
public sealed class TrayHooks
{
    /// <summary>直连状态一句话（菜单打开时刷新）</summary>
    public required Func<string> DirectStatusText { get; init; }
    /// <summary>直连当前是否启用</summary>
    public required Func<bool> DirectEnabled { get; init; }
    /// <summary>开/关直连（立即生效）</summary>
    public required Action<bool> SetDirectEnabled { get; init; }
    /// <summary>当前直连地址（用于"复制"）</summary>
    public required Func<string> DirectAddressText { get; init; }
    /// <summary>设置直连公网地址（形如 1.2.3.4:47890；传空串表示清空）</summary>
    public required Action<string> SetDirectPublicAddress { get; init; }
}

/// <summary>
/// 系统托盘：双击静默启动只显示图标；右键菜单 = 直连(P2P)状态 / 开关 / 地址设置 / 退出。
///
/// 为什么被控端也需要这套开关：主控端那边一直有"链路（自动/仅中继/仅直连）"下拉，
/// 但被控端只有配置文件里的几个字段、托盘里只有一个"退出" —— 客户在机器前面根本没法开关直连，
/// 更没法在路由器做完端口映射后把公网地址填进去（以前必须手改 agent.json 再重启）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _ni;
    private readonly Logger _log;
    private readonly TrayHooks? _hooks;
    private readonly Icon _icon;

    public event Action? ExitRequested;

    public TrayIcon(Logger log, string tooltip = "远程控制 Agent", TrayHooks? hooks = null)
    {
        _log = log;
        _hooks = hooks;
        _icon = BuildIcon();
        var menu = new System.Windows.Forms.ContextMenuStrip();

        var status = new System.Windows.Forms.ToolStripMenuItem("直连：--") { Enabled = false };
        var enable = new System.Windows.Forms.ToolStripMenuItem("启用直连（P2P）") { CheckOnClick = true };
        var copyAddr = new System.Windows.Forms.ToolStripMenuItem("复制直连地址");
        var setAddr = new System.Windows.Forms.ToolStripMenuItem("设置直连公网地址（做完端口映射后填）…");
        var exit = new System.Windows.Forms.ToolStripMenuItem("退出");

        // 状态行每次打开菜单时刷新（状态是动态的）
        menu.Opening += (_, _) =>
        {
            if (_hooks == null)
            {
                status.Text = "直连：--";
                enable.Enabled = copyAddr.Enabled = setAddr.Enabled = false;
                return;
            }
            try
            {
                status.Text = "直连：" + _hooks.DirectStatusText();
                enable.Checked = _hooks.DirectEnabled();
                var addr = _hooks.DirectAddressText();
                copyAddr.Enabled = !string.IsNullOrEmpty(addr);
                copyAddr.Text = string.IsNullOrEmpty(addr) ? "复制直连地址" : $"复制直连地址（{addr}）";
            }
            catch (Exception ex) { status.Text = "直连：读取状态失败"; _log.Debug($"托盘状态刷新失败：{ex.Message}"); }
        };

        enable.Click += (_, _) =>
        {
            try { _hooks?.SetDirectEnabled(enable.Checked); }
            catch (Exception ex) { _log.Warn($"切换直连失败：{ex.Message}"); }
        };

        copyAddr.Click += (_, _) =>
        {
            try
            {
                var addr = _hooks?.DirectAddressText() ?? "";
                if (string.IsNullOrEmpty(addr)) return;
                System.Windows.Forms.Clipboard.SetText(addr);
                ShowBalloon("已复制直连地址", addr + "\n在路由器上把 TCP 端口映射到本机，然后回来填「设置直连公网地址」。");
            }
            catch (Exception ex) { _log.Warn($"复制失败：{ex.Message}"); }
        };

        setAddr.Click += (_, _) =>
        {
            try
            {
                var cur = _hooks?.DirectAddressText() ?? "";
                var v = InputDialog("设置直连公网地址",
                    "格式：公网IP:端口（例如 120.12.200.170:47890）\n留空 = 清空，回退到自动(UPnP)与内网候选。",
                    cur);
                if (v == null) return;
                _hooks?.SetDirectPublicAddress(v);
                ShowBalloon("已保存", string.IsNullOrWhiteSpace(v)
                    ? "已清空直连公网地址（重启被控端后完全生效）"
                    : $"直连公网地址：{v.Trim()}\n主控端下一次连接会尝试直连。");
            }
            catch (Exception ex) { _log.Warn($"设置直连地址失败：{ex.Message}"); }
        };

        exit.Click += (_, _) =>
        {
            _log.Info("用户选择退出（托盘右键 → 退出）");
            ExitRequested?.Invoke();
        };

        menu.Items.Add(status);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(enable);
        menu.Items.Add(copyAddr);
        menu.Items.Add(setAddr);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exit);

        _ni = new System.Windows.Forms.NotifyIcon
        {
            Icon = _icon,
            Text = tooltip,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _ni.DoubleClick += (_, _) => { /* 双击不做任何事（无窗口） */ };
        _log.Info("托盘图标已创建（右键：直连开关 / 复制地址 / 设置公网地址 / 退出）");
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

    /// <summary>一个极简输入框（被控端没有窗口，用不起对话框资源）</summary>
    private static string? InputDialog(string title, string prompt, string initial)
    {
        using var f = new System.Windows.Forms.Form
        {
            Text = title,
            Width = 460,
            Height = 210,
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false,
            TopMost = true,
        };
        var lbl = new System.Windows.Forms.Label { Text = prompt, Left = 14, Top = 14, Width = 420, Height = 50 };
        var tb = new System.Windows.Forms.TextBox { Text = initial, Left = 14, Top = 70, Width = 420 };
        var ok = new System.Windows.Forms.Button { Text = "确定", DialogResult = System.Windows.Forms.DialogResult.OK, Left = 264, Top = 112, Width = 80 };
        var cancel = new System.Windows.Forms.Button { Text = "取消", DialogResult = System.Windows.Forms.DialogResult.Cancel, Left = 354, Top = 112, Width = 80 };
        f.Controls.Add(lbl);
        f.Controls.Add(tb);
        f.Controls.Add(ok);
        f.Controls.Add(cancel);
        f.AcceptButton = ok;
        f.CancelButton = cancel;
        return f.ShowDialog() == System.Windows.Forms.DialogResult.OK ? tb.Text.Trim() : null;
    }

    /// <summary>
    /// 托盘图标：优先用随包嵌进来的品牌图标（src\Assetsgent.ico，多尺寸，托盘/任务栏都清晰）；
    /// 万一资源缺失就退回下面程序内绘制的简易图标，保证托盘一定有个能点的图标。
    /// </summary>
    private static Icon BuildIcon()
    {
        try
        {
            using var s = typeof(TrayIcon).Assembly.GetManifestResourceStream(AgentIconResource);
            if (s != null) return new Icon(s);
        }
        catch { }
        return BuildFallbackIcon();
    }

    private const string AgentIconResource = "Agent.Coordinator.agent.ico";

    /// <summary>兜底：程序内画一个（不依赖任何外部/嵌入资源）</summary>
    private static Icon BuildFallbackIcon()
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
