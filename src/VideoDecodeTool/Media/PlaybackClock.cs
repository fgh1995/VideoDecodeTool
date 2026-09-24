using System.Diagnostics;

namespace VideoDecodeTool.Media;

/// <summary>
/// 播放主时钟。默认以 <b>音频时钟</b> 为准（NAudio 已播样本推算），
/// 无音频轨或音频长时间停滞时自动退化为墙钟，保证画面不会永久卡死。
/// </summary>
public sealed class PlaybackClock
{
    /// <summary>音频停止推进多久后回退到墙钟（秒）。</summary>
    private const double AudioStallTimeoutSeconds = 2.0;

    private readonly object _gate = new();
    private readonly Stopwatch _wall = new();
    private readonly Stopwatch _audioStall = Stopwatch.StartNew();
    private readonly Func<double>? _audioPositionProvider;

    private double _position;
    private double _baseSeconds;
    private double _lastAudioPosition = double.NegativeInfinity;
    private bool _running;
    private bool _audioEverStarted;

    /// <summary>是否已放弃等待音频启动（流到尾仍未出现可解码音频样本）。</summary>
    private bool _giveUpAudioWait;

    /// <param name="audioPositionProvider">音频时钟提供者；为 null 时始终使用墙钟。</param>
    public PlaybackClock(Func<double>? audioPositionProvider = null)
    {
        _audioPositionProvider = audioPositionProvider;
    }

    /// <summary>当前播放位置（秒）。</summary>
    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                return ComputePosition();
            }
        }
    }

    /// <summary>是否已经启动。</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running;
            }
        }
    }

    /// <summary>是否由音频驱动（用于界面显示时钟来源）。</summary>
    public bool IsAudioDriven
    {
        get
        {
            lock (_gate)
            {
                return _audioPositionProvider is not null
                       && _audioEverStarted
                       && _audioStall.Elapsed.TotalSeconds < AudioStallTimeoutSeconds;
            }
        }
    }

    /// <summary>
    /// 是否处于「音频轨已存在、但尚无样本入队」的启动预缓冲期（此时主时钟被冻结在起点）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 解复用背压必须据此<b>跳过</b>「视频超前量」限流。原因：fMP4 的首个碎片常在 mdat 里
    /// 把整段视频样本排在音频样本之前，若此时仍按「视频超前量」限流，解复用会在读到音频包
    /// 之前就被挡在 <c>maxLead</c> 处；而主时钟解冻又恰恰依赖这份音频 —— 于是形成死锁：
    /// 时钟不动 → 超前量不下降 → 解复用永不读音频 → 时钟永远不动。
    /// </para>
    /// <para>
    /// 跳过期间帧队列本身仍有界（满则淘汰最旧帧），内存不会失控；而音频一旦由扬声器
    /// 独立推动时钟，即恢复按超前量限流（此时限流安全，因为时钟不再依赖解复用线程推进）。
    /// </para>
    /// </remarks>
    public bool IsWaitingForAudio
    {
        get
        {
            lock (_gate)
            {
                return _running
                       && _audioPositionProvider is not null
                       && !_audioEverStarted
                       && !_giveUpAudioWait;
            }
        }
    }

    /// <summary>
    /// 放弃等待音频启动：此后按墙钟推进，避免「音频轨存在却始终无样本」的流把画面
    /// 永久冻结在起点。仅在流已结束（不可能再有音频）时调用。
    /// </summary>
    public void AbandonAudioWait()
    {
        lock (_gate)
        {
            _giveUpAudioWait = true;
        }
    }

    /// <summary>启动时钟。</summary>
    public void Start(double initialSeconds = 0)
    {
        lock (_gate)
        {
            _baseSeconds = initialSeconds;
            _position = initialSeconds;
            _wall.Restart();
            _running = true;
        }
    }

    /// <summary>暂停：冻结当前时间。</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _position = ComputePosition();
            _baseSeconds = _position;
            _wall.Stop();
            _running = false;
        }
    }

    /// <summary>继续。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }

            _baseSeconds = _position;
            _wall.Restart();
            _running = true;
        }
    }

    /// <summary>完全停止并归零。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            _wall.Reset();
            _position = 0;
            _baseSeconds = 0;
            _lastAudioPosition = double.NegativeInfinity;
            _audioEverStarted = false;
            _giveUpAudioWait = false;
        }
    }

    private double ComputePosition()
    {
        if (!_running)
        {
            return _position;
        }

        if (_audioPositionProvider is not null)
        {
            var audio = _audioPositionProvider();

            if (!double.IsNaN(audio) && audio > _lastAudioPosition + 0.0005)
            {
                _lastAudioPosition = audio;
                _audioEverStarted = true;
                _audioStall.Restart();
            }

            // 音频作为主时钟的条件：确实在推进、未停滞，且已经追上当前进度。
            // 「追上当前进度」这一条很关键：否则在音频设备卡顿后会出现时间倒流，
            // 表现为画面回退、帧被反复丢弃。
            if (_audioEverStarted
                && _audioStall.Elapsed.TotalSeconds < AudioStallTimeoutSeconds
                && audio >= _position - 0.05)
            {
                _wall.Stop();
                _position = Math.Max(_position, audio);
                return _position;
            }

            // ⚠ 音频轨存在、但还没喂入任何样本（分片流首片尚未到达的预缓冲期）：
            // 此时**不能**退回墙钟 —— 否则主时钟会凭空跑掉一个分片的延迟
            // （frag_duration 2s），等音频真正开始时，开头若干秒的视频帧被当成「过期帧」
            // 全部丢弃，画面直接从中间开始，造成「音频滞后于画面」的明显失同步。
            // 因此未启动期间把时钟冻结在起点，等第一份音频到达后再接管。
            // （无音频轨时 _audioEverStarted 永远不会置位，但上面的 if 已跳过，
            //  会继续走下方的墙钟分支，纯视频播放不受此影响。）
            if (!_audioEverStarted && !_giveUpAudioWait)
            {
                return _position;
            }
        }

        // 墙钟模式（无音频 / 音频未启动 / 停滞 / 严重落后）：
        // 持续计时，且绝不能每次调用都重置基准，否则时钟会永远停在原位。
        if (!_wall.IsRunning)
        {
            _baseSeconds = _position;
            _wall.Restart();
        }

        // 单调递增：无论何种模式切换，播放位置都不允许倒退
        _position = Math.Max(_position, _baseSeconds + _wall.Elapsed.TotalSeconds);
        return _position;
    }
}
