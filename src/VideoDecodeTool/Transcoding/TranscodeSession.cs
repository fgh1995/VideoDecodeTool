using System.Diagnostics;
using System.IO;
using System.Text;
using VideoDecodeTool.Models;

namespace VideoDecodeTool.Transcoding;

/// <summary>
/// 一次 FFmpeg 转码进程会话（模式 A：转码监控 + 边转边播）。
/// </summary>
/// <remarks>
/// <para><b>stdout / stderr 分工</b>（对应需求文档 5.1 的数据流）</para>
/// <list type="bullet">
/// <item><c>stdout</c>：分片 MP4 媒体字节，必须以二进制方式读取。由后台<b>全速抽干</b>线程经
/// <see cref="TeeReadStream"/> 读入并镜像留存在临时文件；预览侧则通过
/// <see cref="GrowingFileReadStream"/> 跟随该文件按实时读取 —— 写读解耦，转码不再被播放速率拖慢；</item>
/// <item><c>stderr</c>：<c>-progress pipe:2</c> 的结构化进度 + 日志，按行异步读取。</item>
/// </list>
/// <para><b>标准 MP4 的产出</b>：等抽干结束（<see cref="WaitForDrainAsync"/>，此时镜像已完整）后
/// 调用 <see cref="FinalizeAsync"/>，把临时分片流以 <c>-c copy -movflags +faststart</c>
/// 重封装为 moov 前置的标准 MP4，随后删除临时文件。重封装是流拷贝，不产生二次编码损失。</para>
/// </remarks>
public sealed class TranscodeSession : IAsyncDisposable
{
    /// <summary>启动探测窗口：该时间内进程退出且退出码非 0，则判定启动失败并把 stderr 抛给调用方。</summary>
    private const int StartupProbeMilliseconds = 3000;

    private readonly Process _process;
    private readonly TranscodeProgressParser _parser;
    private readonly string _ffmpegPath;
    private readonly string _workingDirectory;
    private readonly TaskCompletionSource<int> _exitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StringBuilder _capturedLog = new();
    private readonly object _logGate = new();
    private readonly object _streamGate = new();

    /// <summary>全速抽干线程用它把 stdout 的分片流读入镜像文件（不受播放速率影响）。</summary>
    private TeeReadStream? _liveStream;

    /// <summary>预览读侧：按播放速率跟随「正在写入的镜像文件」。</summary>
    private GrowingFileReadStream? _previewStream;

    private Task _drainTask = Task.CompletedTask;
    private readonly TaskCompletionSource _drainDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _finalized;
    private bool _disposed;

    /// <summary>收尾时等待抽干线程结束的上限（通常已结束，仅作兜底）。</summary>
    private static readonly TimeSpan DrainWaitTimeout = TimeSpan.FromSeconds(10);

    private TranscodeSession(
        Process process,
        TranscodeProgressParser parser,
        string ffmpegPath,
        string workingDirectory,
        string commandLine,
        string outputFilePath,
        bool liveStreamEnabled,
        string? fragmentedFilePath)
    {
        _process = process;
        _parser = parser;
        _ffmpegPath = ffmpegPath;
        _workingDirectory = workingDirectory;
        CommandLine = commandLine;
        OutputFilePath = outputFilePath;
        LiveStreamEnabled = liveStreamEnabled;
        FragmentedFilePath = fragmentedFilePath;

        _process.ErrorDataReceived += OnErrorDataReceived;
        _process.BeginErrorReadLine();

        // 独立任务等待退出，对外暴露为 Completion
        _ = Task.Run(async () =>
        {
            try
            {
                await _process.WaitForExitAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // 进程未启动
            }
            finally
            {
                _exitSource.TrySetResult(SafeExitCode());
            }
        });
    }

    /// <summary>实际执行的命令行（用于界面展示与排障）。</summary>
    public string CommandLine { get; }

    /// <summary>输出文件的绝对路径。</summary>
    public string OutputFilePath { get; }

