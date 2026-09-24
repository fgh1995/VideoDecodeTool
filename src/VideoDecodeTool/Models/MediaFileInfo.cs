namespace VideoDecodeTool.Models;

/// <summary>
/// ffprobe 一次性探测得到的容器 / 视频流 / 音频流信息，用于右侧“文件信息面板”。
/// </summary>
public sealed class MediaFileInfo
{
    public string FilePath { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    /// <summary>容器格式短名，例如 matroska,webm / mov,mp4,m4a,3gp,3g2,mj2。</summary>
    public string FormatName { get; init; } = string.Empty;

    public string FormatLongName { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public TimeSpan Duration { get; init; }

    public long BitRate { get; init; }

    public long FrameCount { get; init; }

    // ---- 视频流 ----

    public string VideoCodec { get; init; } = string.Empty;

    public string VideoProfile { get; init; } = string.Empty;

    public int VideoLevel { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string PixelFormat { get; init; } = string.Empty;

    public string SampleAspectRatio { get; init; } = string.Empty;

    public string DisplayAspectRatio { get; init; } = string.Empty;

    public double FrameRate { get; init; }

    public bool Progressive { get; init; }

    public string ColorPrimaries { get; init; } = string.Empty;

    public string ColorTransfer { get; init; } = string.Empty;

    public string ColorSpace { get; init; } = string.Empty;

    // ---- 音频流 ----

    public string AudioCodec { get; init; } = string.Empty;

    public int AudioSampleRate { get; init; }

    public int AudioChannels { get; init; }

    public string AudioChannelLayout { get; init; } = string.Empty;

    public string AudioSampleFormat { get; init; } = string.Empty;

    public bool HasAudio => !string.IsNullOrEmpty(AudioCodec);

    /// <summary>VIDEO 行，例如 “h264 (High L51)  1920x1080 yuv420p”。</summary>
    public string VideoSummary =>
        $"{VideoCodec} ({ProfileWithLevel})  {Width}x{Height} {PixelFormat}";

    /// <summary>PROFILE 括号内容，例如 “High L51”。</summary>
    public string ProfileWithLevel
    {
        get
        {
            var profile = string.IsNullOrEmpty(VideoProfile) ? "unknown" : VideoProfile;
            return VideoLevel > 0 ? $"{profile} L{VideoLevel}" : profile;
        }
    }

    /// <summary>第二行视频信息，例如 “SAR 1:1  DAR 16:9  23.976 fps  progressive”。</summary>
    public string VideoLayoutSummary =>
        $"SAR {SampleAspectRatio}  DAR {DisplayAspectRatio}  {FrameRate:0.###} fps  " +
        (Progressive ? "progressive" : "interlaced");

    /// <summary>COLOR 行。</summary>
    public string ColorSummary =>
        $"{ColorPrimaries} / {ColorTransfer} / {ColorSpace}";

    /// <summary>LENGTH 行。</summary>
    public string LengthSummary =>
        $"{DurationText}  {FrameCount} frames avg {(BitRate / 1000.0):0} kbps";

    /// <summary>AUDIO 行。</summary>
    public string AudioSummary => HasAudio
        ? $"{AudioCodec} {AudioSampleRate} Hz {AudioChannels} channels {AudioSampleFormat}"
        : "无音频流";

    public string DurationText => Duration.ToString(@"mm\:ss\.fff");

    public string SizeText => SizeBytes <= 0 ? "-" : $"{SizeBytes / 1024.0 / 1024.0:0.00} MB";

    public static MediaFileInfo Empty { get; } = new();
}
