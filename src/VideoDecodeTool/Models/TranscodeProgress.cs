namespace VideoDecodeTool.Models;

/// <summary>
/// 由 FFmpeg <c>-progress</c> 输出解析出的一个进度块。
/// </summary>
public sealed class TranscodeProgress
{
    /// <summary>已编码帧数（frame=）。</summary>
    public long Frame { get; init; }

    /// <summary>实时编码速度（fps=）。</summary>
    public double Fps { get; init; }

    /// <summary>码率（bitrate=，单位 kbps）。</summary>
    public double BitrateKbps { get; init; }

    /// <summary>已写出的总字节数（total_size=）。</summary>
    public long TotalSizeBytes { get; init; }

    /// <summary>已处理的输出时间戳（out_time_us / out_time_ms 换算而来）。</summary>
    public TimeSpan OutTime { get; init; }

    /// <summary>转码速度倍率（speed=，例如 1.85x）。</summary>
    public double Speed { get; init; }

    /// <summary>丢帧数量（drop_frames=）。</summary>
    public int DropFrames { get; init; }

    /// <summary>重复帧数量（dup_frames=）。</summary>
    public int DupFrames { get; init; }

    /// <summary>
    /// 编码器当前量化参数（stream_0_0_q=）。
    /// </summary>
    /// <remarks>
    /// 这是转码链路上**唯一可靠**的 QP 来源：H.264 解码器不会填充 AVFrame.quality，
    /// 因此源片分析模式下 QP 只能显示 n/a，而转码模式下可以取到编码器的实际 QP。
    /// 负值（如 -1）表示当前进度块未报告。
    /// </remarks>
    public double Qp { get; init; }

    /// <summary>是否为最后一个进度块（progress=end）。</summary>
    public bool IsFinal { get; init; }

    /// <summary>已编码帧相对源片总帧数的百分比（0~100），总帧数未知时为 0。</summary>
    public double Percent { get; init; }

    /// <summary>已处理时间相对源片时长的百分比（0~100）。</summary>
    public double TimePercent { get; init; }

    public string SpeedText => Speed > 0 ? $"{Speed:0.00}x" : "-";

    public string BitrateText => BitrateKbps > 0 ? $"{BitrateKbps:0} kbps" : "-";

    public string SizeText => TotalSizeBytes <= 0
        ? "-"
        : $"{TotalSizeBytes / 1024.0 / 1024.0:0.00} MB";

    public string OutTimeText => OutTime.ToString(@"hh\:mm\:ss\.ff");

    public static TranscodeProgress Empty { get; } = new();
}