    /// <summary>是否把媒体输出到了 stdout（边转边播）。</summary>
    public bool LiveStreamEnabled { get; }

    /// <summary>镜像留存的分片 MP4 临时文件路径；非直播模式为 null。</summary>
    public string? FragmentedFilePath { get; }

    /// <summary>重封装失败时的原因。</summary>
    public string? FinalizeError { get; private set; }

    /// <summary>最近一次进度块。</summary>
    public TranscodeProgress Progress { get; private set; } = TranscodeProgress.Empty;

    /// <summary>进程退出任务，结果为退出码。</summary>
    public Task<int> Completion => _exitSource.Task;

    public bool HasExited => _exitSource.Task.IsCompleted;

    /// <summary>镜像已写入的字节数（可用于“边转边播”进度诊断）。</summary>
    public long MirroredBytes => _liveStream?.MirroredBytes ?? 0;

    /// <summary>进度更新（<b>后台线程</b>回调，调用方不得直接访问 UI）。</summary>
    public event Action<TranscodeProgress>? ProgressChanged;

    /// <summary>FFmpeg 日志行（<b>后台线程</b>回调）。</summary>
    public event Action<string>? LogReceived;

    /// <summary>累积的 stderr 文本（仅保留前 32KB，避免长时间转码内存增长）。</summary>
    public string CapturedLog
    {
        get
        {
            lock (_logGate)
            {
                return _capturedLog.ToString();
            }
        }
    }

    /// <summary>
    /// 预览读侧的数据流：跟随「正在写入的镜像分片文件」按需阻塞读取。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 该流的所有权交给 <see cref="Media.PlaybackPipeline"/>，由其在释放时关闭。
    /// </para>
    /// <para>
    /// <b>返回的不是 stdout 本身</b>：stdout 由后台抽干线程全速读入镜像文件，
    /// 转码因此不再被播放速率拖慢；预览改读镜像文件，写读两侧互不阻塞
    /// （原先预览直接读 stdout，必须按播放速率限流，管道随即写满并把 FFmpeg 拖到 1x）。
    /// </para>
    /// </remarks>
    public Stream LiveStream
    {
        get
        {
            if (!LiveStreamEnabled)
            {
                throw new InvalidOperationException("当前会话未启用 stdout 直播流输出");
            }

            lock (_streamGate)
            {
                if (_liveStream is null)
                {
                    // TeeReadStream 构造时创建（清空）镜像文件，抽干线程与预览读侧都基于它
                    _liveStream = new TeeReadStream(_process.StandardOutput.BaseStream, FragmentedFilePath!, ownsSource: true);
                    _drainTask = Task.Run(DrainPumpLoop);
                }

                // 预览流可能已被「停止播放」释放过：按需再给一个新的跟随流，
                // 于是停播后再次点「播放」就能从镜像开头重放（边转边播期间镜像一直在写）。
                if (_previewStream is null || _previewStream.IsDisposed)
                {
                    _previewStream = new GrowingFileReadStream(FragmentedFilePath!);

                    // 转码已结束：新读流直接标记生产者结束，读到末尾即 EOF
                    if (_drainDone.Task.IsCompleted)
                    {
                        _previewStream.MarkProducerDone();
                    }
                }

                return _previewStream;
            }
        }
    }

    /// <summary>
    /// 全速把 stdout 的分片流抽干到镜像文件（字节由 <see cref="TeeReadStream.Read"/> 顺带镜像写入）。
    /// </summary>
    /// <remarks>不按播放速率限流，因此 FFmpeg 永远不会因为 stdout 写满而停滞。</remarks>
    private void DrainPumpLoop()
    {
        var tee = _liveStream;

        if (tee is null)
        {
            _drainDone.TrySetResult();
            return;
        }

        var buffer = new byte[1 << 16];

        try
        {
            while (tee.Read(buffer, 0, buffer.Length) > 0)
            {
                // 持续推进即可；数据已由 TeeReadStream 写入镜像文件
            }
        }
        catch (IOException)
        {
            // 进程被终止 / 管道断开
        }
        catch (ObjectDisposedException)
        {
            // 主动收尾时释放了管道
        }
        finally
        {
            // 关闭镜像文件（冲刷落盘）后，预览读侧读到末尾即可判定 EOF
            tee.Dispose();
            _previewStream?.MarkProducerDone();
            _drainDone.TrySetResult();
        }
    }

