using SkiaSharp;
using VideoDecodeTool.Media;

namespace VideoDecodeTool.Rendering;

/// <summary>
/// 视频帧渲染器：把解码得到的 BGRA 缓冲绘制到 Skia 画布，并叠加编码参数层。
/// </summary>
public static class VideoFrameRenderer
{
    /// <summary>缩放采样策略：双线性 + 三线性 mipmap，兼顾缩小画质与速度。</summary>
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>是否显示“无信号”占位文本的字体缓存。</summary>
    /// <remarks>
    /// ⚠ 必须用带中日韩字形的字体：之前用的是 Consolas —— 它没有中文字形，
    /// 于是「正在缓冲…（等待首帧）」「已停止播放」这类占位文案会整段渲染成一排「?」
    /// （实测反馈：未播放时预览窗口里就是几个问号）。
    /// 叠加层的文字全是 ASCII（I/P/B、fwd/bwd、数字），不受此处影响。
    /// </remarks>
    private static readonly SKTypeface PlaceholderTypeface = ResolvePlaceholderTypeface();

    private static SKTypeface ResolvePlaceholderTypeface()
    {
        // 按「系统中文字体 → 字形匹配 → 默认」回退
        foreach (var family in new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "SimSun" })
        {
            var typeface = SKFontManager.Default.MatchFamily(family);

            if (typeface is not null)
            {
                return typeface;
            }
        }

        return SKFontManager.Default.MatchCharacter('永') ?? SKTypeface.Default;
    }

    /// <summary>
    /// 渲染一帧。画布为预览面板的物理像素尺寸。
    /// </summary>
    /// <remarks>unsafe：需要读取解码帧的非托管像素指针。</remarks>
    public static unsafe void Render(
        SKCanvas canvas,
        SKImageInfo target,
        DecodedFrame? frame,
        OverlayRenderer overlay,
        string placeholder)
    {
        canvas.Clear(OverlayPalette.Background);

        if (frame is null)
        {
            DrawPlaceholder(canvas, target, placeholder);
            return;
        }

        var destination = ComputeLetterbox(target.Width, target.Height, frame.Width, frame.Height);

        // 像素缓冲为非托管内存且在本帧存活期内地址稳定，SKImage 不接管所有权，开销极低
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var image = SKImage.FromPixels(info, (nint)frame.PixelPointer, frame.Stride);

        if (image is null)
        {
            DrawPlaceholder(canvas, target, "创建 SKImage 失败");
            return;
        }

        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(image, destination, Sampling, paint);

        overlay.Draw(canvas, destination, frame);
    }

    /// <summary>计算保持宽高比的居中显示矩形（letterbox）。</summary>
    public static SKRect ComputeLetterbox(int canvasWidth, int canvasHeight, int videoWidth, int videoHeight)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0 || videoWidth <= 0 || videoHeight <= 0)
        {
            return SKRect.Empty;
        }

        var scale = Math.Min(canvasWidth / (double)videoWidth, canvasHeight / (double)videoHeight);
        var width = (float)(videoWidth * scale);
        var height = (float)(videoHeight * scale);

        return SKRect.Create(
            (canvasWidth - width) / 2f,
            (canvasHeight - height) / 2f,
            width,
            height);
    }

    private static void DrawPlaceholder(SKCanvas canvas, SKImageInfo target, string text)
    {
        using var font = new SKFont(PlaceholderTypeface, 15f, 1f, 0f);
        using var paint = new SKPaint { IsAntialias = true, Color = OverlayPalette.TextDim };

        var width = font.MeasureText(text, paint);
        canvas.DrawText(
            text,
            (target.Width - width) / 2f,
            target.Height / 2f,
            SKTextAlign.Left,
            font,
            paint);
    }
}
