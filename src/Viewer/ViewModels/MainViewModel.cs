using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Viewer.ViewModels;

/// <summary>主视图模型：软件列表 + 状态栏（策划 §3.1 / §3.2）</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    public ObservableCollection<Models.RemoteSoftware> SoftwareList { get; } = new();

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { if (_isConnected == value) return; _isConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusGlyph)); }
    }

    public string StatusGlyph => _isConnected ? "🟢" : "🔴";

    private string _statusText = "未连接";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _latencyText = "延迟: --";
    public string LatencyText { get => _latencyText; set => Set(ref _latencyText, value); }

    private string _resolutionText = "分辨率: --";
    public string ResolutionText { get => _resolutionText; set => Set(ref _resolutionText, value); }

    private string _fpsText = "FPS: --";
    public string FpsText { get => _fpsText; set => Set(ref _fpsText, value); }

    private string _clipboardHint = "";
    public string ClipboardHint { get => _clipboardHint; set => Set(ref _clipboardHint, value); }

    private string _bitrateText = "";
    public string BitrateText { get => _bitrateText; set => Set(ref _bitrateText, value); }

    private string _agentText = "";
    public string AgentText { get => _agentText; set => Set(ref _agentText, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
