using FFmpeg.AutoGen;
using VideoDecodeTool.Interop;

namespace VideoDecodeTool.Media;

/// <summary>
/// 视频帧像素格式转换器（默认 YUV420P / NV12 → BGRA），内部复用 <see cref="SwsContext"/>。
/// </summary>
/// <remarks>
/// 非线程安全：每个解码线程应持有独立实例。
/// </remarks>
internal sealed unsafe class FrameConverter : IDisposable
{
    // SWS_BILINEAR 在 4K 实时转换下速度与质量的平衡最佳；可用 SWS_FAST_BILINEAR 进一步提速
    private const int ScalingAlgorithm = ffmpeg.SWS_BILINEAR;

    private SwsContext* _scaler;
    private int _sourceWidth;
    private int _sourceHeight;
    private AVPixelFormat _sourceFormat;
    private int _targetWidth;
    private int _targetHeight;
    private AVPixelFormat _targetFormat;
    private bool _disposed;

    // sws_scale 的原生签名使用数组（byte*[] / int[]），此处按帧复用，避免每帧分配
    private readonly byte*[] _sourceData = new byte*[4];
    private readonly int[] _sourceStride = new int[4];
    private readonly byte*[] _targetData = new byte*[4];
    private readonly int[] _targetStride = new int[4];

    /// <summary>确保转换上下文与给定参数匹配，参数变化时自动重建。</summary>
    public void Ensure(
        int sourceWidth,
        int sourceHeight,
        AVPixelFormat sourceFormat,
        int targetWidth,
        int targetHeight,
        AVPixelFormat targetFormat)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_scaler != null
            && _sourceWidth == sourceWidth
            && _sourceHeight == sourceHeight
            && _sourceFormat == sourceFormat
            && _targetWidth == targetWidth
            && _targetHeight == targetHeight
            && _targetFormat == targetFormat)
        {
            return;
        }

        Release();

        _scaler = ffmpeg.sws_getContext(
            sourceWidth, sourceHeight, sourceFormat,
            targetWidth, targetHeight, targetFormat,
            ScalingAlgorithm, null, null, null);

        if (_scaler == null)
        {
            throw new FfmpegException(
                $"sws_getContext 失败：{sourceWidth}x{sourceHeight} {sourceFormat} -> {targetWidth}x{targetHeight} {targetFormat}");
        }

        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _sourceFormat = sourceFormat;
        _targetWidth = targetWidth;
        _targetHeight = targetHeight;
        _targetFormat = targetFormat;
    }

    /// <summary>
    /// 把一帧转换到目标缓冲区。目标缓冲区需至少 <paramref name="targetStride"/> × 高度 字节。
    /// </summary>
    public void Convert(AVFrame* frame, byte* target, int targetStride)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_scaler == null)
        {
            throw new InvalidOperationException("请先调用 Ensure 初始化转换参数");
        }

        // 源平面（YUV 最多 4 个平面；硬件像素格式在进入本方法前已被拒绝）
        // 注意：AutoGen 生成的 byte_ptrArray8 / int_array8 索引器形参类型为 uint
        for (uint i = 0; i < _sourceData.Length; i++)
        {
            var slot = (int)i;
            _sourceData[slot] = frame->data[i];
            _sourceStride[slot] = frame->linesize[i];
        }

        // 目标只用到第 0 个平面（BGRA 为打包格式）
        _targetData[0] = target;
        _targetStride[0] = targetStride;

        ffmpeg.sws_scale(
            _scaler,
            _sourceData, _sourceStride,
            0, frame->height,
            _targetData, _targetStride);
    }

    private void Release()
    {
        if (_scaler != null)
        {
            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
    }
}