    /// <summary>
    /// 等待「stdout 全速抽干」结束（此时镜像文件已完整）。
    /// </summary>
    /// <remarks>
    /// 收尾重封装必须等它完成，否则镜像会被从分片中间截断。
    /// 与播放端无关：预览此刻通常远没放完，收尾后仍可继续按实时播放。
    /// </remarks>
    public async Task WaitForDrainAsync(CancellationToken cancellationToken = default)
    {
        if (_previewStream is null)
        {
            // 未启用边转边播（没有读侧），无需等待
            return;
        }

        await _drainDone.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------
    // 启动
    // ------------------------------------------------------------------

    /// <summary>启动转码进程。</summary>
    /// <param name="request">转码参数。</param>
    /// <param name="sourceDurationSeconds">源片时长（秒），用于计算进度百分比。</param>
    /// <param name="expectedFrameCount">
    /// 预期的<b>输出</b>总帧数（改了输出帧速率时与源片帧数不同），用于计算进度百分比。
    /// </param>
    public static async Task<TranscodeSession> StartAsync(
        TranscodeRequest request,
        double sourceDurationSeconds,
        long expectedFrameCount,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.FfmpegPath))
        {
            throw new FileNotFoundException($"未找到 ffmpeg：{request.FfmpegPath}");
        }

        var outputPath = Path.GetFullPath(request.OutputPath);
        var workingDirectory = Path.GetDirectoryName(outputPath) ?? AppContext.BaseDirectory;

        Directory.CreateDirectory(workingDirectory);

        var fragmentedPath = request.EnableLiveStream
            ? Path.Combine(
                workingDirectory,
                Path.GetFileNameWithoutExtension(outputPath) + ".partial.frag.mp4")
            : null;

        var arguments = FfmpegArgumentBuilder.Build(request, request.EnableLiveStream);
        var commandLine = FfmpegArgumentBuilder.ToCommandLine(request.FfmpegPath, arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FfmpegPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,   // 媒体数据（二进制）
            RedirectStandardError = true,    // 进度 + 日志
            RedirectStandardInput = true,    // 配合 -nostdin 防止子进程抢占控制台输入
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("FFmpeg 进程无法启动");
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // 忽略
        }

        var session = new TranscodeSession(
            process,
            new TranscodeProgressParser(sourceDurationSeconds, expectedFrameCount),
            request.FfmpegPath,
            workingDirectory,
            commandLine,
            outputPath,
            request.EnableLiveStream,
            fragmentedPath);

        // 启动探测：短时间内退出且退出码非 0，说明参数/编码器不可用，立刻把 stderr 反馈给调用方
        var probe = await Task.WhenAny(session.Completion, Task.Delay(StartupProbeMilliseconds, cancellationToken))
            .ConfigureAwait(false);

