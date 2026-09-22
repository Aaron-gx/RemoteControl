using System.Runtime.InteropServices;
using System.Windows.Forms;
using Agent.Common;

namespace Agent.Worker;

/// <summary>
/// Session 1 的剪贴板监听（策划 §8）：
/// - AddClipboardFormatListener 监听变化
/// - 文本 → 上报主控端
/// - CF_HDROP 文件 → 上报给 Coordinator 走 HTTP 上传
/// - 从主控端下发的文本被写回本会话剪贴板（不回环上报）
/// </summary>
public sealed class ClipboardWatcher : NativeWindow, IDisposable
{
    private readonly Logger _log;
    private uint _lastSequence;
    private string _lastText = "";
    private string _suppressText = "";
    private string _lastFileSignature = "";
    private DateTime _lastFileReportUtc = DateTime.MinValue;

    public event Action<string>? TextCopied;
    public event Action<List<FileDropInfo>>? FilesCopied;

    public ClipboardWatcher(Logger log)
    {
        _log = log;
        CreateHandle(new CreateParams { Caption = "RemoteControlClipboardWatcher" });
        // 创建一个挂在本线程（STA）上的隐藏控件，供其它线程 BeginInvoke 回来
        Invoker = new System.Windows.Forms.Control();
        Invoker.CreateControl();
        if (!NativeApi.AddClipboardFormatListener(Handle))
            _log.Warn($"AddClipboardFormatListener 失败：{NativeApi.LastWin32Error()}");
        else
            _log.Info("剪贴板监听已启动");
    }

    /// <summary>用于把调用 marshal 回创建本窗口的 STA 线程</summary>
    public System.Windows.Forms.Control Invoker { get; }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeApi.WM_CLIPBOARDUPDATE)
        {
            try { OnClipboardUpdate(); }
            catch (Exception ex) { _log.Debug($"剪贴板处理异常：{ex.Message}"); }
        }
        base.WndProc(ref m);
    }

    private void OnClipboardUpdate()
    {
        uint seq = NativeApi.GetClipboardSequenceNumber();
        if (seq == _lastSequence) return;
        _lastSequence = seq;

        // 我们自己写入的文本不再回传
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Clipboard.ContainsFileDropList())
                {
                    var files = new List<FileDropInfo>();
                    foreach (var f in Clipboard.GetFileDropList())
                    {
                        if (string.IsNullOrEmpty(f) || !File.Exists(f)) continue;
                        files.Add(new FileDropInfo
                        {
                            LocalPath = f,
                            FileName = Path.GetFileName(f),
                            FileSize = new FileInfo(f).Length,
                        });
                    }
                    if (files.Count > 0)
                    {
                        // 同一次复制可能触发多次 WM_CLIPBOARDUPDATE，3 秒内相同文件集只上报一次
                        var sig = string.Join("|", files.Select(f => f.LocalPath + ":" + f.FileSize));
                        if (sig == _lastFileSignature && DateTime.UtcNow - _lastFileReportUtc < TimeSpan.FromSeconds(3))
                        {
                            _log.Debug("剪贴板文件与上次相同，跳过重复上报");
                            return;
                        }
                        _lastFileSignature = sig;
                        _lastFileReportUtc = DateTime.UtcNow;
                        _log.Info($"检测到剪贴板文件：{string.Join(", ", files.Select(f => f.FileName))}");
                        FilesCopied?.Invoke(files);
                    }
                    return;
                }

                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (string.IsNullOrEmpty(text)) return;
                    if (text == _lastText) return;
                    _lastText = text;
                    if (text == _suppressText)
                    {
                        _log.Debug("剪贴板文本来自主控端同步，忽略上报");
                        return;
                    }
                    _log.Info($"检测到剪贴板文本（{text.Length} 字符）");
                    TextCopied?.Invoke(text);
                    return;
                }
            }
            catch (ExternalException ex)
            {
                // 剪贴板被其它进程占用
                _log.Debug($"剪贴板忙（第 {attempt + 1} 次）：{ex.Message}");
                Thread.Sleep(60);
            }
        }
    }

    /// <summary>把主控端下发的文本写入本会话剪贴板</summary>
    public void SetTextFromRemote(string text)
    {
        _suppressText = text;
        _lastText = text;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                _lastSequence = NativeApi.GetClipboardSequenceNumber();
                _log.Info($"已写入远程会话剪贴板（{text.Length} 字符）");
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(80);
            }
        }
        _log.Warn("写入剪贴板失败（被占用）");
    }

    public void Dispose()
    {
        try { NativeApi.RemoveClipboardFormatListener(Handle); } catch { }
        try { DestroyHandle(); } catch { }
    }
}
