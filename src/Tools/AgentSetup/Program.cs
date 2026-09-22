using System.Linq;

namespace AgentSetup;

/// <summary>
/// 入口：
///   --silent 时**完全不初始化 WPF**（无人值守/服务/无桌面环境下也能跑，输出走控制台）；
///   否则拉起 WPF 安装界面。
/// 这样静默安装不会再受 WPF 初始化影响（之前出现过 DllNotFoundException 崩在 WPF 启动阶段）。
/// </summary>
public static class Program
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--silent", StringComparison.OrdinalIgnoreCase)))
        {
            // WPF 程序默认没有控制台：附着到调用者（PowerShell/cmd）的控制台，
            // 静默模式的输出才能直接看见 —— 否则在终端里跑完什么都不显示，很容易以为"没反应"。
            try { AttachConsole(-1); } catch { }
            return SilentRunner.Run(args);
        }

        var app = new App();
        app.InitializeComponent();
        var previewArg = args.FirstOrDefault(a => a.StartsWith("--ui-preview", StringComparison.OrdinalIgnoreCase));
        if (previewArg != null)
        {
            // 只展示界面（用于截图核对样式），不执行安装
            int step = 3;
            var parts = previewArg.Split('=');
            if (parts.Length == 2 && int.TryParse(parts[1], out var s2)) step = Math.Clamp(s2, 0, 3);
            App.SuppressAutoWindow = true;       // 只由这里创建窗口，App 不要再建一个
            var w = new MainWindow();
            w.Show();
            w.Preview(step);
            return app.Run();
        }
        return app.Run();
    }
}
