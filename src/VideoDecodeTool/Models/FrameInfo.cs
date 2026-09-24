namespace VideoDecodeTool.Models;

/// <summary>解码帧类型（I/P/B/其他），与 FFmpeg 的 AVPictureType 解耦，便于 UI 层使用。</summary>
public enum FrameKind
{
    Unknown = 0,
    I = 1,
    P = 2,
    B = 3,
    S = 4,
    Other = 5,
}

/// <summary>
/// 单帧编码参数快照。所有字段在解码线程内填充，构造后不可变，可安全跨线程传递。
/// </summary>
public sealed class FrameInfo
{
    /// <summary>帧序号（从 1 开始，按解码输出顺序）。</summary>
    public long Number { get; init; }

    /// <summary>帧类型。</summary>
    public FrameKind Kind { get; init; }

    /// <summary>是否为关键帧（I 帧 / IDR）。</summary>
    public bool KeyFrame { get; init; }

    /// <summary>本帧平均量化参数（QP）；0 表示解码器未提供。</summary>
    public int Qp { get; init; }

    /// <summary>
    /// 本帧所有宏块的最小 QP；0 表示未提供。
    /// </summary>
    /// <remarks>
    /// 逐宏块 QP 由 <c>FrameQpCollector</c> 从解码器调试输出汇总而来，
    /// 只有它能反映「同一帧内不同区域编码质量不同」——逐帧平均值是看不出来的。
    /// </remarks>
    public int QpMin { get; init; }

    /// <summary>本帧所有宏块的最大 QP；0 表示未提供。</summary>
    public int QpMax { get; init; }

    /// <summary>
    /// 「平均 QP」= (min + max) / 2 —— 帧参数面板、播放头读数、QP 图表统一用它。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不用解码器逐宏块算出来的算术平均：界面画出来的量就是 min / max 这一对边界
    /// （柱体的上下沿、区间读数），三者共用同一个中点定义，读数与图形才不会各说各话
    /// （例如 min 10 / max 20 → 中点 15，正好是柱体的中间）。
    /// </para>
    /// <para>
    /// half-QP（x.5）由界面负责格式化（<c>:0.0</c> 保留一位小数）。
    /// 解码器完全没给宏块级 QP 时（min = max = 0）退回帧级 <see cref="Qp"/>。
    /// </para>
    /// </remarks>
    public double QpAverage => QpMax > 0 ? (QpMin + QpMax) / 2.0 : Qp;

    /// <summary>该帧对应的压缩包字节数。</summary>
    public int PacketSize { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>显示时间（秒）。</summary>
    public double Time { get; init; }

    /// <summary>该帧的显示时长（秒），由下一帧时间差或帧率估算。</summary>
    public double Duration { get; init; }

    /// <summary>距上一个关键帧（IDR）的帧数（0 表示当前帧就是关键帧）。</summary>
    /// <remarks>
    /// 归零点用「关键帧(IDR)」而不是「任意 I 帧」：开放 GOP 流里存在非 IDR 的 I 帧，
    /// 它们不归零（在图上与 I 柱 / 帧类型泳道对不齐，属该流本来的结构），
    /// 图表把归零的那些（白色满高标记）与普通柱区分开显示。
    /// </remarks>
    public int GopPosition { get; init; }

    /// <summary>重排延迟：pts 与 dts 之差的帧数（+1 表示存在 1 帧重排）。</summary>
    public int ReorderDelay { get; init; }

    /// <summary>帧扫描方式：true = 逐行，false = 隔行。</summary>
    public bool Progressive { get; init; }

    /// <summary>解码器标记该帧数据损坏。</summary>
    public bool Corrupt { get; init; }

    /// <summary>运动矢量总数。</summary>
    public int VectorCount { get; init; }

    /// <summary>前向运动矢量数量（source &lt; 0，参考过去的帧）。</summary>
    public int ForwardVectors { get; init; }

    /// <summary>后向运动矢量数量（source &gt; 0，参考未来的帧）。</summary>
    public int BackwardVectors { get; init; }

    /// <summary>运动矢量平均长度（像素）。</summary>
    public double MotionMeanPixels { get; init; }

    /// <summary>运动矢量最大长度（像素）。</summary>
    public double MotionMaxPixels { get; init; }

    /// <summary>前向运动矢量（参考过去）的平均长度（像素）；无前向矢量时为 0。</summary>
    public double ForwardMeanPixels { get; init; }

    /// <summary>后向运动矢量（参考未来）的平均长度（像素）；无后向矢量时为 0。</summary>
    public double BackwardMeanPixels { get; init; }

    /// <summary>该帧码率（kbps），由包大小与帧时长推算。</summary>
    public double BitrateKbps => Duration > 0 ? PacketSize * 8.0 / Duration / 1000.0 : 0;

    /// <summary>用于叠加层显示的帧类型字符。</summary>
    public char TypeChar => Kind switch
    {
        FrameKind.I => 'I',
        FrameKind.P => 'P',
        FrameKind.B => 'B',
        FrameKind.S => 'S',
        FrameKind.Other => 'X',
        _ => '?',
    };

    /// <summary>帧类型的文字描述。</summary>
    public string TypeDescription => Kind switch
    {
        FrameKind.I => "I (intra / 关键帧)",
        FrameKind.P => "P (predicted / 前向预测)",
        FrameKind.B => "B (bi-predicted / 双向预测)",
        FrameKind.S => "S (swap / 切换帧)",
        FrameKind.Other => "X (其他)",
        _ => "unknown",
    };

    /// <summary>人可读的帧标记行，对应参考界面中的 FLAGS 一栏。</summary>
    public string FlagsText
    {
        get
        {
            var parts = new List<string>(3);
            parts.Add(Progressive ? "progressive" : "interlaced");
            if (KeyFrame) parts.Add("keyframe");
            if (Corrupt) parts.Add("corrupt");
            return string.Join("  ", parts);
        }
    }
}
