using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
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
