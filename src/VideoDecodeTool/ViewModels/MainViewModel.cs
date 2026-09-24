using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using SkiaSharp;
using VideoDecodeTool.Interop;
using VideoDecodeTool.Media;
using VideoDecodeTool.Models;
using VideoDecodeTool.Probing;
using VideoDecodeTool.Rendering;
using VideoDecodeTool.Transcoding;

namespace VideoDecodeTool.ViewModels;

/// <summary>
/// 主界面 ViewModel：串起“打开源片 → 播放/分析 → GPU 转码 → 边转边播”的完整链路。
/// </summary>
/// <remarks>
/// <para><b>两条数据流（对应需求文档第 5 章）</b></para>
/// <list type="bullet">
/// <item>模式 B（源片分析）：<see cref="PlaybackPipeline.OpenFile"/> 直接解码本地文件；</item>
/// <item>模式 A（转码监控）：<see cref="TranscodeSession"/> 的 stdout 交给
/// <see cref="PlaybackPipeline.OpenStream"/>，stderr 交给进度解析器。</item>
/// </list>
/// <para><b>线程约定</b>：预览与统计由两个 <see cref="DispatcherTimer"/> 在 UI 线程驱动；
/// 解码在后台线程完成；进度与统计通过“拉取”而非“推送”更新，彻底避免跨线程访问 UI。</para>
/// </remarks>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private const string EmptyPlaceholder = "拖入视频文件，或点击「打开源片」开始";

    /// <summary>预览刷新间隔（≈60fps）。</summary>
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(15);

    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly OverlayRenderer _overlayRenderer = new();

    private PlaybackPipeline? _pipeline;
    private TranscodeSession? _transcodeSession;
    private DecodedFrame? _previewFrame;
    private TranscodeProgress _latestProgress = TranscodeProgress.Empty;

    /// <summary>
    /// 当前数据源的重放工厂。停止播放会释放管线，但流不可回退（管道 / 跟随增长的文件），
    /// 因此「重新播放」= 用这个工厂重开一个管线从头放。
    /// </summary>
    private Func<PlaybackPipeline>? _replayFactory;

    /// <summary>重放工厂（写入时自动刷新「播放」按钮的可用性）。</summary>
    private Func<PlaybackPipeline>? ReplayFactory
    {
        get => _replayFactory;
        set
        {
            _replayFactory = value;
            TogglePlayCommand.RaiseCanExecuteChanged();
        }
    }

    private string _sourceFilePath = string.Empty;
    private string _outputFilePath = string.Empty;
    private string _ffmpegPath = string.Empty;
    private string _ffprobePath = string.Empty;

    private MediaFileInfo _fileInfo = MediaFileInfo.Empty;
    private FrameInfo? _currentFrame;
    private StatisticsSnapshot _statistics = StatisticsSnapshot.Empty;
    private bool _isPlaying;
    private bool _isTranscoding;
    private bool _isBusy;
    private double _volume = 0.8;
    private double _quality = 23;
    private string _statusText = "就绪";
    private string _modeText = "源片分析";
    private string _positionText = "00:00.000";
    private double _lastPositionSeconds;
    private string _environmentStatus = "正在探测 FFmpeg 环境…";
    private GpuEncoderInfo? _selectedEncoder;

    public MainViewModel(Dispatcher dispatcher)
    {
        Dispatcher = dispatcher;

        _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = PreviewInterval };
        _previewTimer.Tick += OnPreviewTick;

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _statsTimer.Tick += OnStatsTick;

        OpenFileCommand = new AsyncRelayCommand(OpenFileAsync, () => !IsBusy && !IsTranscoding);
        // 一个按钮两种语义：未转码时「开始转码」，转码中变「停止转码」并可中断
        TranscodeButtonCommand = new AsyncRelayCommand(OnTranscodeButtonAsync, () => IsTranscoding || (HasSource && !IsBusy));
        // 停止后 _pipeline 为空，但只要有重放源，「播放」按钮仍可用（点击即从头重放）
        TogglePlayCommand = new AsyncRelayCommand(TogglePlayAsync, () => _pipeline is not null || _replayFactory is not null);
        StopCommand = new AsyncRelayCommand(StopAsync, () => _pipeline is not null);
        DetectGpuCommand = new AsyncRelayCommand(() => DetectGpuAsync(true), () => !IsBusy);
        OpenOutputFolderCommand = new RelayCommand(OpenOutputFolder, () => _outputFilePath.Length > 0);

        InitializeCharts();
        ResolveToolPaths();
    }

    /// <summary>预览面板需要重绘时触发（由 View 订阅并调用 InvalidateVisual）。</summary>
    public event Action? PreviewInvalidated;

    public Dispatcher Dispatcher { get; }

    // ------------------------------------------------------------------
    // 环境 / GPU
    // ------------------------------------------------------------------

    public string FfmpegPath
    {
        get => _ffmpegPath;
        private set => SetProperty(ref _ffmpegPath, value);
    }

    public string FfprobePath
    {
        get => _ffprobePath;
        private set => SetProperty(ref _ffprobePath, value);
    }

    public ObservableCollection<GpuEncoderInfo> GpuEncoders { get; } = [];

    public GpuEncoderInfo? SelectedEncoder
    {
        get => _selectedEncoder;
        set
        {
            if (SetProperty(ref _selectedEncoder, value))
            {
                OnPropertyChanged(nameof(EncoderSummary));
                RefreshVideoCodecTypeOptions();
                TranscodeButtonCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>各编码器后端经探测支持的编码类型条目（只含 IsSupported 的条目，每种编码类型一条）。</summary>
    private Dictionary<EncoderKind, IReadOnlyList<GpuEncoderInfo>> _codecEntriesByKind = [];

    /// <summary>编码类型下拉的选项：跟随「编码器」所选后端，列出探测到的支持项。</summary>
    public IReadOnlyList<string> VideoCodecTypeOptions { get; private set; } = [VideoCodecLabel(VideoCodecKind.H264)];

    private string _selectedVideoCodecType = VideoCodecLabel(VideoCodecKind.H264);

    public string SelectedVideoCodecType
    {
        get => _selectedVideoCodecType;
        set
        {
            if (SetProperty(ref _selectedVideoCodecType, value))
            {
                OnPropertyChanged(nameof(TranscodeCommandText));
            }
        }
    }

    /// <summary>编码类型的界面显示名。</summary>
    public static string VideoCodecLabel(VideoCodecKind codec) => codec switch
    {
        VideoCodecKind.Hevc => "H.265 (HEVC)",
        VideoCodecKind.Vvc => "H.266 (VVC)",
        VideoCodecKind.Av1 => "AV1",
        VideoCodecKind.Vp9 => "VP9",
        _ => "H.264 (AVC)",
    };

    /// <summary>当前选中的编码类型对应的探测条目（含真实 ffmpeg 编码器名）；未探测到时为 null。</summary>
    private GpuEncoderInfo? SelectedCodecEntry =>
        _selectedEncoder is { } encoder
        && _codecEntriesByKind.TryGetValue(encoder.Kind, out var entries)
            ? entries.FirstOrDefault(e => VideoCodecLabel(e.Codec) == _selectedVideoCodecType)
            : null;

    /// <summary>
    /// 按「编码器」当前后端重建编码类型选项。
    /// </summary>
    /// <remarks>
    /// 未探测（启动早期）或该后端一个都没编译时，只显示 H.264 —— 与旧行为一致；
    /// 之前选中的编码类型若仍受支持则保留，否则回落到第一项。
    /// </remarks>
    private void RefreshVideoCodecTypeOptions()
    {
        var options = _selectedEncoder is { } encoder
            && _codecEntriesByKind.TryGetValue(encoder.Kind, out var entries)
            && entries.Count > 0
                ? entries.Select(e => VideoCodecLabel(e.Codec)).ToArray()
                : [VideoCodecLabel(VideoCodecKind.H264)];

        VideoCodecTypeOptions = options;
        OnPropertyChanged(nameof(VideoCodecTypeOptions));

        if (!options.Contains(_selectedVideoCodecType, StringComparer.Ordinal))
        {
            SelectedVideoCodecType = options[0];
        }
    }

    public string EnvironmentStatus
    {
        get => _environmentStatus;
        private set => SetProperty(ref _environmentStatus, value);
    }

    /// <summary>FFmpeg 版本 + 硬件加速后端摘要。</summary>
    public string HardwareAccelerationText { get; private set; } = "-";

    public string EncoderSummary => SelectedEncoder is null
        ? "未选择编码器"
        : $"{SelectedEncoder.DisplayName} · {SelectedEncoder.StatusText}";

    /// <summary>编码质量（NVENC cq / QSV global_quality / x264 crf）。</summary>
    public double Quality
    {
        get => _quality;
        set
        {
            if (SetProperty(ref _quality, Math.Clamp(value, 0, 51)))
            {
                OnPropertyChanged(nameof(QualityText));
                OnPropertyChanged(nameof(TranscodeCommandText));
            }
        }
    }

    /// <summary>上一次的质量值（切到指定码流时暂存，切回质量模式时恢复）。</summary>
    private double _lastQuality = 23;

    /// <summary>质量滑块的读数；指定码流模式下质量参数不参与编码，如实显示「不适用」。</summary>
    public string QualityText => IsQualityMode
        ? $"质量 {Quality:0}（数值越小画质越高）"
        : "不适用（已按指定码流编码）";

    /// <summary>码控方式：质量模式（CRF/CQ）或指定码流。</summary>
    public string[] BitrateModeOptions { get; } = ["质量模式（CRF/CQ）", "指定码流（kbps）"];

    private string _selectedBitrateMode = "质量模式（CRF/CQ）";

    /// <summary>
    /// 码控方式切换。
    /// </summary>
    /// <remarks>
    /// 两者不会同时生效：切到「指定码流」时把质量滑块<b>归零并禁用</b>（<see cref="IsQualityEnabled"/>），
    /// 切回质量模式时恢复上一次的质量值 —— 否则滑块停在原处会让人以为仍在按质量编码。
    /// 注意顺序：切到指定码流时必须先放开滑块下限（12 → 0），否则滑块会把 0 夹回 12。
    /// </remarks>
    public string SelectedBitrateMode
    {
        get => _selectedBitrateMode;
        set
        {
            if (!SetProperty(ref _selectedBitrateMode, value))
            {
                return;
            }

            if (IsQualityMode)
            {
                Quality = _lastQuality;
                NotifyQualityState();
            }
            else
            {
                NotifyQualityState();
                _lastQuality = Quality;
                Quality = 0;
            }
        }
    }

    /// <summary>是否处于质量模式（只有此模式下 Quality 才参与编码）。</summary>
    public bool IsQualityMode => _selectedBitrateMode.Contains("质量", StringComparison.Ordinal);

    /// <summary>质量滑块是否可用（指定码流模式下归零并禁用）。</summary>
    public bool IsQualityEnabled => IsQualityMode;

    /// <summary>码流输入是否可用（质量模式下禁用）。</summary>
    public bool IsBitrateValueEnabled => !IsQualityMode;

    /// <summary>质量滑块下限：质量模式 12；指定码流模式 0（滑块压到 0 表示不适用）。</summary>
    public double QualitySliderMinimum => IsQualityMode ? 12 : 0;

    private void NotifyQualityState()
    {
        OnPropertyChanged(nameof(IsQualityMode));
        OnPropertyChanged(nameof(IsQualityEnabled));
        OnPropertyChanged(nameof(IsBitrateValueEnabled));
        OnPropertyChanged(nameof(QualitySliderMinimum));
        OnPropertyChanged(nameof(QualityText));
    }

    // ------------------------------------------------------------------
    // 文件信息
    // ------------------------------------------------------------------

    public MediaFileInfo FileInfo
    {
        get => _fileInfo;
        private set
        {
            if (SetProperty(ref _fileInfo, value))
            {
                // 叠加层顶部信息行要显示「源分辨率 → 目标分辨率」：
                // 转码时解码的是输出流，帧分辨率是「目标」，源分辨率只能由这里带过去
                _overlayRenderer.Options.SourceWidth = value.Width;
                _overlayRenderer.Options.SourceHeight = value.Height;

                OnPropertyChanged(nameof(FileNameText));
                OnPropertyChanged(nameof(FormatText));
                OnPropertyChanged(nameof(VideoText));
                OnPropertyChanged(nameof(VideoLayoutText));
                OnPropertyChanged(nameof(ColorText));
                OnPropertyChanged(nameof(LengthText));
                OnPropertyChanged(nameof(AudioText));
                OnPropertyChanged(nameof(DurationText));
                OnPropertyChanged(nameof(HasSource));
                OnPropertyChanged(nameof(SourceSummary));
                OnPropertyChanged(nameof(PlayheadVisibility));
                OnPropertyChanged(nameof(QpMarkerVisibility));
                TranscodeButtonCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string FileNameText => FileInfo.FileName.Length > 0 ? FileInfo.FileName : "-";

    public string FormatText => FileInfo.FormatName.Length > 0
        ? $"{FileInfo.FormatName}  ({FileInfo.FormatLongName})"
        : "-";

    public string VideoText => FileInfo.Width > 0 ? FileInfo.VideoSummary : "-";

    public string VideoLayoutText => FileInfo.Width > 0 ? FileInfo.VideoLayoutSummary : "-";

    public string ColorText => FileInfo.Width > 0 ? FileInfo.ColorSummary : "-";

    public string LengthText => FileInfo.Duration > TimeSpan.Zero
        ? $"{FileInfo.LengthSummary}   文件 {FileInfo.SizeText}"
        : "-";

    public string AudioText => FileInfo.HasAudio ? FileInfo.AudioSummary : "无音频流";

    public bool HasSource => _sourceFilePath.Length > 0;

    public string SourceSummary => HasSource ? _sourceFilePath : "（未打开文件）";

    public string OutputFilePath => _outputFilePath;

    // ------------------------------------------------------------------
    // 当前帧 / 统计
    // ------------------------------------------------------------------

    public FrameInfo? CurrentFrame
    {
        get => _currentFrame;
        private set
        {
            if (SetProperty(ref _currentFrame, value))
            {
                OnPropertyChanged(nameof(FrameHeaderText));
                OnPropertyChanged(nameof(FrameTypeText));
                OnPropertyChanged(nameof(FrameSizeText));
                OnPropertyChanged(nameof(FrameQpText));
                OnPropertyChanged(nameof(FrameMotionText));
                OnPropertyChanged(nameof(FrameGopText));
                OnPropertyChanged(nameof(FrameReorderText));
                OnPropertyChanged(nameof(FrameFlagsText));

                // 图表右侧读数依赖“当前帧”，与帧文本同步刷新，避免慢一拍
                OnPropertyChanged(nameof(ReadoutBitrate));
                OnPropertyChanged(nameof(ReadoutFrameType));
                OnPropertyChanged(nameof(ReadoutQp));
                OnPropertyChanged(nameof(ReadoutMotion));
                OnPropertyChanged(nameof(ReadoutGop));
                OnPropertyChanged(nameof(ReadoutReorder));

                // QP 行的小方块纵向跟着本帧 QP 走
                OnPropertyChanged(nameof(QpMarkerMargin));
                OnPropertyChanged(nameof(QpMarkerVisibility));

                // TYPE 一行的配色跟着帧类型走
                OnPropertyChanged(nameof(FrameTypeBrush));
            }
        }
    }

    public StatisticsSnapshot Statistics
    {
        get => _statistics;
        private set
        {
            if (SetProperty(ref _statistics, value))
            {
                OnPropertyChanged(nameof(IFrameRow));
                OnPropertyChanged(nameof(PFrameRow));
                OnPropertyChanged(nameof(BFrameRow));
                OnPropertyChanged(nameof(OtherFrameRow));
                OnPropertyChanged(nameof(TotalFramesText));
            }
        }
    }

    public string FrameHeaderText
    {
        get
        {
            if (CurrentFrame is null)
            {
                return "等待解码…";
            }

            var total = FileInfo.FrameCount > 0 ? FileInfo.FrameCount.ToString() : "?";
            return $"{CurrentFrame.Number} / {total}    {FormatTime(CurrentFrame.Time)}   dur {CurrentFrame.Duration * 1000:0.00} ms";
        }
    }

    public string FrameTypeText => CurrentFrame?.TypeDescription ?? "-";

    public string FrameSizeText => CurrentFrame is null
        ? "-"
        : $"{CurrentFrame.PacketSize} B ({CurrentFrame.PacketSize / 1024.0:0.0} KB)   {CurrentFrame.BitrateKbps:0} kbps";

    /// <summary>
    /// 「帧参数」面板里的 QP 一行：avg / min / max 都是<b>当前这一帧</b>的宏块统计。
    /// </summary>
    /// <remarks>
    /// ⚠ 这里必须用当前帧的值，不能用 <c>Statistics</c> 里的全片累计值：
    /// 累计 min 只会越来越小、累计 max 只会越来越大（永不重置），
    /// 而参考实现的读数是逐帧重算的（每帧都会跳变），两者语义完全不同。
    /// 全片累计值保留在 QP 图表标题的 ToolTip 里。
    /// </remarks>
    public string FrameQpText => CurrentFrame is null
        ? "-"
        : $"本帧  avg {CurrentFrame.QpAverage:0.0}   min {CurrentFrame.QpMin}   max {CurrentFrame.QpMax}";

    /// <summary>
    /// 「帧参数」栏 TYPE 一行的文字颜色：按当前帧类型取 I 红 / P 蓝 / B 绿
    /// （与图例、BITRATE / FRAME TYPE 图表同源 —— 参考实现里那一行就是按帧类型着色的）。
    /// </summary>
    /// <remarks>用完全限定名是为了不在 ViewModel 里再引入 System.Windows.Media 的 using。</remarks>
    public System.Windows.Media.Brush FrameTypeBrush => CurrentFrame?.Kind switch
    {
        FrameKind.I => FrameTypeIBrush,
        FrameKind.P => FrameTypePBrush,
        FrameKind.B => FrameTypeBBrush,
        _ => FrameTypeOtherBrush,
    };

    private static readonly System.Windows.Media.Brush FrameTypeIBrush = FreezeBrush(0xEF, 0x44, 0x44);
    private static readonly System.Windows.Media.Brush FrameTypePBrush = FreezeBrush(0x21, 0x96, 0xF3);
    private static readonly System.Windows.Media.Brush FrameTypeBBrush = FreezeBrush(0x4C, 0xAF, 0x50);
    private static readonly System.Windows.Media.Brush FrameTypeOtherBrush = FreezeBrush(0xE8, 0xA3, 0x3D);

    /// <summary>构造一支冻结画刷（冻结后不可变，可安全共享）。</summary>
    private static System.Windows.Media.Brush FreezeBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public string FrameMotionText => CurrentFrame is null
        ? "-"
        : $"{CurrentFrame.VectorCount} vectors   mean {CurrentFrame.MotionMeanPixels:0.00} px   max {CurrentFrame.MotionMaxPixels:0.0} px";

    public string FrameGopText => CurrentFrame is null ? "-" : $"+{CurrentFrame.GopPosition} 帧（距关键帧）";

    public string FrameReorderText => CurrentFrame is null ? "-" : $"+{CurrentFrame.ReorderDelay} frame (pts-dts)";

    public string FrameFlagsText => CurrentFrame?.FlagsText ?? "-";

    // ------------------------------------------------------------------
    // 图表右侧的当前值读数（对应参考图右侧的数字列）
    // ------------------------------------------------------------------

    public string ReadoutBitrate => CurrentFrame is null ? "-" : $"{CurrentFrame.BitrateKbps:0} kbps";

    public string ReadoutFrameType
    {
        get
        {
            if (CurrentFrame is null)
            {
                return "-";
            }

            return $"{CurrentFrame.TypeChar}   I{Statistics.IPercent:0}% P{Statistics.PPercent:0}% B{Statistics.BPercent:0}%";
        }
    }

    /// <summary>
    /// 播放头处的 QP 读数。
    /// </summary>
    /// <remarks>
    /// ⚠ 必须用「当前帧」的值，不能用全片累计平均：
    /// 图表曲线画的是逐帧值，读数若给累计平均，两边看起来就是完全不同的两条数
    /// （实测反馈：播放线旁的数值和曲线完全不同步）。
    /// </remarks>
    /// <summary>QP 读数：带 <c>qp</c> 前缀，只给本帧平均值（界面反馈：区间段没意义，已去掉）。</summary>
    public string ReadoutQp => CurrentFrame is { Qp: > 0 } frame
        ? $"qp {frame.QpAverage:0.0}"
        : IsTranscoding && _latestProgress.Qp > 0
            ? $"qp {_latestProgress.Qp:0}（转码）"
            : "qp -";

    /// <summary>
    /// MOTION 读数：直接给「平均矢量长度 + px 单位」。
    /// </summary>
    /// <remarks>
    /// 不再拆成 fwd / bwd 两个数：曲线画的本来就是逐帧平均矢量长度，
    /// 拆开写既长又和曲线对不上（界面反馈：直接显示均值即可）。
    /// </remarks>
    public string ReadoutMotion => CurrentFrame is null
        ? "-"
        : $"{CurrentFrame.MotionMeanPixels:0.00} px";

    public string ReadoutGop => CurrentFrame is null ? "-" : $"+{CurrentFrame.GopPosition}";

    public string ReadoutReorder => CurrentFrame is null ? "-" : $"+{CurrentFrame.ReorderDelay}";

    public string IFrameRow => Statistics.IRow;

    public string PFrameRow => Statistics.PRow;

    public string BFrameRow => Statistics.BRow;

    public string OtherFrameRow => Statistics.OtherRow;

    public string TotalFramesText =>
        $"累计 {Statistics.FrameCount} 帧 / {Statistics.TotalBytes / 1024.0 / 1024.0:0.00} MB" +
        (Statistics.DroppedFrames > 0 ? $"（渲染丢弃 {Statistics.DroppedFrames} 帧）" : string.Empty);

    /// <summary>
    /// QP 图表标题：明确告知 QP 数据是否可用。
    /// </summary>
    /// <remarks>
    /// H.264 等主流解码器不会填充 AVFrame.quality，源片分析模式下拿不到逐帧 QP；
    /// 转码模式下则可以从 -progress 的 stream_0_0_q 读到编码器实际 QP。
    /// </remarks>
    public string QpChartTitle =>
        Statistics.QpMax > 0
            ? $"QP  亮黄曲线 = 逐帧平均 QP（= 每帧 min-max 的中点，本片累计 avg {Statistics.QpAverage:0.0}）；" +
              $"柱 = 以该中点为柱心、高度取自平均值的实心柱"
            : IsTranscoding
                ? $"QP  解码器未提供（转码输出 QP {_latestProgress.Qp:0}）"
                : "QP  当前解码器未提供逐帧 QP（H.264/HEVC 常见）";

    // ------------------------------------------------------------------
    // 转码进度
    // ------------------------------------------------------------------

    /// <summary>
    /// 转码进度百分比：两种估算都可用时取<b>较小</b>者。
    /// </summary>
    /// <remarks>
    /// 帧数估算（已输出帧 ÷ 预期输出帧数）依赖容器里的总帧数，时间估算（已输出时间 ÷ 源片时长）
    /// 依赖容器时长，两者都可能偏高；取小值可保证任一估计失真都不会让进度提前冲到 100%
    /// （实测反馈：显示 100.0% 但转码仍在继续）。结束时由 IsFinal 强制置满。
    /// </remarks>
    public double ProgressPercent
    {
        get
        {
            var framePercent = _latestProgress.Percent;
            var timePercent = _latestProgress.TimePercent;

            if (framePercent > 0 && timePercent > 0)
            {
                return Math.Min(framePercent, timePercent);
            }

            return framePercent > 0 ? framePercent : timePercent;
        }
    }

    public string ProgressText
    {
        get
        {
            var value = ProgressPercent;
            return $"{value:0.0}%";
        }
    }

    /// <summary>转码实时读数：帧 / 已输出时间 / 速度 / 实际码流 / 已写大小。</summary>
    /// <remarks>
    /// 不含 QP：该字段取自转码进程的 <c>-progress</c> 输出，而 x264 / NVENC / QSV / AMF
    /// 都不上报它，实测恒为 0（界面反馈「一直是 0」），留着只占宽度。
    /// </remarks>
    public string ProgressDetailText
    {
        get
        {
            var progress = _latestProgress;

            return $"frame {progress.Frame}   {progress.OutTimeText}   {progress.SpeedText}   " +
                   $"{progress.BitrateText}   {progress.SizeText}";
        }
    }

    public string TranscodeCommandText
    {
        get
        {
            if (!HasSource || FfmpegPath.Length == 0)
            {
                return "（打开源片后显示将要执行的 FFmpeg 命令）";
            }

            var request = BuildTranscodeRequest(FfmpegRuntime.IsAvailable);
            var arguments = FfmpegArgumentBuilder.Build(request, request.EnableLiveStream);

            var commandLine = FfmpegArgumentBuilder.ToCommandLine(FfmpegPath, arguments);

            // 直播模式下还需在收尾时执行一次流拷贝重封装，这里一并展示
            if (request.EnableLiveStream)
            {
                var remux = FfmpegArgumentBuilder.BuildRemux(
                    Path.GetFileNameWithoutExtension(request.OutputPath) + ".partial.frag.mp4",
                    Path.GetFileName(request.OutputPath));

                commandLine += "\n[转码结束后自动执行]\n" + FfmpegArgumentBuilder.ToCommandLine(FfmpegPath, remux);
            }

            return commandLine;
        }
    }

    // ------------------------------------------------------------------
    // 播放状态
    // ------------------------------------------------------------------

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseGlyph));
                OnPropertyChanged(nameof(PlayPauseTooltip));
            }
        }
    }

    public bool IsTranscoding
    {
        get => _isTranscoding;
        private set
        {
            if (SetProperty(ref _isTranscoding, value))
            {
                OnPropertyChanged(nameof(ModeText));
                OnPropertyChanged(nameof(TranscodeButtonText));
                TranscodeButtonCommand.RaiseCanExecuteChanged();
                OpenFileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OpenFileCommand.RaiseCanExecuteChanged();
                TranscodeButtonCommand.RaiseCanExecuteChanged();
                DetectGpuCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string PlayPauseGlyph => IsPlaying ? "⏸" : "▶";

    public string PlayPauseTooltip => IsPlaying ? "暂停" : "播放";

    public string TranscodeButtonText => IsTranscoding ? "停止转码" : "开始转码";

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0, 1)) && _pipeline is not null)
            {
                _pipeline.Volume = (float)_volume;
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>当前模式：源片分析 / 实时转码。</summary>
    public string ModeText
    {
        get => _modeText;
        private set => SetProperty(ref _modeText, value);
    }

    public string PositionText
    {
        get => _positionText;
        private set => SetProperty(ref _positionText, value);
    }

    public string DurationText => FileInfo.Duration > TimeSpan.Zero
        ? FileInfo.Duration.ToString(@"mm\:ss\.fff")
        : "--:--.---";

    /// <summary>播放进度百分比（0-100），供「帧统计」末尾的播放进度条使用。</summary>
    public double PlaybackPercent => FileInfo.Duration > TimeSpan.Zero
        ? Math.Clamp(_lastPositionSeconds / FileInfo.Duration.TotalSeconds * 100.0, 0, 100)
        : 0;

    public string PlaybackPercentText => $"{PlaybackPercent:0.0}%";

    // ------------------------------------------------------------------
    // 叠加层开关
    // ------------------------------------------------------------------

    public bool ShowMotionVectors
    {
        get => _overlayRenderer.Options.ShowMotionVectors;
        set
        {
            if (_overlayRenderer.Options.ShowMotionVectors == value)
            {
                return;
            }

            _overlayRenderer.Options.ShowMotionVectors = value;
            OnPropertyChanged();
            RequestPreviewRepaint();
        }
    }

    public bool ShowFrameTypeOverlay
    {
        get => _overlayRenderer.Options.ShowFrameType;
        set
        {
            if (_overlayRenderer.Options.ShowFrameType == value)
            {
                return;
            }

            _overlayRenderer.Options.ShowFrameType = value;
            OnPropertyChanged();
            RequestPreviewRepaint();
        }
    }

    public bool ShowHeaderOverlay
    {
        get => _overlayRenderer.Options.ShowHeader;
        set
        {
            if (_overlayRenderer.Options.ShowHeader == value)
            {
                return;
            }

            _overlayRenderer.Options.ShowHeader = value;
            OnPropertyChanged();
            RequestPreviewRepaint();
        }
    }

    /// <summary>运动矢量绘制放大倍数。</summary>
    public double MotionVectorScale
    {
        get => _overlayRenderer.Options.VectorScale;
        set
        {
            var clamped = Math.Clamp(value, 0.25, 4.0);

            if (Math.Abs(_overlayRenderer.Options.VectorScale - clamped) < 0.001)
            {
                return;
            }

            _overlayRenderer.Options.VectorScale = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(MotionVectorScaleText));
            RequestPreviewRepaint();
        }
    }

    public string MotionVectorScaleText => $"矢量放大 {MotionVectorScale:0.0}x";

    // ------------------------------------------------------------------
    // 命令
    // ------------------------------------------------------------------

    public AsyncRelayCommand OpenFileCommand { get; }

    /// <summary>转码按钮：未转码时开始转码，转码中则停止转码（会先询问如何处理已转码部分）。</summary>
    public AsyncRelayCommand TranscodeButtonCommand { get; }

    /// <summary>由 View 注入：弹出「停止转码」三选一对话框并返回用户选择。</summary>
    public Func<StopTranscodeChoice>? ConfirmStopTranscode { get; set; }

    public AsyncRelayCommand TogglePlayCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand DetectGpuCommand { get; }

    public RelayCommand OpenOutputFolderCommand { get; }

    // ------------------------------------------------------------------
    // 忙碌提示层（整窗遮罩：说明此时按钮为什么不可用、正在干什么）
    // ------------------------------------------------------------------

    private bool _isBusyOverlayVisible;

    /// <summary>是否显示整窗忙碌提示层（打开源片 / 启动初始化等整窗被禁用的场景）。</summary>
    public bool IsBusyOverlayVisible
    {
        get => _isBusyOverlayVisible;
        private set => SetProperty(ref _isBusyOverlayVisible, value);
    }

    private string _busyOverlayTitle = string.Empty;

    /// <summary>提示层标题：说明当前在做什么。</summary>
    public string BusyOverlayTitle
    {
        get => _busyOverlayTitle;
        private set => SetProperty(ref _busyOverlayTitle, value);
    }

    private void ShowBusyOverlay(string title)
    {
        BusyOverlayTitle = title;
        IsBusyOverlayVisible = true;
    }

    private void HideBusyOverlay() => IsBusyOverlayVisible = false;

    // ------------------------------------------------------------------
    // 初始化
    // ------------------------------------------------------------------

    /// <summary>窗口加载完成后调用：初始化 FFmpeg 原生绑定并做一次快速 GPU 枚举。</summary>
    /// <remarks>
    /// 冷启动（重启系统后第一次）时 FFmpeg 原生库从磁盘加载、编码器枚举要拉起多个 ffmpeg 进程，
    /// 可能持续数秒 —— 期间整窗禁用，必须给用户一个可见的提示层，否则会疑惑「按钮怎么灰了」。
    /// </remarks>
    public async Task InitializeAsync()
    {
        ShowBusyOverlay("正在初始化解码环境…");

        try
        {
            var available = FfmpegRuntime.Initialize();

            if (available)
            {
                EnvironmentStatus =
                    $"解码库就绪：{FfmpegRuntime.SharedLibraryDirectory}（avutil {FfmpegRuntime.AvUtilVersion}）";
            }
            else
            {
                EnvironmentStatus = FfmpegRuntime.FailureReason ?? "FFmpeg 初始化失败";
                StatusText = "解码库未就绪，仅可使用外部 ffmpeg.exe 进行转码";
            }

            await DetectGpuAsync(runFunctionalTest: false).ConfigureAwait(true);
        }
        finally
        {
            HideBusyOverlay();
        }

        // 秒开优先：启动只做 -encoders 枚举（状态停在「可用（未试编码）」），
        // 随后在后台自动补做功能性试编码，把状态落实为「可用 / 不可用：原因」。
        _ = VerifyEncodersInBackgroundAsync();
    }

    private void ResolveToolPaths()
    {
        FfmpegPath = NativeLibraryLocator.ResolveFfmpegExecutable() ?? string.Empty;
        FfprobePath = NativeLibraryLocator.ResolveFfprobeExecutable() ?? string.Empty;
    }

    /// <summary>探测进行中的互斥标志（防「检测 GPU」按钮与后台自动验证并发跑两套试编码）。</summary>
    private int _detectionInFlight;

    /// <summary>探测 GPU 编码器（「检测 GPU」按钮入口，带试编码验证）。</summary>
    private async Task DetectGpuAsync(bool runFunctionalTest)
    {
        if (FfmpegPath.Length == 0)
        {
            ResolveToolPaths();
        }

        if (FfmpegPath.Length == 0)
        {
            StatusText = "未找到 ffmpeg.exe，无法探测 GPU 编码器";
            return;
        }

        if (Interlocked.CompareExchange(ref _detectionInFlight, 1, 0) != 0)
        {
            return;
        }

        IsBusy = true;
        StatusText = runFunctionalTest ? "正在试编码探测 GPU…" : "正在枚举编码器…";

        try
        {
            await ApplyDetectionAsync(runFunctionalTest).ConfigureAwait(true);

            StatusText = runFunctionalTest ? "GPU 探测完成" : "编码器枚举完成";
        }
        catch (Exception ex)
        {
            StatusText = $"GPU 探测失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _detectionInFlight, 0);
        }
    }

    /// <summary>启动后自动补做功能性试编码：把「可用（未试编码）」落实为「可用 / 不可用」。</summary>
    /// <remarks>
    /// 与按钮入口共用 <see cref="ApplyDetectionAsync"/>，通过 <see cref="_detectionInFlight"/> 与手动探测互斥；
    /// 失败时静默保持原状态。<b>不持有 <c>IsBusy</c></b> —— 验证只是后台打磨状态，不能把「打开源片」
    /// 这类主流程按钮禁掉（冷启动本来就慢，再叠加禁用用户会不知道窗口在干什么）。
    /// </remarks>
    private async Task VerifyEncodersInBackgroundAsync()
    {
        if (FfmpegPath.Length == 0
            || Interlocked.CompareExchange(ref _detectionInFlight, 1, 0) != 0)
        {
            return;
        }

        StatusText = "正在试编码验证 GPU 编码器…";

        try
        {
            await ApplyDetectionAsync(runFunctionalTest: true).ConfigureAwait(true);

            StatusText = "GPU 编码器自动验证完成";
        }
        catch (Exception)
        {
            // 后台自动验证失败不打扰用户：保留「可用（未试编码）」，可手动点「检测 GPU」重试
        }
        finally
        {
            Interlocked.Exchange(ref _detectionInFlight, 0);
        }
    }

    /// <summary>执行探测并应用结果：重建编码类型映射、编码器下拉与选择状态。</summary>
    /// <remarks>
    /// 下拉只保留试编码<b>可用</b>的条目 —— 不可用的直接不出现（编译了但驱动/硬件跑不通的
    /// 对用户没有意义，留着只会制造困惑）；启动早期未试编码的条目 IsAvailable=true 先进下拉，
    /// 后台自动验证完成后再刷新掉不可用者。
    /// </remarks>
    private async Task ApplyDetectionAsync(bool runFunctionalTest)
    {
        var detector = new GpuEncoderDetector(FfmpegPath);
        var capabilities = await detector.DetectAsync(runFunctionalTest).ConfigureAwait(true);

        // 探测器动态解析 -encoders，按（后端 × 编码类型）逐条返回；「编码器」下拉保持每个后端一条，
        // 代表条目优先取试编码可用者、再按编码类型顺序（H.264 在前）—— 显示名与旧行为一致。
        _codecEntriesByKind = capabilities.Encoders
            .Where(e => e.IsAvailable)
            .GroupBy(e => e.Kind)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<GpuEncoderInfo>)g.ToArray());

        GpuEncoders.Clear();
        foreach (var group in capabilities.Encoders
            .Where(e => e.IsAvailable)
            .GroupBy(e => e.Kind))
        {
            GpuEncoders.Add(group
                .OrderByDescending(e => e.IsAvailable)
                .ThenBy(e => (int)e.Codec)
                .First());
        }

        HardwareAccelerationText = capabilities.HardwareAccelerations.Count > 0
            ? string.Join(" / ", capabilities.HardwareAccelerations)
            : "无";

        EnvironmentStatus = $"{capabilities.FfmpegVersion}\n硬件加速后端：{HardwareAccelerationText}";

        // 保留用户已选编码器；否则自动选择第一个可用项
        var previous = SelectedEncoder?.Kind;
        var target = GpuEncoders.FirstOrDefault(e => e.Kind == previous && e.IsAvailable)
                     ?? GpuEncoders.FirstOrDefault(e => e.IsAvailable)
                     ?? GpuEncoders.FirstOrDefault();

        SelectedEncoder = target;
        // 编码器引用未变时 setter 不触发，这里显式刷新一次（探测结果可能已变化）
        RefreshVideoCodecTypeOptions();
    }

    // ------------------------------------------------------------------
    // 打开源片 / 播放控制
    // ------------------------------------------------------------------

    private async Task OpenFileAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "打开源片",
            Filter = "视频文件|*.mp4;*.mkv;*.mov;*.avi;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.m2ts;*.mpg;*.mpeg;*.vob;*.rmvb|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await LoadFileAsync(dialog.FileName).ConfigureAwait(true);
    }

    /// <summary>加载文件：探测元数据 → 启动源片分析播放。</summary>
    public async Task LoadFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            StatusText = $"文件不存在：{filePath}";
            return;
        }

        IsBusy = true;
        ShowBusyOverlay($"正在打开 {Path.GetFileName(filePath)}");
        StatusText = $"正在探测 {Path.GetFileName(filePath)} …";

        try
        {
            await StopInternalAsync().ConfigureAwait(true);

            // 换源片：旧的重放源立即失效（探测失败时也不会误放上一个文件）
            ReplayFactory = null;

            _sourceFilePath = filePath;
            _outputFilePath = Path.Combine(
                Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory,
                $"{Path.GetFileNameWithoutExtension(filePath)}_transcoded.mp4");

            OnPropertyChanged(nameof(SourceSummary));
            OnPropertyChanged(nameof(HasSource));
            OnPropertyChanged(nameof(OutputFilePath));
            OnPropertyChanged(nameof(TranscodeCommandText));
            OpenOutputFolderCommand.RaiseCanExecuteChanged();
            TranscodeButtonCommand.RaiseCanExecuteChanged();

            if (FfprobePath.Length == 0)
            {
                ResolveToolPaths();
            }

            FileInfo = FfprobePath.Length > 0
                ? await FfprobeService.ProbeAsync(FfprobePath, filePath).ConfigureAwait(true)
                : new MediaFileInfo { FilePath = filePath, FileName = Path.GetFileName(filePath) };

            ResetCharts();
            Statistics = StatisticsSnapshot.Empty;
            CurrentFrame = null;

            await StartSourcePlaybackAsync(filePath).ConfigureAwait(true);

            ModeText = "源片分析";
            StatusText = $"已加载 {FileInfo.FileName}（{FileInfo.VideoCodec} {FileInfo.Width}x{FileInfo.Height} {FileInfo.FrameRate:0.###}fps）";
        }
        catch (Exception ex)
        {
            StatusText = $"打开失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            HideBusyOverlay();
        }
    }

    private async Task StartSourcePlaybackAsync(string filePath)
    {
        var pipeline = await Task.Run(() => PlaybackPipeline.OpenFile(filePath, CreatePlaybackOptions())).ConfigureAwait(true);

        // 记住数据源：停止播放后按「播放」可从头重放
        _replayFactory = () => PlaybackPipeline.OpenFile(filePath, CreatePlaybackOptions());

        AttachPipeline(pipeline);
        IsPlaying = true;
        StatusText = "播放中（源片直解）";
    }

    private PlaybackOptions CreatePlaybackOptions() => new()
    {
        ExportMotionVectors = true,
        EnableAudio = true,

        // 时间轴是固定像素柱距（柱宽 5px / 缝 1px），屏幕越宽窗口跨度越长；
        // 「播放头居中」意味着右侧半屏显示的都是还没播到的未来，解码必须超前同样多，
        // 否则那半屏永远是空的。超前量目标 5.0s（窗口跨度上限 9.2s → 半屏 4.6s），
        // 23.976fps 下约 120 帧，故队列开到 140。
        // 单帧是「预览 1024 宽 BGRA + 运动矢量」约 2.4MB，148 帧峰值约 380MB。
        MaxQueuedFrames = 140,
        FramePoolCapacity = 148,
    };

    private void AttachPipeline(PlaybackPipeline pipeline)
    {
        _pipeline = pipeline;

        // 帧率此时才确定，按真实帧率重算一次时间轴跨度（柱宽与缝宽是固定值，不用动）
        UpdateTimelineSpan();

        pipeline.Volume = (float)Volume;
        pipeline.ErrorOccurred += OnPipelineError;
        pipeline.Start();

        _latestProgress = TranscodeProgress.Empty;
        _previewTimer.Start();
        _statsTimer.Start();

        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressDetailText));
        TogglePlayCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    private void OnPipelineError(Exception exception) =>
        Dispatcher.InvokeAsync(() => StatusText = $"播放错误：{exception.Message}");

    /// <summary>播放 / 暂停；管线已释放（停止过）时改为按重放源重新打开并从头播放。</summary>
    private async Task TogglePlayAsync()
    {
        // 管线为空（停止过）或已放到流尾（队列也空了）都按「从头重放」处理：
        // 在流尾继续 Resume 只会让冻结的时钟重新走起来、画面却不动 —— 正是
        // 「播放停止了时间还在走」这一现象的来源。
        if (_pipeline is null || (_pipeline.IsEndOfStream && _pipeline.QueuedFrameCount == 0))
        {
            await RestartPlaybackAsync().ConfigureAwait(true);
            return;
        }

        if (IsPlaying)
        {
            _pipeline.Pause();
            IsPlaying = false;
            StatusText = "已暂停";
        }
        else
        {
            _pipeline.Resume();
            IsPlaying = true;
            StatusText = "播放中";
        }
    }

    /// <summary>停止播放后再次播放：按当前数据源重新打开管线，从头开始。</summary>
    /// <remarks>
    /// 管线读的是不可回退的流（FFmpeg 管道 / 跟随增长的分片文件），无法 seek 回起点，
    /// 所以「重新播放」只能是重开一次数据源；图表与统计一并清空，
    /// 否则两次播放的样本会重叠在同一条时间轴上。
    /// </remarks>
    private async Task RestartPlaybackAsync()
    {
        var factory = _replayFactory;

        if (factory is null)
        {
            StatusText = "没有可重放的数据源";
            return;
        }

        IsBusy = true;
        ShowBusyOverlay("正在重新打开数据源");
        StatusText = "正在重新打开数据源…";

        try
        {
            var pipeline = await Task.Run(factory).ConfigureAwait(true);

            ResetCharts();
            Statistics = StatisticsSnapshot.Empty;
            CurrentFrame = null;

            AttachPipeline(pipeline);
            IsPlaying = true;
            StatusText = "播放中（已从头重新开始）";
        }
        catch (Exception ex)
        {
            StatusText = $"重新播放失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            HideBusyOverlay();
        }
    }

    /// <summary>重放最终产出的标准 MP4（文件存在才可用）。</summary>
    private Func<PlaybackPipeline>? TryCreateOutputReplayFactory(string outputPath) =>
        File.Exists(outputPath)
            ? () => PlaybackPipeline.OpenFile(outputPath, CreatePlaybackOptions())
            : null;

    private async Task StopAsync()
    {
        await StopInternalAsync().ConfigureAwait(true);

        // 转码仍在后台继续（这里只停了预览），状态要说清楚，避免看起来像“转码也停了”
        StatusText = IsTranscoding ? "已停止播放（转码继续）" : "已停止";
    }

    /// <summary>停止并释放全部播放资源；顺序很关键，见各步骤注释。</summary>
    /// <remarks>
    /// 只停播放、不动转码：转码由 <see cref="Transcoding.TranscodeSession"/> 的全速抽干线程独立推进，
    /// 预览读的只是镜像文件，因此停掉预览不影响转码。转码中必须保留统计定时器，
    /// 否则转码进度会停止刷新（看起来就像转码也被停了）。
    /// </remarks>
    private async Task StopInternalAsync()
    {
        _previewTimer.Stop();

        if (!IsTranscoding)
        {
            _statsTimer.Stop();
        }

        var pipeline = _pipeline;
        _pipeline = null;
        _previewFrame = null;
        IsPlaying = false;

        if (pipeline is not null)
        {
            pipeline.ErrorOccurred -= OnPipelineError;

            // 管线析构会 join 解码线程，放到线程池避免卡 UI 线程
            await Task.Run(pipeline.Dispose).ConfigureAwait(true);
        }

        RequestPreviewRepaint();
        TogglePlayCommand.RaiseCanExecuteChanged();
        StopCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------
    // 转码参数（分辨率 / 码流 / 音频格式）
    // ------------------------------------------------------------------

    /// <summary>分辨率预设；下拉可编辑，直接输入「宽x高」即为自定义。</summary>
    public string[] ResolutionPresets { get; } =
        ["保持源分辨率", "3840x2160", "2560x1440", "1920x1080", "1280x720", "854x480"];

    private string _resolutionText = "保持源分辨率";

    public string ResolutionText
    {
        get => _resolutionText;
        set => SetProperty(ref _resolutionText, value);
    }

    /// <summary>码流预设（kbps）。下拉可编辑，直接输入数字即为自定义码率；是否启用见 <see cref="IsBitrateValueEnabled"/>。</summary>
    public string[] BitratePresets { get; } =
        ["2000", "4000", "6000", "8000", "12000", "20000"];

    private string _bitrateText = "4000";

    public string BitrateText
    {
        get => _bitrateText;
        set => SetProperty(ref _bitrateText, value);
    }

    /// <summary>H.264 profile；「自动」= 交给编码器默认（H.264 通常为 High）。</summary>
    public string[] ProfileOptions { get; } = ["自动", "Baseline", "Main", "High"];

    private string _selectedProfile = "自动";

    public string SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    /// <summary>速度/质量档位；「自动」= 各家默认档（nvenc p4 / QSV、x264 medium / AMF balanced）。</summary>
    public string[] SpeedOptions { get; } = ["自动", "最快", "较快", "中等", "较慢", "最慢"];

    private string _selectedSpeed = "自动";

    public string SelectedSpeed
    {
        get => _selectedSpeed;
        set => SetProperty(ref _selectedSpeed, value);
    }

    /// <summary>调谐；QSV / AMF 不支持该参数（选了会被忽略）。</summary>
    public string[] TuneOptions { get; } = ["自动", "胶片", "动画", "低延迟"];

    private string _selectedTune = "自动";

    public string SelectedTune
    {
        get => _selectedTune;
        set => SetProperty(ref _selectedTune, value);
    }

    /// <summary>输出帧率预设；「自动（保持源）」= 保持源帧率（不传 -r）。下拉可编辑，可直接输入自定义帧率。</summary>
    public string[] FrameRateOptions { get; } =
        ["自动（保持源）", "23.976", "24", "25", "30", "50", "60", "120"];

    private string _frameRateText = "自动（保持源）";

    /// <summary>
    /// 输出帧率（可编辑）：预设标签或自定义值（<c>120</c> / <c>29.97</c> / <c>30000/1001</c>）。
    /// 无法识别时按「保持源帧率」处理。
    /// </summary>
    public string FrameRateText
    {
        get => _frameRateText;
        set
        {
            if (SetProperty(ref _frameRateText, value))
            {
                OnPropertyChanged(nameof(TranscodeCommandText));
            }
        }
    }

    /// <summary>缩放质量（对应 swscale 采样算法：双线性 / 双三次 / Lanczos）。</summary>
    public string[] ScaleQualityOptions { get; } = ["良好（快）", "更好", "最好"];

    private string _selectedScaleQuality = "更好";

    public string SelectedScaleQuality
    {
        get => _selectedScaleQuality;
        set => SetProperty(ref _selectedScaleQuality, value);
    }

    /// <summary>目标画幅的适配方式：保持比例加框 / 拉伸铺满 / 裁剪铺满。</summary>
    public string[] ResizeModeOptions { get; } = ["保持比例加框", "拉伸铺满", "裁剪铺满"];

    private string _selectedResizeMode = "保持比例加框";

    public string SelectedResizeMode
    {
        get => _selectedResizeMode;
        set => SetProperty(ref _selectedResizeMode, value);
    }

    /// <summary>编码类型：单步 / 双步（多步）；双步仅 NVENC、QSV 有对应参数，其余编码器忽略。</summary>
    public string[] EncodingTypeOptions { get; } = ["单步", "双步（多步）"];

    private string _selectedEncodingType = "单步";

    public string SelectedEncodingType
    {
        get => _selectedEncodingType;
        set => SetProperty(ref _selectedEncodingType, value);
    }

    /// <summary>音频比特率预设（kbps）；下拉可编辑。</summary>
    public string[] AudioBitratePresets { get; } = ["64", "96", "128", "192", "256", "320"];

    /// <summary>音频比特率（kbps）。</summary>
    private string _audioBitrateText = "128";

    public string AudioBitrateText
    {
        get => _audioBitrateText;
        set => SetProperty(ref _audioBitrateText, value);
    }

    /// <summary>音频采样率；「自动」= 保持源。</summary>
    public string[] AudioSampleRateOptions { get; } = ["自动（保持源）", "32000", "44100", "48000", "96000"];

    private string _selectedAudioSampleRate = "自动（保持源）";

    public string SelectedAudioSampleRate
    {
        get => _selectedAudioSampleRate;
        set => SetProperty(ref _selectedAudioSampleRate, value);
    }

    /// <summary>音频声道；「自动」= 保持源。</summary>
    public string[] AudioChannelOptions { get; } = ["自动（保持源）", "单声道", "立体声", "5.1"];

    private string _selectedAudioChannels = "自动（保持源）";

    public string SelectedAudioChannels
    {
        get => _selectedAudioChannels;
        set => SetProperty(ref _selectedAudioChannels, value);
    }

    /// <summary>音频格式预设；「复制源音频」不做重编码。</summary>
    public string[] AudioFormatOptions { get; } =
        ["AAC（默认）", "MP3", "AC3", "FLAC", "复制源音频"];

    private string _selectedAudioFormat = "AAC（默认）";

    public string SelectedAudioFormat
    {
        get => _selectedAudioFormat;
        set => SetProperty(ref _selectedAudioFormat, value);
    }

    /// <summary>
    /// 解析分辨率输入：「保持源分辨率」或非法输入返回 null；
    /// 自定义「宽x高」向下取偶（编码器要求宽高为偶数）。
    /// </summary>
    private (int Width, int Height)? ParseTargetResolution()
    {
        var text = ResolutionText?.Trim();
        if (string.IsNullOrEmpty(text) || text.StartsWith("保持", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = text.Split('x', 'X', '×');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var width)
            || !int.TryParse(parts[1], out var height))
        {
            return null;
        }

        width = Math.Max(16, width & ~1);
        height = Math.Max(16, height & ~1);
        return (width, height);
    }

    /// <summary>解析码流输入：「质量模式」或非法输入返回 null；兼容 2000k / 2000kbps / 2000 写法。</summary>
    private int? ParseVideoBitrateKbps()
    {
        var text = BitrateText?.Trim();
        if (string.IsNullOrEmpty(text) || text.Contains("质量", StringComparison.Ordinal))
        {
            return null;
        }

        text = text.EndsWith("kbps", StringComparison.OrdinalIgnoreCase)
            ? text[..^4]
            : text.TrimEnd('k', 'K');

        return int.TryParse(text, out var kbps) && kbps >= 100 ? kbps : null;
    }

    private static string MapScaleQuality(string label) => label switch
    {
        "良好（快）" => "fast",
        "最好" => "best",
        _ => "good",
    };

    private static string MapResizeMode(string label) => label switch
    {
        "拉伸铺满" => "stretch",
        "裁剪铺满" => "crop",
        _ => "fit",
    };

    private static int? MapAudioSampleRate(string? label) =>
        int.TryParse(label, out var rate) ? rate : null;

    private static int? MapAudioChannels(string label) => label switch
    {
        "单声道" => 1,
        "立体声" => 2,
        "5.1" => 6,
        _ => null,
    };

    /// <summary>界面标签 → TranscodeRequest 的规范化取值（「自动」一律返回 null，表示交给编码器默认）。</summary>
    private static string? MapProfile(string label) => label switch
    {
        "Baseline" => "baseline",
        "Main" => "main",
        "High" => "high",
        _ => null,
    };

    /// <summary>把编码类型显示名映射回枚举（与 <see cref="VideoCodecLabel"/> 互逆）；仅在未探测到条目时兜底。</summary>
    private static VideoCodecKind MapVideoCodec(string label) => label switch
    {
        "H.265 (HEVC)" => VideoCodecKind.Hevc,
        "H.266 (VVC)" => VideoCodecKind.Vvc,
        "AV1" => VideoCodecKind.Av1,
        "VP9" => VideoCodecKind.Vp9,
        _ => VideoCodecKind.H264,
    };

    private static string? MapSpeed(string label) => label switch
    {
        "最快" => "fastest",
        "较快" => "fast",
        "中等" => "medium",
        "较慢" => "slow",
        "最慢" => "slowest",
        _ => null,
    };

    private static string? MapTune(string label) => label switch
    {
        "胶片" => "film",
        "动画" => "animation",
        "低延迟" => "lowlatency",
        _ => null,
    };

    private static string? MapFrameRate(string label) => label switch
    {
        "23.976" => "24000/1001",
        "24" => "24",
        "25" => "25",
        "30" => "30",
        "50" => "50",
        "60" => "60",
        _ => null,
    };

    /// <summary>把音频格式选择映射成 FFmpeg 编码器名。</summary>
    private string MapAudioCodec() => SelectedAudioFormat switch
    {
        "MP3" => "mp3",
        "AC3" => "ac3",
        "FLAC" => "flac",
        "复制源音频" => "copy",
        _ => "aac",
    };

    // ------------------------------------------------------------------
    // 转码
    // ------------------------------------------------------------------

    /// <param name="enableLiveStream">
    /// 是否启用“边转边播”。该能力依赖 FFmpeg 共享库，
    /// 缺失时自动退化为“只转码落盘”（不含实时预览），保证功能可用而不是直接失败。
    /// </param>
    private TranscodeRequest BuildTranscodeRequest(bool enableLiveStream) => new()
    {
        InputPath = _sourceFilePath,
        OutputPath = _outputFilePath,
        FfmpegPath = FfmpegPath,
        Encoder = SelectedEncoder?.Kind ?? EncoderKind.Software,
        VideoCodec = SelectedCodecEntry?.Codec ?? MapVideoCodec(SelectedVideoCodecType),
        VideoEncoderName = SelectedCodecEntry?.CodecName,
        Quality = (int)Math.Round(Quality),
        GopSize = 180,
        EnableLiveStream = enableLiveStream,
        Overwrite = true,

        // 可选参数：帧大小 / 画幅适配 / 编码档位 / 帧率 / 音频（无效输入按「保持默认」处理）
        TargetResolution = ParseTargetResolution() is { } size ? $"{size.Width}x{size.Height}" : null,
        ResizeMode = MapResizeMode(SelectedResizeMode),
        ScaleQuality = MapScaleQuality(SelectedScaleQuality),
        // 只有「指定码流」模式才传目标码率，否则走质量模式（CRF/CQ）
        VideoBitrateKbps = IsQualityMode ? null : ParseVideoBitrateKbps(),
        Profile = MapProfile(SelectedProfile),
        SpeedPreset = MapSpeed(SelectedSpeed),
        Tune = MapTune(SelectedTune),
        OutputFrameRate = MapFrameRate(FrameRateText),
        TwoPass = SelectedEncodingType.StartsWith("双步", StringComparison.Ordinal),
        AudioCodec = MapAudioCodec(),
        AudioBitrate = int.TryParse(AudioBitrateText?.Trim(), out var audioBitrate) && audioBitrate >= 8
            ? audioBitrate
            : 128,
        AudioSampleRate = MapAudioSampleRate(SelectedAudioSampleRate),
        AudioChannels = MapAudioChannels(SelectedAudioChannels),
    };

    /// <summary>转码按钮的统一入口：未转码时开始，转码中则询问用户后停止。</summary>
    private Task OnTranscodeButtonAsync() =>
        IsTranscoding ? RequestStopTranscodeAsync() : StartTranscodeAsync();

    /// <summary>停止转码前询问用户如何处理「已转码的部分」，再据此收尾。</summary>
    /// <remarks>
    /// 对话框由 View 注入到 <see cref="ConfirmStopTranscode"/>（ViewModel 不直接创建窗口）；
    /// 未注入时按「保留已转码部分」处理，避免误删用户数据。
    /// </remarks>
    private async Task RequestStopTranscodeAsync()
    {
        var choice = ConfirmStopTranscode?.Invoke() ?? StopTranscodeChoice.KeepTranscoded;

        if (choice == StopTranscodeChoice.Continue)
        {
            StatusText = "已取消停止，转码继续";
            return;
        }

        await CancelTranscodeAsync(choice == StopTranscodeChoice.KeepTranscoded).ConfigureAwait(true);
    }

    private async Task StartTranscodeAsync()
    {
        if (!HasSource)
        {
            StatusText = "请先打开源片";
            return;
        }

        // 「指定码流」模式下必须给出有效码率：否则会退化成质量模式，而质量此时已归零（-cq 0）
        if (!IsQualityMode && ParseVideoBitrateKbps() is null)
        {
            StatusText = "码流无效：请填 100 以上的数字（kbps），或把码控切回质量模式";
            return;
        }

        IsBusy = true;
        StatusText = "正在启动 FFmpeg 转码进程…";

        try
        {
            await StopInternalAsync().ConfigureAwait(true);

            // 换数据源（源片分析 → 转码流）：清空上一会话的曲线、统计与当前帧。
            // 图表时间轴是全局唯一的（X = 媒体时间），转码流的时间轴从 0 重新递增，
            // 不清空的话源片阶段的旧样本会与新样本叠在同一条时间轴上 —— 表现为两条曲线。
            ResetCharts();
            Statistics = StatisticsSnapshot.Empty;
            CurrentFrame = null;

            // 共享库决定能否“边转边播”；缺失时退化为纯转码，而不是直接拒绝服务
            var liveEnabled = FfmpegRuntime.IsAvailable;
            var request = BuildTranscodeRequest(liveEnabled);

            // 进度按帧数算时的分母必须是「预期的输出帧数」：改了帧速率（如 24 → 60）后
            // 输出帧数是源片的 2.5 倍，仍拿源片帧数当分母，进度会在真正转完之前就冲到 100%
            // （实测反馈：显示 100.0% / frame 10915，而转码仍在继续）。
            var session = await TranscodeSession.StartAsync(
                request,
                FileInfo.Duration.TotalSeconds,
                ResolveExpectedOutputFrameCount(request)).ConfigureAwait(true);

            _transcodeSession = session;
            _outputFilePath = session.OutputFilePath;
            OnPropertyChanged(nameof(OutputFilePath));
            OpenOutputFolderCommand.RaiseCanExecuteChanged();

            session.ProgressChanged += OnTranscodeProgress;

            if (!liveEnabled)
            {
                // 降级路径：不读取 stdout（无共享库无法解复用），只等进程结束与收尾重封装
                IsTranscoding = true;
                ModeText = "实时转码（无实时预览）";
                StatusText = "转码中：FFmpeg 共享库缺失，已跳过边转边播，仅输出标准 MP4";
                _ = WatchTranscodeAsync(session);
                return;
            }

            // 立刻在后台打开 stdout：avformat_open_input 会阻塞等待首段分片数据，
            // 期间 FFmpeg 可能因管道写满而停滞，因此必须尽快开始读取。
            StatusText = "正在解析转码输出流（等待首个分片）…";

            var stream = session.LiveStream;
            var pipeline = await Task.Run(() => PlaybackPipeline.OpenStream(stream, CreatePlaybackOptions()))
                .WaitAsync(TimeSpan.FromSeconds(30))
                .ConfigureAwait(true);

            // 停止播放后重放同一份镜像（边转边播期间镜像文件一直在写，会话会按需再给一个跟随流）
            ReplayFactory = () => PlaybackPipeline.OpenStream(session.LiveStream, CreatePlaybackOptions());

            AttachPipeline(pipeline);
            IsPlaying = true;
            IsTranscoding = true;
            ModeText = "实时转码（边转边播）";

            StatusText = "转码中：stdout 分片流实时播放，分片同时镜像留存，结束后自动重封装为标准 MP4";

            _ = WatchTranscodeAsync(session);
        }
        catch (Exception ex)
        {
            StatusText = $"转码启动失败：{ex.Message}";
            await CancelTranscodeAsync(keepPartial: true).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// 推算预期的输出总帧数：保持源帧率时等于源片帧数；改了帧速率则按时长折算。
    /// </summary>
    /// <remarks>
    /// 用时长折算而不是「源帧数 × 目标帧率 ÷ 源帧率」，是为了对源片帧数（容器里的 nb_frames）
    /// 不准的情况更宽容：时长通常比总帧数可靠。
    /// </remarks>
    private long ResolveExpectedOutputFrameCount(TranscodeRequest request)
    {
        var sourceFrames = FileInfo.FrameCount;
        var duration = FileInfo.Duration.TotalSeconds;

        // 指定了输出帧率（含自定义值）：输出一定是 CFR，按 时长 × 目标帧率 折算
        if (ParseFrameRate(request.OutputFrameRate) is { } outputFps && duration > 0)
        {
            return (long)Math.Ceiling(duration * outputFps);
        }

        // 保持源帧率：优先用容器里的总帧数（VFR 源也准）。
        // ⚠ 有些容器没写 nb_frames（总帧数为 0），此时按 时长 × 源帧率 折算 ——
        // 否则进度会退化成只靠时间估算，高帧率源片下会明显偏慢。
        if (sourceFrames <= 0 && duration > 0 && FileInfo.FrameRate > 0.01)
        {
            return (long)Math.Ceiling(duration * FileInfo.FrameRate);
        }

        return sourceFrames;
    }

    /// <summary>解析帧速率文本（<c>"60"</c> 或 <c>"24000/1001"</c>）；无效或未指定返回 null。</summary>
    private static double? ParseFrameRate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Split('/');

        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator > 0)
        {
            var value = numerator / denominator;
            return value > 0.01 ? value : null;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var fps) && fps > 0.01
            ? fps
            : null;
    }

    /// <summary>监听转码进程退出，并在收尾阶段产出标准 MP4。</summary>
    private async Task WatchTranscodeAsync(TranscodeSession session)
    {
        var exitCode = await session.Completion.ConfigureAwait(true);

        if (!ReferenceEquals(_transcodeSession, session))
        {
            return;
        }

        IsTranscoding = false;
        _latestProgress = new TranscodeProgress { IsFinal = true, Percent = 100 };

        OnPropertyChanged(nameof(ProgressPercent));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(ProgressDetailText));
        TranscodeButtonCommand.RaiseCanExecuteChanged();

        if (exitCode != 0)
        {
            StatusText = $"转码结束（退出码 {exitCode}）：{FirstLine(session.CapturedLog)}";

            await StopInternalAsync().ConfigureAwait(true);
            await session.DisposeAsync().ConfigureAwait(true);

            // 失败的会话不产出可重放的成品（镜像已随会话清理）
            ReplayFactory = TryCreateOutputReplayFactory(session.OutputFilePath);

            if (ReferenceEquals(_transcodeSession, session))
            {
                _transcodeSession = null;
            }

            return;
        }

        // 收尾期间禁止再次发起任务（重封装同样需要 ffmpeg 进程）
        IsBusy = true;

        try
        {
            // 只需等「stdout 全速抽干」结束：镜像分片文件的完整性由抽干线程保证，
            // 与播放端无关（转码已与播放解耦，进程退出只代表编码完成，管道里可能还剩尾巴）。
            await session.WaitForDrainAsync().ConfigureAwait(true);

            // 刻意**不**在这里停止播放：转码已跑完，预览仍按实时把镜像里剩余的内容放完
            // （进度条上即为「红色转码跑在前面、蓝色播放随后跟上」）。
            StatusText = "转码完成，正在重封装为标准 MP4…";
            var finalized = await session.FinalizeAsync().ConfigureAwait(true);

            StatusText = finalized
                ? $"转码完成：{session.OutputFilePath}"
                : $"转码完成，但标准 MP4 重封装失败（{session.FinalizeError ?? "原因未知"}）";

            await session.DisposeAsync().ConfigureAwait(true);

            // 转码结束、镜像已删除：之后「播放」重放的是最终产出的标准 MP4
            ReplayFactory = TryCreateOutputReplayFactory(session.OutputFilePath);
        }
        finally
        {
            IsBusy = false;

            if (ReferenceEquals(_transcodeSession, session))
            {
                _transcodeSession = null;
            }
        }
    }

    private void OnTranscodeProgress(TranscodeProgress progress)
    {
        // 后台线程回调：只写字段，由 1Hz 的统计定时器统一刷新到界面（避免跨线程访问 UI）
        _latestProgress = progress;
    }

    /// <param name="keepPartial">
    /// true = 停止后把已转码部分重封装为标准 MP4 保存；false = 停止并删除已转码的临时文件。
    /// </param>
    private async Task CancelTranscodeAsync(bool keepPartial)
    {
        var session = _transcodeSession;
        _transcodeSession = null;

        if (session is not null)
        {
            session.ProgressChanged -= OnTranscodeProgress;
        }

        // 先落 IsTranscoding，StopInternalAsync 才会连统计定时器一起停
        IsTranscoding = false;
        ModeText = "源片分析";

        // 停止转码要连预览一起停：预览读的正是这次转码的镜像文件
        await StopInternalAsync().ConfigureAwait(true);

        if (session is null)
        {
            StatusText = "已取消转码";
            return;
        }

        IsBusy = true;

        try
        {
            session.Cancel();

            if (!keepPartial)
            {
                // 未收尾时 DisposeAsync 会删除临时分片文件，不再产出任何输出
                await session.DisposeAsync().ConfigureAwait(true);

                _latestProgress = TranscodeProgress.Empty;
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ProgressDetailText));

                // 已转码内容被删除，镜像也没了：没有可重放的源
                ReplayFactory = null;

                StatusText = "已停止转码，已删除已转码文件";
                return;
            }

            // 保留：尽力把已转码的部分重封装出来，避免用户白等
            var finalized = await session.FinalizeAsync().ConfigureAwait(true);
            await session.DisposeAsync().ConfigureAwait(true);

            StatusText = finalized
                ? $"已停止转码，已保存已转码部分：{session.OutputFilePath}"
                : "已停止转码（已转码部分为空，未生成文件）";

            // 已保存的成品可作为重放源
            ReplayFactory = TryCreateOutputReplayFactory(session.OutputFilePath);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OpenOutputFolder()
    {
        var folder = Path.GetDirectoryName(_outputFilePath);

        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            StatusText = "输出目录不存在";
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{_outputFilePath}\"",
            UseShellExecute = true,
        });
    }

    // ------------------------------------------------------------------
    // 定时器：预览与统计
    // ------------------------------------------------------------------

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        var pipeline = _pipeline;
        if (pipeline is null)
        {
            return;
        }

        var clock = pipeline.Clock.PositionSeconds;

        if (pipeline.TryTakeFrame(clock, out var frame) && frame is not null)
        {
            // UI 线程内交换，先归还上一帧再持有新帧
            var previous = _previewFrame;
            _previewFrame = frame;
            pipeline.ReleaseFrame(previous);

            CurrentFrame = frame.Info;

            // 时间轴窗口随当前帧向左滑动（播放头始终停在窗口正中）
            UpdateTimelineWindow(frame.Info.Time);
            RequestPreviewRepaint();
        }

        // 播放头处的时间读数跟着帧刷新：只靠 1Hz 统计定时器的话，播放头上的时间会一秒一跳
        PositionText = FormatTime(ResolveDisplayPosition(pipeline));

        // 逐帧把采样点搬入图表：滑动窗口是逐帧推进的，
        // 若只在 1Hz 统计定时器里搬运，最新 1 秒会周期性留空再整批弹出。
        PumpChartSamples();

        // ⚠ 每 tick 末尾无条件显式触发图表 measure（静默集合不发集合通知，
        // LiveCharts 的自动更新通路已关闭）：即使本 tick 没有新采样点，
        // 时间轴窗口也随主时钟连续滑动，9 条 X 轴上下限每 tick 都在变。
        // Throttling=false 走 ForceCall，没有 8ms 的 Task.Delay 续体，重画节拍不再抖。
        RequestChartsUpdate();

        if (pipeline.IsEndOfStream && pipeline.QueuedFrameCount == 0)
        {
            _previewTimer.Stop();
            IsPlaying = false;

            // ⚠ 必须冻结主时钟：音频早已结束，PlaybackClock 会退化成墙钟继续计时，
            // 位置随之无限增长（实测反馈：播放结束后时间还在走，最后显示成
            // 「04:48 / 03:43」这种越界读数）。冻结在当前位置即可。
            pipeline.Clock.Pause();

            if (!IsTranscoding)
            {
                StatusText = "播放结束";
            }
        }
    }

    /// <summary>
    /// 主时钟位置 → 界面时间读数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 播放<b>真正结束</b>（流尾 + 已无待渲染帧）时把读数对齐到媒体时长，原因是音频主时钟
    /// 天然比容器时长少一截：
    /// <c>位置 = 已入队音频末端 − 缓冲中未播部分 − 设备内部缓冲</c>，
    /// 最后一段音频播完时，少的正好是一个设备缓冲（100ms × 2 = 200ms）。
    /// 不补的话终点会显示成「03:43.230 / 03:43.399」，播放进度条也永远差最后 0.08%。
    /// </para>
    /// <para>
    /// 播放中不做上界钳制：容器时长偶尔略小于实际媒体，播放中就钳会让读数提前冻住。
    /// </para>
    /// </remarks>
    private double ResolveDisplayPosition(PlaybackPipeline pipeline)
    {
        var position = pipeline.Clock.PositionSeconds;

        if (FileInfo.Duration <= TimeSpan.Zero)
        {
            return position;
        }

        return pipeline.IsEndOfStream && pipeline.QueuedFrameCount == 0
            ? FileInfo.Duration.TotalSeconds
            : position;
    }

    private void OnStatsTick(object? sender, EventArgs e)
    {
        // 播放已停止但转码仍在继续时，管线为空也必须照常刷新转码进度
        if (_pipeline is { } pipeline)
        {
            Statistics = pipeline.Statistics.Snapshot();
            PumpChartSamples();

            var position = ResolveDisplayPosition(pipeline);

            PositionText = FormatTime(position);
            _lastPositionSeconds = position;
            OnPropertyChanged(nameof(PlaybackPercent));
            OnPropertyChanged(nameof(PlaybackPercentText));

            OnPropertyChanged(nameof(FrameQpText));
            OnPropertyChanged(nameof(FrameHeaderText));
            OnPropertyChanged(nameof(QpChartTitle));
        }

        if (IsTranscoding)
        {
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(ProgressDetailText));
        }
    }

    private void RequestPreviewRepaint() => PreviewInvalidated?.Invoke();

    /// <summary>由 View 在 SKElement.PaintSurface 中调用。</summary>
    public void RenderPreview(SKCanvas canvas, SKImageInfo info)
    {
        // 播放已停止（管线已释放）时不能再说“正在缓冲”，否则看起来像卡住了
        var placeholder = !HasSource
            ? EmptyPlaceholder
            : _pipeline is null
                ? "已停止播放"
                : "正在缓冲…（等待首帧）";
        VideoFrameRenderer.Render(canvas, info, _previewFrame, _overlayRenderer, placeholder);
    }

    private static string QpText(int qp) => qp > 0 ? qp.ToString() : "n/a";

    private static string FormatTime(double seconds) =>
        TimeSpan.FromSeconds(Math.Max(0, seconds)).ToString(@"mm\:ss\.fff");

    private static string FirstLine(string text)
    {
        var line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(line) ? "无输出" : line.Trim();
    }

    public void Dispose()
    {
        _previewTimer.Stop();
        _statsTimer.Stop();
        _previewTimer.Tick -= OnPreviewTick;
        _statsTimer.Tick -= OnStatsTick;

        var pipeline = _pipeline;
        _pipeline = null;
        pipeline?.Dispose();

        var session = _transcodeSession;
        _transcodeSession = null;

        if (session is not null)
        {
            session.ProgressChanged -= OnTranscodeProgress;
            session.Cancel();
            _ = session.DisposeAsync();
        }

        _overlayRenderer.Dispose();
    }
}
