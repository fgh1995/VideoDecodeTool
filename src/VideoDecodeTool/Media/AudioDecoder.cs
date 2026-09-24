using FFmpeg.AutoGen;
using VideoDecodeTool.Interop;

namespace VideoDecodeTool.Media;

/// <summary>
/// 音频解码器封装：解码后统一重采样为 <b>16bit / 48000Hz / 立体声</b>，
/// 直接匹配 NAudio <c>BufferedWaveProvider</c> 的输出格式，避免播放端再做转换。
/// </summary>
internal sealed unsafe class AudioDecoder : IDisposable
{
    /// <summary>输出采样率。</summary>
    public const int OutputSampleRate = 48000;

    /// <summary>输出声道数。</summary>
    public const int OutputChannels = 2;

    /// <summary>输出样本格式：16bit 交错。</summary>
    public const AVSampleFormat OutputFormat = AVSampleFormat.AV_SAMPLE_FMT_S16;

    private AVCodecContext* _codec;
    private AVFrame* _frame;
    private SwrContext* _resampler;
    private byte[] _sampleBuffer = new byte[OutputSampleRate * OutputChannels * 2];
    private bool _disposed;
    private bool _layoutInitialized;

    public AudioDecoder(AVStream* stream)
    {
        var codecId = stream->codecpar->codec_id;
        var codec = ffmpeg.avcodec_find_decoder(codecId);
        if (codec == null)
        {
            throw new FfmpegException($"没有可用的音频解码器：{ffmpeg.avcodec_get_name(codecId)}");
        }

        _codec = ffmpeg.avcodec_alloc_context3(codec);
        ((nint)_codec).ThrowIfNull("avcodec_alloc_context3(音频)");

        try
        {
            ffmpeg.avcodec_parameters_to_context(_codec, stream->codecpar)
                .ThrowIfError("avcodec_parameters_to_context(音频)");
            _codec->pkt_timebase = stream->time_base;
            ffmpeg.avcodec_open2(_codec, codec, null).ThrowIfError("avcodec_open2(音频)");
        }
        catch
        {
            Dispose();
            throw;
        }

        _frame = ffmpeg.av_frame_alloc();
        ((nint)_frame).ThrowIfNull("av_frame_alloc(音频)");

        CodecName = ffmpeg.avcodec_get_name(codecId);
        SourceSampleRate = _codec->sample_rate;
        SourceChannels = _codec->ch_layout.nb_channels;
        TimeBase = stream->time_base;
    }

    public string CodecName { get; }

    public AVRational TimeBase { get; }

    public int SourceSampleRate { get; }

    public int SourceChannels { get; }

    /// <summary>投喂一个音频包。</summary>
    public int SendPacket(AVPacket* packet) => ffmpeg.avcodec_send_packet(_codec, packet);

    /// <summary>解码输出帧复用的 <see cref="AVFrame"/>（每次 receive 前由 FFmpeg unref）。</summary>
    public AVFrame* Frame => _frame;

    /// <summary>取出一帧解码后的音频。</summary>
    public int ReceiveFrame() => ffmpeg.avcodec_receive_frame(_codec, _frame);

    /// <summary>
    /// 把最近一次 <see cref="ReceiveFrame"/> 得到的音频帧重采样为输出格式。
    /// </summary>
    /// <param name="buffer">输出缓冲区（内部复用数组）。</param>
    /// <param name="byteCount">有效字节数。</param>
    /// <param name="timestampSeconds">该帧第一个样本的时间戳（秒）。</param>
    public void Resample(out byte[] buffer, out int byteCount, out double timestampSeconds)
    {
        EnsureResampler();

        var frame = _frame;

        // 输出样本数：源样本数 + 重采样器内部残留延迟，按比例换算到目标采样率并向上取整
        var delay = ffmpeg.swr_get_delay(_resampler, SourceSampleRate);
        var outputSamples = (int)ffmpeg.av_rescale_rnd(
            delay + frame->nb_samples,
            OutputSampleRate,
            SourceSampleRate,
            AVRounding.AV_ROUND_UP);

        var requiredBytes = outputSamples * OutputChannels * sizeof(short);
        if (_sampleBuffer.Length < requiredBytes)
        {
            _sampleBuffer = new byte[requiredBytes];
        }

        byte* outputPlanar = null;
        fixed (byte* output = _sampleBuffer)
        {
            outputPlanar = output;
            var converted = ffmpeg.swr_convert(_resampler, &outputPlanar, outputSamples, frame->extended_data, frame->nb_samples);
            if (converted < 0)
            {
                throw new FfmpegException(FfmpegRuntime.Describe(converted, "swr_convert"));
            }

            byteCount = converted * OutputChannels * sizeof(short);
        }

        buffer = _sampleBuffer;
        timestampSeconds = PtsToSeconds(frame->pts, ResolveTimeBase(frame->time_base, TimeBase));
    }

    /// <summary>
    /// 选取可用的时间基：优先使用帧自带时间基，退化（分子或分母为 0）时回退到流时间基。
    /// </summary>
    /// <remarks>
    /// 实测发现：某些情况下 <c>AVFrame.time_base</c> 为 0/0（解码器未从 pkt_timebase 继承），
    /// 若直接用它换算会导致所有时间戳变成 NaN，进而使音视频同步彻底失效，因此必须回退。
    /// </remarks>
    public static AVRational ResolveTimeBase(AVRational preferred, AVRational fallback) =>
        preferred is { num: > 0, den: > 0 } ? preferred : fallback;

    /// <summary>把时间基下的 pts 换算为秒；无法换算时返回 NaN。</summary>
    public static double PtsToSeconds(long pts, AVRational timeBase)
    {
        if (pts == ffmpeg.AV_NOPTS_VALUE || timeBase.den == 0)
        {
            return double.NaN;
        }

        return pts * (timeBase.num / (double)timeBase.den);
    }

    private void EnsureResampler()
    {
        if (_resampler != null && _layoutInitialized)
        {
            return;
        }

        if (_resampler != null)
        {
            FreeResampler();
        }

        AVChannelLayout outputLayout;
        ffmpeg.av_channel_layout_default(&outputLayout, OutputChannels);

        SwrContext* resampler = null;
        var error = ffmpeg.swr_alloc_set_opts2(
            &resampler,
            &outputLayout, OutputFormat, OutputSampleRate,
            &_codec->ch_layout, _codec->sample_fmt, _codec->sample_rate,
            0, null);

        error.ThrowIfError("swr_alloc_set_opts2");

        _resampler = resampler;
        ffmpeg.swr_init(_resampler).ThrowIfError("swr_init");
        _layoutInitialized = true;
    }

    /// <summary>清空解码器与重采样器内部缓冲。</summary>
    public void Flush()
    {
        ffmpeg.avcodec_flush_buffers(_codec);
        FreeResampler();
        _layoutInitialized = false;
    }

    /// <summary>释放重采样器（托管类的指针字段无法直接取地址，需先复制到局部变量）。</summary>
    private void FreeResampler()
    {
        if (_resampler == null)
        {
            return;
        }

        var resampler = _resampler;
        ffmpeg.swr_free(&resampler);
        _resampler = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        FreeResampler();

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
