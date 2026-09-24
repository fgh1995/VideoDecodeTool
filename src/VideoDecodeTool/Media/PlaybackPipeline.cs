using FFmpeg.AutoGen;
using System.IO;
using VideoDecodeTool.Interop;
using VideoDecodeTool.Models;

namespace VideoDecodeTool.Media;

/// <summary>播放管线选项。</summary>
public sealed record PlaybackOptions
{
    /// <summary>是否请求解码器导出运动矢量（H.264/MPEG-2 等支持）。</summary>
    public bool ExportMotionVectors { get; init; } = true;

    /// <summary>
    /// 是否采集逐帧 QP。
    /// </summary>
    /// <remarks>H.264 需要打开解码器的宏块 QP 调试输出，代价见 <see cref="FrameQpCollector"/>。</remarks>
    public bool CaptureFrameQp { get; init; } = true;

    /// <summary>是否播放音频。</summary>
    public bool EnableAudio { get; init; } = true;

    /// <summary>
    /// 视频帧队列上限。需与 <c>MaxLeadSeconds</c> 匹配：
    /// 队列能容纳的媒体时长应当 ≥ 解码超前量，否则队列里只剩「未来帧」。
    /// 140 帧 @23.976fps ≈ 5.84s，可覆盖 <c>MaxLeadSecondsTarget</c> = 5.0s（≈120 帧）。
    /// </summary>
    public int MaxQueuedFrames { get; init; } = 140;

    /// <summary>帧缓冲池容量（会自动放大到至少 MaxQueuedFrames + 2）。</summary>
    public int FramePoolCapacity { get; init; } = 148;
}

/// <summary>
/// 播放管线：解复用 → 解码 → 像素转换 / 运动矢量提取 → 帧队列 + 音频输出。
/// </summary>
/// <remarks>
/// <para><b>线程模型</b></para>
/// <list type="bullet">
/// <item>解复用线程（后台，独占）：<c>av_read_frame</c> 循环，视频包/音频包分别投喂解码器，
/// 解码结果写入帧队列或音频缓冲区。队列满时休眠等待，形成天然背压。</item>
/// <item>UI 线程：按 60Hz 调用 <see cref="TryTakeFrame"/>，依据主时钟取帧、丢帧、渲染。</item>
/// </list>
/// <para><b>数据来源</b></para>
/// 既可以是本地文件（模式 B：源片分析），也可以是转码会话的镜像分片文件
/// （模式 A：边转边播 —— 由 <c>GrowingFileReadStream</c> 按实时跟随正在写入的文件），
/// 上游差异被完全封装在本类内部，UI 层无感知。
/// </remarks>
public sealed unsafe class PlaybackPipeline : IDisposable
{
    /// <summary>
    /// 预览像素缓冲的宽度上限。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 预览面板最宽约 1000px，1080p 源降到 1024 完全不损失观感，
    /// 却能把单帧内存从 8.3MB 降到约 2.4MB，同时降低 sws_scale 的转换耗时。
    /// </para>
    /// <para>
    /// ⚠ 「单帧缓冲大小 × 帧队列上限」就是进程的峰值内存，而队列上限又必须能装下
    /// 「解码超前量」，所以这里与 <see cref="PlaybackOptions.MaxQueuedFrames"/> 是一体两面：
    /// 时间轴想显示更多「未来」，就得付出线性增长的内存。
    /// </para>
    /// </remarks>
    private const int MaxPreviewWidth = 1024;

    /// <summary>
    /// 解码位置允许超前主时钟的目标秒数（实际上限见 <see cref="_maxLeadSeconds"/>）。
    /// </summary>
    /// <remarks>
    /// <para>这个值必须与「帧队列能容纳的媒体时长」相匹配，否则会出严重问题：</para>
    /// <list type="bullet">
    /// <item>超前量过大（例如 3s）而队列只能放 0.25s 时，队列里永远只剩下「未来帧」，
    /// 渲染端需要的「当前帧」会被淘汰策略全部丢弃 —— 实测表现为整段播放只呈现 1 帧；</item>
    /// <item>超前量过小则音频缓冲不足，容易出现爆音。</item>
    /// </list>
    /// <para>
    /// 取 5.0s：时间轴是固定像素柱距，屏幕越宽窗口跨度越长，
    /// 「播放头居中」意味着右侧半屏都是还没播到的未来，超前量必须覆盖它，
    /// 否则时间轴右侧会一直空着（界面上由时间轴跨度按本值封顶来保证：跨度 ≤ 2 × 超前量）。
    /// 实际生效值还会被帧队列容量压下来（见 <see cref="_maxLeadSeconds"/>），
    /// 高帧率片源因此自动降级而不是把内存撑爆。
    /// 音频缓冲同步放大到 8 秒，避免超前量超过缓冲导致丢样本爆音。
    /// </para>
    /// </remarks>
    private const double MaxLeadSecondsTarget = 5.0;

    /// <summary>
    /// 音频缓冲背压阈值（秒）。缓冲容量 8s，留 2s 余量：突发到达的分片音频
    /// 超过该值就暂停解复用，避免溢出丢样本破坏音画同步。
    /// </summary>
    private const double AudioMaxLeadSeconds = 6.0;

