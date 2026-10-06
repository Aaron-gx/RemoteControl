using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Agent.Common;

namespace Viewer.Models;

/// <summary>左侧软件列表项（策划 §3.1：🔵 已打开 / ⚪ 未打开）</summary>
public sealed class RemoteSoftware : INotifyPropertyChanged
{
    private bool _isRunning;
    private int _pid;

    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (_isRunning == value) return;
            _isRunning = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(StatusGlyph));
        }
    }

    public int Pid
    {
        get => _pid;
        set
        {
            if (_pid == value) return;
            _pid = value;
            OnPropertyChanged();
        }
    }

    /// <summary>🔵 运行中 / ⚪ 未运行</summary>
    public string StatusGlyph => _isRunning ? "🔵" : "⚪";

    public Brush StatusBrush => _isRunning
        ? new SolidColorBrush(Color.FromRgb(0x2E, 0x86, 0xDE))
        : new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            OnPropertyChanged();
        }
    }

    public string ToolTipText => string.IsNullOrEmpty(ExePath) ? Name : $"{Name}\n{ExePath}";

    /// <summary>置顶的「此电脑」固定项（不是被控端枚举出来的软件，2026-10 新增）</summary>
    public bool IsThisPc { get; private init; }

    /// <summary>构造置顶的「此电脑」项：ExePath 用被控端认识的哨兵值，图标程序内绘制</summary>
    public static RemoteSoftware CreateThisPc()
        => new() { Name = "此电脑", ExePath = SoftwareListMarkers.ThisPc, IsThisPc = true, Icon = DrawThisPcIcon() };

    /// <summary>画一个简易显示器图标（不依赖被控端传 icon base64 —— 这一项是主控端自己造的）</summary>
    private static ImageSource? DrawThisPcIcon()
    {
        try
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0x3E, 0x9E, 0xF0)), null,
                    new Rect(2, 3, 20, 14), 2, 2);
                dc.DrawRectangle(Brushes.White, null, new Rect(4, 5, 16, 10));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3E, 0x9E, 0xF0)), null, new Rect(9, 17, 6, 2));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3E, 0x9E, 0xF0)), null, new Rect(6, 19, 12, 2));
            }
            var bmp = new RenderTargetBitmap(24, 24, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(dv);
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public static RemoteSoftware From(SoftwareInfo info)
    {
        var sw = new RemoteSoftware
        {
            Name = info.Name,
            ExePath = info.ExePath,
            IsRunning = info.IsRunning,
            Pid = info.Pid,
        };
        sw.Icon = DecodeIcon(info.Icon);
        return sw;
    }

    private static ImageSource? DecodeIcon(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 24;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
