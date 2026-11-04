using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

namespace AgentSetup;

/// <summary>被控端安装向导：欢迎 → 安装位置 → 安装中 → 完成（典型的"下一步"式安装）。</summary>
public partial class MainWindow : Window
{
    private int _step;                 // 0=欢迎 1=位置 2=安装中 3=完成
    private bool _installing;
    private string _server = "";
    private string _token = "";
    private string _agentId = "";
    private string _installDir = @"E:\RemoteControl\agent";
    private readonly List<string> _logLines = new();   // 安装日志（失败时写成文件给技术支持）

    public MainWindow()
    {
        InitializeComponent();
        var def = EmbeddedPayload.LoadDefaults();
        var saved = SetupSettings.Load();
        // 优先级：上次填过的 > 安装包预置值 > 内置默认。
        // 令牌特意做成"文件里为空就保留安装包预置的"：老版本写出来的空令牌文件不该把
        // 预置令牌顶掉（那会导致"装完连不上服务器"，而且界面看不出哪里不对）。
        _server = string.IsNullOrWhiteSpace(saved.ServerUrl) ? def.ServerUrl : saved.ServerUrl;
        _token = string.IsNullOrWhiteSpace(saved.Token) ? def.Token : saved.Token;
        _installDir = string.IsNullOrWhiteSpace(saved.InstallDir) ? SetupSettings.DefaultInstallDir() : saved.InstallDir;
        ServerBox.Text = _server;
        TokenBox.Text = _token;
        DirBox.Text = _installDir;

        // 默认 = 共享模式：远程操作本机已登录用户的桌面 + 虚拟外屏，软件与数据跟本机是同一份。
        // 所以不勾"独立会话模式"、虚拟外屏默认开；要旧方案（新建用户、两边互不干扰）再勾上。
        MultiSessionBox.IsChecked = false;
        AutoStartBox.IsChecked = true;
        DefenderBox.IsChecked = true;
        VddBox.IsChecked = true;

        BrowseBtn.Click += (_, _) => Browse();
        NextBtn.Click += (_, _) => OnNext();
        BackBtn.Click += (_, _) => ShowStep(_step - 1);
        Closed += (_, _) => EmbeddedPayload.Cleanup();

        Append("准备就绪。");
        ShowStep(0);
    }

    private void ShowStep(int step)
    {
        _step = step;
        Step1.Visibility = step == 0 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;

        BackBtn.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Content = step == 0 ? "下一步" : step == 1 ? "开始安装" : step == 3 ? "完成" : "安装中…";
        NextBtn.IsEnabled = step != 2;
        FooterText.Text = step == 1
            ? "安装过程约 1~2 分钟，期间请不要关闭窗口"
            : step == 3 ? "安装完成后，被控端会在后台运行" : "";
    }

    private void OnNext()
    {
        if (_installing) return;
        switch (_step)
        {
            case 0:
                ShowStep(1);                   // 欢迎 → 选择安装位置（不开始安装！）
                break;
            case 1:
                _ = StartInstall();            // 只有在"选择安装位置"页点按钮才真的开始装
                break;
            case 3:
                Close();
                break;
        }
    }