        if (probe == session.Completion)
        {
            var exitCode = await session.Completion.ConfigureAwait(false);

            if (exitCode != 0)
            {
                var log = session.CapturedLog.Trim();
                await session.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"FFmpeg 退出（码 {exitCode}）：{log}");
            }
        }

        return session;
    }

    // ------------------------------------------------------------------
    // 进度
    // ------------------------------------------------------------------

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is null)
        {
            return;
        }

        var line = e.Data;

        lock (_logGate)
        {
            if (_capturedLog.Length < 32 * 1024)
            {
                _capturedLog.AppendLine(line);
            }
        }

        // 逐行投喂：只有凑齐一个完整块（progress=continue|end）才回调进度，
        // 其余属于进度 key 的行被静默消费，避免污染日志
        var result = _parser.Consume(line, out var progress);

        if (result == ProgressLineResult.BlockCompleted)
        {
            Progress = progress;
            ProgressChanged?.Invoke(progress);
            return;
        }

        if (result == ProgressLineResult.NotProgress && line.Length > 0)
        {
            LogReceived?.Invoke(line);
        }
    }

    // ------------------------------------------------------------------
    // 收尾
    // ------------------------------------------------------------------

    /// <summary>
    /// 产出最终的标准 MP4：关闭镜像文件后执行流拷贝重封装，并清理临时文件。
    /// </summary>
    /// <returns>是否成功产出非空的标准 MP4。</returns>
    public async Task<bool> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        CloseLiveStream();

        if (!LiveStreamEnabled)
        {
            _finalized = true;
            return File.Exists(OutputFilePath) && new FileInfo(OutputFilePath).Length > 0;
        }

        var fragment = FragmentedFilePath;
        if (fragment is null || !File.Exists(fragment) || new FileInfo(fragment).Length == 0)
        {
            FinalizeError = "临时分片文件为空";
            _finalized = true;
            return false;
        }

        var arguments = FfmpegArgumentBuilder.BuildRemux(fragment, OutputFilePath);

        // 重封装是流拷贝，耗时通常为原转码的百分之几
        var result = await ProcessRunner
            .RunAsync(_ffmpegPath, arguments, TimeSpan.FromMinutes(30), _workingDirectory, cancellationToken)
            .ConfigureAwait(false);

        _finalized = true;
        TryDelete(fragment);

        if (!result.Succeeded)
        {
            FinalizeError = FirstLine(result.Diagnostics);
            LogReceived?.Invoke($"[remux] {FinalizeError}");
            return false;
        }

        return File.Exists(OutputFilePath) && new FileInfo(OutputFilePath).Length > 0;
    }

    /// <summary>
    /// 停止抽干并关闭镜像流（触发临时文件落盘）。
    /// </summary>
    /// <remarks>
    /// 预览读侧（<see cref="GrowingFileReadStream"/>）<b>不</b>在这里关闭：它归播放管线所有，
    /// 收尾后预览可继续把镜像里剩余的内容按实时放完（镜像即便被删除，已打开的句柄仍可读）。
    /// </remarks>
    private void CloseLiveStream()
    {
        TeeReadStream? tee;
        Task drain;

        lock (_streamGate)
        {
            tee = _liveStream;
            drain = _drainTask;

            if (tee is null)
            {
                return;
            }
        }

        // 关闭管道：抽干线程中阻塞的读取会立刻返回，随后自行冲刷并关闭镜像文件
        try
        {
            tee.Dispose();
        }
        catch (IOException)
        {
            // 忽略
        }

        try
        {
            drain.Wait(DrainWaitTimeout);
        }
        catch (AggregateException)
        {
            // 抽干线程内的异常不影响收尾：镜像里仍是已成功写入的部分
        }
    }

    /// <summary>请求取消：终止整个进程树（硬解场景下 FFmpeg 可能派生子进程）。</summary>
    public void Cancel()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 进程已退出
        }
        catch (NotSupportedException)
        {
            // 平台不支持进程树终止
        }
    }

    private int SafeExitCode()
    {
        try
        {
            return _process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 文件被占用时留待用户手动清理
        }
        catch (UnauthorizedAccessException)
        {
            // 忽略
        }
    }

    private static string FirstLine(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "无输出";

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Cancel();
        CloseLiveStream();

        try
        {
            await Completion.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // 强杀后仍未回收，继续释放托管资源
        }

        // 未被正式收尾过：清理临时分片文件，避免留下垃圾
        if (!_finalized && FragmentedFilePath is not null)
        {
            TryDelete(FragmentedFilePath);
        }

        try
        {
            _process.ErrorDataReceived -= OnErrorDataReceived;
            _process.Dispose();
        }
        catch (InvalidOperationException)
        {
            // 忽略
        }
    }
}