    /// <summary>
    /// 实际生效的解码超前量上限（秒）。
    /// </summary>
    /// <remarks>
    /// 时间轴的窗口跨度按它封顶（≤ 2 × 本值），这样「播放头居中」的右半屏永远有数据可画：
    /// 超前量够时窗口就宽（看到更长的未来），超前量被帧队列压小时窗口自动收窄
    /// （柱距变大），而不是在右侧留一条空白。
    /// </remarks>
    public double MaxLeadSeconds => _maxLeadSeconds;

    /// <summary>
    /// 实际生效的解码超前量（秒）= min(目标值, 帧队列能装下的时长)。
    /// </summary>
    /// <remarks>
    /// 队列只能装 <see cref="PlaybackOptions.MaxQueuedFrames"/> 帧，
    /// 若超前量换算出的帧数超过队列容量，队列里就只剩「未来帧」，
    /// 「当前帧」会被淘汰策略丢光（实测表现为整段播放只呈现 1 帧）。
    /// 帧率越高、同样秒数需要的帧数越多，因此必须按帧率折算，不能直接用秒数比较。
    /// </remarks>
    private readonly double _maxLeadSeconds;

    private readonly AVFormatContext* _formatContext;
    private readonly StreamAvioContext? _avioContext;
    private readonly Stream? _ownedStream;
    private readonly VideoDecoder _videoDecoder;
    private readonly AudioDecoder? _audioDecoder;
    private readonly AudioOutput? _audioOutput;
    private readonly FrameConverter _converter = new();
    private readonly FrameBufferPool _framePool;
    private readonly Queue<DecodedFrame> _frameQueue = new();
    private readonly object _frameGate = new();
    private readonly ManualResetEventSlim _resumeEvent = new(true);
    private readonly CancellationTokenSource _cancellation = new();
    private readonly int _mediaVideoStreamIndex;
    private readonly int _mediaAudioStreamIndex;
    private readonly AVRational _videoTimeBase;
    private readonly double _defaultFrameDuration;
    private readonly int _maxQueuedFrames;

    private Thread? _demuxThread;
    private long _frameNumber;
    private long _lastKeyFrameNumber;
    private double _lastFrameTime = double.NaN;
    private int _droppedSinceLastLog;
    private bool _disposed;

    /// <summary>
    /// 音视频时间原点对齐用：首视频包 / 首音频包的时间戳（秒）。
    /// </summary>
    /// <remarks>
    /// 读<b>管道</b>（边转边播）时 avformat 不会应用 MP4 的 <c>elst</c> 编辑列表，
    /// 于是音频时间戳整体比视频晚一个固定偏移（约 3s，正是 AAC 编码器延迟补偿项）；
    /// 读<b>文件</b>时 avformat 会修正，两路原点本就一致。无论哪种来源，这里都按
    /// 首包差值“实测”偏移并补偿进音频时间戳，使两种模式共用同一时间原点。
    /// </remarks>
    private double? _firstVideoPts;
    private double? _firstAudioPts;

    /// <summary>音频时间戳需要叠加的修正量（秒），量出后锁定。</summary>
    private double _audioTimestampOffset;

    /// <summary>偏移是否已锁定并开始补偿。</summary>
    private bool _audioOffsetLocked;

    /// <summary>
    /// 偏移锁定前，解码出的音频先暂存在这里（样本需拷贝，因为解码缓冲会被复用），
    /// 待偏移确定后一次性按修正量入队，避免未校正的时间戳把音频主时钟带偏。
    /// </summary>
    private readonly List<(byte[] Data, int Count, double Timestamp)> _pendingAudio = new();

    /// <summary>
    /// 最近若干压缩包的「显示时间戳 → 解码时间戳」登记表。
    /// </summary>
    /// <remarks>
    /// FFmpeg 6.0 起 <c>AVFrame.pkt_dts</c> 已不再携带真实解码时间戳（实测恒等于 pts），
    /// 重排延迟只能自己算：包送入解码器时登记 <c>pts → dts</c>，
    /// 输出帧再按自己的 pts 反查（解码器缓存导致「送包顺序」与「输出顺序」不一致，
    /// 用 pts 反查可以避免错配）。表大小按最大重排深度留有充分余量。
    /// </remarks>
    private readonly Dictionary<long, long> _packetDtsByPts = new();
    private readonly Queue<long> _packetDtsOrder = new();

