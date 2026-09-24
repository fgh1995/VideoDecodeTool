namespace VideoDecodeTool.Models;

/// <summary>转码所使用的编码器后端。</summary>
public enum EncoderKind
{
    /// <summary>NVIDIA NVENC（需要 GTX 10 系及以上 + 470 以上驱动）。</summary>
    Nvenc = 0,

    /// <summary>Intel Quick Sync Video（需要 Intel 核显或 Arc 独显）。</summary>
    Qsv = 1,

    /// <summary>AMD Advanced Media Framework（需要 AMD 显卡 + 较新驱动）。</summary>
    Amf = 2,

    /// <summary>软件编码 libx264，作为无可用 GPU 时的兜底。</summary>
    Software = 3,
}

/// <summary>编码类型（编解码格式）；枚举顺序即界面下拉的排序依据（H.264 → H.265 → H.266 → AV1 → VP9）。</summary>
public enum VideoCodecKind
{
    /// <summary>H.264 / AVC。</summary>
    H264 = 0,

    /// <summary>H.265 / HEVC。</summary>
    Hevc = 1,

    /// <summary>H.266 / VVC（仅个别 FFmpeg 构建带 libvvenc）。</summary>
    Vvc = 2,

    /// <summary>AV1。</summary>
    Av1 = 3,

    /// <summary>VP9。</summary>
    Vp9 = 4,
}

/// <summary>某个 GPU 编码器的探测结果。</summary>
public sealed class GpuEncoderInfo
{
    public required EncoderKind Kind { get; init; }

    /// <summary>该条目对应的编码类型（同一后端会按 h264/hevc/av1 各探测一条）。</summary>
    public VideoCodecKind Codec { get; init; } = VideoCodecKind.H264;

    /// <summary>FFmpeg 编码器名，例如 h264_nvenc。</summary>
    public required string CodecName { get; init; }

    /// <summary>界面显示名。</summary>
    public required string DisplayName { get; init; }

    /// <summary>FFmpeg 是否编译了该编码器。</summary>
    public bool IsSupported { get; init; }

    /// <summary>功能性试编码是否通过（构建 5 帧测试流）。</summary>
    public bool IsFunctional { get; init; }

    /// <summary>
    /// 是否已做过功能性试编码。
    /// </summary>
    /// <remarks>
    /// 启动时为了秒开只做「能力枚举」（<c>ffmpeg -encoders</c>），此时只能证明
    /// <b>FFmpeg 编译了这个编码器</b>，不能证明本机驱动/硬件真的能跑 —— 所以未验证时
    /// 状态必须区别于「可用」，否则三种编码器会一起显示成「可用」，误导用户。
    /// </remarks>
    public bool IsVerified { get; init; }

    /// <summary>探测到的问题描述。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>是否可用（既被支持，又通过试编码）。</summary>
    public bool IsAvailable => IsSupported && IsFunctional;

    public string StatusText => !IsSupported
        ? "未编译"
        : !IsVerified
            ? "可用（未试编码）"
            : IsFunctional
                ? "可用"
                : $"不可用：{Message}";

    public override string ToString() => $"{DisplayName} ({CodecName}) - {StatusText}";
}

/// <summary>整机 GPU / FFmpeg 能力快照。</summary>
public sealed class GpuCapabilities
{
    public required IReadOnlyList<GpuEncoderInfo> Encoders { get; init; }

    /// <summary>ffmpeg -hwaccels 报告的解码硬件加速后端。</summary>
    public required IReadOnlyList<string> HardwareAccelerations { get; init; }

    /// <summary>ffmpeg -version 首行。</summary>
    public string FfmpegVersion { get; init; } = string.Empty;

    public static GpuCapabilities Empty { get; } = new()
    {
        Encoders = Array.Empty<GpuEncoderInfo>(),
        HardwareAccelerations = Array.Empty<string>(),
    };
}

/// <summary>一次转码任务的参数。</summary>
public sealed class TranscodeRequest
{
    /// <summary>源文件路径。</summary>
    public required string InputPath { get; init; }

