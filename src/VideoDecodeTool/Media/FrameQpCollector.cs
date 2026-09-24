using System.Globalization;
using System.Text;
using FFmpeg.AutoGen;

namespace VideoDecodeTool.Media;

/// <summary>
/// 逐帧 QP（量化参数）采集器：把解码器调试输出里的逐宏块 QP 汇总成「每帧平均 QP」。
/// </summary>
/// <remarks>
/// <para><b>为什么不能直接从解码 API 取</b></para>
/// <para>
/// <c>AVFrame.quality</c> 是唯一与 QP 相关的字段，但 libavcodec 的 H.264 解码器
/// **从不填写它**（实测恒为 0，且 <c>AVFrame</c> 里也没有 qp_table 之类的成员），
/// 所以逐帧 QP 只能另找来源。
/// </para>
/// <para><b>可用来源：<c>FF_DEBUG_QP</c></b></para>
/// <para>
/// 打开 <c>AVCodecContext.debug</c> 的 <c>FF_DEBUG_QP</c> 后，H.264 解码器会：
/// 每帧开头打一行 <c>"New frame, type: %c\n"</c>，
/// 随后为每个宏块打一行 <c>"%2d"</c>（即该宏块的 QP）。实测 1080p 一帧正好
/// 120×68 = 8160 行，行数与宏块数严格相等 —— 因此按固定行数切分即可还原每帧 QP，
/// 不必依赖帧头标记（多片并行时帧头标记可能重复出现）。
/// </para>
/// <para><b>性能</b></para>
/// <para>
/// 打开调试开关后，每条日志都会调用一次 <c>av_log</c>（1080p24 下约 20 万次/秒）。
/// 回调里先用<b>格式化字符串本身</b>做判断，只有确认是宏块 QP 行才真正格式化，
/// 其余日志一律直接丢弃，因此绝大部分调用只花一次字符串比较。
/// </para>
/// <para><b>线程</b></para>
/// <para>
/// 帧级并行会让多帧的宏块日志交错、无法按帧切分，因此采集期间视频解码器改用片级并行
/// （见 <see cref="VideoDecoder"/>）；回调本身加锁保证多片线程安全。
/// </para>
/// </remarks>
internal static unsafe class FrameQpCollector
{
    /// <summary>宏块 QP 行的格式化字符串（解码器固定用 <c>"%2d"</c>）。</summary>
    private const string MacroblockQpFormat = "%2d";

    /// <summary>帧头标记的格式化字符串。</summary>
    private const string FrameStartFormat = "New frame, type: %c\n";

    /// <summary>保留的帧数上限（超出后丢弃最旧的，避免长时间播放时无界增长）。</summary>
    private const int MaxBlocks = 4096;

    private static readonly object Gate = new();
    private static readonly List<QpBlock> Blocks = [];
    private static readonly byte[] LineBuffer = new byte[64];
    private static readonly av_log_set_callback_callback LogCallback = OnLog;

    private static int _expectedMacroblocks;
    private static int _firstBlockIndex;
    private static long _sum;
    private static int _count;
    private static int _min;
    private static int _max;
    private static bool _enabled;

    /// <summary>采集是否已启用。</summary>
    public static bool IsEnabled
    {
        get
        {
            lock (Gate)
            {
                return _enabled;
            }
        }
    }

