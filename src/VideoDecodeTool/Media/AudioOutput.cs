using NAudio;
using NAudio.Wave;

namespace VideoDecodeTool.Media;

/// <summary>
/// NAudio 音频输出管线：<see cref="BufferedWaveProvider"/> + <see cref="WaveOutEvent"/>。
/// </summary>
/// <remarks>
/// 音频时钟 = 已入队音频的结束时间 − 仍在缓冲区中尚未播放的时长。
/// 该值即“扬声器此刻正在播放的时间点”，是音视频同步的基准。
/// </remarks>
public sealed class AudioOutput : IDisposable
{
    private readonly object _gate = new();
    private readonly BufferedWaveProvider _provider;
    private readonly WaveOutEvent _device;
    private readonly int _bytesPerSecond;

    private double _queuedEndSeconds;
    private bool _disposed;

    /// <summary>是否已有真实音频样本入队。用于让主时钟在音频真正开始之前保持冻结，
    /// 避免「位置 = 0 &gt; -∞」把 <see cref="PlaybackClock"/> 的启动冻结条件提前打破。</summary>
    private bool _started;

    public AudioOutput(int sampleRate = AudioDecoder.OutputSampleRate, int channels = AudioDecoder.OutputChannels)
    {
        SampleRate = sampleRate;
        Channels = channels;
        _bytesPerSecond = sampleRate * channels * sizeof(short);

        _provider = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
        {
            // 8 秒环形缓冲；溢出时丢弃最旧数据，保证实时性优先。
            // ⚠ 必须 ≥ 解码超前量（PlaybackPipeline 中可达 5.0s）：
            // 解复用线程是按「视频超前量」限流的，音频会跟着一起超前入队，
            // 缓冲若小于超前量就会持续溢出丢样本 —— 表现为周期性爆音。
            BufferDuration = TimeSpan.FromSeconds(BufferSeconds),
            DiscardOnBufferOverflow = true,
            // 欠载时输出静音而不是让 WaveOut 结束播放（重要：边转边播时数据是断续到达的）
            ReadFully = true,
        };

        _device = new WaveOutEvent
        {
            // 设备内部缓冲总量 = DesiredLatency × NumberOfBuffers。
            // 取 100ms×2：既避免欠载爆音，又能让音频时钟的误差控制在 ~200ms 以内。
            DesiredLatency = DeviceLatencyMilliseconds,
            NumberOfBuffers = DeviceBufferCount,
        };

        _device.Init(_provider);
    }

    /// <summary>provider 环形缓冲容量（秒）。</summary>
    private const double BufferSeconds = 8;

    /// <summary>设备期望延迟（毫秒）。</summary>
    private const int DeviceLatencyMilliseconds = 100;

    /// <summary>设备缓冲块数量。</summary>
    private const int DeviceBufferCount = 2;

    /// <summary>
    /// 设备内部尚未播出的缓冲时长（秒）。
    /// </summary>
    /// <remarks>
    /// <see cref="BufferedWaveProvider.BufferedDuration"/> 只统计「还在 provider 里」的数据，
    /// 已被 waveOut 取走放进设备环形缓冲的部分并不会计入；若忽略这部分，
    /// 音频时钟会凭空超前约一个设备缓冲时长，表现为画面比声音早几百毫秒。
    /// </remarks>
    private static readonly double DeviceBufferedSeconds = DeviceLatencyMilliseconds * DeviceBufferCount / 1000.0;

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>缓冲区中尚未播放的时长（秒）。</summary>
    public double BufferedSeconds => _provider.BufferedDuration.TotalSeconds;

    /// <summary>是否已有真实音频样本入队（即音频是否真正启动）。</summary>
    public bool HasSamples
    {
        get
        {
            lock (_gate)
            {
                return _started;
            }
        }
    }

    /// <summary>
    /// 当前音频播放位置（秒）= 已入队音频结束时间 − provider 中未播出的时长 − 设备内部缓冲时长。
    /// 即“扬声器此刻正在播放的时间点”。
    /// </summary>
    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                // 还没有任何样本入队时返回 NaN：让主时钟知道「音频尚未开始」，
                // 从而保持冻结而不是退回墙钟跑掉首片到达前的延迟（边转边播的 GOP 延迟，
                // 高帧率下可达数秒），否则时钟会凭空领先、开头若干秒被当成过期帧丢弃，
                // 表现为「音频滞后于画面」。
                if (!_started)
                {
                    return double.NaN;
                }

                return Math.Max(0, _queuedEndSeconds - BufferedSeconds - DeviceBufferedSeconds);
            }
        }
    }

    /// <summary>音量（0.0 ~ 1.0）。</summary>
    public float Volume
    {
        get => _device.Volume;
        set => _device.Volume = Math.Clamp(value, 0f, 1f);
    }

    public PlaybackState PlaybackState => _device.PlaybackState;

    /// <summary>把解码并重采样后的 PCM 数据放入播放缓冲。</summary>
    /// <param name="buffer">PCM 数据（16bit 交错）。</param>
    /// <param name="count">有效字节数。</param>
    /// <param name="timestampSeconds">该块第一个样本的时间戳；NaN 表示未知，按连续流推算。</param>
    public void Enqueue(byte[] buffer, int count, double timestampSeconds)
    {
        if (count <= 0)
        {
            return;
        }

        var duration = count / (double)_bytesPerSecond;

        lock (_gate)
        {
            // 溢出保护：缓冲装不下整块时整块丢弃，并且**不**推进音频时钟。
            // 之前无条件先推进 _queuedEndSeconds，而缓冲溢出时 provider 会把
            // 装不下的样本丢掉 —— 时钟被推到未来，视频丢帧追赶时钟，
            // 表现为音频滞后数秒（边转边播时分片突发到达，极易触发）。
            if (BufferedSeconds + duration > BufferSeconds)
            {
                return;
            }

            if (double.IsNaN(timestampSeconds))
            {
                _queuedEndSeconds += duration;
            }
            else
            {
                // 允许 PTS 轻微回退（封装层抖动），但不允许倒退
                _queuedEndSeconds = Math.Max(_queuedEndSeconds, timestampSeconds) + duration;
            }

            _provider.AddSamples(buffer, 0, count);
            _started = true;
        }
    }

    /// <summary>开始播放。</summary>
    public void Play()
    {
        if (_device.PlaybackState != PlaybackState.Playing)
        {
            _device.Play();
        }
    }

    public void Pause()
    {
        if (_device.PlaybackState == PlaybackState.Playing)
        {
            _device.Pause();
        }
    }

    /// <summary>停止并清空缓冲（重新开始播放时使用）。</summary>
    public void Stop()
    {
        _device.Stop();
        _provider.ClearBuffer();

        lock (_gate)
        {
            _queuedEndSeconds = 0;
            _started = false;
        }
    }

    /// <summary>清空缓冲但不停止设备。</summary>
    public void Flush()
    {
        _provider.ClearBuffer();

        lock (_gate)
        {
            _queuedEndSeconds = PositionSeconds;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _device.Stop();
            _device.Dispose();
        }
        catch (MmException)
        {
            // 设备已被移除等异常场景，忽略
        }
    }
}