    private void Browse()
    {
        var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择被控端安装目录",
            SelectedPath = Directory.Exists(DirBox.Text) ? DirBox.Text : @"E:\",
            ShowNewFolderButton = true,
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK) DirBox.Text = dlg.SelectedPath;
    }

    private void Append(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        // 同时留一份在内存里：安装失败要把它写成文件发给技术支持（界面上的日志框会随面板切换消失）
        _logLines.Add(line);
        if (_logLines.Count > 5000) _logLines.RemoveRange(0, 1000);
    }

    /// <summary>把本次安装日志写到文件（优先程序目录，写不了就退到临时目录），返回路径。</summary>
    private string SaveLogFile()
    {
        var name = "安装日志-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
        foreach (var dir in new[] { AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try
            {
                var path = Path.Combine(dir, name);
                File.WriteAllLines(path, _logLines, Encoding.UTF8);
                return path;
            }
            catch { }
        }
        return "（日志写入失败，请直接复制上方窗口里的内容）";
    }

    /// <summary>从第 1 步进入安装流程；返回 false 表示被拦下（校验不通过/在提权）。</summary>
    private bool StartInstall()
    {
        if (!string.IsNullOrWhiteSpace(ServerBox.Text)) _server = ServerBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(TokenBox.Text)) _token = TokenBox.Text.Trim();
        _installDir = DirBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(_server))
        {
            System.Windows.MessageBox.Show("安装包里没有预置服务器地址，请在「高级设置」里填写。",
                "被控端安装程序", MessageBoxButton.OK, MessageBoxImage.Warning);
            Advanced.IsExpanded = true;
            return false;
        }

        // 令牌不能为空：服务器要求令牌，留空装出来的被控端连不上中继，
        // 而且界面/日志都不会明显报错（表现为"主控端看不到这台电脑"），所以在这里拦住。
        if (string.IsNullOrWhiteSpace(_token))
        {
            System.Windows.MessageBox.Show("请填写「连接令牌」：服务器要求令牌，留空装好后会连不上。\n" +
                "令牌由服务提供方给出（安装包若已预置就不必手填）。",
                "被控端安装程序", MessageBoxButton.OK, MessageBoxImage.Warning);
            Advanced.IsExpanded = true;
            return false;
        }

        // 提权：建用户 / 装驱动 / 注册计划任务都需要管理员
        if (!Installer.IsElevated)
        {
            var args = new[] { "--silent", "--dir", _installDir, "--server", _server, "--token", _token };
            if (!Installer.RelaunchElevated(args, out var err))
            {
                System.Windows.MessageBox.Show("需要管理员权限才能安装：" + err,
                    "被控端安装程序", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            Append("已申请管理员权限，安装将在新窗口继续（本窗口自动关闭）。");
            Close();
            return false;
        }

        _ = RunInstallAsync();
        return true;
    }

    private async Task RunInstallAsync()
    {
        _installing = true;
        ShowStep(2);
        Bar.IsIndeterminate = true;
        StatusText.Text = "正在部署程序文件…";
        Append("===== 开始安装 " + DateTime.Now.ToString("HH:mm:ss") + " =====");

        var o = new Installer.Options
        {
            InstallDir = _installDir,
            ServerUrl = _server,
            Token = _token,
            MultiSession = MultiSessionBox.IsChecked == true,
            VirtualDisplay = VddBox.IsChecked == true,
            AutoStart = AutoStartBox.IsChecked == true,
            DefenderExclusion = DefenderBox.IsChecked == true,
        };
        // 记住本次填写（不能只存 InstallDir：那样会把服务器/令牌/开关全写成默认值，
        // 下次打开就会看到空令牌并真的用空令牌去装）
        SetupSettings.Save(new SetupSettings.Data
        {
            InstallDir = o.InstallDir,
            ServerUrl = o.ServerUrl,
            Token = o.Token,
            MultiSession = o.MultiSession,
            VirtualDisplay = o.VirtualDisplay,
            AutoStart = o.AutoStart,
            DefenderExclusion = o.DefenderExclusion,
        });

        int code = -1;
        try { code = await Task.Run(() => Installer.RunInstall(o, line => Dispatcher.Invoke(() => Append(line)))); }
        catch (Exception ex) { Append("安装过程异常：" + ex.Message); }

        Bar.IsIndeterminate = false;
        _installing = false;

        if (code != 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 0;
            // 关键：**停在"安装中"这一页**（日志框在这里）。原来切到"完成"页，
            // 那里没有日志框，用户被提示"把日志发给技术支持"却根本看不到日志。
            var logPath = SaveLogFile();
            Append("");
            Append("===== 安装失败（退出码 " + code + "）=====");
            Append("完整日志已保存：" + logPath);
            Append("请把该文件发给技术支持（里面有失败发生在哪一步、报的什么错）。");
            StatusText.Text = "安装失败（退出码 " + code + "）。完整日志：" + logPath;
            NextBtn.Content = "关闭";
            NextBtn.IsEnabled = true;
            FooterText.Text = "安装未完成：先看日志最后几行";
            return;
        }

        // 装完就让它跑起来（用户要的是"装完即可用"）：安装脚本本身不负责启动，得这里来。
        // 勾选框（RunNowBox）就是控制这一件事的 —— 以前它是个摆设，没人读。
        var started = false;
        if (RunNowBox.IsChecked != false)
        {
            Installer.StartAgent(o.InstallDir, out var startErr);
            started = string.IsNullOrEmpty(startErr);
            Append(started ? "已启动被控端" : "启动被控端失败：" + startErr);
            if (started) await Task.Delay(1500);      // 给它一点时间连上中继
        }
        else
        {
            Append("（按你的选择：不自动启动被控端）");
        }

        // 装完自检：直接告诉用户"能不能连上服务器 + 这台电脑的 AgentId"
        Bar.Value = 70;
        StatusText.Text = "正在检查与服务器的连通性…";
        Append("");
        Append("===== 连通性自检 =====");
        var agentId = Installer.ReadAgentId(o.InstallDir);
        var verdict = await Task.Run(() => Installer.RunSelfCheck(o, agentId, line => Dispatcher.Invoke(() => Append(line))));
        Bar.Value = 100;

        _agentId = agentId;
        AgentIdText.Text = string.IsNullOrEmpty(agentId) ? "（未读到，查看程序目录 config\agent.json）" : agentId;
        HostText.Text = "计算机名：" + Environment.MachineName;
        ShowStep(3);
        if (verdict.Ok)
        {
            StatusText.Text = started ? "安装完成，已连上服务器" : "安装完成（未自动启动被控端）";
            DoneTitle.Text = "安装完成";
            DoneText.Text = started
                ? "被控端已启动，并且已经连上服务器。主控端点「刷新」后，在列表里选下面这个 AgentId 即可连接。"
                : "被控端已装好，但按你的选择没有自动启动。启动它之后主控端即可连接。";
        }
        else
        {
            StatusText.Text = "安装完成，但连不上服务器";
            DoneGlyph.Text = "!";
            DoneGlyph.Foreground = System.Windows.Media.Brushes.OrangeRed;
            DoneTitle.Text = "安装完成（暂未连上服务器）";
            DoneText.Text = (started ? "被控端已装好并启动" : "被控端已装好（未自动启动）") +
                            "，但这台电脑现在连不上服务器，所以主控端列表里还看不到它。" +
                            Environment.NewLine + "原因：" + string.Join("；", verdict.Reasons) +
                            Environment.NewLine + "常见处理：换手机热点试一次（多为这台电脑的网络封了服务器端口）；" +
                            "或让服务提供方开启 443 端口（被控端会自动改用该端口）。";
        }
    }

    /// <summary>调试/预览：只展示界面（不安装、不启动被控端）。step: 0..3</summary>
    public void Preview(int step)
    {
        if (step == 2)
        {
            Append("===== 开始安装 17:00:00 =====");
            Append("安装目录：E:" + "\\RemoteControl" + "\\agent");
            Append("1. 部署程序文件");
            Append("2. 创建远程会话用户");
            Append("3. 开启远程桌面与防火墙");
            Append("4. 安装多会话支持（RDPWrap）");
            Append("5. 安装虚拟副屏驱动（按需）");
            Append("6. Defender 排除项");
            Append("7. 写入配置并注册开机自启");
            StatusText.Text = "正在写入配置并注册开机自启…";
            Bar.Value = 70;
        }
        if (step == 3)
        {
            AgentIdText.Text = "AURA-PREVIEW";
            HostText.Text = "计算机名：PREVIEW-PC";
            DoneTitle.Text = "安装完成";
            DoneText.Text = "被控端已启动，并且已经连上服务器。主控端刷新后在列表里选这个 AgentId 即可连接。";
            StatusText.Text = "安装完成，已连上服务器";
        }
        ShowStep(step);
    }

    private void OpenDirClicked(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", _installDir) { UseShellExecute = true }); }
        catch (Exception ex) { System.Windows.MessageBox.Show("打开失败：" + ex.Message); }
    }
}
