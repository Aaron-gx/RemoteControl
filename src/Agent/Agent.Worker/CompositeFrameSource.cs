using Agent.Common;

namespace Agent.Worker;

/// <summary>
/// 把多块屏"拼成一块画布"的采集源：每块屏用自己的采集后端（默认 WGC，和单屏模式走的是同一条路），
/// 抓到的帧按各自在画布里的位置贴进去。
///
/// 【为什么不是"一次 GDI BitBlt 抓整个虚拟桌面"】
/// 那样实现简单（一行 BitBlt），但跨显卡时会踩坑：笔记本是"集显 + 独显"、或者主屏在独显上而
/// 虚拟外屏在系统虚拟显示适配器上时，用桌面 DC 一次抓"主屏 + 外屏"的并集**会抓回一整片黑**
/// （现场实测：切换"全部屏幕"后画面全黑）。而"每块屏单独采"从始至终都是好的
/// （单屏模式就是这么做），所以画布改成"各屏各采、再拼"——代价是每帧多一次内存拷贝，换来稳。
/// </summary>
public sealed class CompositeFrameSource : FrameSource
{
    private readonly Logger _log;
    private readonly List<Part> _parts = new();
    private readonly byte[] _canvas;
    private readonly MemoryStream _scratch = new(1024 * 1024);

    private sealed record Part(FrameSource Source, int X, int Y, DisplayInfo Info);

    public override string Backend =>
        "拼接(" + string.Join("+", _parts.Select(p => p.Source.Backend).Distinct()) + ")";

    /// <param name="canvas">画布 = 整个虚拟桌面的并集（左上角 + 尺寸）</param>
    /// <param name="screens">要拼进来的屏（各自的绝对坐标）</param>
    /// <param name="backend">每块屏使用的后端（"auto" = 先试 WGC）</param>
    public CompositeFrameSource(DisplayInfo canvas, IReadOnlyList<DisplayInfo> screens, string backend, Logger log)
    {
        _log = log;
        Width = canvas.Width;
        Height = canvas.Height;
        if (Width <= 0 || Height <= 0) throw new InvalidOperationException($"画布尺寸非法 {Width}x{Height}");
        _canvas = new byte[(long)Width * Height * 4 <= int.MaxValue ? Width * Height * 4 : throw new InvalidOperationException("画布过大")];

        foreach (var s in screens)
        {
            try
            {
                var src = FrameSourceFactory.Create(backend, s, log);
                _parts.Add(new Part(src, s.Left - canvas.Left, s.Top - canvas.Top, s));
            }
            catch (Exception ex)
            {
                _log.Warn($"画布：屏 {s.DeviceName} 的采集源创建失败（该区域将保持黑色）：{ex.Message}");
            }
        }
        if (_parts.Count == 0) throw new InvalidOperationException("画布：没有任何一块屏能采集");
        _log.Info($"画布采集已就绪：{Width}x{Height}，拼入 {_parts.Count} 块屏（{Backend}）");
    }

    public override bool Capture(Stream output, out string error)
    {
        error = "";
        int ok = 0;
        foreach (var part in _parts)
        {
            _scratch.SetLength(0);
            if (!part.Source.Capture(_scratch, out var err))
            {
                error = $"{part.Info.DeviceName}: {err}";
                continue;   // 某块屏这一帧失败：保留上一帧的该区域，其余照常
            }
            Blit(_scratch.GetBuffer(), part);
            ok++;
        }
        if (ok == 0) return false;
        output.Write(_canvas, 0, _canvas.Length);
        return true;
    }

    /// <summary>把一块屏的帧贴进画布（自动处理"屏在画布左边外面"这种负偏移）</summary>
    private void Blit(byte[] frame, Part part)
    {
        int srcW = part.Info.Width, srcH = part.Info.Height;
        int rowBytes = srcW * 4;
        for (int row = 0; row < srcH; row++)
        {
            int dstY = part.Y + row;
            if (dstY < 0 || dstY >= Height) continue;

            int dstX = part.X;
            int srcOffset = row * rowBytes;
            int copyBytes = rowBytes;

            if (dstX < 0)
            {
                srcOffset -= dstX * 4;      // 跳过画布左边之外的那几列
                copyBytes += dstX * 4;
                dstX = 0;
            }
            if (dstX >= Width || copyBytes <= 0) continue;
            if (dstX + copyBytes / 4 > Width) copyBytes = (Width - dstX) * 4;
            if (copyBytes <= 0) continue;

            Buffer.BlockCopy(frame, srcOffset, _canvas, (dstY * Width + dstX) * 4, copyBytes);
        }
    }

    public override void Dispose()
    {
        foreach (var p in _parts) { try { p.Source.Dispose(); } catch { } }
        _parts.Clear();
        try { _scratch.Dispose(); } catch { }
    }
}
