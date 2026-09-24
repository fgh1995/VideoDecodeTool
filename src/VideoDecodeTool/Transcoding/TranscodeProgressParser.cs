using System.Globalization;
using VideoDecodeTool.Models;

namespace VideoDecodeTool.Transcoding;

/// <summary>逐行消化 stderr 文本的结果。</summary>
public enum ProgressLineResult
{
    /// <summary>不是进度行（属于 FFmpeg 日志，调用方可按日志处理）。</summary>
    NotProgress,

    /// <summary>是进度行，已被累积，但一个进度块尚未结束。</summary>
    Accumulated,

    /// <summary>凑齐了一个完整进度块，<c>progress</c> 输出有效。</summary>
    BlockCompleted,
}

/// <summary>
/// 解析 FFmpeg <c>-progress pipe:2</c> 输出。
/// </summary>
/// <remarks>
/// <para>输出形如（<b>每个 key 独占一行</b>）：</para>
/// <code>
/// frame=100
/// fps=92.5
/// stream_0_0_q=28.0
/// bitrate=1227.0kbits/s
/// total_size=4823040
/// out_time_us=4000000
/// out_time_ms=4000000
/// out_time=00:00:04.000000
/// dup_frames=0
/// drop_frames=0
/// speed=1.85x
/// progress=continue
/// </code>
/// <para>因此解析器必须<b>跨行累积状态</b>，以 <c>progress=continue|end</c> 作为块结束标志；
/// 不能假设一次性拿到整块文本（<see cref="System.Diagnostics.Process.ErrorDataReceived"/> 是逐行回调）。</para>
/// <para>stderr 中同时混有 FFmpeg 日志，故只识别已知 key，其余交给调用方按日志处理。</para>
/// <para>注意 FFmpeg 的 <c>out_time_ms</c> 历史遗留单位其实是微秒，与 <c>out_time_us</c> 等价。</para>
/// </remarks>
public sealed class TranscodeProgressParser
{
    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "frame", "fps", "stream_0_0_q", "bitrate", "total_size", "out_time_us", "out_time_ms",
        "out_time", "dup_frames", "drop_frames", "speed", "progress",
    };

    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly double _sourceDurationSeconds;
    private readonly long _expectedFrameCount;

    /// <param name="sourceDurationSeconds">源片时长（秒），用于按已输出时间估算进度。</param>
    /// <param name="expectedFrameCount">
    /// <b>预期的输出总帧数</b>（不是源片帧数）。改了输出帧速率时两者并不相等：
    /// 例如 24fps 源片转成 60fps，输出帧数是源片的 2.5 倍。若拿源片帧数当分母，
    /// 进度会在真正转完之前就冲到 100%（实测反馈：显示 100.0% 而转码仍在继续）。
    /// 传 0 表示未知，此时进度回退为按已输出时间估算。
    /// </param>
    public TranscodeProgressParser(double sourceDurationSeconds, long expectedFrameCount)
    {
        _sourceDurationSeconds = sourceDurationSeconds;
        _expectedFrameCount = expectedFrameCount;
    }

    /// <summary>已累积但尚未成块的 key 数量（诊断用）。</summary>
    public int PendingKeyCount => _values.Count;

    /// <summary>
    /// 消化一行 stderr 文本。调用方应传入<b>单行</b>文本（已去掉换行符）。
    /// </summary>
    public ProgressLineResult Consume(string line, out TranscodeProgress progress)
    {
        progress = TranscodeProgress.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return ProgressLineResult.NotProgress;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return ProgressLineResult.NotProgress;
        }

        var key = line[..separator].Trim();
        if (!KnownKeys.Contains(key))
        {
            return ProgressLineResult.NotProgress;
        }

        _values[key] = line[(separator + 1)..].Trim();

        if (!_values.TryGetValue("progress", out var status))
        {
            return ProgressLineResult.Accumulated;
        }

        var frame = ParseLong("frame");
        var outTime = ParseOutTime();

        progress = new TranscodeProgress
        {
            Frame = frame,
            Fps = ParseDouble("fps"),
            BitrateKbps = ParseKbps("bitrate"),
            TotalSizeBytes = ParseLong("total_size"),
            OutTime = outTime,
            Speed = ParseSpeed("speed"),
            DropFrames = (int)ParseLong("drop_frames"),
            DupFrames = (int)ParseLong("dup_frames"),
            Qp = ParseDouble("stream_0_0_q"),
            IsFinal = string.Equals(status, "end", StringComparison.OrdinalIgnoreCase),
            Percent = _expectedFrameCount > 0 && frame > 0
                ? Math.Min(100.0, frame * 100.0 / _expectedFrameCount)
                : 0,
            TimePercent = _sourceDurationSeconds > 0 && outTime.TotalSeconds > 0
                ? Math.Min(100.0, outTime.TotalSeconds * 100.0 / _sourceDurationSeconds)
                : 0,
        };

        _values.Clear();
        return ProgressLineResult.BlockCompleted;
    }

    /// <summary>重置累积状态（重新开始一次转码时调用）。</summary>
    public void Reset() => _values.Clear();

    private long ParseLong(string key) =>
        _values.TryGetValue(key, out var raw)
        && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private double ParseDouble(string key) =>
        _values.TryGetValue(key, out var raw)
        && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    /// <summary>bitrate 形如 “1227.0kbits/s” 或 “N/A”。</summary>
    private double ParseKbps(string key)
    {
        if (!_values.TryGetValue(key, out var raw) || raw.Length == 0
            || raw.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var digits = new string(raw.TakeWhile(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>speed 形如 “1.85x” 或 “N/A”。</summary>
    private double ParseSpeed(string key)
    {
        if (!_values.TryGetValue(key, out var raw) || raw.Length == 0
            || raw.StartsWith("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var digits = new string(raw.TakeWhile(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>优先使用 out_time_us；缺失时退化为 out_time 文本。</summary>
    private TimeSpan ParseOutTime()
    {
        var micros = ParseLong("out_time_us");
        if (micros <= 0)
        {
            // 部分构建只输出 out_time_ms（单位同样是微秒）
            micros = ParseLong("out_time_ms");
        }

        if (micros > 0)
        {
            return TimeSpan.FromSeconds(micros / 1_000_000.0);
        }

        if (_values.TryGetValue("out_time", out var text)
            && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return TimeSpan.Zero;
    }
}
