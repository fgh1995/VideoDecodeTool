using System.Collections.ObjectModel;
using System.Windows;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using VideoDecodeTool.Models;
using VideoDecodeTool.Rendering;

namespace VideoDecodeTool.ViewModels;

/// <summary>
/// 底部时序图表区：6 层共享时间轴的「剪辑软件式」柱状图表。
/// </summary>
/// <remarks>
/// <para>分层顺序与参考界面一致：
/// BITRATE（每帧码率柱，按 I / P / B 着色）/ FRAME TYPE（I / P / B 三条独立泳道）/
/// QP（min-max 堆叠区间柱）/ MOTION（mean / max 分组柱）/
/// GOP（距关键帧帧数柱）/ REORDER（pts-dts 柱）。</para>
/// <para><b>时间轴</b>：以当前帧为播放头、向前后各展开半个窗口的滑动窗口，
/// 播放头固定在窗口 <see cref="PlayheadFraction"/> 处（正中）；窗口随当前帧向左滑动，
/// 因此画面表现为「数据向左滚、播放头不动」。</para>
/// <para><b>柱宽 / 柱间缝</b>：两者都是固定像素值（<see cref="SingleBarWidth"/> /
/// <see cref="FramePitchPixels"/> − <see cref="SingleBarWidth"/>），不随控件尺寸变化。
/// 为此窗口跨度是「由绘图区宽度反推」的：屏幕越宽看到的时长越长，
/// 但每帧始终占同样的 <see cref="FramePitchPixels"/> 像素，柱子粗细与缝隙恒定
/// （见 <see cref="UpdateTimelineSpan"/>）。</para>
/// <para><b>播放头</b>：不用 LiveCharts 的 <c>RectangularSection</c>（竖线只能落在绘图区内部，
/// 多层之间会被切成一段段、上下留缺口），而是由 View 在图表面板上叠加一层覆盖元素
/// （整条贯穿竖线 + 右侧当前值），位置由 View 读到的绘图区几何下发给
/// <see cref="UpdateTimelineGeometry"/>。</para>
/// 数据通路：解码线程按帧写入 <see cref="Media.ChartDataBuffer"/>，
/// UI 线程逐帧批量搬入各 <see cref="SilentChartValues"/>（不发集合通知，
/// 由 <see cref="RequestChartsUpdate"/> 在数据变更后显式触发 measure）。</para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>首次布局完成之前的窗口跨度兜底值（秒）。</summary>
    public const double DefaultTimelineWindowSeconds = 4;

    /// <summary>
    /// 播放头在时间轴窗口中的相对位置（0 = 最左，0.5 = 正中，1 = 最右）。
    /// </summary>
    /// <remarks>
    /// ⚠ 固定值，不要改成自适应：试过「超前量不足时把播放头右移」来填补右侧空白，
    /// 结果是播放头会在两处之间来回跳（轴与覆盖层竖线刷新时机不同步，看着像两条线在闪），
    /// 而且整体一直偏右。播放头位置必须恒定，右侧是否有数据取决于解码超前量。
    /// </remarks>
    public const double PlayheadFraction = 0.5;

    /// <summary>窗口左边缘之外额外保留的时间（秒），避免轴动画期间左侧出现空洞。</summary>
    private const double TimelineTrimMarginSeconds = 1.5;

    /// <summary>每条曲线保留的采样点上限（时间裁剪之外的兜底，防止超高帧率下无界增长）。</summary>
    private const int MaxChartPoints = 1200;

    /// <summary>单次最多从缓冲搬入的采样点数（防止长时间挂起后一次性灌入导致卡顿）。</summary>
    private const int MaxSamplesPerTick = 240;

    /// <summary>单柱宽度（像素，固定值）。</summary>
    private const double SingleBarWidth = 5;

    /// <summary>GOP 行关键帧标记的宽度：比数据柱略窄，看起来像一根「刻度」而不是一根数据柱。</summary>
    private const double KeyFrameMarkerWidth = 4;

    /// <summary>
    /// GOP 行关键帧白柱的填充比例（相对该行量程）：接近满高但留一点顶隙，
    /// 视觉上「拉满」又不至于贴着图表上边缘被裁（用户反馈：不能贴顶）。
    /// </summary>
    private const double KeyFrameMarkerFillRatio = 0.96;

    /// <summary>
    /// 重排行的量程余量：柱子压得更矮（约半行高），阶梯状的重排深度不那么压迫（用户反馈）。
    /// </summary>
    private const double ReorderHeadroomRatio = 1.0;

    /// <summary>
    /// 帧类型 I 泳道上关键帧白帽的高度（占该泳道柱高的比例）。
    /// 泳道柱高固定为 1，帽高 0.15 即柱顶约 15% 的一段，观感就是「一点点」。
    /// </summary>
    private const double IFrameCapRatio = 0.15;

    /// <summary>QP 行播放头小方块的边长（像素）。</summary>
    private const double QpMarkerSize = 7;

    /// <summary>
    /// 相邻帧之间的像素间距（固定值）。
    /// 与 <see cref="SingleBarWidth"/> 的差值即为柱间缝宽（= 1px），两者都不随控件尺寸变化。
    /// </summary>
    private const double FramePitchPixels = 6;

    /// <summary>同 X 上并排两根柱时，组内的缝宽（像素，固定值）。</summary>
    private const double GroupedBarGap = 2;

    /// <summary>
    /// 窗口跨度的允许范围（秒），避免极端控件尺寸下窗口长到没法看。
    /// </summary>
    /// <remarks>
    /// 上限与「解码超前量」挂钩：播放头居中意味着窗口右半屏显示的是「还没播到的未来」，
    /// 上限 9.2s（半屏 4.6s）必须落在 <c>PlaybackPipeline</c> 的超前量（目标 5.2s，
    /// 且被帧队列容量压到约 4.67s @23.976fps）之内，否则时间轴右侧会空出一条。
    /// 换更大帧队列可以同时抬高这两个上限，代价是峰值内存线性增长。
    /// </remarks>
    private const double MinTimelineWindowSeconds = 2;
    private const double MaxTimelineWindowSeconds = 9.2;

    /// <summary>拿到播放管线之前的帧率兜底值（用于首次推算窗口跨度）。</summary>
    private const double FallbackFrameRate = 24;

    /// <summary>QP 区间带的颜色（参考界面里那种半透明橄榄黄）。</summary>
    private static readonly SKColor QpBand = OverlayPalette.QpBar;

    /// <summary>QP 平均曲线的颜色（参考界面里那条亮黄色曲线）。</summary>
    private static readonly SKColor QpAverageLine = OverlayPalette.QpLine;

    /// <summary>
    /// 图表背景色（与 XAML 中图表容器的 <c>Background="#FF0E0E14"</c> 一致）。
    /// QP 区间带靠「画一根这个颜色的柱」把 0~min 那段擦掉来实现悬空效果。
    /// </summary>
    private static readonly SKColor ChartBackground = new(0x0E, 0x0E, 0x14);

    /// <summary>首次布局完成之前的绘图区宽度兜底值（像素）。</summary>
    private const double FallbackPlotWidth = 1200;

    private readonly List<ChartSample> _pendingSamples = new(MaxSamplesPerTick);

    /// <summary>
    /// View 在 Loaded 后从可视化树收集并登记的全部图表（见 <see cref="AttachChartViews"/>）。
    /// 静默集合不发集合通知，图表更新只能经此显式触发。
    /// </summary>
    private readonly List<IChartView> _chartViews = [];

    /// <summary>
    /// 柱心相对上一帧的最大变化比例（逐帧限幅）。
    /// 平均 QP 逐帧也会有零点几到一两个 QP 的抖动，限幅后柱心是平滑爬升/下降的，
    /// 相邻柱体不会出现一格一格乱跳的锯齿；量程自适应时这也让量程的变化是缓的。
    /// </summary>
    private const double MaxBarCenterChangeRatio = 0.08;

    /// <summary>柱心的最小变化步长（QP 单位），保证变化缓慢的片段柱子也在持续移动。</summary>
    private const double MinBarCenterStep = 0.2;

    /// <summary>
    /// 柱体半高相对「平均 QP」的比例：柱高 = avg × 2 × 该值。
    /// </summary>
    /// <remarks>
    /// QP 行量程是自适应的（随窗口内数据缩放），柱高若再按量程算，量程与柱高就会互相追着变；
    /// 因此柱高只由 avg 决定 —— 量程怎么缩放，柱子看起来都还是同一套比例。
    /// 取 0.15 → 柱高约占 avg 的 30%；调大柱子更饱满，调小更贴住平均线。
    /// </remarks>
    private const double BarHalfRatioOfAverage = 0.15;

    /// <summary>柱体最小半高（QP 单位），保证任何帧都有一根看得见的柱子。</summary>
    private const double MinBarHalfRange = 0.6;

    /// <summary>
    /// 数值图轴上限的顶部余量：上限取「窗口内最大值 × (1 + 该值)」，
    /// 最高柱与图表上沿之间留一点空，不会紧贴。
    /// </summary>
    private const double AxisHeadroomRatio = 0.1;

    /// <summary>
    /// GOP / REORDER 两行专用的更大顶部余量。
    /// 这两行的纵轴单位是「帧数」这种小整数，柱高本身就是整齐的阶梯，
    /// 按 10% 留白时柱子几乎顶满整行、看不出阶梯高度差，所以余量放大到 60%
    /// （最高柱约占行高 62%，用户反馈：柱子不能贴顶）。重排行单独用更大的
    /// <see cref="ReorderHeadroomRatio"/> 压到约半行。
    /// </summary>
    private const double TallRowHeadroomRatio = 0.6;

    private string _bitrateMaxText = "-";
    private string _qpMaxText = "-";
    private string _qpMinText = "-";
    private string _motionMaxText = "-";
    private string _gopMaxText = "-";
    private string _reorderMaxText = "-";

    /// <summary>
    /// BITRATE 行量程上沿读数（悬浮在图表右上角；下沿固定 0，写在 XAML 里）。
    /// </summary>
    /// <remarks>
    /// 它是「当前窗口内单帧瞬时码率峰值 ×(1+顶部留白)」，**不是**全片平均码率，
    /// 也不是转码时填写的目标码率 —— 见 <see cref="BitrateChartHint"/>。
    /// </remarks>
    public string BitrateMaxText => _bitrateMaxText;

    /// <summary>BITRATE 行的解释性 ToolTip（悬浮在标题上），区分「单帧瞬时码率」与「目标平均码率」。</summary>
    public string BitrateChartHint =>
        "柱 = 每帧瞬时码率 = 该帧字节数 ÷ 帧时长（kbps），按 I 红 / P 蓝 / B 绿着色。\n" +
        "右上角读数是本窗口内该峰值的量程上限（含顶部留白），属单帧尖峰 —— I 帧尤其大，\n" +
        "因此它远高于平均值是正常的。转码目标码率约束的是平均值，\n" +
        "真实平均码流请看「转码进度 · TRANSCODE」段的读数。";

    /// <summary>QP 行量程上沿读数。</summary>
    public string QpMaxText => _qpMaxText;

    /// <summary>QP 行量程下沿读数。</summary>
    public string QpMinText => _qpMinText;

    /// <summary>MOTION 行量程上沿读数。</summary>
    public string MotionMaxText => _motionMaxText;

    /// <summary>GOP 行量程上沿读数。</summary>
    public string GopMaxText => _gopMaxText;

    /// <summary>REORDER 行量程上沿读数。</summary>
    public string ReorderMaxText => _reorderMaxText;

    /// <summary>上一帧实际绘制的柱心高度（逐帧限幅用；只影响绘图，不影响读数）。</summary>
    private double _qpBarCenter;

    private readonly SilentChartValues _bitrateIValues = [];
    private readonly SilentChartValues _bitratePValues = [];
    private readonly SilentChartValues _bitrateBValues = [];
    private readonly SilentChartValues _iFrameValues = [];
    private readonly SilentChartValues _pFrameValues = [];
    private readonly SilentChartValues _bFrameValues = [];

    /// <summary>
    /// QP 柱体上沿（= 平均值 + 半个柱高）。
    /// 柱高只由 avg 决定、与 min-max 跨度无关，而且整根柱子<b>居中在 avg 上</b>，
    /// 因此平均曲线永远穿过每根柱子的正中间（avg = 22.2 时柱子中线就在 22.2）。
    /// </summary>
    private readonly SilentChartValues _qpBarValues = [];

    /// <summary>QP 柱体下沿（= 平均值 − 半个柱高；填成图表背景色，用来擦掉轴底到柱底那一段）。</summary>
    private readonly SilentChartValues _qpBarBottomValues = [];

    /// <summary>QP 平均曲线（= 本帧宏块平均 QP）。</summary>
    private readonly SilentChartValues _qpAverageValues = [];
    private readonly SilentChartValues _motionForwardValues = [];
    private readonly SilentChartValues _motionBackwardValues = [];

    /// <summary>GOP 行关键帧白柱当前的绘制高度（= 该行量程 × <see cref="KeyFrameMarkerFillRatio"/>）。</summary>
    private double _gopMarkerHeight = 10;

    /// <summary>关键帧白柱上次批量重写的高度（量程变了才需要重写历史白条）。</summary>
    private double _gopKeyFrameMarkerHeight;

    private readonly SilentChartValues _gopValues = [];
    private readonly SilentChartValues _reorderValues = [];

    /// <summary>
    /// GOP 行的关键帧标记：只在「真关键帧」（距关键帧 = 0）处有点，值是远超量程的常数，
    /// 于是渲染成一根被绘图区上沿裁掉的白色满高柱；非 IDR 的 I 帧落在这条序列之外，
    /// 保持普通柱色 —— 两者一眼可分辨。
    /// </summary>
    private readonly SilentChartValues _gopKeyFrameValues = [];

    // 播放头所在那一根柱的高亮：每行一条序列，只放「当前帧」一个点，
    // 值取该行在当前帧位置上的实际绘制值（二分查表），因此高亮框与柱子完全重合。
    private readonly SilentChartValues _highlightBitrateValues = [];

    /// <summary>I / P / B 三条帧类型泳道的高亮（各只有一个点，值 1 = 占满整条泳道）。</summary>
    private readonly SilentChartValues _highlightILaneValues = [];
    private readonly SilentChartValues _highlightPLaneValues = [];
    private readonly SilentChartValues _highlightBLaneValues = [];

    /// <summary>
    /// 帧类型 I 泳道上「关键帧柱」顶部的白色小帽（值 1 = 与 I 柱同高），
    /// 配合同高的 I 柱本色柱（值 <see cref="IFrameCapRatio"/> 的余量）把下方盖回去，
    /// 于是只剩柱顶那一小段是白的 —— 也就是「白帽重叠叠在 I 柱顶部」。
    /// </summary>
    private readonly SilentChartValues _iLaneCapValues = [];

    /// <summary>白帽之下用于「盖回本色」的 I 柱（值 = 1 - 帽高）。</summary>
    private readonly SilentChartValues _iLaneCapBaseValues = [];
    private readonly SilentChartValues _highlightMotionForwardValues = [];
    private readonly SilentChartValues _highlightMotionBackwardValues = [];
    private readonly SilentChartValues _highlightGopValues = [];
    private readonly SilentChartValues _highlightReorderValues = [];

    /// <summary>各层的 X 轴，用于统一下发滑动窗口上下限。</summary>
    private Axis[] _timelineXAxes = [];

    /// <summary>绘图区像素宽度（由 View 测量后下发）。</summary>
    private double _plotWidth = FallbackPlotWidth;

    /// <summary>播放头覆盖层在图表面板中的水平位置（像素，由 View 测量后下发）。</summary>
    private double _playheadOffset;

    /// <summary>当前窗口跨度（秒）。由绘图区宽度反推，保证每帧间距恒为 <see cref="FramePitchPixels"/>。</summary>
    private double _timelineWindowSeconds = DefaultTimelineWindowSeconds;

    /// <summary>最近一次的播放头时间（秒）；控件尺寸变化后需要用它重算窗口上下限。</summary>
    private double _lastPlayheadSeconds;

    /// <summary>播放头竖线的左边距。</summary>
    public Thickness PlayheadMargin => new(_playheadOffset, 0, 0, 0);

    /// <summary>播放头右侧当前值文本的边距（略右移并下移，避免压住竖线、贴住面板顶边）。</summary>
    public Thickness PlayheadReadoutMargin => new(_playheadOffset + 6, 3, 0, 0);

    /// <summary>
    /// 播放头处时间文本的边距。
    /// </summary>
    /// <remarks>
    /// 垂直方向抬高 <see cref="PlayheadTimeBottomGap"/>：重排行底部是 18px 高、文字垂直居中的
    /// 时间刻度条，读数文本自身（11px 字 + 1px 上下内边距，约 16.5px 高）贴底时会比刻度低；
    /// 抬起来后两者落在同一行（界面反馈：播放线的时间要和时刻在同一行）。
    /// </remarks>
    public Thickness PlayheadTimeMargin => new(_playheadOffset + 6, 0, 0, PlayheadTimeBottomGap);

    /// <summary>
    /// 时间读数相对面板底边的抬升量（像素），用于与时间刻度条里的时刻对齐。
    /// 刻度条高 18、文字居中（中心距底 9），读数连内边距约 16.5 高，抬 1px 后中心也在 9 附近。
    /// </summary>
    private const int PlayheadTimeBottomGap = 1;

    /// <summary>有片源时才显示播放头覆盖层。</summary>
    public Visibility PlayheadVisibility => HasSource ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>QP 行绘图区在 ChartHost 坐标系里的上边（由 View 测量后下发）。</summary>
    private double _qpPlotTop;

    /// <summary>QP 行绘图区像素高度（由 View 测量后下发）。</summary>
    private double _qpPlotHeight;

    /// <summary>当前 QP 行 Y 轴的上下限（显式下发，见 <see cref="UpdateTimelineWindow"/>）。</summary>
    private double _qpAxisMin;
    private double _qpAxisMax = 1;

    /// <summary>
    /// QP 行播放头右侧的白色小方块位置：横向贴在播放头右边，纵向按本帧平均 QP
    /// 在 QP 量程里的比例换算成行内像素高度 —— 于是它始终压在那条 QP 曲线上。
    /// </summary>
    public Thickness QpMarkerMargin
    {
        get
        {
            var left = _playheadOffset + 2;
            var value = CurrentFrame?.QpAverage ?? 0;

            if (_qpPlotHeight <= 1 || _qpAxisMax <= _qpAxisMin || value <= 0)
            {
                // 没有可用的 QP 数据：挪到可视区之外（配合 Visibility 也不显示）
                return new Thickness(left, -100, 0, 0);
            }

            var ratio = Math.Clamp((value - _qpAxisMin) / (_qpAxisMax - _qpAxisMin), 0, 1);
            var top = _qpPlotTop + _qpPlotHeight * (1 - ratio) - QpMarkerSize / 2;

            return new Thickness(left, top, 0, 0);
        }
    }

    /// <summary>有片源且本帧有 QP 时才显示白色小方块。</summary>
    public Visibility QpMarkerVisibility =>
        HasSource && CurrentFrame is { Qp: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public ObservableCollection<ISeries> BitrateChartSeries { get; private set; } = [];

    public Axis[] BitrateXAxes { get; private set; } = [];

    public Axis[] BitrateYAxes { get; private set; } = [];

    /// <summary>FRAME TYPE 第 1 行：I 帧泳道。</summary>
    public FrameTypeLane IFrameLane { get; } = new();

    /// <summary>FRAME TYPE 第 2 行：P 帧泳道。</summary>
    public FrameTypeLane PFrameLane { get; } = new();

    /// <summary>FRAME TYPE 第 3 行：B 帧泳道。</summary>
    public FrameTypeLane BFrameLane { get; } = new();

    public ObservableCollection<ISeries> QpChartSeries { get; private set; } = [];

    public Axis[] QpXAxes { get; private set; } = [];

    public Axis[] QpYAxes { get; private set; } = [];

    public ObservableCollection<ISeries> MotionChartSeries { get; private set; } = [];

    public Axis[] MotionXAxes { get; private set; } = [];

    public Axis[] MotionYAxes { get; private set; } = [];

    public ObservableCollection<ISeries> GopChartSeries { get; private set; } = [];

    public Axis[] GopXAxes { get; private set; } = [];

    public Axis[] GopYAxes { get; private set; } = [];

    public ObservableCollection<ISeries> ReorderChartSeries { get; private set; } = [];

    public Axis[] ReorderXAxes { get; private set; } = [];

    /// <summary>
    /// 时间轴窗口左边缘对应的媒体时间（秒）。底部时间刻度条据此把「整秒时刻」换算成 X，
    /// 与 <see cref="UpdateTimelineWindow"/> 下发给各 X 轴的 <c>MinLimit</c> 同源。
    /// </summary>
    public double TimelineLeftSeconds => _lastPlayheadSeconds - _timelineWindowSeconds * PlayheadFraction;

    /// <summary>时间轴窗口的跨度（秒）。窗口内逐秒打一个刻度，最多约 10 个。</summary>
    public double TimelineSpanSeconds => _timelineWindowSeconds;

    public Axis[] ReorderYAxes { get; private set; } = [];

    private void InitializeCharts()
    {
        // ---------- 1. BITRATE：每帧码率柱，按帧类型着色 ----------
        // 三条序列都设 IgnoresBarPosition：它们共用同一个 X 位置叠加绘制，
        // 而每帧只有对应类型的一条非零（其余为 0，柱高 0 不可见），
        // 于是同一根柱子的颜色就跟着帧类型走。若不忽略 bar position，
        // LiveCharts 会把三条序列在 X 上并排错开，柱子的时间位置就全错了。
        BitrateChartSeries =
        [
            CreateBarSeries(_bitrateIValues, OverlayPalette.IFrame, ignoresBarPosition: true),
            CreateBarSeries(_bitratePValues, OverlayPalette.PFrame, ignoresBarPosition: true),
            CreateBarSeries(_bitrateBValues, OverlayPalette.BFrame, ignoresBarPosition: true),

            CreateHighlightSeries(_highlightBitrateValues),
        ];

        BitrateXAxes = [CreateTimeAxis()];
        // 量程自适应：limit 传 null，量程跟着窗口内的数据走（与 QP 行同一套做法）
        BitrateYAxes = [CreateValueAxis(null, null, "0")];

        // ---------- 2. FRAME TYPE：I / P / B 拆成三条独立泳道（每种类型一行、各自着色）----------
        // I 泳道多两层：关键帧柱顶的白色小帽 + 把它下方盖回本色的 I 柱
        // （P / B 泳道不需要，传 null 即可）
        InitializeFrameTypeLane(
            IFrameLane, _iFrameValues, _highlightILaneValues, OverlayPalette.IFrame,
            _iLaneCapValues, _iLaneCapBaseValues);
        InitializeFrameTypeLane(PFrameLane, _pFrameValues, _highlightPLaneValues, OverlayPalette.PFrame);
        InitializeFrameTypeLane(BFrameLane, _bFrameValues, _highlightBLaneValues, OverlayPalette.BFrame);

        // ---------- 3. QP：以平均值为基准高度的实心柱 + 平均曲线 ----------
        // 柱高**只由 avg 决定**：从轴底直接画到本帧平均 QP。
        // 早先是按 min-max 跨度决定柱高（悬空区间带），但跨度在不同帧之间能差好几倍
        // （例如 10~20 与 10~40），按跨度画就会出现相邻两根柱一根高 10、一根高 30 的落差；
        // 改用 avg 当基准后，柱高与 QP 水平成正比，整排柱子才协调。
        // 逐帧 min / max 不再上图，仍可在右侧读数里看到（格式：avg [min-max]）。
        var qpBarSeries = new ColumnSeries<DateTimePoint>
        {
            Values = _qpBarValues,
            Fill = new SolidColorPaint(QpBand),
            Stroke = null,
            Rx = 0,
            Ry = 0,
            IgnoresBarPosition = true,
            Padding = 0,
            MaxBarWidth = SingleBarWidth,
        };

        // 柱底以下用**背景色**再画一根等宽柱把它擦掉（LiveCharts 的柱永远从轴底起画），
        // 这样柱子才是悬空的 —— 只有把整根柱子上移半个高度，它的中线才能落在 avg 上。
        // 与 BITRATE / MOTION 同一套机制：IgnoresBarPosition + Padding = 0，
        // 保证擦除柱与本色柱在 X 方向完全重合。
        var qpBarEraserSeries = new ColumnSeries<DateTimePoint>
        {
            Values = _qpBarBottomValues,
            Fill = new SolidColorPaint(ChartBackground),
            Stroke = null,
            Rx = 0,
            Ry = 0,
            IgnoresBarPosition = true,
            Padding = 0,

            // 比本色柱宽 2px：两者的抗锯齿边缘互相盖不干净时，会在每根柱的左右
            // 各留一条发丝竖线（柱体下方看起来就是一片「虚格子」）。
            // 加宽后擦除柱的边缘完全包住本色柱的边缘；多擦的那一点落在柱间空隙里，本来也是空的。
            MaxBarWidth = SingleBarWidth + 2,
        };

        var qpAverageSeries = new LineSeries<DateTimePoint>
        {
            Values = _qpAverageValues,
            Fill = null,
            Stroke = new SolidColorPaint(QpAverageLine, 1.6f),
            GeometrySize = 0,

            // 0 = 逐点直连的折线：曲线不做任何平滑（含贝塞尔圆滑），
            // 幅度的每一次变化都是真实数据，不做圆滑过渡
            LineSmoothness = 0,
        };

        // QP 行不加高亮（实测反馈「QP 去掉高亮」）：
        // 这一行有悬浮的区间带 + 平均曲线，再加一层描边只会互相干扰；
        // 播放头位置已经由白色小方块 + 黄色曲线表达得很清楚。
        QpChartSeries = [qpBarSeries, qpBarEraserSeries, qpAverageSeries];

        // 这一行不画竖向网格线：柱体下沿以下是用背景色柱「擦」出来的，
        // 擦除柱会把网格线一起擦掉，看上去就是一条条断开的虚线（实测反馈的「虚格子」）。
        QpXAxes = [CreateTimeAxis(showGrid: false)];

        // 量程**自适应**：两个 limit 传 null，由 LiveCharts 按当前窗口内的数据自动算。
        // 之所以可以放开自适应：柱体自身有固定比例的高度（avg 的 ±15%），
        // 窗口内的数据永远不会退化成一条线，自动量程因此总落在
        // 「平均线的上下浮动范围」这个有意义的区间里，而不会被某一帧的极值拉爆。
        QpYAxes = [CreateValueAxis(null, null, "0")];

        // ---------- 4. MOTION：矢量平均长度（fwd / bwd 分开，同 X 叠加的两根柱）----------
        // 两条序列叠在**同一帧位置**上、宽度也一致，颜色直接用叠加层图例里的 fwd / bwd 配色。
        // 列表顺序 = 绘制顺序：后向放后面（后画的在上层）—— 后向多数比前向矮，
        // 压在上面时前向更高的那段仍能露出来，两根都看得见。
        // 两者都忽略 bar position，柱子才会落在自己的 X 上而不是被分组错开。
        var motionForwardSeries = CreateBarSeries(_motionForwardValues, OverlayPalette.ForwardVector, ignoresBarPosition: true);
        var motionBackwardSeries = CreateBarSeries(_motionBackwardValues, OverlayPalette.BackwardVector, ignoresBarPosition: true);

        MotionChartSeries =
        [
            motionForwardSeries,
            motionBackwardSeries,
            CreateHighlightSeries(_highlightMotionForwardValues),
            CreateHighlightSeries(_highlightMotionBackwardValues),
        ];

        MotionXAxes = [CreateTimeAxis()];
        MotionYAxes = [CreateValueAxis(null, null, "0")];

        // ---------- 5. GOP：距关键帧的帧数 ----------
        // 两层：白色满高标记（真关键帧，对齐的那些）+ 普通柱（其余帧）。
        // 标记排在前面，普通柱画在它上面，关键帧处柱高为 0，于是只看到白色标记。
        GopChartSeries =
        [
            CreateKeyFrameMarkerSeries(_gopKeyFrameValues),
            CreateBarSeries(_gopValues, OverlayPalette.GopBar, ignoresBarPosition: true),
            CreateHighlightSeries(_highlightGopValues),
        ];

        GopXAxes = [CreateTimeAxis()];
        GopYAxes = [CreateValueAxis(null, null, "0")];

        // ---------- 6. REORDER：pts-dts（帧数）----------
        ReorderChartSeries =
        [
            CreateBarSeries(_reorderValues, OverlayPalette.ReorderBar, ignoresBarPosition: true),
            CreateHighlightSeries(_highlightReorderValues),
        ];

        // ⚠ 本行 X 轴**不画刻度**：LiveCharts 会把带刻度那张图的绘图区横向收窄约 20px
        // （实测 1179.67 vs 1200），挂在数据行上会让整行 x 映射比其它行窄，
        // 看起来就是「重排整体提前、网格线与上面几行对不齐」。
        // 时刻由本行下方那条自绘刻度条显示（见 Views/TimeRuler.cs），数据图因此与其它行几何一致。
        ReorderXAxes = [CreateTimeAxis()];

        // 量程自适应：重排深度典型只有 0~4 帧，固定上限不是把柱子压扁就是留一大片空白
        ReorderYAxes = [CreateValueAxis(null, null, "0")];

        _timelineXAxes =
        [
            BitrateXAxes[0],
            IFrameLane.XAxes[0],
            PFrameLane.XAxes[0],
            BFrameLane.XAxes[0],
            QpXAxes[0],
            MotionXAxes[0],
            GopXAxes[0],
            ReorderXAxes[0],
        ];

        // 初始窗口：以 0 秒为播放头
        UpdateTimelineWindow(0);
    }

    /// <summary>
    /// View 在 Loaded 后把可视化树里收集到的全部 <c>CartesianChart</c> 登记进来。
    /// </summary>
    /// <remarks>
    /// 静默集合（<see cref="SilentChartValues"/>）不发 <c>INotifyCollectionChanged</c>，
    /// LiveCharts 的「集合变更 → 自动更新」通路因此完全关闭；
    /// 图表重画的驱动权收归 <see cref="RequestChartsUpdate"/>，View 必须先完成登记。
    /// </remarks>
    public void AttachChartViews(IEnumerable<IChartView> chartViews)
    {
        _chartViews.Clear();
        _chartViews.AddRange(chartViews);
    }

    /// <summary>
    /// 显式触发全部图表的 measure：绕开 8ms 节流的 <c>Task.Delay</c> 续体，
    /// measure 经 <c>Task.Run + InvokeOnUIThread</c> 回 UI 线程执行，节拍不再抖。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>IsAutomaticUpdate = false</c> 绕开 <c>View.AutoUpdateEnabled</c> 检查，保证调用一定生效；
    /// <c>Throttling = false</c> 走 <c>ForceCall()</c>，没有 Task.Delay。
    /// measure 只把画布标记为失效，真正重画由 View 的 <c>CompositionTarget.Rendering</c>
    /// 驱动（只重画 <c>IsValid=false</c> 的画布），与屏幕刷新对齐。
    /// </para>
    /// </remarks>
    private void RequestChartsUpdate()
    {
        foreach (var view in _chartViews)
        {
            view.CoreChart?.Update(new ChartUpdateParams { IsAutomaticUpdate = false, Throttling = false });
        }
    }

    /// <summary>
    /// 创建一个柱序列。
    /// 柱宽取固定像素值 <see cref="SingleBarWidth"/>，因此控件被拉宽时柱子不会跟着变粗。
    /// </summary>
    /// <param name="values">该序列的数据集合。</param>
    /// <param name="color">柱体颜色。</param>
    /// <param name="ignoresBarPosition">
    /// 是否忽略分组位置。
    /// 置 true 时序列始终按自己的 X 居中绘制、不参与分组均分宽度，
    /// 用于「多条序列叠加在同一个 X 上」——
    /// BITRATE 按帧类型着色、MOTION 的峰值柱与均值柱都是一宽一窄叠在同一帧位置。
    /// </param>
    /// <summary>
    /// GOP 行的关键帧标记序列：白色、比数据柱略窄，接近满高（量程 × <see cref="KeyFrameMarkerFillRatio"/>），
    /// 在压低的紫柱之间一眼标出关键帧位置。
    /// </summary>
    private static ColumnSeries<DateTimePoint> CreateKeyFrameMarkerSeries(SilentChartValues values) =>
        new()
        {
            Values = values,
            Fill = new SolidColorPaint(OverlayPalette.GopKeyFrameMarker),
            Stroke = null,
            Rx = 0,
            Ry = 0,

            // ⚠ 同一张图里所有柱序列都必须忽略 bar position：否则 LiveCharts 会把它们
            // 当成一组横向均分宽度，柱子会变窄并整体偏移（高亮框也就对不上了）
            IgnoresBarPosition = true,
            Padding = 0,
            MaxBarWidth = KeyFrameMarkerWidth,
        };

    /// <summary>
    /// 播放头所在柱子的高亮序列：与数据柱同宽、值取同一份绘制值，
    /// 视觉上就是「播放头扫过的那根柱子套了一圈白边并高亮」。
    /// </summary>
    private static ColumnSeries<DateTimePoint> CreateHighlightSeries(SilentChartValues values) =>
        new()
        {
            Values = values,
            Fill = new SolidColorPaint(OverlayPalette.HighlightFill),
            Stroke = new SolidColorPaint(OverlayPalette.HighlightStroke, 1.5f),
            Rx = 0,
            Ry = 0,
            IgnoresBarPosition = true,
            Padding = 0,

            // 必须与数据柱同宽：宽出去一圈看着就是「对不齐」
            MaxBarWidth = SingleBarWidth,

            // 高亮每帧换位置，跟着图表动画走会拖影 —— 这一层必须瞬时到位
            AnimationsSpeed = TimeSpan.Zero,
        };

    private static ColumnSeries<DateTimePoint> CreateBarSeries(
        SilentChartValues values,
        SKColor color,
        bool ignoresBarPosition = false,
        double maxBarWidth = SingleBarWidth)
    {
        return new ColumnSeries<DateTimePoint>
        {
            Values = values,
            Fill = new SolidColorPaint(color),
            Stroke = null,

            // 统一为直角：LiveCharts 的柱体圆角由 Rx / Ry 控制，显式归零，
            // 否则不同柱体在不同尺寸下会被抗锯齿抹出圆角，看起来有的圆有的方。
            Rx = 0,
            Ry = 0,

            IgnoresBarPosition = ignoresBarPosition,
            Padding = 0,

            // 柱宽是死值，不随控件尺寸变化（个别层需要略宽以包住下层边缘，由参数指定）
            MaxBarWidth = maxBarWidth,
        };
    }

    /// <summary>
    /// 初始化一条帧类型泳道：单行、单序列。
    /// 只有「该类型」的帧对应值为 1（柱高占满泳道），其余帧为 0（柱高 0，不可见），
    /// 因此相邻的 I / P / B 三种帧各自落在自己的行里，互不重叠。
    /// 本行也是全图唯一保持<b>固定量程</b>的：0/1 两态没有自适应的意义。
    /// </summary>
    private void InitializeFrameTypeLane(
        FrameTypeLane lane,
        SilentChartValues values,
        SilentChartValues highlightValues,
        SKColor color,
        SilentChartValues? capValues = null,
        SilentChartValues? capBaseValues = null)
    {
        // ⚠ 本色柱也必须 IgnoresBarPosition：同一张图里再加一条高亮序列后，
        // 两者不一致时 LiveCharts 会按「组」横向均分宽度，柱子会变窄并整体偏移
        lane.Series.Add(CreateBarSeries(values, color, ignoresBarPosition: true));

        if (capValues is not null && capBaseValues is not null)
        {
            // 白帽与它的「盖回层」：白柱先铺满（值 1），再用略矮的本色柱压回去，
            // 于是只有柱顶 IFrameCapRatio 那一段留白 —— 白帽是叠在 I 柱顶部的。
            //
            // ⚠ 两处细节都是为了「看着亮、且与柱子完全贴合」：
            // 1) 白帽用**纯白不透明**（半透明白叠在红柱上会偏粉发暗，实测反馈「不够亮」）；
            // 2) 白帽比本色柱宽 1px —— 帽沿完整包住柱顶左右两条边，顶面才不会露出红边、
            //    看起来才像「和柱顶完全贴合」的一截白色。
            var capSeries = CreateBarSeries(
                capValues, OverlayPalette.KeyFrameCap, ignoresBarPosition: true, maxBarWidth: SingleBarWidth + 1);
            capSeries.AnimationsSpeed = TimeSpan.Zero;
            lane.Series.Add(capSeries);

            // 盖回层与本色柱同宽同色：它在白帽之下，宽度多 1px 会在柱侧露出红边
            var capBaseSeries = CreateBarSeries(capBaseValues, color, ignoresBarPosition: true);
            capBaseSeries.AnimationsSpeed = TimeSpan.Zero;
            lane.Series.Add(capBaseSeries);
        }

        // 高亮放最后：描边压在所有层之上
        lane.Series.Add(CreateHighlightSeries(highlightValues));

        lane.XAxes = [CreateTimeAxis()];
        lane.YAxes = [CreateLaneYAxis()];
    }

    /// <summary>
    /// 帧类型泳道的 Y 轴：取值只有 0 / 1（属于本类型 / 不属于），
    /// 隐藏刻度文字，只留一点顶部余量让柱顶不贴边，同时让上下两条泳道之间保持可见的分隔。
    /// </summary>
    private static Axis CreateLaneYAxis() => new()
    {
        MinLimit = 0,
        MaxLimit = 1.15,
        TextSize = 9,
        LabelsPaint = null,

        // 横向网格与其他行保持一致（用户反馈：帧类型行没有横线）；
        // 量程固定 0~1.15，步长 0.5 → 只有 0.5 / 1.0 两条线，1.0 正好压在柱顶
        SeparatorsPaint = new SolidColorPaint(new SKColor(0xFF, 0xFF, 0xFF, 14)),
        ShowSeparatorLines = true,
        MinStep = 0.5,
    };

    /// <summary>
    /// 按绘图区宽度重算时间轴窗口跨度，使「每帧占用的像素间距」恒为 <see cref="FramePitchPixels"/>。
    /// </summary>
    /// <remarks>
    /// 柱宽和柱间缝都是固定像素值，如果窗口跨度也固定成 4 秒，
    /// 那么窗口一被拉宽，每帧的像素间距变大、缝隙就越来越宽（柱子也会显得更细）。
    /// 反过来固定「每帧像素间距」、让窗口跨度随控件宽度浮动，
    /// 就能做到：屏幕越宽看到的时间范围越长，而柱子的粗细与缝隙始终不变。
    /// </remarks>
    private void UpdateTimelineSpan()
    {
        var frameRate = ResolveFrameRate();
        var span = _plotWidth / (FramePitchPixels * frameRate);

        // ⚠ 窗口跨度按「解码超前量」封顶（≤ 2 × 超前量）。
        // 播放头恒定居中，所以右半屏放的就是「还没播到的未来帧」：
        // 跨度超过超前量的两倍，右半屏就必然空一条 —— 而且空的是「根本没有的数据」，
        // 不是渲染问题，靠调更新时机之类的办法填不上。
        // 直接封顶后：屏幕再宽也只是柱距变大（可见时间范围不变），右侧始终被数据填满。
        // 60fps 输出时超前量被帧队列压到约 2.2s，窗口因此最多约 4.4s。
        if (_pipeline is { } pipeline)
        {
            span = Math.Min(span, 2 * pipeline.MaxLeadSeconds);
        }

        _timelineWindowSeconds = Math.Clamp(span, MinTimelineWindowSeconds, MaxTimelineWindowSeconds);

        // 跨度变了，立即用同一个播放头位置重算轴上下限
        UpdateTimelineWindow(_lastPlayheadSeconds);
        RequestChartsUpdate();
    }

    /// <summary>取当前片源帧率；管线未就绪或帧率异常时退回 <see cref="FallbackFrameRate"/>。</summary>
    private double ResolveFrameRate()
    {
        var frameRate = _pipeline?.FrameRate ?? FallbackFrameRate;

        return frameRate > 0.01 && !double.IsNaN(frameRate) && !double.IsInfinity(frameRate)
            ? frameRate
            : FallbackFrameRate;
    }

    /// <summary>
    /// View 测量到时间轴绘图区几何后下发：把播放头覆盖层对齐到绘图区内的数据位置。
    /// </summary>
    /// <param name="playheadOffset">
    /// 播放头所在 X 相对图表面板左边缘的像素位置（= 绘图区左边缘 + 绘图区宽度 × <see cref="PlayheadFraction"/>）。
    /// </param>
    /// <param name="plotWidth">绘图区像素宽度。</param>
    /// <param name="qpPlotTop">QP 行绘图区上边（ChartHost 坐标系，像素）。</param>
    /// <param name="qpPlotHeight">QP 行绘图区像素高度。</param>
    public void UpdateTimelineGeometry(double playheadOffset, double plotWidth, double qpPlotTop, double qpPlotHeight)
    {
        // 播放头竖线对齐「柱子的左边缘」：柱心落在播放头所在的 X 上、左右各占半个柱宽，
        // 所以覆盖层要左移半个柱宽，才会正好压在柱子的左边界上。
        // 这只影响覆盖层（竖线与读数），时间轴窗口本身仍以播放头为正中。
        var alignedOffset = playheadOffset - SingleBarWidth / 2;

        var offsetChanged = Math.Abs(alignedOffset - _playheadOffset) > 0.5;
        var widthChanged = Math.Abs(plotWidth - _plotWidth) > 0.5;
        var qpPlotChanged = Math.Abs(qpPlotTop - _qpPlotTop) > 0.5
                            || Math.Abs(qpPlotHeight - _qpPlotHeight) > 0.5;

        if (!offsetChanged && !widthChanged && !qpPlotChanged)
        {
            return;
        }

        _playheadOffset = alignedOffset;

        if (widthChanged)
        {
            _plotWidth = plotWidth;
            UpdateTimelineSpan();
        }

        if (qpPlotChanged)
        {
            _qpPlotTop = qpPlotTop;
            _qpPlotHeight = qpPlotHeight;
        }

        if (offsetChanged)
        {
            OnPropertyChanged(nameof(PlayheadMargin));
            OnPropertyChanged(nameof(PlayheadReadoutMargin));
            // ⚠ 必须一并通知：漏掉它会让播放头处的时间读数停在初始边距（x≈6），
            // 看上去就是「播放线旁的时间不见了」（实测反馈）。
            OnPropertyChanged(nameof(PlayheadTimeMargin));
        }

        if (offsetChanged || qpPlotChanged)
        {
            OnPropertyChanged(nameof(QpMarkerMargin));
        }
    }

    /// <summary>
    /// 把各层的 X 轴窗口移动到以 <paramref name="mediaSeconds"/> 为播放头的位置。
    /// </summary>
    /// <param name="mediaSeconds">当前帧的媒体时间（秒）；也是播放头所处的位置。</param>
    private void UpdateTimelineWindow(double mediaSeconds)
    {
        var center = double.IsFinite(mediaSeconds) ? Math.Max(0, mediaSeconds) : 0;
        _lastPlayheadSeconds = center;

        // 播放头扫到哪根柱子，那根就高亮：按播放头时间先查出各行此刻的绘制值
        UpdatePlayheadMarker(center);

        var leftSeconds = center - _timelineWindowSeconds * PlayheadFraction;

        var minTicks = (double)TimeSpan.FromSeconds(leftSeconds).Ticks;
        var maxTicks = (double)TimeSpan.FromSeconds(leftSeconds + _timelineWindowSeconds).Ticks;

        foreach (var axis in _timelineXAxes)
        {
            axis.MinLimit = minTicks;
            axis.MaxLimit = maxTicks;
        }

        // 四行数值图：Y 轴 = 下 0、上「当前窗口内最大值 ×(1 + 顶部余量)」。
        // 用窗口内最大值而不是「有史以来的最大值」：历史峰值会把刻度永久撑高，
        // 之后的柱子全趴在底部、上方空一大片（实测反馈：BITRATE 看着和下面的图表没贴上）。
        // 峰值滚出窗口后刻度才会回落，所以它是缓的、不会每帧抖动。
        var bitrateLimit = ResolveAxisLimit(WindowMax(minTicks, maxTicks, _bitrateIValues, _bitratePValues, _bitrateBValues));
        var motionLimit = ResolveAxisLimit(WindowMax(minTicks, maxTicks, _motionForwardValues, _motionBackwardValues));
        // GOP：紫柱整体压低（余量同其他高行），关键帧白柱单独拉满到接近行顶；
        // 重排：柱高压到约半行，看起来不那么压迫
        var gopLimit = ResolveAxisLimit(WindowMax(minTicks, maxTicks, _gopValues), TallRowHeadroomRatio);
        var reorderLimit = ResolveAxisLimit(WindowMax(minTicks, maxTicks, _reorderValues), ReorderHeadroomRatio);

        ApplyAxisRange(BitrateYAxes, bitrateLimit);
        ApplyAxisRange(MotionYAxes, motionLimit);
        ApplyAxisRange(GopYAxes, gopLimit);
        ApplyAxisRange(ReorderYAxes, reorderLimit);

        // 关键帧白柱高度跟量程走；量程变化时同步重写历史白条，
        // 否则早先画进去的白条停留在旧量程上、高度参差（「不可能比前面还低」）
        _gopMarkerHeight = gopLimit * KeyFrameMarkerFillRatio;

        if (_gopKeyFrameValues.Count > 0 && Math.Abs(_gopKeyFrameMarkerHeight - _gopMarkerHeight) > 0.01)
        {
            for (var i = 0; i < _gopKeyFrameValues.Count; i++)
            {
                var point = _gopKeyFrameValues[i];
                _gopKeyFrameValues[i] = new DateTimePoint(point.DateTime, _gopMarkerHeight);
            }

            _gopKeyFrameMarkerHeight = _gopMarkerHeight;
        }

        // QP 行用**显式**上下限（不交给 LiveCharts 自动量程）：
        // 播放头右侧那个白色小方块要精确贴在 QP 曲线上，必须能算出「QP 值 → 行内像素高度」，
        // 而自动量程的上下限只有 LiveCharts 自己知道、VM 取不到。
        // 上下限取**当前窗口内实际柱体的最低下沿 / 最高上沿**（与其它行同一套 WindowMax 思路）：
        // 早先用的是「只跟最新几帧、还带衰减的包络」，窗口里较早的那些高柱就会被裁掉上边
        // （实测反馈「QP 上边被裁掉了」）。按窗口取，画面里出现的柱子一定都在量程内。
        var qpTopValue = WindowMax(minTicks, maxTicks, _qpBarValues);
        var qpBottomValue = WindowMin(minTicks, maxTicks, _qpBarBottomValues);
        var qpPadding = Math.Max(0.5, (qpTopValue - qpBottomValue) * 0.08);

        _qpAxisMin = Math.Max(0, qpBottomValue - qpPadding);
        _qpAxisMax = Math.Max(_qpAxisMin + 1, qpTopValue + qpPadding);
        ApplyAxisBand(QpYAxes, _qpAxisMin, _qpAxisMax);

        // 悬浮读数：数值图下沿固定 0（直接写在 XAML 里），QP 行上下沿都给。
        // BITRATE 这格是「窗口内单帧瞬时码率峰值 + 顶部留白」，很容易被误读成全片平均码率
        // （实测反馈「填了 200 却看到 3838」），因此显式带上单位。
        UpdateRangeText(
            ref _bitrateMaxText,
            nameof(BitrateMaxText),
            bitrateLimit <= 1.0001 ? "-" : $"{FormatRangeValue(bitrateLimit)} kbps");
        UpdateRangeText(ref _motionMaxText, nameof(MotionMaxText), FormatRangeValue(motionLimit));
        UpdateRangeText(ref _gopMaxText, nameof(GopMaxText), FormatRangeValue(gopLimit));
        UpdateRangeText(ref _reorderMaxText, nameof(ReorderMaxText), FormatRangeValue(reorderLimit));

        UpdateRangeText(ref _qpMaxText, nameof(QpMaxText), FormatRangeValue(_qpAxisMax));
        UpdateRangeText(ref _qpMinText, nameof(QpMinText), FormatRangeValue(_qpAxisMin));

        // QP 行的小方块位置依赖上面刚更新的量程
        OnPropertyChanged(nameof(QpMarkerMargin));
    }

    /// <summary>把一行的 Y 轴量程固定成显式的上下限（QP 行用）。</summary>
    private static void ApplyAxisBand(Axis[] axes, double min, double max)
    {
        if (axes.Length == 0)
        {
            return;
        }

        var axis = axes[0];

        if (axis.MinLimit != min || axis.MaxLimit != max)
        {
            axis.MinLimit = min;
            axis.MaxLimit = max;

            // 横向网格密度：步长 = 量程的 ~1/4（与其他行同一套 NiceStep 规则）
            axis.MinStep = NiceStep(max - min);
        }
    }

    /// <summary>
    /// 刷新「播放头所在柱」的高亮：按播放头时间在各行的数据集合里二分查表，
    /// 取到绘制值后写进高亮序列（每行只有一个点）。
    /// </summary>
    /// <remarks>
    /// ⚠ 必须查表取<b>实际绘制值</b>，不能按当前帧现算：
    /// QP 柱高带逐帧限幅、BITRATE 还拆成 I/P/B 三条互斥序列，现算出来的值和图上那根柱子
    /// 对不上，高亮框就会错位。表格本身按时间有序，二分即可。
    /// </remarks>
    private void UpdatePlayheadMarker(double mediaSeconds)
    {
        var frame = CurrentFrame;

        if (frame is null || !double.IsFinite(mediaSeconds))
        {
            ClearHighlights();
            return;
        }

        // X 用当前帧的媒体时间：与时间轴窗口的圆心、播放头竖线同一个来源，三者严格同源
        var x = new DateTime(TimeSpan.FromSeconds(Math.Max(0, mediaSeconds)).Ticks);

        SetHighlight(_highlightBitrateValues, x, frame.BitrateKbps);
        SetHighlight(_highlightILaneValues, x, frame.Kind == FrameKind.I ? 1 : 0);
        SetHighlight(_highlightPLaneValues, x, frame.Kind == FrameKind.P ? 1 : 0);
        SetHighlight(_highlightBLaneValues, x, frame.Kind == FrameKind.B ? 1 : 0);
        SetHighlight(_highlightMotionForwardValues, x, frame.ForwardMeanPixels);
        SetHighlight(_highlightMotionBackwardValues, x, frame.BackwardMeanPixels);
        SetHighlight(_highlightGopValues, x, frame.GopPosition);
        SetHighlight(_highlightReorderValues, x, frame.ReorderDelay);
    }

    /// <summary>清除全部高亮（没有当前帧时）。</summary>
    private void ClearHighlights()
    {
        _highlightBitrateValues.Clear();
        _highlightILaneValues.Clear();
        _highlightPLaneValues.Clear();
        _highlightBLaneValues.Clear();
        _highlightMotionForwardValues.Clear();
        _highlightMotionBackwardValues.Clear();
        _highlightGopValues.Clear();
        _highlightReorderValues.Clear();
    }

    /// <summary>
    /// 把高亮序列收敛成「当前帧那一个点」；取不到值就清空（不画高亮）。
    /// </summary>
    /// <remarks>
    /// 静默集合不发通知，写入本身不触发 measure（measure 由 <see cref="RequestChartsUpdate"/> 统一驱动），
    /// 因此可以直接用索引器覆盖；值与位置都没变时干脆不写，省掉逐帧的点对象分配。
    /// </remarks>
    private static void SetHighlight(SilentChartValues target, DateTime x, double? value)
    {
        if (value is not { } v || !double.IsFinite(v) || v <= 0)
        {
            if (target.Count > 0)
            {
                target.Clear();
            }

            return;
        }

        if (target.Count == 1)
        {
            var existing = target[0];

            if (existing.DateTime == x && existing.Value == v)
            {
                return;
            }

            target[0] = new DateTimePoint(x, v);
            return;
        }

        target.Clear();
        target.Add(new DateTimePoint(x, v));
    }

    /// <summary>轴上限 = 数据最大值再留一点顶部余量，避免最高柱紧贴图表上沿。</summary>
    private static double ResolveAxisLimit(double max, double headroom = AxisHeadroomRatio) =>
        max <= 0 ? 1 : max * (1 + headroom);

    /// <summary>
    /// 取若干数据集合在 [minTicks, maxTicks] 窗口内的最大值。
    /// </summary>
    /// <remarks>
    /// 只统计窗口内的点：这样量程跟的是「当前画面里的内容」，
    /// 而不是整段的历史峰值（否则峰值一出现，后面所有柱子都会被压扁）。
    /// </remarks>
    private static double WindowMax(
        double minTicks,
        double maxTicks,
        params SilentChartValues[] collections)
    {
        var max = 0.0;

        foreach (var values in collections)
        {
            foreach (var point in values)
            {
                var ticks = point.DateTime.Ticks;

                if (ticks < minTicks || ticks > maxTicks)
                {
                    continue;
                }

                if (point.Value is { } value && value > max)
                {
                    max = value;
                }
            }
        }

        return max;
    }

    /// <summary>
    /// 取若干数据集合在 [minTicks, maxTicks] 窗口内的最小值（0 与负数视为「该点没有数据」跳过）。
    /// </summary>
    private static double WindowMin(
        double minTicks,
        double maxTicks,
        params SilentChartValues[] collections)
    {
        var min = double.MaxValue;

        foreach (var values in collections)
        {
            foreach (var point in values)
            {
                var ticks = point.DateTime.Ticks;

                if (ticks < minTicks || ticks > maxTicks)
                {
                    continue;
                }

                if (point.Value is { } value && value > 0 && value < min)
                {
                    min = value;
                }
            }
        }

        return min == double.MaxValue ? 0 : min;
    }

    /// <summary>
    /// 把一行的 Y 轴量程固定成「下 0、上 = 会到过的最大值」。
    /// </summary>
    /// <remarks>
    /// 量程只增不减：数据一旦到过某个高度，轴上限就停在那里，不随数据回落自动缩小 ——
    /// 否则峰值一过去整行图都会跟着缩放，看着一直在动。
    /// </remarks>
    private static void ApplyAxisRange(Axis[] axes, double max)
    {
        if (axes.Length == 0)
        {
            return;
        }

        var axis = axes[0];
        var limit = Math.Max(1, max);

        if (axis.MaxLimit != limit)
        {
            axis.MinLimit = 0;
            axis.MaxLimit = limit;

            // 横向网格密度：步长 = 量程的 ~1/4，把横线控制在 4 格左右（用户反馈：太密）
            axis.MinStep = NiceStep(limit);
        }
    }

    /// <summary>量程读数的数值格式：大数取整、小数保留一位；无数据时显示 "-"。</summary>
    private static string FormatRangeValue(double value) => value <= 0 ? "-" : $"{value:0.#}";

    /// <summary>刷新一格读数文案，值没变时不通知界面（避免无谓重绘）。</summary>
    private void UpdateRangeText(ref string field, string propertyName, string text)
    {
        if (text == field)
        {
            return;
        }

        field = text;
        OnPropertyChanged(propertyName);
    }

    /// <summary>
    /// 时间轴：X 值使用「媒体时间对应的 DateTime.Ticks」。
    /// </summary>
    /// <remarks>
    /// ⚠ 所有数据行都**不画**刻度文字（<c>LabelsPaint = null</c>）。
    /// LiveCharts 是按「有没有 LabelsPaint」来决定是否测量刻度文字、并据此给绘图区留左右余量
    /// （首末刻度文字会被顶进来）—— 实测带刻度的图绘图区会横向收窄约 20px
    /// （1179.67 vs 1200），各行几何一旦不同，同一个时间在各行就落在不同的 x 上。
    /// 反向也走不通：让薄行（I/P/B 泳道）也参与标签测量，文字会把绘图区高度吃光、
    /// 柱子整行消失（实测反馈「帧类型图表不见了」）。
    /// 因此刻度文字统一交给底部那条自绘刻度条（见 <see cref="Views.TimeRuler"/>）：
    /// 它的位置由 View 用图表自己的「数据 → 像素」换算下发，与数据列严格对齐，也不占轴的纵向空间。
    /// </remarks>
    private static Axis CreateTimeAxis(bool showGrid = false) => new()
    {
        // ⚠ 标签器必须做健壮性校验：
        // LiveCharts 会用各种内部中间值（含 0、负数、越界值）调用 Labeler，
        // 直接 new DateTime(ticks) 会抛 ArgumentOutOfRangeException，
        // 而该异常会被 LiveCharts 内部吞掉并**中断整张图的绘制** —— 表现为图表完全空白。
        Labeler = FormatAxisTime,
        UnitWidth = TimeSpan.FromSeconds(1).Ticks,
        MinStep = TimeSpan.FromSeconds(1).Ticks,
        TextSize = 9,
        LabelsPaint = null,

        // 竖向网格线默认不画：时间轴的刻度信息已由底部自绘刻度条承担，
        // 图内竖条只是视觉噪音（用户反馈「去掉图表默认的竖条」）
        SeparatorsPaint = showGrid ? new SolidColorPaint(new SKColor(0xFF, 0xFF, 0xFF, 14)) : null,
        ShowSeparatorLines = showGrid,
    };

    private static Axis CreateValueAxis(
        double? minLimit,
        double? maxLimit,
        string format,
        bool showSeparators = true) => new()
    {
        Labeler = value => FormatAxisNumber(value, format),
        MinLimit = minLimit,
        MaxLimit = maxLimit,
        TextSize = 9,

        // ⚠ 刻意不画 Y 轴刻度文字。
        // 绘图区左边缘的留白由刻度文字宽度决定，而各行量程不同、文字长短也不一样
        // （"12000" / "63" / "300" / 泳道无刻度），于是每行的绘图区宽度都不同 ——
        // 同一个时间在各行就落在不同的 x 上：播放头对不齐、同样的柱宽看起来间距也不一致。
        // 统一不画刻度后各行绘图区几何完全一致，量程改由左侧标题文字标注。
        LabelsPaint = null,

        // 横向网格线保留（不占边距、只在绘图区内部）；
        // 密度由 <see cref="ApplyAxisRange"/> 按量程下发 MinStep 控制（默认自动刻度太密）
        SeparatorsPaint = showSeparators
            ? new SolidColorPaint(new SKColor(0xFF, 0xFF, 0xFF, 14))
            : null,
    };

    /// <summary>
    /// 挑一个 ≈ range/4 的「整齐」步长：横向网格最多 4 格左右，不密。
    /// </summary>
    /// <remarks>
    /// LiveCharts 的自动刻度固定按 ~10 格取步长，在矮图上会排出十几条线；
    /// rc5 没有直接的「刻度数」旋钮，只能用 <see cref="Axis.MinStep"/> 抬高步长下限来稀释。
    /// </remarks>
    private static double NiceStep(double range)
    {
        var rough = Math.Max(range, 1) / 4;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var normalized = rough / magnitude;

        return (normalized switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10,
        }) * magnitude;
    }

    /// <summary>把 ticks 安全地格式化为 m:ss。</summary>
    private static string FormatAxisTime(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)
            || value < DateTime.MinValue.Ticks || value > DateTime.MaxValue.Ticks)
        {
            return string.Empty;
        }

        return new DateTime((long)value).ToString(@"m\:ss");
    }

    private static string FormatAxisNumber(double value, string format)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return string.Empty;
        }

        return value.ToString(format);
    }

    /// <summary>把一帧采样点追加到全部 6 层曲线。</summary>
    private void AppendChartSample(ChartSample sample)
    {
        var x = sample.MediaTime;

        // BITRATE 按帧类型拆成三条互斥序列，颜色才能跟着帧类型走
        Append(_bitrateIValues, x, sample.IFrame == 1 ? sample.BitrateKbps : 0);
        Append(_bitratePValues, x, sample.PFrame == 1 ? sample.BitrateKbps : 0);
        Append(_bitrateBValues, x, sample.BFrame == 1 ? sample.BitrateKbps : 0);

        Append(_iFrameValues, x, sample.IFrame);
        Append(_pFrameValues, x, sample.PFrame);
        Append(_bFrameValues, x, sample.BFrame);

        // QP：区间带以平均值为基准线对称展开（公式见 FrameInfo.ResolveQpBand），
        // 平均本身再单独画一条曲线
        // 柱体半高 = 本帧 min-max 跨度的一半，但**必须和上一帧比较后再限幅**：
        // 各帧跨度会差好几倍，若每帧各画各的，相邻柱体高度就会剧烈跳动。
        // 这里限制「相对上一帧的变化量不超过 ±MaxBandHalfChangeRatio」，
        // 于是柱体是逐帧缓缓升高/降低的 —— 有参差，但上下差异不会那么夸张。
        // 柱心（= avg，逐帧限幅后）：
        // 「和上一帧比」这一步保留 —— avg 逐帧会有零点几 QP 的抖动，
        // 直接照抄会让柱心一排锯齿；限制住相对上一帧的变化量后，
        // 柱体是平滑爬升/下降的，既有参差又不会上下乱跳。
        // 柱高**不取** min-max 跨度，所以 10~20 与 10~40 两帧
        // 的柱高只由各自的 avg 决定，不会再出现一根高 10、一根高 30 的落差。
        _qpBarCenter = _qpBarCenter <= 0
            ? sample.QpAverage
            : Math.Clamp(
                sample.QpAverage,
                _qpBarCenter - Math.Max(MinBarCenterStep, _qpBarCenter * MaxBarCenterChangeRatio),
                _qpBarCenter + Math.Max(MinBarCenterStep, _qpBarCenter * MaxBarCenterChangeRatio));

        // 柱高只由 avg 决定（不含任何量程项），并整根上下对称展开，
        // 让**柱心正好落在 avg 上** —— avg = 22.2 时柱子的中间就在 22.2 那一条水平线上。
        // 这样量程自适应缩放时，柱子的观感比例始终一致。
        var half = Math.Max(MinBarHalfRange, _qpBarCenter * BarHalfRatioOfAverage);

        Append(_qpBarValues, x, _qpBarCenter + half);
        Append(_qpBarBottomValues, x, _qpBarCenter - half);

        // 曲线**不做平滑**：直接画本帧的平均 QP（= 本帧 min-max 中点），
        // 与右侧读数、柱心完全同源 —— 平滑会让曲线比读数慢半拍，两边对不上。
        Append(_qpAverageValues, x, sample.QpAverage);

        // MOTION 的两根柱**共用同一个 X**（叠加绘制）：后向柱宽、前向柱窄，
        // 两者都落在本帧的位置上 —— 表达「这一帧的运动来自过去还是未来」
        Append(_motionForwardValues, x, sample.ForwardMeanPixels);
        Append(_motionBackwardValues, x, sample.BackwardMeanPixels);
        Append(_gopValues, x, sample.GopPosition);

        // 真关键帧（距关键帧 = 0）额外加一根接近满高的白竖条（用户指定样式：
        // 紫柱整体压低、关键帧白柱拉满）。高度 = 该行量程 × KeyFrameMarkerFillRatio，
        // 由 UpdateTimelineWindow 随量程同步重写历史白条。
        // 非 IDR 的 I 帧（距关键帧 ≠ 0）只有普通柱色，一眼能分辨
        if (sample.GopPosition <= 0)
        {
            Append(_gopKeyFrameValues, x, _gopMarkerHeight);

            // 关键帧在**帧类型 I 泳道**的柱顶叠一点点白：白帽与 I 柱同高（1），
            // 再用略矮的本色柱把下方压回去，于是只有柱顶那一小段留白。
            // 注意：加在 I 泳道上，不加在比特率行（实测要求）。
            Append(_iLaneCapValues, x, 1);
            Append(_iLaneCapBaseValues, x, 1 - IFrameCapRatio);
        }

        Append(_reorderValues, x, sample.ReorderDelay);
    }

    /// <summary>批量搬入新产生的帧采样点，并按时间窗口裁剪。</summary>
    private void PumpChartSamples()
    {
        var pipeline = _pipeline;

        if (pipeline is null)
        {
            return;
        }

        var count = pipeline.Charts.Drain(_pendingSamples, MaxSamplesPerTick);

        if (count == 0)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            AppendChartSample(_pendingSamples[i]);
        }

        // 采样点按媒体时间递增，搬完统一裁剪一次
        TrimTimeline();
        RequestChartsUpdate();
    }

    /// <summary>
    /// 丢弃滑出时间窗口左侧（含 <see cref="TimelineTrimMarginSeconds"/> 余量）的采样点。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ 裁剪基准必须是<b>播放头</b>（当前帧时间），而不是「最新解码到的帧」。
    /// 解码是超前的：最新采样点位于窗口右边缘之外（超前 <c>MaxLeadSeconds</c>），
    /// 若拿它当基准，保留区间整体右移了「超前量」那么多，
    /// 窗口左边缘那一段（约超前量 − 余量）就会被误删 ——
    /// 表现就是时间轴左边一直空着、数据全挤在右侧。
    /// </para>
    /// <para>
    /// ⚠ X 值是以「媒体时间换算成的 <see cref="DateTime.Ticks"/>」表示，起点贴着
    /// <see cref="DateTime.MinValue"/>：播放刚开始时播放头仅比 0 大几十毫秒，
    /// 若用 <see cref="DateTime"/> 做减法会下溢并抛
    /// <see cref="ArgumentOutOfRangeException"/>（该异常在 UI 定时器里会每 15ms 触发一次，
    /// 表现为播放一开始就不断弹出「未处理的异常」）。
    /// 因此这里在 ticks 层面做 long 减法，允许 <c>keepFromTicks</c> 为负值
    /// （负值等价于「窗口左侧还没进入数据区，全部保留」）。
    /// </para>
    /// </remarks>
    private void TrimTimeline()
    {
        var keepFromSeconds = _lastPlayheadSeconds
            - _timelineWindowSeconds * PlayheadFraction
            - TimelineTrimMarginSeconds;

        var keepFromTicks = (long)(keepFromSeconds * TimeSpan.TicksPerSecond);

        foreach (var values in AllValueCollections())
        {
            while (values.Count > 0 && values[0].DateTime.Ticks < keepFromTicks)
            {
                values.RemoveAt(0);
            }

            while (values.Count > MaxChartPoints)
            {
                values.RemoveAt(0);
            }
        }

    }

    private static void Append(SilentChartValues target, DateTime x, double y) =>
        target.Add(new DateTimePoint(x, y));

    /// <summary>清空全部曲线并复位时间窗口（切换片源时调用）。</summary>
    private void ResetCharts()
    {
        foreach (var values in AllValueCollections())
        {
            values.Clear();
        }

        _qpBarCenter = 0;
        _gopMarkerHeight = 10;
        _gopKeyFrameMarkerHeight = 0;
        UpdateTimelineWindow(0);
        RequestChartsUpdate();
    }

    private IEnumerable<SilentChartValues> AllValueCollections()
    {
        yield return _bitrateIValues;
        yield return _bitratePValues;
        yield return _bitrateBValues;
        yield return _iFrameValues;
        yield return _pFrameValues;
        yield return _bFrameValues;
        yield return _qpBarValues;
        yield return _qpBarBottomValues;
        yield return _qpAverageValues;
        yield return _motionForwardValues;
        yield return _motionBackwardValues;
        yield return _gopValues;
        yield return _gopKeyFrameValues;
        yield return _reorderValues;

        // 高亮序列只有当前帧一个点，裁剪时不会命中，但一并列出以免将来漏掉
        yield return _highlightBitrateValues;

        yield return _highlightMotionForwardValues;
        yield return _highlightMotionBackwardValues;
        yield return _highlightGopValues;
        yield return _highlightReorderValues;
        yield return _highlightILaneValues;
        yield return _highlightPLaneValues;
        yield return _highlightBLaneValues;
        yield return _iLaneCapValues;
        yield return _iLaneCapBaseValues;
    }

    /// <summary>
    /// FRAME TYPE 图的一行（泳道）所需的全部图表绑定对象。
    /// </summary>
    /// <remarks>
    /// LiveCharts 单张图只能有一套 Y 轴，无法在「同一张图」内画出三条互不相干的泳道，
    /// 因此 I / P / B 各自对应一张独立的小图（<see cref="Views.MainWindow"/> 中纵向叠放三行），
    /// 但共享同一条时间窗口 —— 每行的 X 轴都登记在 <c>_timelineXAxes</c> 里统一下发上下限。
    /// 这些属性都在 ViewModel 构造期间（<c>DataContext</c> 赋值之前）一次性赋好，
    /// 绑定之后不再变化，故无需实现 <see cref="System.ComponentModel.INotifyPropertyChanged"/>。
    /// </remarks>
    public sealed class FrameTypeLane
    {
        /// <summary>本行的柱序列（只有属于该帧类型的采样点才有非零柱高）。</summary>
        public ObservableCollection<ISeries> Series { get; } = [];

        /// <summary>本行的时间轴（与其他层共享同一个滑动窗口）。</summary>
        public Axis[] XAxes { get; set; } = [];

        /// <summary>本行的 Y 轴（0 / 1 两态，不显示刻度）。</summary>
        public Axis[] YAxes { get; set; } = [];
    }
}

