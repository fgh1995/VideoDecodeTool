using VideoDecodeTool.Models;

namespace VideoDecodeTool.Media;

/// <summary>
/// 图表数据缓冲：解码线程**按帧**写入，UI 线程每秒批量取走（对应需求文档的“环形缓冲区保留最近 N 帧数据”）。
/// </summary>
/// <remarks>
/// <para>与“每秒聚合一个点”的方案相比，逐帧采样才能画出参考界面那种
/// 「每个 I 帧一根绿色尖峰、帧类型按帧着色、QP 锯齿」的剪辑软件式时间轴。</para>
/// <para>解码线程只做入队（无锁竞争、无分配之外的额外开销），
/// UI 侧每秒一次 <see cref="Drain"/> 批量搬运，避免高频触发界面刷新。</para>
/// </remarks>
public sealed class ChartDataBuffer
{
    /// <summary>缓冲上限（约 2.5 分钟 @24fps 的冗余，实际显示窗口远小于此）。</summary>
    private const int Capacity = 4096;

    private readonly object _gate = new();
    private readonly Queue<ChartSample> _pending = new();

    /// <summary>当前待取走的采样点数量。</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>写入一帧（解码线程调用）。</summary>
    public void Add(FrameInfo info)
    {
        // 只保「有限 + 非负」的兜底，**不再设上限**：
        // 这几处原本是为固定量程（BITRATE 0~12000、MOTION 0~300、GOP 0~60、REORDER 0~15）写的保护，
        // 量程改成自适应后这些上限就变成了削峰 —— 真实数据一旦超过它就被截断成上限值，
        // 图表/量程读数于是永远停在那（实测反馈：GOP 值超过 60 了，图上最大还是 60）。
        var bitrate = double.IsFinite(info.BitrateKbps) ? Math.Max(0, info.BitrateKbps) : 0;
        var motionMean = double.IsFinite(info.MotionMeanPixels) ? Math.Max(0, info.MotionMeanPixels) : 0;
        var motionMax = double.IsFinite(info.MotionMaxPixels) ? Math.Max(0, info.MotionMaxPixels) : 0;

        var sample = new ChartSample(
            // X 轴用媒体时间：时间轴与片源进度一致，叠加显示时能对得上
            new DateTime(TimeSpan.FromSeconds(Math.Max(0, info.Time)).Ticks),
            bitrate,
            info.Kind == FrameKind.I ? 1 : 0,
            info.Kind == FrameKind.P ? 1 : 0,
            info.Kind == FrameKind.B ? 1 : 0,
            // 图表用的「平均 QP」与读数同源（= 本帧 min-max 的中点），柱心才不会和读数差半个 QP
            info.QpAverage,
            // min / max 取本帧「宏块级」的最小 / 最大 QP：柱高按 avg 定，区间跨度仍保留原始数据
            info.QpMin,
            info.QpMax,
            motionMean,
            motionMax,
            Math.Max(0, info.ForwardMeanPixels),
            Math.Max(0, info.BackwardMeanPixels),
            Math.Max(0, info.GopPosition),
            Math.Max(0, info.ReorderDelay));

        lock (_gate)
        {
            _pending.Enqueue(sample);

            // 超过上限时丢弃最旧的，保证内存恒定
            while (_pending.Count > Capacity)
            {
                _pending.Dequeue();
            }
        }
    }

    /// <summary>批量取走采样点；最多取 <paramref name="maxCount"/> 个。</summary>
    public int Drain(List<ChartSample> target, int maxCount)
    {
        target.Clear();

        lock (_gate)
        {
            var count = Math.Min(maxCount, _pending.Count);

            for (var i = 0; i < count; i++)
            {
                target.Add(_pending.Dequeue());
            }

            return count;
        }
    }

    /// <summary>清空缓冲（切换片源时调用）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }
}
