using System.Windows;

namespace AgentSetup;

/// <summary>
/// 被控端一键安装程序（界面）：选安装目录 -> 填令牌 -> 自动完成全部安装步骤。
/// 静默模式（无人值守部署）走 Program.Main，不经过 WPF。
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>true 时 App 不再自动创建主窗口（由 Program 自己创建，用于 --ui-preview 预览）</summary>
    public static bool SuppressAutoWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SuppressAutoWindow) return;      // 避免出现两个窗口
        new MainWindow().Show();
    }
}