/// <summary>
/// 不发 <c>INotifyCollectionChanged</c> 通知的 <see cref="DateTimePoint"/> 集合（内部 <see cref="List{T}"/>）。
/// </summary>
/// <remarks>
/// <para>
/// 绑到序列后 LiveCharts 的「集合变更 → 自动更新」通路完全关闭：
/// <c>CollectionDeepObserver</c> 只在集合实现 INCC 时订阅，静默集合下每帧批量
/// Append / Clear / 裁剪都不再触发 8ms 节流的 <c>Task.Delay</c> 更新链路；
/// measure 改由 <c>MainViewModel.RequestChartsUpdate</c> 显式驱动。
/// </para>
/// <para>
/// ⚠ 必须同时实现 <see cref="IList{T}"/> 与 <see cref="IReadOnlyCollection{T}"/>：
/// 前者供 <c>WindowMax</c> / <c>TrimTimeline</c> / <c>SetHighlight</c> 等辅助方法做索引访问，
/// 后者是因为 rc5 的 <c>Series.Values</c> 属性类型就是它（只实现 IList 会赋值失败 CS0029）。
/// </para>
/// </remarks>
internal sealed class SilentChartValues : IList<DateTimePoint>, IReadOnlyCollection<DateTimePoint>
{
    private readonly List<DateTimePoint> _items = [];

    public int Count => _items.Count;

    public bool IsReadOnly => false;

    public DateTimePoint this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    public void Add(DateTimePoint item) => _items.Add(item);

    public void Clear() => _items.Clear();

    public bool Contains(DateTimePoint item) => _items.Contains(item);

    public void CopyTo(DateTimePoint[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    public int IndexOf(DateTimePoint item) => _items.IndexOf(item);

    public void Insert(int index, DateTimePoint item) => _items.Insert(index, item);

    public bool Remove(DateTimePoint item) => _items.Remove(item);

    public void RemoveAt(int index) => _items.RemoveAt(index);

    public IEnumerator<DateTimePoint> GetEnumerator() => _items.GetEnumerator();

    // ⚠ 文件隐式引用了 System.Collections.Generic，裸写 IEnumerator 会解析到泛型版本
    // （CS0305 / CS0539 / CS0738），非泛型枚举器必须写全名。
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
}