    /// <summary>标准 MP4 落盘路径。</summary>
    public required string OutputPath { get; init; }

    /// <summary>ffmpeg.exe 路径。</summary>
    public required string FfmpegPath { get; init; }

    /// <summary>目标编码器。</summary>
    public EncoderKind Encoder { get; init; } = EncoderKind.Nvenc;

    /// <summary>目标编码类型（编解码格式）；实际 -c:v 名称由（后端 × 编码类型）共同决定。</summary>
    public VideoCodecKind VideoCodec { get; init; } = VideoCodecKind.H264;

    /// <summary>
    /// FFmpeg 视频编码器名（如 hevc_nvenc），直接来自探测结果；
    /// null = 未探测到该组合，由参数构造器按（后端 × 编码类型）推断兜底。
    /// </summary>
    public string? VideoEncoderName { get; init; }

    /// <summary>质量参数：NVENC cq / QSV global_quality / x264 crf；CQP 模式下作为基准 QP。</summary>
    public int Quality { get; init; } = 23;

    /// <summary>固定 GOP 长度。</summary>
    public int GopSize { get; init; } = 180;

    /// <summary>音频码率（kbps）。</summary>
    public int AudioBitrate { get; init; } = 128;

    /// <summary>目标分辨率（“宽x高”，如 1920x1080）；null = 保持源分辨率。</summary>
    public string? TargetResolution { get; init; }

    /// <summary>视频目标码率（kbps）；null = 跟随质量模式（CRF/CQ）。</summary>
    public int? VideoBitrateKbps { get; init; }

    /// <summary>H.264 profile（baseline / main / high）；null = 交给编码器默认。</summary>
    public string? Profile { get; init; }

    /// <summary>速度/质量档位（fastest / fast / medium / slow / slowest）；null = 编码器默认档。</summary>
    public string? SpeedPreset { get; init; }

    /// <summary>调谐（film / animation / lowlatency）；null = 不指定（QSV/AMF 无此参数）。</summary>
    public string? Tune { get; init; }

    /// <summary>输出帧率（如 <c>24</c>、<c>24000/1001</c>）；null = 保持源帧率。</summary>
    public string? OutputFrameRate { get; init; }

    /// <summary>缩放质量（fast / good / best）→ swscale 采样算法；null = good（双三次）。</summary>
    public string? ScaleQuality { get; init; }

    /// <summary>目标画幅适配方式（fit / stretch / crop）；null = fit（保持比例 + 补黑边）。</summary>
    public string? ResizeMode { get; init; }

    /// <summary>是否双步编码（NVENC multipass / QSV look-ahead；其它编码器忽略）。</summary>
    public bool TwoPass { get; init; }

    /// <summary>音频采样率（Hz）；null = 保持源。</summary>
    public int? AudioSampleRate { get; init; }

    /// <summary>音频声道数；null = 保持源。</summary>
    public int? AudioChannels { get; init; }

    /// <summary>音频编码器（FFmpeg 编码器名）；"copy" = 直接复制源音频流。</summary>
    public string AudioCodec { get; init; } = "aac";

    /// <summary>是否同时把 fMP4 分片流写到 stdout，实现“边转边播”。</summary>
    public bool EnableLiveStream { get; init; } = true;

    /// <summary>目标时长（秒），0 表示整片；用于“只转一小段”的调试场景。</summary>
    public double LimitSeconds { get; init; }

    public bool Overwrite { get; init; } = true;
}

/// <summary>解码得到的视频流参数。</summary>
public sealed class VideoStreamInfo
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    public required double FrameRate { get; init; }

    public required string PixelFormat { get; init; }

    public required string CodecName { get; init; }
}

/// <summary>解码得到的音频流参数。</summary>
public sealed class AudioStreamInfo
{
    public required int SampleRate { get; init; }

    public required int Channels { get; init; }

    public required string CodecName { get; init; }
}
