using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace VideoDecodeTool.Views;

/// <summary>
/// 底部时间刻度条：把一串「整秒时刻」按图表自己的 X 映射画成 m:ss 文字。
/// </summary>
/// <remarks>
/// <para>
/// 刻意**不用 LiveCharts 的坐标轴**来画刻度。只要轴的 <c>LabelsPaint</c> 非 null，
/// 引擎就会为首末刻度文字预留左右余量：实测同一宽度的图带刻度后绘图区会窄约 20px
/// （1179.67 vs 1200），刻度线于是与下面的柱子越靠右错得越多；
/// 窗口左端从负时间滑到正时间时那个余量还会左右变化，刻度会跟着漂。
/// </para>
/// <para>
/// 位置改由 View 用图表自己的「数据 → 像素」换算下发（与播放头同一套 <c>ScaleDataToPixels</c>），
/// 因此刻度与数据列严格对齐，也不占用任何轴的纵向空间。
/// 顺带绕开另一个坑：LiveCharts 在绘图区高度不足时整张图都不画 ——
/// 实测 36px 以下的刻度条上轴刻度完全不出现（早先 15px 的旧刻度条因此一直是空的）。
/// </para>
/// </remarks>
public sealed class TimeRuler : FrameworkElement
{
    /// <summary>刻度文字画刷：与旧版 LiveCharts 轴刻度用的灰色一致。</summary>
    private static readonly Brush LabelBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x99));

    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Consolas, Microsoft YaHei UI, Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);

    /// <summary>时刻字号。与播放头时间读数（<c>PlayheadReadout</c>）同为 11，二者落在同一行时视觉一致。</summary>
    private const double LabelFontSize = 11;

    /// <summary>本帧要画的刻度：每项是「相对本控件左边缘的像素 X」+「已格式化文字」。</summary>
    private readonly List<(double X, string Label)> _ticks = [];

    /// <summary>按文字内容缓存排版结果（同一个时刻的标签每帧都在重复使用）。</summary>
    private readonly Dictionary<string, FormattedText> _textCache = [];

    private double _cachedPixelsPerDip;

    /// <summary>由 View 下发本帧的刻度集合；与上一帧相同时不触发重绘。</summary>
    public void SetTicks(List<(double X, string Label)> ticks)
    {
        if (TicksEqual(ticks))
        {
            return;
        }

        _ticks.Clear();
        _ticks.AddRange(ticks);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_ticks.Count == 0)
        {
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (Math.Abs(pixelsPerDip - _cachedPixelsPerDip) > 1e-6)
        {
            _textCache.Clear();
            _cachedPixelsPerDip = pixelsPerDip;
        }

        foreach (var (x, label) in _ticks)
        {
            var text = ResolveText(label, pixelsPerDip);
            drawingContext.DrawText(
                text,
                new Point(x - text.Width / 2, (ActualHeight - text.Height) / 2));
        }
    }

    /// <summary>比较新旧刻度是否一致：位置容差取半个像素，避免亚像素抖动引起无谓重绘。</summary>
    private bool TicksEqual(List<(double X, string Label)> ticks)
    {
        if (ticks.Count != _ticks.Count)
        {
            return false;
        }

        for (var i = 0; i < ticks.Count; i++)
        {
            if (ticks[i].Label != _ticks[i].Label || Math.Abs(ticks[i].X - _ticks[i].X) > 0.5)
            {
                return false;
            }
        }

        return true;
    }

    private FormattedText ResolveText(string label, double pixelsPerDip)
    {
        if (_textCache.TryGetValue(label, out var cached))
        {
            return cached;
        }

        var text = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            LabelFontSize,
            LabelBrush,
            pixelsPerDip);

        _textCache[label] = text;
        return text;
    }
}
