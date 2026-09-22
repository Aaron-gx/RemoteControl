using System.Windows.Controls;
using System.Windows.Input;

namespace LicenseKeygen;

/// <summary>
/// 滚轮"跟手"的 ScrollViewer（「使用说明」页用它，里面装 FlowDocumentScrollViewer）。
///
/// 为什么要自己写：FlowDocumentScrollViewer 内部那个 ScrollViewer 是按**逻辑单位**滚的，
/// 一次滚轮直接跳一整段（标题/表格/代码块各算一个单位），长文档里一次跳掉大半屏，
/// 看着像"滚不动"或者"一下蹦很远"。这里在隧道事件里把滚轮接管过来，
/// 按**像素**滚，并把默认的 3 行提到 4 倍，滚起来是连续的、速度也合适。
/// </summary>
public sealed class FastScrollViewer : ScrollViewer
{
    /// <summary>滚轮倍数：1 ≈ WPF 默认的 3 行（约 48px），4 ≈ 一档约 190px —— 长文档里连续翻页不费劲</summary>
    private const double WheelFactor = 4;

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (e.Handled) return;

        // 已经滚到顶/底了就让事件照常冒泡，别把父级（窗口）的滚动也吃掉
        bool canScroll = e.Delta > 0 ? VerticalOffset > 0
                                     : VerticalOffset < ScrollableHeight;
        if (canScroll)
        {
            ScrollToVerticalOffset(VerticalOffset - e.Delta / 120.0 * WheelFactor * 48);
            e.Handled = true;
        }
        base.OnPreviewMouseWheel(e);
    }
}