    private PlaybackPipeline(
        AVFormatContext* formatContext,
        StreamAvioContext? avioContext,
        Stream? ownedStream,
        PlaybackOptions options)
    {
        _formatContext = formatContext;
        _avioContext = avioContext;
        _ownedStream = ownedStream;
        _maxQueuedFrames = Math.Max(2, options.MaxQueuedFrames);

        // ---- 选择最佳视频流 ----
        _mediaVideoStreamIndex = ffmpeg.av_find_best_stream(
            formatContext, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);

        if (_mediaVideoStreamIndex < 0)
        {
            throw new FfmpegException("输入中没有可解码的视频流");
        }

        var videoStream = formatContext->streams[_mediaVideoStreamIndex];
        _videoDecoder = new VideoDecoder(videoStream, options.ExportMotionVectors, options.CaptureFrameQp);

        // 作为 frame->time_base 退化时的兜底时间基（见 AudioDecoder.ResolveTimeBase 的说明）
        _videoTimeBase = videoStream->time_base;

        var frameRate = ffmpeg.av_q2d(videoStream->avg_frame_rate);
        if (frameRate <= 0.01 || double.IsNaN(frameRate))
        {
            frameRate = ffmpeg.av_q2d(videoStream->r_frame_rate);
        }

        if (frameRate <= 0.01 || double.IsNaN(frameRate) || double.IsInfinity(frameRate))
        {
            frameRate = 25.0;
        }

        FrameRate = frameRate;
        _defaultFrameDuration = 1.0 / frameRate;

        // 超前量的实际上限：不能超过帧队列能装下的时长，否则队列里只剩「未来帧」，
        // 当前帧会被淘汰掉导致画面卡死。
        // ⚠ 余量按「固定帧数」留，不要按比例留：按 20% 留时，60fps 输出只剩 0.5s，
        // 超前量被压到 1.87s，比「播放头居中所需的半窗口」（≈ 绘图区宽/(6×2×帧率) ≈ 2.1s）
        // 还小 —— 于是即便解码完全跟得上，时间轴右侧也必然空出一条。
        // 改成固定留 8 帧：24fps 下 132 帧 ≈ 5.5s，仍被 MaxLeadSecondsTarget（5.0s）截住；
        // 高帧率下则能吃到队列的真实容量（60fps → 2.2s）。
        _maxLeadSeconds = Math.Min(MaxLeadSecondsTarget, (_maxQueuedFrames - 8) / frameRate);

        Video = new VideoStreamInfo
        {
            Width = videoStream->codecpar->width,
            Height = videoStream->codecpar->height,
            FrameRate = frameRate,
            PixelFormat = ffmpeg.av_get_pix_fmt_name((AVPixelFormat)videoStream->codecpar->format) ?? "unknown",
            CodecName = _videoDecoder.CodecName,
        };

        var (previewWidth, previewHeight) = ComputePreviewSize(Video.Width, Video.Height);
        PreviewWidth = previewWidth;
        PreviewHeight = previewHeight;

        // 池容量必须大于队列上限（渲染端还会额外持有 1 帧）
        _framePool = new FrameBufferPool(
            Video.Width,
            Video.Height,
            previewWidth,
            previewHeight,
            Math.Max(options.FramePoolCapacity, _maxQueuedFrames + 2));

        // ---- 可选音频流 ----
        if (options.EnableAudio)
        {
            var audioIndex = ffmpeg.av_find_best_stream(
                formatContext, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, _mediaVideoStreamIndex, null, 0);

            if (audioIndex >= 0)
            {
                try
                {
                    _audioDecoder = new AudioDecoder(formatContext->streams[audioIndex]);
                    _audioOutput = new AudioOutput();
                    _mediaAudioStreamIndex = audioIndex;
                    Audio = new AudioStreamInfo
                    {
                        SampleRate = _audioDecoder.SourceSampleRate,
                        Channels = _audioDecoder.SourceChannels,
                        CodecName = _audioDecoder.CodecName,
                    };
                }
                catch (FfmpegException)
                {
                    // 音频解码器不可用时降级为纯视频播放，不影响主流程
                    _audioDecoder?.Dispose();
                    _audioDecoder = null;
                    _audioOutput?.Dispose();
                    _audioOutput = null;
                    _mediaAudioStreamIndex = -1;
                }
            }
        }

        Clock = new PlaybackClock(_audioOutput is null ? null : () => _audioOutput.PositionSeconds);
        Statistics = new MediaStatistics();
        Charts = new ChartDataBuffer();
    }

    /// <summary>视频流参数。</summary>
    public VideoStreamInfo Video { get; }

    /// <summary>音频流参数；无音频时为 null。</summary>
    public AudioStreamInfo? Audio { get; }

    /// <summary>视频帧率（用于时间轴换算）。</summary>
    public double FrameRate { get; }

    /// <summary>预览像素缓冲宽度（可能小于源分辨率）。</summary>
    public int PreviewWidth { get; }

    /// <summary>预览像素缓冲高度。</summary>
    public int PreviewHeight { get; }

    /// <summary>播放主时钟。</summary>
    public PlaybackClock Clock { get; }

    /// <summary>累计统计。</summary>
    public MediaStatistics Statistics { get; }

    /// <summary>图表数据缓冲。</summary>
    public ChartDataBuffer Charts { get; }

    /// <summary>是否已到达流末尾。</summary>
    public bool IsEndOfStream => _endOfStream;

    /// <summary>当前排队等待渲染的帧数。</summary>
    public int QueuedFrameCount
    {
        get
        {
            lock (_frameGate)
            {
                return _frameQueue.Count;
            }
        }
    }

    /// <summary>解复用线程是否仍在运行。</summary>
    public bool IsRunning => _demuxThread is { IsAlive: true };

    /// <summary>诊断用：音频缓冲中尚未播放的秒数。</summary>
    public double AudioBufferedSeconds => _audioOutput?.BufferedSeconds ?? 0;

    /// <summary>诊断用：音频输出设备的播放状态。</summary>
    public string AudioDeviceState => _audioOutput?.PlaybackState.ToString() ?? "无音频输出";

    /// <summary>音量（0.0 ~ 1.0）。</summary>
    public float Volume
    {
        get => _audioOutput?.Volume ?? 0;
        set
        {
            if (_audioOutput is not null)
            {
                _audioOutput.Volume = value;
            }
        }
    }

    private volatile bool _endOfStream;

    /// <summary>发生不可恢复错误。</summary>
    public event Action<Exception>? ErrorOccurred;

    /// <summary>解复用线程结束（正常 EOF 或异常）。</summary>
    public event Action? Completed;

