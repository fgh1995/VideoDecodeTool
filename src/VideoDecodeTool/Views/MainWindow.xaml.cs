using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LiveChartsCore.Drawing;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView.WPF;
using SkiaSharp.Views.Desktop;
using VideoDecodeTool.ViewModels;

namespace VideoDecodeTool.Views;

/// <summary>
/// 主窗口：只负责视图生命周期与输入事件，业务逻辑全部在 <see cref="MainViewModel"/>。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 播放头几何同步周期。绘图区的左右留白由 LiveCharts 按刻度文字宽度算出来，
    /// 只有等它真正渲染过一次才知道；布局又可能随窗口缩放而变化，
    /// 因此用一个低频定时器周期性测量，值没变时 <c>UpdateTimelineGeometry</c> 会直接返回。
    /// </summary>
    private static readonly TimeSpan PlayheadSyncInterval = TimeSpan.FromMilliseconds(400);

    private readonly MainViewModel _viewModel;
    private readonly DispatcherTimer _playheadSyncTimer;

    /// <summary>
    /// 从可视化树收集到的 9 张图表内部的 MotionCanvas。
    /// measure 只把画布标记为失效（<c>CanvasCore.IsValid=false</c>），
    /// 真正重画由 <see cref="OnChartRendering"/> 对齐屏幕刷新驱动。
    /// </summary>
    private readonly List<MotionCanvas> _chartCanvases = [];

    /// <summary>复用给底部时间刻度条的刻度集合（避免每帧重新分配）。</summary>
    private readonly List<(double X, string Label)> _pendingRulerTicks = [];

    public MainWindow()
    {
        InitializeComponent();

        // 用窗口的 Dispatcher 构造 ViewModel，保证定时器运行在 UI 线程
        _viewModel = new MainViewModel(Dispatcher);
        DataContext = _viewModel;

        // 「停止转码」询问框由 View 负责弹出（ViewModel 只持有回调，不直接创建窗口）
        _viewModel.ConfirmStopTranscode = () =>
        {
            var dialog = new StopTranscodeDialog { Owner = this };
            dialog.ShowDialog();
            return dialog.Choice;
        };

        _viewModel.PreviewInvalidated += OnPreviewInvalidated;
        PreviewSurface.PaintSurface += OnPreviewPaintSurface;

        // measure 之后只标记画布失效，重画时机由合成时钟统一驱动（与屏幕刷新同步）
        CompositionTarget.Rendering += OnChartRendering;

        _playheadSyncTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = PlayheadSyncInterval,
        };
        _playheadSyncTimer.Tick += OnPlayheadSyncTick;
        _playheadSyncTimer.Start();

        Loaded += OnWindowLoaded;
        Closing += OnWindowClosing;
    }

    private void OnPlayheadSyncTick(object? sender, EventArgs e) => SyncPlayheadGeometry();

    /// <summary>待应用到音频页的最小高度（视频页最近一次测得的高度）。</summary>
    private double _pendingAudioMinHeight;

    /// <summary>
    /// 视频页内容高度 → 音频页的最小高度。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两个页签行数不同（视频 7 行、音频 2 行），TabControl 按内容自适应高度，
    /// 于是切换页签时整个参数面板忽高忽低（实测反馈「动来动去」）。
    /// 把音频页的最小高度钉到视频页的高度后，内容区尺寸恒定；宽度本来就由外层拉伸决定。
    /// </para>
    /// <para>
    /// ⚠ 必须用 <see cref="Dispatcher"/> 延后到本次布局结束之后再改 <c>MinHeight</c>：
    /// <c>SizeChanged</c> 是在 Measure/Arrange 过程中回调的，此时直接修改布局属性
    /// 属于「在布局过程中反向修改布局」，会让 WPF 重入重排。
    /// （实测反馈的闪退：崩溃转储里 UI 线程正停在
    /// <c>ScrollViewer.ArrangeOverride → TextBlock.Format → LoCreateLine</c> 的排版栈上，
    /// 异常码 0xc0000374 = 原生堆损坏。）
    /// 延后执行后既保留等高效果，又不会在排版过程中反复触发重排。
    /// </para>
    /// </remarks>
    private void OnVideoParamsPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Height <= 0)
        {
            return;
        }

        var target = Math.Ceiling(e.NewSize.Height);

        // 布局过程中本回调会多次触发，高度未实质变化时不再排队
        if (Math.Abs(target - _pendingAudioMinHeight) < 1)
        {
            return;
        }

        _pendingAudioMinHeight = target;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            () =>
            {
                if (AudioParamsPanel is not null)
                {
                    AudioParamsPanel.MinHeight = _pendingAudioMinHeight;
                }
            });
    }

    /// <summary>
    /// 上半行的预览列宽度：按可用高度反推 16:9，多出来的横向空间留给右侧参数面板。
    /// </summary>
    /// <remarks>
    /// 之前是两列各占一半，而 16:9 的画面在偏方的高宽比下占不满自己那一半，
    /// 两侧就留下大块空白（实测反馈）。这里只改列宽、不动卡片比例：
    /// 卡片仍按 16:9 内接（见 <see cref="OnPreviewSlotSizeChanged"/>），此时槽位本身已是 16:9，
    /// 于是卡片正好铺满。比较后再赋值，避免「改宽度 → 触发 SizeChanged → 再改」的循环。
    /// </remarks>
    private void OnTopGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var height = e.NewSize.Height;

        if (height <= 0)
        {
            return;
        }

        var width = Math.Max(PreviewColumn.MinWidth, height * 16 / 9);

        if (PreviewColumn.Width.IsAbsolute && Math.Abs(PreviewColumn.Width.Value - width) < 0.5)
        {
            return;
        }

        PreviewColumn.Width = new GridLength(width);
    }

    /// <summary>上一次给预览卡片锁定的尺寸；用来判断是否真的变了（避免自己改尺寸时递归）。</summary>
    private Size _previewCardSize;

    /// <summary>
    /// 预览卡片固定 16:9：按可用区域（<c>PreviewSlot</c>）<b>内接</b>出一个 16:9 的矩形。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只按宽度算高度是不够的：宽度变大时算出来的高度会超出可用高度，
    /// 卡片被行高裁掉一截、看起来就成了 19:7 之类更扁的比例（实测反馈）。
    /// 所以先按宽度算，高度放不下时改按高度反推宽度 —— 卡片永远完整落在可用区域内。
    /// </para>
    /// <para>
    /// 回调挂在 <c>PreviewSlot</c> 上而不是卡片自身：槽位的尺寸由外层布局决定，
    /// 改卡片的宽高不会反过来影响它，因此不会形成尺寸循环。
    /// </para>
    /// </remarks>
    private void OnPreviewSlotSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = e.NewSize;

        if (available.Width <= 0 || available.Height <= 0)
        {
            return;
        }

        // 16:9 内接：宽度吃满或高度吃满，取其中较小的一个
        var width = Math.Min(available.Width, available.Height * 16 / 9);
        var height = width * 9 / 16;

        if (Math.Abs(width - _previewCardSize.Width) < 0.5 && Math.Abs(height - _previewCardSize.Height) < 0.5)
        {
            return;
        }

        _previewCardSize = new Size(width, height);
        PreviewCard.Width = Math.Round(width);
        PreviewCard.Height = Math.Round(height);
    }

    /// <summary>
    /// 给画面容器（<c>PreviewFrame</c>）按它自己的圆角裁一次。
    /// </summary>
    /// <remarks>
    /// <c>Border</c> 的 <c>CornerRadius</c> 只决定自己背景的形状，<b>不会裁剪子内容</b>；
    /// 而 SkiaSharp 画出来的是直角矩形，四个直角会从圆角处透出来，把卡片的圆角盖掉。
    /// 所以按同一个半径显式裁一次，半径直接从 <c>CornerRadius</c> 取，不额外维护常量。
    /// </remarks>
    private void OnPreviewFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var radius = PreviewFrame.CornerRadius.TopLeft;

        PreviewFrame.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), radius, radius);
    }

    /// <summary>
    /// 读取图表绘图区几何，换算到 <c>ChartHost</c> 坐标系后下发给 ViewModel：
    /// 播放头覆盖层据此对齐到数据位置，柱宽也据此推算。
    /// </summary>
    private void SyncPlayheadGeometry()
    {
        var chart = BitrateChart;
        var core = chart.CoreChart;

        if (core is null)
        {
            return;
        }

        var margin = core.DrawMarginSize;

        if (margin.Width <= 1 || margin.Height <= 1)
        {
            return;
        }

        // 各层共用同一套列布局与同一条时间轴，故取第一层（BITRATE）的几何即可代表全部
        var origin = chart.TranslatePoint(new Point(0, 0), ChartHost);
        var location = core.DrawMarginLocation;

        // 播放头的横向位置**直接用引擎自己的换算**：数据值（ticks）→ 控件像素。
        // 之前是「绘图区左边缘 + 宽度 × 0.5」几何推算的，与引擎实际的坐标排布之间存在
        // 系统性偏差（实测反馈：总差一格）。ScaleDataToPixels 就是渲染时用的那套映射，
        // 用它算出来的位置与柱子必然一致。
        var playheadInChart = _viewModel.CurrentFrame is { } playheadFrame
            ? chart.ScaleDataToPixels(
                new LvcPointD((double)TimeSpan.FromSeconds(Math.Max(0, playheadFrame.Time)).Ticks, 0),
                0,
                0).X
            : double.NaN;

        if (double.IsNaN(playheadInChart) || playheadInChart <= 1)
        {
            playheadInChart = location.X + margin.Width * MainViewModel.PlayheadFraction;
        }

        // QP 行另外单独测一份：播放头右侧那个白色小方块要按「QP 值 → 该行像素高度」换算，
        // 因此需要 QP 绘图区的上边与高度（各图的 DrawMargin 不一定一致）。
        //
        // ⚠ 这里的 Y 必须是**相对 QP 那一行**的偏移，不能加上 TranslatePoint 得到的行绝对 Y：
        // 那个白色方块是 ChartHost 第 2 行的子元素，它的 Margin 是相对**所在单元格**左上角算的，
        // 加上行绝对 Y 会把方块整体推到下面几行去（实测反馈「方块没看见」）。
        // 横向不用减：各行都从第 0 列铺满整宽，所以相对单元格的 X 与相对 ChartHost 的 X 相同。
        var qpTop = 0.0;
        var qpHeight = 0.0;
        var qpCore = QpChart.CoreChart;

        if (qpCore is not null)
        {
            qpTop = qpCore.DrawMarginLocation.Y;
            qpHeight = qpCore.DrawMarginSize.Height;
        }

        _viewModel.UpdateTimelineGeometry(
            origin.X + playheadInChart,
            margin.Width,
            qpTop,
            qpHeight);
    }


    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnWindowLoaded;

        // Loaded = 首次布局与模板应用完成：从可视化树收集 9 张 CartesianChart 登记给 VM
        //（静默集合下图表更新的显式通路依赖它），并收集各自内部的 MotionCanvas 供重画驱动用。
        var chartViews = new List<IChartView>();
        CollectChartsAndCanvases(this, chartViews, _chartCanvases);
        _viewModel.AttachChartViews(chartViews);

        await _viewModel.InitializeAsync();

        // 支持「打开方式 / 拖到 exe 上 / 命令行传参」直接载入片源
        var argument = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(File.Exists);

        if (argument is not null)
        {
            await _viewModel.LoadFileAsync(argument);
        }

        // 面板内容高于可视区域；等布局与数据填充完全稳定后再归位到顶部，
        // 否则会被内部可聚焦控件（如命令预览 TextBox）的 BringIntoView 带动到中部。
        await Dispatcher.InvokeAsync(() => InfoScroll.ScrollToTop(), DispatcherPriority.ApplicationIdle);
    }

    private void OnPreviewInvalidated() => PreviewSurface.InvalidateVisual();

    /// <summary>
    /// 合成时钟驱动的图表重画：只把「measure 已标记失效」的画布重画一遍。
    /// </summary>
    /// <remarks>
    /// ⚠ 必须带 <c>IsValid</c> 条件：无条件全量重画 9 张图会把合成帧率打到屏幕刷新率以下；
    /// 带条件的语义是「measure 提交了变更 → 本合成帧把变更画出来」，时机与屏幕刷新严格对齐
    /// （MotionCanvas 自带的轮询重画循环走 Task.Delay，节拍抖动大，只作兜底）。
    /// </remarks>
    private void OnChartRendering(object? sender, EventArgs e)
    {
        var repainted = false;

        foreach (var canvas in _chartCanvases)
        {
            if (!canvas.CanvasCore.IsValid)
            {
                canvas.InvalidateVisual();
                repainted = true;
            }
        }

        // 时间刻度条与图表共用同一个时间窗口：图表这一帧要重画，刻度就跟着一起挪。
        // 静态时（没有新数据、窗口没滑动）这里一次都不会执行。
        if (repainted)
        {
            UpdateTimeRuler();
        }
    }

    /// <summary>
    /// 底部时间刻度条：把窗口内的整秒时刻按图表实际的数据 → 像素换算摆到刻度条上。
    /// </summary>
    /// <remarks>
    /// 位置刻意用 <c>ScaleDataToPixels</c>（引擎渲染时用的那套映射）而不是几何推算，
    /// 与播放头竖线的取法同源，因此刻度线必然落在柱子的真实位置上。
    /// 时刻本身取整秒，窗口跨度最大约 9.2 秒，一屏最多十来个刻度。
    /// </remarks>
    private void UpdateTimeRuler()
    {
        var chart = BitrateChart;

        if (chart.CoreChart is null || TimeRulerBar is null)
        {
            return;
        }

        var leftSeconds = _viewModel.TimelineLeftSeconds;
        var span = _viewModel.TimelineSpanSeconds;

        if (!(span > 0) || !double.IsFinite(leftSeconds))
        {
            return;
        }

        // 播放头在窗口正中，所以左半屏可能是负时间 —— 那些位置不显示时刻
        var firstSeconds = Math.Max(0, Math.Ceiling(leftSeconds));
        var lastSeconds = leftSeconds + span;

        _pendingRulerTicks.Clear();

        for (var seconds = firstSeconds; seconds <= lastSeconds; seconds += 1)
        {
            var x = chart.ScaleDataToPixels(
                new LvcPointD(seconds * TimeSpan.TicksPerSecond, 0),
                0,
                0).X;

            if (double.IsNaN(x) || x < -20 || x > chart.ActualWidth + 20)
            {
                continue;
            }

            _pendingRulerTicks.Add((x, TimeSpan.FromSeconds(seconds).ToString(@"m\:ss")));
        }

        TimeRulerBar.SetTicks(_pendingRulerTicks);
    }

    /// <summary>递归遍历可视化树，收集图表视图与其内部的 MotionCanvas。</summary>
    private static void CollectChartsAndCanvases(
        DependencyObject root, List<IChartView> charts, List<MotionCanvas> canvases)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);

        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            switch (child)
            {
                case CartesianChart chart:
                    charts.Add(chart);
                    break;

                case MotionCanvas canvas:
                    canvases.Add(canvas);
                    break;
            }

            CollectChartsAndCanvases(child, charts, canvases);
        }
    }

    /// <summary>SkiaSharp 绘制回调：直接解码帧 + 叠加层，避免 WPF 中间层拷贝。</summary>
    private void OnPreviewPaintSurface(object? sender, SKPaintSurfaceEventArgs e) =>
        _viewModel.RenderPreview(e.Surface.Canvas, e.Info);

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _playheadSyncTimer.Stop();
        _playheadSyncTimer.Tick -= OnPlayheadSyncTick;

        // 先解绑，避免释放过程中触发已销毁控件的重绘
        CompositionTarget.Rendering -= OnChartRendering;
        _viewModel.PreviewInvalidated -= OnPreviewInvalidated;
        PreviewSurface.PaintSurface -= OnPreviewPaintSurface;
        _viewModel.Dispose();
    }

    // ------------------------------------------------------------------
    // 拖放打开文件
    // ------------------------------------------------------------------

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        var path = files[0];

        if (!File.Exists(path))
        {
            return;
        }

        await _viewModel.LoadFileAsync(path);
    }
}
