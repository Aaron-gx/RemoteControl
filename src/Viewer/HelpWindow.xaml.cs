using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Viewer;

/// <summary>软件内使用说明（含 P2P 说明与授权说明）</summary>
public partial class HelpWindow : Window
{
    private double _fontSize = 13;

    public HelpWindow()
    {
        InitializeComponent();
        Render();
        PathText.Text = File.Exists(DocPath) ? DocPath : "（内置说明）";
        CloseBtn.Click += (_, _) => Close();
        FontUpBtn.Click += (_, _) => { _fontSize = Math.Min(20, _fontSize + 1); Render(); };
        FontDownBtn.Click += (_, _) => { _fontSize = Math.Max(10, _fontSize - 1); Render(); };
        OpenDocsBtn.Click += (_, _) =>
        {
            try
            {
                var dir = Path.GetDirectoryName(DocPath)!;
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("打开失败：" + ex.Message); }
        };
    }

    private static string DocPath => Path.Combine(AppContext.BaseDirectory, "docs", "使用说明.md");

    /// <summary>把 Markdown 渲染成 FlowDocument 显示（标题/表格/代码块/列表/链接都能正常排版）</summary>
    private void Render()
    {
        try
        {
            var doc = MarkdownRenderer.Render(LoadDoc());
            doc.FontSize = _fontSize;
            DocView.Document = doc;
        }
        catch (Exception ex)
        {
            // 渲染失败也不能让说明看不了：退回纯文本
            var doc = new System.Windows.Documents.FlowDocument(
                new System.Windows.Documents.Paragraph(new System.Windows.Documents.Run(LoadDoc())))
            { FontSize = _fontSize };
            DocView.Document = doc;
            PathText.Text = "（Markdown 渲染失败，已退回纯文本：" + ex.Message + "）";
        }
    }

    /// <summary>优先读随程序分发的说明文件；找不到则用内置精简版（保证任何情况下都有说明可看）</summary>
    private static string LoadDoc()
    {
        try
        {
            if (File.Exists(DocPath)) return File.ReadAllText(DocPath);
        }
        catch { }
        return Builtin;
    }

    private const string Builtin = """
远程控制软件 · 使用说明（内置精简版；完整版见程序目录 docs\使用说明.md）

【一、连接】
  1) 填服务器地址（默认 ws://202.60.232.209:8080/ws）
  2) 点「刷新」列出在线被控端
  3) 选一个 → 点「连接」→ 状态栏变绿即已连通

【二、操作远程电脑】
  · 左侧「已安装软件」：右键 → 打开 / 关闭(退出)
  · 右侧画面区域：鼠标点进去即可操作远程（远程有独立光标，不影响本机）
  · 远程复制文件后，在画面内按 Ctrl+V 即下载到本地下载目录
  · 剪贴板文本双向同步（远程 Ctrl+C → 本机 Ctrl+V 可用，反之亦然）

【三、连接方式（P2P 说明）】
  默认经香港中继服务器转发：主控端与被控端都只做出站连接，无需公网 IP 或端口映射。
  直连（P2P）分三级，能直连就直连、不行自动回落中继：
    L1 同局域网 / 一方有公网 IP → 直接点对点（规划中）
    L2 双方都在 NAT 后        → UDP 打洞（QUIC，规划中）
    L3 对称 NAT / 企业网      → 中继转发（已上线，随时可用）
  直连的好处：延迟更低、不占服务器带宽；局限：对称型 NAT 下打洞会失败，只能走中继。

【四、授权（时间限制）】
  中继带有效期；到期后中继拒绝一切通信（/healthz 除外），需安装新激活码。
  服务器上查看：/opt/rcserver/rcctl status
  续期：用上位机 LicenseKeygen.exe 按「部署ID」签发激活码 →
        /opt/rcserver/rcctl install "<激活码>"
  激活码由厂商私钥签名，改时间会验签失败，绑定部署ID，且会检测系统时间回拨。

【五、出问题时】
  · 「被控端不在线」→ 查中继 /api/agents 与被控端日志
  · 「unauthorized」→ 两端 AgentToken 与服务器 RC_TOKEN 不一致
  · 「license_expired」→ 授权到期，按上面续期
  · 画面黑屏 → 被控端 RDP 会话掉线，看护会在 20 秒内自动重建
""";
}