    private event Action<FrameInfo>? FrameDecodedInternal;

    /// <summary>每解码一帧触发（<b>后台线程</b>），仅供统计/日志使用。</summary>
    public event Action<FrameInfo>? FrameDecoded
    {
        add => FrameDecodedInternal += value;
        remove => FrameDecodedInternal -= value;
    }

    // ------------------------------------------------------------------
    // 打开
    // ------------------------------------------------------------------

    /// <summary>打开本地文件（模式 B：源片分析）。</summary>
    public static PlaybackPipeline OpenFile(string filePath, PlaybackOptions options)
    {
        AVFormatContext* context = null;
        var result = ffmpeg.avformat_open_input(&context, filePath, null, null);
        if (result < 0)
        {
            throw new FfmpegException(FfmpegRuntime.Describe(result, $"无法打开文件 {filePath}"));
        }

        return Create(context, null, null, options);
    }

    /// <summary>
    /// 打开一个只进的字节流（模式 A：转码会话的镜像分片文件，按实时跟随读取）。
    /// 调用方交出流的所有权，管线释放时会一并关闭它（同时用于打断阻塞中的读取）。
    /// </summary>
    public static PlaybackPipeline OpenStream(Stream stream, PlaybackOptions options)
    {
        var avio = new StreamAvioContext(stream);

        try
        {
            var context = ffmpeg.avformat_alloc_context();
            if (context == null)
            {
                throw new FfmpegException("avformat_alloc_context 失败");
            }

            context->pb = avio.Context;
            context->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

            var result = ffmpeg.avformat_open_input(&context, null, null, null);
            if (result < 0)
            {
                // 失败时 FFmpeg 已释放 context 并把指针置空
                throw new FfmpegException(FfmpegRuntime.Describe(result, "无法解析转码输出流"));
            }

            return Create(context, avio, stream, options);
        }
        catch
        {
            avio.Dispose();
            throw;
        }
    }

    private static PlaybackPipeline Create(
        AVFormatContext* context,
        StreamAvioContext? avio,
        Stream? ownedStream,
        PlaybackOptions options)
    {
        try
        {
            // 对管道输入，probe 需要读到足够数据；fMP4 有 moov 前置，通常很快返回
            ffmpeg.avformat_find_stream_info(context, null).ThrowIfError("avformat_find_stream_info");
            return new PlaybackPipeline(context, avio, ownedStream, options);
        }
        catch
        {
            ffmpeg.avformat_close_input(&context);
            avio?.Dispose();
            if (ownedStream is not null)
            {
                try
                {
                    ownedStream.Dispose();
                }
                catch (IOException)
                {
                    // 忽略关闭异常
                }
            }

            throw;
        }
    }

    // ------------------------------------------------------------------
    // 控制
    // ------------------------------------------------------------------

    /// <summary>启动解码线程。</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_demuxThread is not null)
        {
            return;
        }

        _resumeEvent.Set();

        // 音频设备必须在启动时就 Play，否则缓冲只进不出：音频时钟会卡住、视频帧也会被同步逻辑判定为“未到期”
        _audioOutput?.Play();
        Clock.Start();

        var thread = new Thread(DemuxLoop)
        {
            IsBackground = true,
            Name = "Media-Demux",
            Priority = ThreadPriority.AboveNormal,
        };

