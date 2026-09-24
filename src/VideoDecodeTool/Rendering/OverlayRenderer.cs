using SkiaSharp;
using VideoDecodeTool.Media;
using VideoDecodeTool.Models;

namespace VideoDecodeTool.Rendering;

/// <summary>
/// 参数叠加层：运动矢量箭头、帧类型徽标、QP、GOP / 重排等编码参数。
/// </summary>
/// <remarks>
/// <para>性能考量：单帧运动矢量可达上万个，逐个 <c>DrawLine</c> 会产生海量原生调用。
/// 因此这里把同方向的矢量合并到两条复用的 <see cref="SKPath"/> 中，每帧只提交 2 次绘制调用。</para>
/// <para>SkiaSharp 3.x 已弃用 <c>SKPaint.TextSize</c> 等文本属性，全部改用 <see cref="SKFont"/>。</para>
/// </remarks>
public sealed class OverlayRenderer : IDisposable
{
    private readonly SKTypeface _typeface;
    private readonly SKFont _hudFont;
    private readonly SKFont _badgeFont;
    private readonly SKFont _smallFont;

    // ⚠ 必须显式给颜色：SKPaint 的默认颜色是「不透明黑」，
    // 叠在深色底板上几乎看不见字（实测反馈：右下角图例的文字看不清）。
    private readonly SKPaint _textPaint = new() { IsAntialias = true, Color = OverlayPalette.Text };
    private readonly SKPaint _dimTextPaint = new() { IsAntialias = true, Color = OverlayPalette.TextDim };
    private readonly SKPaint _chipPaint = new() { IsAntialias = true, Color = OverlayPalette.ChipBackground };
    private readonly SKPaint _borderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = OverlayPalette.Border };
    private readonly SKPaint _forwardPaint = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = OverlayPalette.ForwardVector.WithAlpha(150) };
    private readonly SKPaint _backwardPaint = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1, Color = OverlayPalette.BackwardVector.WithAlpha(150) };

    private readonly SKPath _forwardPath = new();
    private readonly SKPath _backwardPath = new();

    private bool _disposed;

    public OverlayRenderer()
    {
        _typeface = SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default;

        // SkiaSharp 3.x 只保留四参数构造函数
        _hudFont = new SKFont(_typeface, 13f, 1f, 0f);
        // 徽标字号：界面反馈「左下角 I/P/B 太小」，由 30 提到 38
        _badgeFont = new SKFont(_typeface, 38f, 1f, 0f);
        _smallFont = new SKFont(_typeface, 11f, 1f, 0f);
    }

    /// <summary>叠加层开关。</summary>
    public OverlayOptions Options { get; } = new();

    /// <summary>绘制叠加层。所有坐标基于目标画布的物理像素。</summary>
    public void Draw(SKCanvas canvas, SKRect videoRect, DecodedFrame? frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (frame is null || videoRect.Width <= 1 || videoRect.Height <= 1)
        {
            return;
        }

        if (Options.ShowMotionVectors)
        {
            DrawMotionVectors(canvas, videoRect, frame);
        }

        if (Options.ShowHeader)
        {
            DrawHeader(canvas, videoRect, frame);
        }

        if (Options.ShowFrameType)
        {
            DrawFrameBadge(canvas, videoRect, frame.Info);
        }

        // ⚠ 不能再附加「本帧有运动矢量」这个条件：I 帧（以及抽取不到矢量的帧）没有运动矢量，
        // 图例会在这些帧上整块消失、下一帧又出现 —— 表现就是图例一直在闪。
        // 图例是「颜色说明」，与当前帧有没有矢量无关：只要在画运动矢量就应该一直显示。
        if (Options.ShowLegend && Options.ShowMotionVectors)
        {
            DrawLegend(canvas, videoRect);
        }
    }

    /// <summary>绘制运动矢量箭头（按方向合并为两条路径）。</summary>
    private void DrawMotionVectors(SKCanvas canvas, SKRect videoRect, DecodedFrame frame)
    {
        var count = frame.VectorCount;
        if (count <= 0)
        {
            return;
        }

        _forwardPath.Reset();
        _backwardPath.Reset();

        // 运动矢量坐标定义在「源视频像素」坐标系下，与预览缓冲尺寸无关，故用源分辨率换算
        var scaleX = videoRect.Width / frame.SourceWidth;
        var scaleY = videoRect.Height / frame.SourceHeight;
        var vectorScale = (float)Options.VectorScale;

        var step = count > Options.MaxDrawnVectors
            ? (int)Math.Ceiling(count / (double)Math.Max(1, Options.MaxDrawnVectors))
            : 1;

        var drawn = 0;

        for (var i = 0; i < count; i += step)
        {
            var mv = frame.Vectors[i];

            // 零位移矢量不绘制（画上去只是噪声）
            if (mv.Dx == 0 && mv.Dy == 0)
            {
                continue;
            }

            var startX = videoRect.Left + mv.CenterX * scaleX;
            var startY = videoRect.Top + mv.CenterY * scaleY;
            var endX = startX + mv.Dx * scaleX * vectorScale;
            var endY = startY + mv.Dy * scaleY * vectorScale;

            var path = mv.Direction < 0 ? _forwardPath : _backwardPath;
            path.MoveTo(startX, startY);
            path.LineTo(endX, endY);
            drawn++;
        }

        if (drawn == 0)
        {
            return;
        }

        canvas.DrawPath(_forwardPath, _forwardPaint);
        canvas.DrawPath(_backwardPath, _backwardPaint);
    }

    /// <summary>顶部参数行：源分辨率 → 目标分辨率 + 运动矢量计数（fwd / bwd）。</summary>
    private void DrawHeader(SKCanvas canvas, SKRect videoRect, DecodedFrame frame)
    {
        var info = frame.Info;

        // ⚠ 这里**不能**用 videoRect：那是画面在预览面板里的显示尺寸（例如 484x484），
        // 与媒体分辨率无关 —— 之前就是这么画的，于是「目标分辨率 → 一个固定值」（实测反馈）。
        // 正确语义：源片分辨率 → 目标（= 当前解码帧的）分辨率；QP 已按反馈移除。
        var sourceWidth = Options.SourceWidth > 0 ? Options.SourceWidth : info.Width;
        var sourceHeight = Options.SourceHeight > 0 ? Options.SourceHeight : info.Height;
        var text =
            $"{sourceWidth}x{sourceHeight} -> {info.Width}x{info.Height}   " +
            $"MV {info.VectorCount} (fwd {info.ForwardVectors} / bwd {info.BackwardVectors})";

        var width = _hudFont.MeasureText(text, _textPaint);
        var padding = 6f;
        var rect = SKRect.Create(videoRect.Left + 8, videoRect.Top + 8, width + padding * 2, 22);

        canvas.DrawRect(rect, _chipPaint);
        canvas.DrawRect(rect, _borderPaint);
        canvas.DrawText(text, rect.Left + padding, rect.Bottom - 6, SKTextAlign.Left, _hudFont, _textPaint);
    }

    /// <summary>
    /// 左下角帧类型徽标：底板与左上角参数行同一套（半透明黑底 + 描边），
    /// 帧类型只靠<b>字母颜色</b>区分（I 红 / P 蓝 / B 绿）。
    /// </summary>
    /// <remarks>
    /// 方块右侧原先还挂着一列编码参数（帧号 / 时长 / 包大小 / GOP + 重排 / flags），
    /// 这些信息右侧信息面板里本来就有，叠在画面上只是白挡一块画面，故已移除。
    /// </remarks>
    private void DrawFrameBadge(SKCanvas canvas, SKRect videoRect, FrameInfo info)
    {
        // 底板同步放大（44 → 56），与 38px 的字号配比保持一致
        const float chipSize = 56f;
        var chipRect = SKRect.Create(videoRect.Left + 10, videoRect.Bottom - chipSize - 10, chipSize, chipSize);

        // 复用左上角那支底板画笔与描边笔：同一块画面上两处 HUD 的底色、
        // 透明度、描边粗细完全一致（此前这里另配了 59% 不透明的色块，比左上角更透）
        canvas.DrawRect(chipRect, _chipPaint);
        canvas.DrawRect(chipRect, _borderPaint);

        // 底板不着色：保持中性黑底，帧类型信息全部由字母颜色承载
        using var letterPaint = new SKPaint
        {
            IsAntialias = true,
            Color = OverlayPalette.ForFrameKind(info.Kind),
        };

        var letter = info.TypeChar.ToString();
        var letterWidth = _badgeFont.MeasureText(letter, letterPaint);

        // 基线按字体度量算，而不是写死偏移：字号以后再调也会自动居中
        var metrics = _badgeFont.Metrics;
        var baseline = chipRect.MidY - (metrics.Ascent + metrics.Descent) / 2f;

        canvas.DrawText(
            letter,
            chipRect.MidX - letterWidth / 2f,
            baseline,
            SKTextAlign.Left,
            _badgeFont,
            letterPaint);
    }

    /// <summary>右下角 fwd / bwd 图例（只有文字说明，不显示矢量条数）。</summary>
    private void DrawLegend(SKCanvas canvas, SKRect videoRect)
    {
        const float square = 9f;
        const float gap = 5f;

        // 图例只负责说明「颜色代表哪个方向的矢量」，
        // 具体条数在顶部参数行里已经给过，这里不再重复。
        // 用简写 Fwd / Bwd（与顶部参数行的 fwd / bwd 写法一致），底板可以窄一截、不挡画面。
        const string forwardText = "Fwd";
        const string backwardText = "Bwd";
        var forwardWidth = _smallFont.MeasureText(forwardText, _textPaint);
        var backwardWidth = _smallFont.MeasureText(backwardText, _textPaint);
        var totalWidth = square + gap + forwardWidth + 16 + square + gap + backwardWidth;

        var rect = SKRect.Create(
            videoRect.Right - totalWidth - 20,
            videoRect.Bottom - 32,
            totalWidth + 14,
            24);

        canvas.DrawRect(rect, _chipPaint);
        canvas.DrawRect(rect, _borderPaint);

        var x = rect.Left + 7;
        var squareTop = rect.MidY - square / 2f;

        using (var forwardPaint = new SKPaint { IsAntialias = true, Color = OverlayPalette.ForwardVector })
        using (var backwardPaint = new SKPaint { IsAntialias = true, Color = OverlayPalette.BackwardVector })
        {
            canvas.DrawRect(SKRect.Create(x, squareTop, square, square), forwardPaint);
            x += square + gap;
            canvas.DrawText(forwardText, x, rect.Bottom - 8, SKTextAlign.Left, _smallFont, _textPaint);
            x += forwardWidth + 16;

            canvas.DrawRect(SKRect.Create(x, squareTop, square, square), backwardPaint);
            x += square + gap;
            // 两个标签用同一支亮色画笔：先前 forward 用亮色、backward 用暗色，
            // 同一个图例里两块文字亮度不一致，看着就像「有一个是禁用状态」
            canvas.DrawText(backwardText, x, rect.Bottom - 8, SKTextAlign.Left, _smallFont, _textPaint);
        }
    }

    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.fff");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _forwardPath.Dispose();
        _backwardPath.Dispose();
        _textPaint.Dispose();
        _dimTextPaint.Dispose();
        _chipPaint.Dispose();
        _borderPaint.Dispose();
        _forwardPaint.Dispose();
        _backwardPaint.Dispose();
        _hudFont.Dispose();
        _badgeFont.Dispose();
        _smallFont.Dispose();
        _typeface.Dispose();
    }
}
