using System.Windows;
using System.Windows.Threading;

namespace Viewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 窗口在这里手动创建（App.xaml 不再用 StartupUri）：
        // 这样 `Viewer.exe --help-doc` 可以只打开使用说明，方便排障与支持。
        DispatcherUnhandledException += OnUnhandledException;
        if (e.Args.Any(a => a.Equals("--help-doc", StringComparison.OrdinalIgnoreCase)))
            new HelpWindow().Show();
        else
            new MainWindow().Show();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                Agent.Common.Logger.Current?.Error($"AppDomain 未处理异常：{args.ExceptionObject}");
            }
            catch { }
        };
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Agent.Common.Logger.Current?.Error("UI 未处理异常", e.Exception);
            MessageBox.Show($"发生异常：{e.Exception.Message}\n\n已写入日志，程序继续运行。", "远程控制",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
        e.Handled = true;
    }
}
