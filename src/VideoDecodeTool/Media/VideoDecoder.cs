using FFmpeg.AutoGen;
using VideoDecodeTool.Interop;

namespace VideoDecodeTool.Media;

/// <summary>
/// 视频解码器封装：负责创建 <see cref="AVCodecContext"/>、投喂包并取回 <see cref="AVFrame"/>。
/// </summary>
internal sealed unsafe class VideoDecoder : IDisposable
{
    private AVCodecContext* _codec;
    private AVFrame* _frame;
    private bool _disposed;

    public VideoDecoder(AVStream* stream, bool exportMotionVectors, bool captureFrameQp = true)
    {
        var codecId = stream->codecpar->codec_id;
        var codec = ffmpeg.avcodec_find_decoder(codecId);
        if (codec == null)
        {
            throw new FfmpegException($"没有可用的解码器：{ffmpeg.avcodec_get_name(codecId)}");
        }

        _codec = ffmpeg.avcodec_alloc_context3(codec);
        ((nint)_codec).ThrowIfNull("avcodec_alloc_context3");

        try
        {
            ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar)
                .ThrowIfError("avcodec_parameters_to_context");

            // 让解码器输出的 frame->pts 使用流时间基，便于音视频对齐
            _codec->pkt_timebase = stream->time_base;

            if (exportMotionVectors)
            {
                // FFmpeg 6.0 中 mv 导出由 export_side_data 控制；同时保留旧标志以兼容不同构建
                _codec->export_side_data |= FfmpegNative.AV_CODEC_EXPORT_DATA_MVS;
                _codec->flags2 |= ffmpeg.AV_CODEC_FLAG2_EXPORT_MVS;
            }

            // thread_count = 0 表示由 FFmpeg 按 CPU 核数自动决定并行度
            _codec->thread_count = 0;

            if (captureFrameQp && codecId == AVCodecID.AV_CODEC_ID_H264)
            {
                // 打开逐宏块 QP 调试输出（H.264 解码器不填 AVFrame.quality，
                // 这是唯一能拿到逐帧 QP 的途径，详见 FrameQpCollector 的说明）
                _codec->debug |= ffmpeg.FF_DEBUG_QP;

                // 帧级并行会让多帧的宏块日志交错、无法按帧切分，改用片级并行
                _codec->thread_type = ffmpeg.FF_THREAD_SLICE;

                var macroblocks = ((stream->codecpar->width + 15) / 16) * ((stream->codecpar->height + 15) / 16);
                FrameQpCollector.Enable(macroblocks);
            }

            ffmpeg.avcodec_open2(_codec, codec, null).ThrowIfError("avcodec_open2(视频)");
        }
        catch
        {
            Dispose();
            throw;
        }

        _frame = ffmpeg.av_frame_alloc();
        ((nint)_frame).ThrowIfNull("av_frame_alloc(视频)");

        CodecName = ffmpeg.avcodec_get_name(codecId);
        TimeBase = stream->time_base;
    }

    /// <summary>解码器名称。</summary>
    public string CodecName { get; }

    /// <summary>流时间基。</summary>
    public AVRational TimeBase { get; }

    /// <summary>解码器上下文中的像素格式（软件格式）。</summary>
    public AVPixelFormat PixelFormat => _codec->pix_fmt;

    /// <summary>解码输出帧复用的 <see cref="AVFrame"/>（每次 receive 前由 FFmpeg unref）。</summary>
    public AVFrame* Frame => _frame;

    /// <summary>投喂一个压缩包；返回 AVERROR(EAGAIN) 表示需要先取走已解码帧。</summary>
    public int SendPacket(AVPacket* packet) => ffmpeg.avcodec_send_packet(_codec, packet);

    /// <summary>取出一帧；返回 0 成功、AVERROR(EAGAIN) 无更多帧、AVERROR_EOF 流结束。</summary>
    public int ReceiveFrame() => ffmpeg.avcodec_receive_frame(_codec, _frame);

    /// <summary>清空解码器内部缓冲（seek / 流结束冲刷时使用）。</summary>
    public void Flush() => ffmpeg.avcodec_flush_buffers(_codec);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 注意：托管类的指针字段无法直接取地址（CS0212），必须先复制到局部变量
        if (_frame != null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }

        if (_codec != null)
        {
            var codec = _codec;
            ffmpeg.avcodec_free_context(&codec);
            _codec = null;
        }
    }
}
