using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ComboProbe;

/// <summary>
/// 下拉框冒烟测试：把主控端真实的深色主题加载起来，渲染一个可编辑下拉框，
/// 然后**对右侧箭头所在的位置做命中测试** —— 命中的必须是箭头按钮（ToggleButton 或其子元素）。
///
/// 【为什么需要】被控端那个下拉框以前是"输入框把箭头整个盖住"：界面上看不到箭头、也点不开，
/// 用户根本没法从列表里选在线的那台机器（现场反馈："连下拉按钮都没有"）。这类问题编译期发现不了，
/// 只有真渲染一次、真点一下才知道。本探针窗口一闪而过（不置顶、不入任务栏）。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 下拉框冒烟测试（加载主控端真实主题）===");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        int failed = 0;

        try
        {
            // 真实主题：Viewer 程序集里的 Themes/Dark.xaml
            var dict = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Viewer;component/Themes/Dark.xaml", UriKind.Absolute),
            };
            app.Resources.MergedDictionaries.Add(dict);
            Console.WriteLine($"  [PASS] 主题已加载（资源数 {dict.Count}）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] 主题加载失败：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        // 三个下拉框：可编辑的（被控端）+ 两个不可编辑的（链路/鼠标）
        failed += CheckCombo("被控端（IsEditable=true）", editable: true);
        failed += CheckCombo("链路（IsEditable=false）", editable: false);

        // 主窗口能不能被加载起来 —— 这一步专治"改完 XAML 一启动就崩"：
        // 窗口图标、资源引用这类错误只在真正构造窗口时才炸（Icon="pack://.../Assets/viewer.ico" 就是这种）。
        // 只构造不显示：ctor 里不会发起连接（连接在 Loaded 里做）。
        try
        {
            var win = new Viewer.MainWindow();
            win.Close();
            Console.WriteLine("  [PASS] 主窗口（MainWindow）能构造：XAML 与资源引用都没问题");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] 主窗口构造失败（主控端会一启动就崩）：{ex.GetType().Name}: {ex.Message}");
            failed++;
        }

        // 日志目录写不进去时**不能崩**、要自动换到用户目录（Program Files 安装现场踩到的那个框）
        failed += CheckLoggerFallback();

        app.Shutdown();
        Console.WriteLine(failed == 0
            ? "\n➜ 下拉框外观/命中测试全部通过：箭头可见、可点，输入框不再遮住它。"
            : $"\n➜ 有 {failed} 项失败，需要修。");
        Environment.Exit(failed == 0 ? 0 : 1);
        return failed == 0 ? 0 : 1;
    }

    private static int CheckCombo(string name, bool editable)
    {
        var win = new Window
        {
            Title = "ComboProbe",
            Width = 420,
            Height = 120,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 40 + (editable ? 0 : 480),
            Top = 40,
            ShowInTaskbar = false,
            ShowActivated = false,
        };
        var combo = new ComboBox { Width = 300, IsEditable = editable, VerticalContentAlignment = VerticalAlignment.Center };
        combo.Items.Add("WIN-MEQIVMJ5283-956BD4F8");
        combo.Items.Add("WIN-MEQMJ5283-1A2B3C4D");
        combo.Text = "WIN-MEQMJ5283-956BD4F8";
        win.Content = new StackPanel { Margin = new Thickness(20), Children = { combo } };

        int problems = 0;
        try
        {
            win.Show();
            win.UpdateLayout();
            combo.ApplyTemplate();

            // 找箭头按钮与输入框
            var toggle = FindChild<ToggleButton>(combo);
            var textBox = FindChild<TextBox>(combo);
            if (toggle == null)
            {
                Console.WriteLine($"  [FAIL] {name}：模板里找不到箭头按钮（ToggleButton）");
                return 1;
            }
            var tb = toggle.TransformToAncestor(combo).Transform(new Point(0, 0));
            var toggleRect = new Rect(tb, new Size(toggle.ActualWidth, toggle.ActualHeight));

            // 关键：在箭头位置做命中测试 —— 必须命中箭头按钮（或其内部元素），
            // 否则说明有东西盖在它上面（就是这次修的 bug：输入框盖住箭头）
            var probe = new Point(combo.ActualWidth - 6, combo.ActualHeight / 2);
            var hit = VisualTreeHelper.HitTest(combo, probe)?.VisualHit;
            bool hitToggle = hit != null && IsDescendantOf(hit, toggle);

            string state = $"箭头按钮 {toggleRect.Width:F0}x{toggleRect.Height:F0}，" +
                           $"命中点 x={probe.X:F0} → {(hitToggle ? "命中箭头" : "被别的元素挡住")}";
            if (!hitToggle) { Console.WriteLine($"  [FAIL] {name}：{state}"); problems++; }
            else Console.WriteLine($"  [PASS] {name}：{state}");

            // 可编辑时：输入框不能压到箭头那一列（否则用户看不到箭头）
            if (editable && textBox != null)
            {
                var tbt = textBox.TransformToAncestor(combo).Transform(new Point(0, 0));
                bool covered = tbt.X + textBox.ActualWidth > toggleRect.X + 1;
                if (covered) { Console.WriteLine($"  [FAIL] {name}：输入框右边缘 {tbt.X + textBox.ActualWidth:F0} 压住了箭头列（起点 {toggleRect.X:F0}）"); problems++; }
                else Console.WriteLine($"  [PASS] {name}：输入框右边缘 {tbt.X + textBox.ActualWidth:F0} < 箭头列起点 {toggleRect.X:F0}");
            }

            // 能不能真的展开（模拟点箭头）
            toggle.IsChecked = true;
            win.UpdateLayout();
            bool opened = combo.IsDropDownOpen;
            if (!opened) { Console.WriteLine($"  [FAIL] {name}：点箭头没能展开下拉"); problems++; }
            else Console.WriteLine($"  [PASS] {name}：点箭头能展开下拉（共 {combo.Items.Count} 项）");
            toggle.IsChecked = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] {name}：渲染异常 {ex.GetType().Name}: {ex.Message}");
            problems++;
        }
        finally
        {
            try { win.Close(); } catch { }
        }
        return problems;
    }

    /// <summary>
    /// 模拟"日志目录建不出来"（拿一个**文件**当基目录，CreateDirectory 必然失败），
    /// 验证 Logger 既不抛异常、又能落到 %LOCALAPPDATA%\远程控制\logs。
    /// </summary>
    private static int CheckLoggerFallback()
    {
        try
        {
            var fileAsDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "combo-probe-not-a-dir.txt");
            File.WriteAllText(fileAsDir, "x");
            var log = new Agent.Common.Logger("probe-fallback", fileAsDir, Agent.Common.LogLevel.Info, echoConsole: false);
            log.Info("这条日志应当出现在兜底目录里");
            var ok = !string.IsNullOrEmpty(log.FallbackPath) && File.Exists(log.FallbackPath);
            Console.WriteLine(ok
                ? $"  [PASS] 日志目录不可写时不崩溃、自动改写到：{log.FallbackPath}"
                : "  [FAIL] 日志兜底没有生效（FallbackPath 为空或文件不存在）");
            var p = log.Path;
            log.Dispose();
            try { File.Delete(p); } catch { }
            try { File.Delete(fileAsDir); } catch { }
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [FAIL] 日志兜底测试抛异常：{ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var deeper = FindChild<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static bool IsDescendantOf(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }
}
