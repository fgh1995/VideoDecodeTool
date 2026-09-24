namespace VideoDecodeTool.Models;

/// <summary>
/// 累计帧统计快照（对应参考界面右下角的 I/P/B 统计表）。
/// 由解码线程累加、UI 线程每秒读取一次，构造后不可变。
/// </summary>
public sealed class StatisticsSnapshot
{
    public static StatisticsSnapshot Empty { get; } = new();

    /// <summary>已解码帧总数。</summary>
    public long FrameCount { get; init; }

    /// <summary>已解码数据总字节数。</summary>
    public long TotalBytes { get; init; }

    public long IFrames { get; init; }

    public long PFrames { get; init; }

    public long BFrames { get; init; }

    public long OtherFrames { get; init; }

    public long IKeyBytes { get; init; }

    public long PKeyBytes { get; init; }

    public long BKeyBytes { get; init; }

    public long OtherKeyBytes { get; init; }

    public double QpAverage { get; init; }

    public int QpMin { get; init; }

    public int QpMax { get; init; }

    /// <summary>运动矢量总数。</summary>
    public long MotionVectorCount { get; init; }

    /// <summary>所有帧运动矢量平均长度的均值（像素）。</summary>
    public double MotionMeanPixels { get; init; }

    /// <summary>单帧运动矢量最大长度的峰值（像素）。</summary>
    public double MotionMaxPixels { get; init; }

    /// <summary>前向矢量总数。</summary>
    public long ForwardVectors { get; init; }

    /// <summary>后向矢量总数。</summary>
    public long BackwardVectors { get; init; }

    /// <summary>因为渲染跟不上而被丢弃的帧数。</summary>
    public long DroppedFrames { get; init; }

    public long TotalBytesAll => TotalBytes;

    private double Percent(long value) => FrameCount <= 0 ? 0 : value * 100.0 / FrameCount;

    private double BytePercent(long value) => TotalBytes <= 0 ? 0 : value * 100.0 / TotalBytes;

    public double IPercent => Percent(IFrames);

    public double PPercent => Percent(PFrames);

    public double BPercent => Percent(BFrames);

    public double OtherPercent => Percent(OtherFrames);

    public double IBytePercent => BytePercent(IKeyBytes);

    public double PBytePercent => BytePercent(PKeyBytes);

    public double BBytePercent => BytePercent(BKeyBytes);

    public double OtherBytePercent => BytePercent(OtherKeyBytes);

    public long IAverageBytes => IFrames <= 0 ? 0 : IKeyBytes / IFrames;

    public long PAverageBytes => PFrames <= 0 ? 0 : PKeyBytes / PFrames;

    public long BAverageBytes => BFrames <= 0 ? 0 : BKeyBytes / BFrames;

    public double MeanByteCount => FrameCount <= 0 ? 0 : (double)TotalBytes / FrameCount;

    /// <summary>I 帧行，例如 “29 frames  1.6%  7.52 MB  10.4%”。</summary>
    public string IRow => FormatRow(IFrames, IKeyBytes, IPercent, IBytePercent);

    public string PRow => FormatRow(PFrames, PKeyBytes, PPercent, PBytePercent);

    public string BRow => FormatRow(BFrames, BKeyBytes, BPercent, BBytePercent);

    public string OtherRow => FormatRow(OtherFrames, OtherKeyBytes, OtherPercent, OtherBytePercent);

    private static string FormatRow(long frames, long bytes, double framePercent, double bytePercent) =>
        $"{frames} frames  {framePercent:0.0}%  {bytes / 1024.0 / 1024.0:0.00} MB  {bytePercent:0.0}%";
}

/// <summary>
/// 时序图表的单帧采样点。
/// </summary>
/// <param name="MediaTime">
/// 该帧的媒体时间（作为时间轴的 X 值）。
/// 使用媒体时间而非墙钟时间，时间轴才能真实反映片源进度（与剪辑软件时间轴一致）。
/// </param>
/// <param name="IFrame">1 表示该帧是 I 帧，否则 0（用于帧类型堆叠柱）。</param>
/// <param name="PFrame">1 表示该帧是 P 帧。</param>
/// <param name="BFrame">1 表示该帧是 B 帧。</param>
public readonly record struct ChartSample(
    DateTime MediaTime,
    double BitrateKbps,
    int IFrame,
    int PFrame,
    int BFrame,
    double QpAverage,
    int QpMin,
    int QpMax,
    double MotionMeanPixels,
    double MotionMaxPixels,
    double ForwardMeanPixels,
    double BackwardMeanPixels,
    int GopPosition,
    int ReorderDelay);