    /// <summary>
    /// 启用采集。
    /// </summary>
    /// <param name="macroblocksPerFrame">每帧宏块数（16×16 网格，含边缘补齐）。</param>
    public static void Enable(int macroblocksPerFrame)
    {
        if (macroblocksPerFrame <= 0)
        {
            return;
        }

        lock (Gate)
        {
            _expectedMacroblocks = macroblocksPerFrame;
            _firstBlockIndex = 0;
            Blocks.Clear();
            ResetAccumulator();
            _enabled = true;
        }

        // 调试输出本身只在 DEBUG 级别才会走到回调
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_DEBUG);
        ffmpeg.av_log_set_callback(LogCallback);
    }

    /// <summary>停止采集（切走片源/关闭管线时调用）。</summary>
    public static void Disable()
    {
        lock (Gate)
        {
            _enabled = false;
            _expectedMacroblocks = 0;
            _firstBlockIndex = 0;
            Blocks.Clear();
            ResetAccumulator();
        }
    }

    /// <summary>
    /// 把当前累加器结算成一帧的 QP 统计（顺序即解码顺序）。
    /// </summary>
    /// <remarks>
    /// 平均值用「实际累积到的宏块数」作分母，而不是期望宏块数 ——
    /// 帧头标记切分时最后一帧的宏块数不一定是完整的 8160，用期望值会把平均值算小。
    /// </remarks>
    private static void FlushBlock()
    {
        if (_count > 0)
        {
            Blocks.Add(new QpBlock(_min, _max, (int)Math.Round(_sum / (double)_count)));
        }

        ResetAccumulator();
    }

    /// <summary>复位当前帧的累加器。</summary>
    private static void ResetAccumulator()
    {
        _sum = 0;
        _count = 0;
        _min = int.MaxValue;
        _max = 0;
    }

    /// <summary>单帧的 QP 统计（该帧所有宏块的最小 / 最大 / 平均值）。</summary>
    public readonly record struct QpBlock(int Min, int Max, int Average);

    /// <summary>
    /// 取某张解码图片的 QP 统计（本帧所有宏块的最小 / 最大 / 平均值）。
    /// </summary>
    /// <param name="codedPictureNumber">
    /// 解码顺序编号（<c>AVFrame.coded_picture_number</c>）。
    /// 宏块日志是按解码顺序打出来的，两者一一对应 —— 解码器输出的是显示顺序，
    /// 正是靠这个编号才能把「显示顺序的帧」对回「解码顺序的 QP」。
    /// </param>
    /// <param name="block">取到的每帧 QP 统计。</param>
    /// <returns>是否取到（尚未采集到任何数据时返回 false）。</returns>
    /// <remarks>
    /// 编号越界时按<b>就近</b>取（钳制到已有区间），而不是直接放弃：
    /// 编号与宏块块号只在极少数边界情况下会错开一两帧，
    /// 就近取可以保证界面不会时不时冒出「N/A」——QP 本就是相邻帧高度相关的量。
    /// </remarks>
    public static bool TryTake(long codedPictureNumber, out QpBlock block)
    {
        block = default;

        lock (Gate)
        {
            if (Blocks.Count == 0)
            {
                return false;
            }

            var index = (int)Math.Clamp(codedPictureNumber - _firstBlockIndex, 0, Blocks.Count - 1);
            block = Blocks[index];
            return true;
        }
    }

    /// <summary>
    /// <c>av_log</c> 全局回调：只挑出宏块 QP 行与帧头标记，其余直接丢弃。
    /// </summary>
    private static void OnLog(void* avcl, int level, string format, byte* vl)
    {
        if (format is null)
        {
            return;
        }

        // 先用格式化字符串过滤：绝大多数日志在这一步就被丢掉了（不做任何格式化）
        var isMacroblockQp = format == MacroblockQpFormat;
        var isFrameStart = format == FrameStartFormat;

        if (!isMacroblockQp && !isFrameStart)
        {
            return;
        }

        lock (Gate)
        {
            if (!_enabled || _expectedMacroblocks <= 0)
            {
                return;
            }

            if (isFrameStart)
            {
                // 帧头标记才是权威的帧边界：把已累积的宏块直接结算成一帧。
                // ⚠ 不能用「宏块数凑满 8160」来切分：数量一旦对不上（边缘宏块、多 slice 等），
                // 每个块都会横跨相邻两帧 —— 表现就是区间带高度恒为某个值（帧间差）、
                // 平均值逐帧乱跳，而真实数据其实很平稳。
                FlushBlock();
                return;
            }

            if (!TryReadQp(avcl, level, format, vl, out var qp))
            {
                return;
            }

            _sum += qp;
            _count++;

            if (qp < _min)
            {
                _min = qp;
            }

            if (qp > _max)
            {
                _max = qp;
            }

            // 兜底：万一没有帧头标记（部分构建不打），凑满宏块数也结算一次
            if (_count >= _expectedMacroblocks)
            {
                FlushBlock();
            }

            while (Blocks.Count > MaxBlocks)
            {
                Blocks.RemoveAt(0);
                _firstBlockIndex++;
            }
        }
    }

    /// <summary>直接读 va_list 的抽样核对次数（核对通过后不再走格式化路径）。</summary>
    private const int DirectReadCheckCount = 16;

    /// <summary>直接读 va_list 已核对通过的次数。</summary>
    private static int _directReadChecks;

    /// <summary>核对发现直接读与格式化结果不一致（ABI 意外），此后永久退回格式化路径。</summary>
    private static bool _directReadRejected;

    /// <summary>
    /// 取出宏块 QP 行里的那个整数。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>"%2d"</c> 只有一个 int 实参，而回调拿到的 <paramref name="vl"/> 就是 <c>va_list</c>：
    /// Windows x64 下它直接指向第一个可变实参，因此可以直接读出这个数。
    /// 这样每条宏块日志就省掉一次 <c>vsnprintf</c> + 一次字符串分配 + 一次文本解析。
    /// </para>
    /// <para>
    /// 这不是纯优化小事：1080p 下这里是<b>每帧约 8000 条</b>日志、每秒数十万次，
    /// 而它恰好是预览解码里最重的一环。改了输出帧速率（例如 24 → 60fps）以后
    /// 帧数成倍增长，这一环的开销也成倍 —— 解码因此跟不上实时，超前量被吃光，
    /// 画面就会卡顿一次、时间轴右侧也跟着空掉。
    /// </para>
    /// <para>
    /// 直接读属于 ABI 取巧，因此带自查：前 <see cref="DirectReadCheckCount"/> 条会与
    /// 「格式化 + 解析」的结果逐条比对，一致才继续用直接读；
    /// 一旦不一致（或读到的值不在合法 QP 范围内）就永久退回格式化路径。
    /// </para>
    /// </remarks>
    private static bool TryReadQp(void* avcl, int level, string format, byte* vl, out int value)
    {
        if (!_directReadRejected)
        {
            var direct = *(int*)vl;

            // H.264 的 QP 合法范围是 0~51，这里放宽到 63 作为「看起来像 QP」的判定
            if (direct is >= 0 and <= 63)
            {
                if (_directReadChecks < DirectReadCheckCount)
                {
                    _directReadChecks++;

                    if (!TryFormatInt(avcl, level, format, vl, out var formatted))
                    {
                        // 格式化路径本身失败（缓冲不够等），本条按未取到处理
                        value = 0;
                        return false;
                    }

                    if (formatted == direct)
                    {
                        value = direct;
                        return true;
                    }

                    _directReadRejected = true;
                }
                else
                {
                    value = direct;
                    return true;
                }
            }
        }

        return TryFormatInt(avcl, level, format, vl, out value);
    }

    /// <summary>把本质上是单个整数的日志行格式化后解析出来。</summary>
    private static bool TryFormatInt(void* avcl, int level, string format, byte* vl, out int value)
    {
        value = 0;

        var printPrefix = 0;
        int written;
        int end;

        fixed (byte* line = LineBuffer)
        {
            written = ffmpeg.av_log_format_line2(avcl, level, format, vl, line, LineBuffer.Length, &printPrefix);

            if (written <= 0)
            {
                return false;
            }

            var length = Math.Min(written, LineBuffer.Length);
            end = Array.IndexOf(LineBuffer, (byte)0, 0, length);

            if (end < 0)
            {
                end = length;
            }
        }

        var text = Encoding.ASCII.GetString(LineBuffer, 0, end).Trim();

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