        _demuxThread = thread;
        thread.Start();
    }

    /// <summary>暂停解码与播放。</summary>
    public void Pause()
    {
        _resumeEvent.Reset();
        Clock.Pause();
        _audioOutput?.Pause();
    }

    /// <summary>继续解码与播放。</summary>
    public void Resume()
    {
        Clock.Resume();
        _audioOutput?.Play();
        _resumeEvent.Set();
    }

    /// <summary>
    /// 按主时钟取一帧用于渲染。
    /// </summary>
    /// <param name="clockSeconds">当前主时钟位置（秒）。</param>
    /// <param name="frame">取出的帧；调用方渲染完成后必须通过 <see cref="ReleaseFrame"/> 归还。</param>
    /// <returns>是否取到帧。</returns>
    public bool TryTakeFrame(double clockSeconds, out DecodedFrame? frame)
    {
        frame = null;

        lock (_frameGate)
        {
            // 1) 丢弃明显滞后的帧（解码快于播放时会发生），避免累积延迟
            while (_frameQueue.Count > 0 && _frameQueue.Peek().Time < clockSeconds - 0.08)
            {
                var late = _frameQueue.Dequeue();
                _framePool.Return(late);
                Statistics.AddDroppedFrame();
                _droppedSinceLastLog++;
            }

            if (_frameQueue.Count == 0)
            {
                return false;
            }

            // 2) 队首帧还没到显示时间则继续等待，由下一次 tick 处理
            var head = _frameQueue.Peek();
            if (head.Time > clockSeconds + 0.002)
            {
                return false;
            }

            // 3) 若下一帧也已到期，则跳过队首只显示最新的，实现“快进式追帧”
            while (_frameQueue.Count > 1 && _frameQueue.ElementAt(1).Time <= clockSeconds + 0.002)
            {
                var skipped = _frameQueue.Dequeue();
                _framePool.Return(skipped);
                Statistics.AddDroppedFrame();
                _droppedSinceLastLog++;
            }

            frame = _frameQueue.Dequeue();
            return true;
        }
    }

    /// <summary>归还渲染完成的帧。</summary>
    public void ReleaseFrame(DecodedFrame? frame) => _framePool.Return(frame);

    /// <summary>最近一次统计周期内被丢弃的帧数（用于界面诊断）。</summary>
    public int ConsumeDroppedFrameCount() => Interlocked.Exchange(ref _droppedSinceLastLog, 0);

    // ------------------------------------------------------------------
    // 解复用循环
    // ------------------------------------------------------------------

    private void DemuxLoop()
    {
        var token = _cancellation.Token;
        var packet = ffmpeg.av_packet_alloc();

        if (packet == null)
        {
            RaiseError(new FfmpegException("av_packet_alloc 失败"));
            Completed?.Invoke();
            return;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                // 暂停时挂起（不做无谓的读取，避免管道堆积）
                _resumeEvent.Wait(token);

                var result = ffmpeg.av_read_frame(_formatContext, packet);

                if (result < 0)
                {
                    if (result == FfmpegNative.AVERROR_EOF_VALUE || result == FfmpegNative.AVERROR_EPIPE)
                    {
                        _endOfStream = true;
                        break;
                    }

                    // 其它负值（如 EIO、EINVAL）在管道场景下同样视为流结束
                    _endOfStream = true;
                    break;
                }

                try
                {
                    if (packet->stream_index == _mediaVideoStreamIndex)
                    {
                        DecodeVideoPacket(packet);
                    }
                    else if (_mediaAudioStreamIndex >= 0 && packet->stream_index == _mediaAudioStreamIndex)
                    {
                        DecodeAudioPacket(packet);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }

                // 两路首包都到齐后锁定音视频原点偏移（边转边播的管道模式靠它修正 elst 偏移）
                TryLockAudioOffset();

                ThrottleWhileTooFarAhead(token);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
        finally
        {
            ffmpeg.av_packet_free(&packet);
            _endOfStream = true;

            // 流已到尾却始终没有音频样本（音频轨异常/无可解码样本）：解除主时钟的音频等待，
            // 否则预缓冲期的冻结会一直保持，画面被永久钉在起点。
            if (_audioOutput is { HasSamples: false })
            {
                Clock.AbandonAudioWait();
            }

            Completed?.Invoke();
        }
    }

    /// <summary>
    /// 背压：解码位置超前主时钟过多时休眠。
    /// </summary>
    /// <remarks>
    /// <para>这里刻意**不**以「视频队列是否满」作为限流条件，原因：</para>
    /// <list type="bullet">
    /// <item>主时钟由音频驱动，若因为视频队列满而阻塞读取，音频就会断流，时钟随之冻结，
    /// 视频队列永远得不到释放 —— 形成死锁（实测已复现）；</item>
    /// <item>改用「媒体时间超前量」限流后，音频始终能持续补充，时钟正常推进，
    /// 而超前量被限制在 <see cref="MaxLeadSeconds"/> 秒内，音频缓冲不会溢出、内存也不会膨胀。</item>
    /// </list>
    /// </remarks>
    private void ThrottleWhileTooFarAhead(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // ⚠ 启动预缓冲期（音频轨已存在但尚无样本入队）必须跳过「视频超前量」限流，
            // 否则会死锁：fMP4 首个碎片常把整段视频样本排在音频之前，若在此按超前量
            // 限流，解复用会在读到音频包之前就被挡住；而主时钟的解冻又恰恰依赖这份音频。
            // 结果是 —— 时钟不动 → 超前量不下降 → 永不读音频 → 时钟永远不动，
            // 实测表现为边转边播在 ~_maxLeadSeconds 处永久卡住、无声音、镜像文件截断。
            // 跳过期间帧队列仍有界（满则淘汰最旧帧），内存不会失控。
            if (!Clock.IsWaitingForAudio)
            {
                var lastDecoded = _lastFrameTime;

                if (!double.IsNaN(lastDecoded))
                {
                    var lead = lastDecoded - Clock.PositionSeconds;

                    if (lead > _maxLeadSeconds)
                    {
                        Thread.Sleep(10);
                        continue;
                    }
                }
            }

            // 音频背压：边转边播时分片是突发到达的，若只按视频超前量限流，
            // 音频缓冲会被瞬间灌满溢出丢样本（即使 Enqueue 已改为整块丢弃且不推时钟，
            // 仍会造成音频空洞）。缓冲接近容量时同样挂起解复用 —— 音频由扬声器
            // 实时消费，缓冲必然逐渐腾空，不会死锁。
            if (_audioOutput is { } audio && audio.BufferedSeconds >= AudioMaxLeadSeconds)
            {
                Thread.Sleep(10);
                continue;
            }

            return;
        }
    }

    private void DecodeVideoPacket(AVPacket* packet)
    {
        // 记录首视频包时间戳（秒），用于和音频对齐时间原点
        if (!_firstVideoPts.HasValue)
        {
            var raw = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
            if (raw != ffmpeg.AV_NOPTS_VALUE)
            {
                _firstVideoPts = AudioDecoder.PtsToSeconds(raw, _videoTimeBase);
            }
        }

        var send = _videoDecoder.SendPacket(packet);

        if (FfmpegNative.IsEagain(send))
        {
            // 解码器内部帧队列已满：必须先把积压帧取走再重投，否则该包会被静默丢弃
            DrainVideoFrames(packet->size);
            send = _videoDecoder.SendPacket(packet);
        }

        if (send < 0 && !FfmpegNative.IsEagain(send))
        {
            throw new FfmpegException(FfmpegRuntime.Describe(send, "avcodec_send_packet(视频)"));
        }

        // 送包成功才登记：这个包会产出一帧，登记它的 dts 以便输出时算重排延迟
        if (send >= 0 && packet->pts != ffmpeg.AV_NOPTS_VALUE && packet->dts != ffmpeg.AV_NOPTS_VALUE)
        {
            _packetDtsByPts[packet->pts] = packet->dts;
            _packetDtsOrder.Enqueue(packet->pts);

            while (_packetDtsOrder.Count > 128)
            {
                _packetDtsByPts.Remove(_packetDtsOrder.Dequeue());
            }
        }

        DrainVideoFrames(packet->size);
    }

    /// <summary>取空解码器内部所有可用帧（循环直到 AVERROR(EAGAIN)）。</summary>
    private void DrainVideoFrames(int packetSize)
    {
        while (true)
        {
            var receive = _videoDecoder.ReceiveFrame();

            if (FfmpegNative.IsEagain(receive) || FfmpegNative.IsEndOfStream(receive))
            {
                break;
            }

            if (receive < 0)
            {
                throw new FfmpegException(FfmpegRuntime.Describe(receive, "avcodec_receive_frame(视频)"));
            }

            var frame = _videoDecoder.Frame;
            try
            {
                ConsumeVideoFrame(frame, packetSize);
            }
            finally
            {
                ffmpeg.av_frame_unref(frame);
            }
        }
    }

    /// <summary>把解码后的一帧转换为可渲染对象并投入队列。</summary>
    private void ConsumeVideoFrame(AVFrame* frame, int packetSize)
    {
        if (frame->hw_frames_ctx != null)
        {
            // 当前实现只做 CPU 解码；出现硬件帧说明上游使用了 -hwaccel 输出，需要 av_hwframe_transfer_data
            throw new FfmpegException("收到硬件解码帧，请去掉 -hwaccel 或改用 CPU 解码");
        }

        var decoded = _framePool.Rent();
        if (decoded is null)
        {
            // 池耗尽：渲染端消费不过来，丢弃本帧（不抛异常，保证长跑稳定性）
            Statistics.AddDroppedFrame();
            _droppedSinceLastLog++;
            return;
        }

        var width = frame->width > 0 ? frame->width : Video.Width;
        var height = frame->height > 0 ? frame->height : Video.Height;

        // ---- YUV → BGRA 转换（同时降采样到预览尺寸），直接写入非托管缓冲 ----
        // 目标尺寸固定取池缓冲尺寸，保证任何情况下都不会越界写
        _converter.Ensure(
            width,
            height,
            (AVPixelFormat)frame->format,
            decoded.Width,
            decoded.Height,
            AVPixelFormat.AV_PIX_FMT_BGRA);

        _converter.Convert(frame, decoded.PixelPointer, decoded.Stride);

        // ---- 运动矢量 ----
        var motion = MotionVectorExtractor.Extract(frame, decoded.Vectors, out var vectorCount);
        decoded.VectorCount = vectorCount;

        // ---- 时间与帧类型 ----
        // 注意：frame->time_base 在部分场景下会退化为 0/0，必须回退到流时间基，
        // 否则时间戳全部变成 NaN，音视频同步与叠加层时间轴会一起失效。
        var timeBase = AudioDecoder.ResolveTimeBase(frame->time_base, _videoTimeBase);
        var timestamp = frame->pts == ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
        var time = AudioDecoder.PtsToSeconds(timestamp, timeBase);

        if (double.IsNaN(time))
        {
            // 完全没有可用时间戳时按帧率递推，保证时间轴单调递增
            time = double.IsNaN(_lastFrameTime) ? 0 : _lastFrameTime + _defaultFrameDuration;
        }

        var duration = double.IsNaN(_lastFrameTime) || time <= _lastFrameTime
            ? _defaultFrameDuration
            : Math.Min(time - _lastFrameTime, 1.0);

        _lastFrameTime = time;

        var number = ++_frameNumber;
        var kind = MapPictureType(frame->pict_type);
        var keyFrame = frame->key_frame != 0;

        // GOP 距离的归零点取「关键帧(IDR)」：这才是「距关键帧帧数」的严格语义。
        // 于是开放 GOP 流里那些**非 IDR 的 I 帧**（实测源片前 900 帧里 42 个 I 帧只有 27 个 IDR）
        // 不会归零 —— 它们在图上与 I 柱 / 帧类型泳道「对不齐」，这不是算错，而是这类流本来的结构。
        // 图表侧对此的处理：真关键帧（本值 = 0）在 GOP 行画成白色满高标记，
        // 非 IDR 的 I 帧保持普通柱色，两者一眼可分辨。
        if (keyFrame)
        {
            _lastKeyFrameNumber = number;
        }

        // ---- 重排延迟：显示时间戳 − 解码时间戳 ----
        // ⚠ 不能用 frame->pkt_dts：FFmpeg 6.0 起它恒等于 pts（实测），
        // 于是 (pts - dts) 永远是 0，图表上 REORDER 就成了死线。
        // 改用自己的登记表按 pts 反查该帧所在包的 dts。
        var reorderDelay = 0;
        var decodeTimestamp = ResolveDecodeTimestamp(frame, timestamp);

        if (decodeTimestamp != ffmpeg.AV_NOPTS_VALUE && timestamp != ffmpeg.AV_NOPTS_VALUE)
        {
            var deltaSeconds = AudioDecoder.PtsToSeconds(timestamp - decodeTimestamp, timeBase);

            if (!double.IsNaN(deltaSeconds) && deltaSeconds > 0)
            {
                reorderDelay = (int)Math.Round(deltaSeconds * FrameRate);
            }
        }

        ResolveQp(frame, out var qpMin, out var qpMax, out var qpAverage);

        var info = new FrameInfo
        {
            Number = number,
            Kind = kind,
            KeyFrame = keyFrame,
            Qp = qpAverage,
            QpMin = qpMin,
            QpMax = qpMax,
            PacketSize = frame->pkt_size > 0 ? frame->pkt_size : packetSize,
            Width = width,
            Height = height,
            Time = time,
            Duration = duration,
            GopPosition = (int)(number - _lastKeyFrameNumber),
            ReorderDelay = reorderDelay,
            Progressive = frame->interlaced_frame == 0,
            Corrupt = (frame->flags & ffmpeg.AV_FRAME_FLAG_CORRUPT) != 0,
            VectorCount = motion.Total,
            ForwardVectors = motion.Forward,
            BackwardVectors = motion.Backward,
            MotionMeanPixels = motion.MeanPixels,
            MotionMaxPixels = motion.MaxPixels,
            ForwardMeanPixels = motion.ForwardMeanPixels,
            BackwardMeanPixels = motion.BackwardMeanPixels,
        };

        decoded.Info = info;

        Statistics.AddFrame(info);
        Charts.Add(info);
        FrameDecodedInternal?.Invoke(info);

        // 有界队列 + 保留最新：队列满时淘汰最旧的一帧，而不是阻塞解码线程。
        // 这样既能保证统计/图表拿到「每一帧」，又不会因为渲染端慢而让解码线程停摆
        // （解码线程一旦停摆，音频就断流，音频主时钟随即冻结，形成死锁）。
        lock (_frameGate)
        {
            while (_frameQueue.Count >= _maxQueuedFrames)
            {
                _framePool.Return(_frameQueue.Dequeue());
                Statistics.AddDroppedFrame();
                _droppedSinceLastLog++;
            }

            _frameQueue.Enqueue(decoded);
        }
    }

    /// <summary>取该输出帧所在压缩包的解码时间戳（用于计算重排延迟）。</summary>
    private long ResolveDecodeTimestamp(AVFrame* frame, long presentationTimestamp)
    {
        if (_packetDtsByPts.TryGetValue(presentationTimestamp, out var dts))
        {
            return dts;
        }

        // 退路：老版本 FFmpeg 会把真实 dts 填进 AVFrame.pkt_dts
        return frame->pkt_dts;
    }

    /// <summary>
    /// 取该帧的 QP 最小 / 最大 / 平均值；解码器未提供时全为 0（界面按「未提供」显示）。
    /// </summary>
    private static void ResolveQp(AVFrame* frame, out int min, out int max, out int average)
    {
        // 多数解码器（MPEG-1/2、MPEG-4…）只会给一个帧级 QP，min = max = avg
        if (frame->quality is > 0 and <= 63)
        {
            min = max = average = frame->quality;
            return;
        }

        // H.264 等从不填 quality：改用逐宏块 QP 日志汇总出来的统计量
        if (FrameQpCollector.TryTake(frame->coded_picture_number, out var block))
        {
            min = block.Min;
            max = block.Max;
            average = block.Average;
            return;
        }

        min = max = average = 0;
    }

    private void DecodeAudioPacket(AVPacket* packet)
    {
        if (_audioDecoder is null || _audioOutput is null)
        {
            return;
        }

        // 记录首音频包时间戳（秒），用于和视频对齐时间原点
        if (!_firstAudioPts.HasValue)
        {
            var raw = packet->pts != ffmpeg.AV_NOPTS_VALUE ? packet->pts : packet->dts;
            if (raw != ffmpeg.AV_NOPTS_VALUE)
            {
                _firstAudioPts = AudioDecoder.PtsToSeconds(raw, _audioDecoder.TimeBase);
            }
        }

        var send = _audioDecoder.SendPacket(packet);

        if (FfmpegNative.IsEagain(send))
        {
            DrainAudioFrames();
            send = _audioDecoder.SendPacket(packet);
        }

        if (send < 0 && !FfmpegNative.IsEagain(send))
        {
            // 音频包解码失败不致命：跳过该包，保证视频链路继续
            return;
        }

        DrainAudioFrames();
    }

    private void DrainAudioFrames()
    {
        if (_audioDecoder is null || _audioOutput is null)
        {
            return;
        }

        while (true)
        {
            var receive = _audioDecoder.ReceiveFrame();

            if (FfmpegNative.IsEagain(receive) || FfmpegNative.IsEndOfStream(receive))
            {
                break;
            }

            if (receive < 0)
            {
                break;
            }

            try
            {
                _audioDecoder.Resample(out var buffer, out var count, out var timestamp);
                EnqueueAudio(buffer, count, timestamp);
            }
            finally
            {
                ffmpeg.av_frame_unref(_audioDecoder.Frame);
            }
        }
    }

    /// <summary>
    /// 把一帧解码后的音频交给音频输出，但先按时间原点对齐逻辑处理。
    /// </summary>
    /// <remarks>
    /// 偏移未锁定时先把样本连同时间戳暂存（样本必须拷贝，解码缓冲会被复用）；
    /// 偏移锁定后直接入队并在时间戳上叠加修正量。这样音频主时钟从一开始就用的是
    /// 与视频同源的时间戳，不会被未校正的管道时间戳带偏。
    /// </remarks>
    private void EnqueueAudio(byte[] buffer, int count, double rawTimestamp)
    {
        if (_audioOutput is null)
        {
            return;
        }

        if (_audioOffsetLocked)
        {
            _audioOutput.Enqueue(buffer, count, rawTimestamp + _audioTimestampOffset);
            return;
        }

        var copy = new byte[count];
        Buffer.BlockCopy(buffer, 0, copy, 0, count);
        _pendingAudio.Add((copy, count, rawTimestamp));

        // 兜底：万一始终等不到首视频包（异常流），暂存音频超过 4s 就以 0 偏移放行，
        // 避免音频永远卡在暂存区。
        var pendingSeconds = _pendingAudio.Sum(p => p.Count)
                            / (double)(AudioDecoder.OutputSampleRate * AudioDecoder.OutputChannels * 2);
        if (pendingSeconds > 4.0)
        {
            _audioTimestampOffset = 0;
            _audioOffsetLocked = true;
            FlushPendingAudio();
        }
    }

    /// <summary>
    /// 当两路首包时间戳都拿到时，算出音视频原点的固定偏移并锁定，随后把暂存音频按修正量入队。
    /// </summary>
    private void TryLockAudioOffset()
    {
        if (_audioOffsetLocked || !_firstVideoPts.HasValue || !_firstAudioPts.HasValue)
        {
            return;
        }

        // 偏移 = 视频原点 − 音频原点；管道模式下音频迟到 → 结果为负 → 把音频时间戳往早拨。
        // 仅当音频确实整体晚于视频时才修正；其余情况（文件模式差值≈0 或反向）视为已对齐。
        var delta = _firstVideoPts.Value - _firstAudioPts.Value;
        _audioTimestampOffset = delta < -0.001 ? delta : 0;
        _audioOffsetLocked = true;
        FlushPendingAudio();
    }

    private void FlushPendingAudio()
    {
        if (_audioOutput is null)
        {
            _pendingAudio.Clear();
            return;
        }

        foreach (var (data, count, ts) in _pendingAudio)
        {
            _audioOutput.Enqueue(data, count, ts + _audioTimestampOffset);
        }

        _pendingAudio.Clear();
    }

    /// <summary>按预览宽度上限计算缓冲尺寸（保持宽高比，取偶数）。</summary>
    private static (int Width, int Height) ComputePreviewSize(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return (2, 2);
        }

        if (sourceWidth <= MaxPreviewWidth)
        {
            return (sourceWidth & ~1, Math.Max(2, sourceHeight & ~1));
        }

        var scale = MaxPreviewWidth / (double)sourceWidth;
        var width = MaxPreviewWidth & ~1;
        var height = Math.Max(2, (int)Math.Round(sourceHeight * scale) & ~1);

        return (width, height);
    }

    private static FrameKind MapPictureType(AVPictureType type) => type switch
    {
        AVPictureType.AV_PICTURE_TYPE_I => FrameKind.I,
        AVPictureType.AV_PICTURE_TYPE_P => FrameKind.P,
        AVPictureType.AV_PICTURE_TYPE_B => FrameKind.B,
        AVPictureType.AV_PICTURE_TYPE_S => FrameKind.S,
        AVPictureType.AV_PICTURE_TYPE_SI => FrameKind.S,
        AVPictureType.AV_PICTURE_TYPE_SP => FrameKind.S,
        AVPictureType.AV_PICTURE_TYPE_BI => FrameKind.B,
        _ => FrameKind.Unknown,
    };

    private void RaiseError(Exception exception) => ErrorOccurred?.Invoke(exception);

    // ------------------------------------------------------------------
    // 释放
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 停止全局 av_log 回调（它是进程级的，必须先关掉，否则下一个片源会继续被采集）
        FrameQpCollector.Disable();

        // 1) 取消并唤醒可能阻塞在暂停上的解码线程
        _cancellation.Cancel();
        _resumeEvent.Set();

        // 2) 关闭源流：这是打断 av_read_frame 阻塞的关键（管道读取会立即返回 EOF）
        if (_ownedStream is not null)
        {
            try
            {
                _ownedStream.Dispose();
            }
            catch (IOException)
            {
                // 忽略
            }
        }

        // 3) 等待解码线程退出
        try
        {
            _demuxThread?.Join(TimeSpan.FromSeconds(2));
        }
        catch (ThreadStateException)
        {
            // 线程尚未启动
        }

        _demuxThread = null;

        // 4) 释放渲染侧仍持有的帧
        lock (_frameGate)
        {
            while (_frameQueue.Count > 0)
            {
                _framePool.Return(_frameQueue.Dequeue());
            }
        }

        _audioOutput?.Dispose();
        _audioDecoder?.Dispose();
        _videoDecoder.Dispose();
        _converter.Dispose();
        _framePool.Dispose();

        if (_formatContext != null)
        {
            var context = _formatContext;
            ffmpeg.avformat_close_input(&context);
        }

        _avioContext?.Dispose();
        _resumeEvent.Dispose();
        _cancellation.Dispose();
    }
}
