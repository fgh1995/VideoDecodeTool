using SkiaSharp;

namespace VideoDecodeTool.Rendering;

/// <summary>叠加层开关与样式（可在界面上切换，用于对照原片与参数层）。</summary>
public sealed class OverlayOptions
{
    /// <summary>是否绘制运动矢量箭头。</summary>
    public bool ShowMotionVectors { get; set; } = true;

    /// <summary>是否绘制顶部参数行（分辨率 / 矢量数）。</summary>
    public bool ShowHeader { get; set; } = true;

    /// <summary>是否绘制左下角帧类型徽标与 QP。</summary>
    public bool ShowFrameType { get; set; } = true;

    /// <summary>是否绘制右下角 fwd / bwd 图例。</summary>
    public bool ShowLegend { get; set; } = true;

    /// <summary>运动矢量长度放大倍数（1.0 为真实像素位移）。默认 3 倍：1 倍在预览尺寸下几乎看不出方向。</summary>
    public double VectorScale { get; set; } = 3.0;

    /// <summary>
    /// 源片分辨率（顶部信息行显示「源 → 目标」用）；0 表示未知，退回用当前帧分辨率。
    /// </summary>
    /// <remarks>
    /// 转码时解码的是<b>输出</b>流，所以帧自身的分辨率是「目标」而非「源」；
    /// 源分辨率只能由界面从打开的文件信息里带进来。
    /// </remarks>
    public int SourceWidth { get; set; }

    /// <summary>源片高度，见 <see cref="SourceWidth"/>。</summary>
    public int SourceHeight { get; set; }

    /// <summary>单帧最多绘制的矢量数量；超过时按等差采样，保证 60fps 下不掉帧。</summary>
    public int MaxDrawnVectors { get; set; } = 4000;
}

/// <summary>叠加层的统一配色（与需求文档的帧类型颜色编码一致）。</summary>
internal static class OverlayPalette
{
    public static readonly SKColor Background = new(10, 10, 14);
    public static readonly SKColor Surface = new(20, 20, 26);
    public static readonly SKColor Text = new(230, 230, 236);
    public static readonly SKColor TextDim = new(146, 146, 162);
    public static readonly SKColor ChipBackground = new(0, 0, 0, 165);
    public static readonly SKColor Border = new(255, 255, 255, 38);

    /// <summary>I 帧：红色</summary>
    public static readonly SKColor IFrame = new(0xEF, 0x44, 0x44);

    /// <summary>P 帧：蓝色</summary>
    public static readonly SKColor PFrame = new(0x21, 0x96, 0xF3);

    /// <summary>B 帧：绿色</summary>
    public static readonly SKColor BFrame = new(0x4C, 0xAF, 0x50);

    /// <summary>其他帧：琥珀色</summary>
    public static readonly SKColor OtherFrame = new(0xE8, 0xA3, 0x3D);

    /// <summary>前向运动矢量（参考过去）</summary>
    public static readonly SKColor ForwardVector = new(0x22, 0xD3, 0xEE);

    /// <summary>
    /// 后向运动矢量（参考未来）。
    /// 用洋红而不是琥珀：参考实现里 fwd 是青色、bwd 是洋红，
    /// 而琥珀要留给 REORDER 行，两者混用会看不出图例对应关系。
    /// </summary>
    public static readonly SKColor BackwardVector = new(0xEC, 0x48, 0x99);

    // ---------- 图表柱体配色 ----------
    // 叠加层与图表共用同一套色值，这里是全局唯一出处；
    // Views/Theme.xaml 里的同名画刷（面板图例、芯片用）必须与这里保持一致。

    /// <summary>GOP 行：真关键帧的满高标记（白色）。与普通 GOP 柱区分「对齐关键帧」与否。</summary>
    public static readonly SKColor GopKeyFrameMarker = new(0xF2, 0xF2, 0xFF, 0xF0);

    /// <summary>
    /// 帧类型 I 泳道上关键帧柱顶的白帽：**纯白不透明**。
    /// 早先用的是带透明度的白（0xF0），叠在红色 I 柱上会偏粉、发暗（实测反馈「不够亮」）。
    /// </summary>
    public static readonly SKColor KeyFrameCap = new(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>播放头所在柱子的高亮填充（白色，极淡：只提亮一点，不盖住柱色）。</summary>
    public static readonly SKColor HighlightFill = new(0xFF, 0xFF, 0xFF, 0x1A);

    /// <summary>播放头所在柱子的高亮描边（纯白，不透明：边框要够亮才看得出来）。</summary>
    public static readonly SKColor HighlightStroke = new(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>QP 行：柱体（琥珀，半透明）</summary>
    public static readonly SKColor QpBar = new(0x9A, 0x84, 0x24, 0xC8);

    /// <summary>QP 行：平均曲线（亮黄）</summary>
    public static readonly SKColor QpLine = new(0xFF, 0xD2, 0x4A);

    /// <summary>GOP 行：柱体（紫，与面板 GOP 读数同色）</summary>
    public static readonly SKColor GopBar = new(0xA7, 0x8B, 0xFA);

    /// <summary>REORDER 行：柱体（橙，与面板 REORDER 读数同色）</summary>
    public static readonly SKColor ReorderBar = new(0xF5, 0x9E, 0x0B);

    public static SKColor ForFrameKind(Models.FrameKind kind) => kind switch
    {
        Models.FrameKind.I => IFrame,
        Models.FrameKind.P => PFrame,
        Models.FrameKind.B => BFrame,
        _ => OtherFrame,
    };
}
