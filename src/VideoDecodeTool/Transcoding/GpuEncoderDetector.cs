using System.Text.RegularExpressions;
using VideoDecodeTool.Models;

namespace VideoDecodeTool.Transcoding;

/// <summary>
/// GPU 硬件编码器探测。
/// </summary>
/// <remarks>
/// <para>
/// 分两级：
/// <list type="number">
/// <item><b>能力枚举</b>：解析 <c>ffmpeg -encoders</c> 的输出，<b>动态发现</b>该构建编译的所有视频编码器 ——
/// 按后端后缀（_nvenc / _qsv / _amf）归类、按名称前缀（h264 / hevc / vvc / av1 / vp9）映射编码类型，
/// 有什么列什么，不预设候选矩阵；</item>
/// <item><b>功能性试编码</b>：用 lavfi 合成源编码 6 帧到 null，只有真正跑通（驱动/硬件就绪）才判定可用。
/// 这一步能区分“FFmpeg 编译了 NVENC”与“本机有能用的 N卡”。各编码器为独立 ffmpeg 进程，
/// <b>并发执行</b>（限 4 路），整个探测 ~2 秒内完成。</item>
/// </list>
/// </para>
/// <para>
/// 软件编码走白名单（libx264 / libx265 / libsvtav1 / libvvenc / libvpx-vp9）——
/// 否则 mpeg4、libxvid 之类几百个通用视频编码器都会涌进「编码类型」下拉；
/// 白名单同样按编译情况动态过滤，编译里没有就不显示。
/// </para>
/// </remarks>
public sealed partial class GpuEncoderDetector
{
    /// <summary>硬件编码后端：按编码器名的后缀归类。</summary>
    private static readonly (EncoderKind Kind, string Suffix, string Display)[] HardwareBackends =
    [
        (EncoderKind.Nvenc, "_nvenc", "NVIDIA NVENC"),
        (EncoderKind.Qsv, "_qsv", "Intel Quick Sync"),
        (EncoderKind.Amf, "_amf", "AMD AMF"),
    ];

    /// <summary>软件编码白名单：名称 → 显示名 / 提示（按编译情况动态过滤）。</summary>
    private static readonly (string Name, VideoCodecKind Codec, string Display, string Message)[] SoftwareCandidates =
    [
        ("libx264", VideoCodecKind.H264, "软件编码 libx264", "CPU 编码，始终可用（速度最慢）"),
        ("libx265", VideoCodecKind.Hevc, "软件编码 libx265", "CPU 编码（H.265，速度最慢）"),
        ("libvvenc", VideoCodecKind.Vvc, "软件编码 libvvenc", "CPU 编码（H.266/VVC，速度最慢）"),
        ("libsvtav1", VideoCodecKind.Av1, "软件编码 SVT-AV1", "CPU 编码（AV1，速度最慢）"),
        ("libvpx-vp9", VideoCodecKind.Vp9, "软件编码 libvpx-vp9", "CPU 编码（VP9，速度最慢）"),
    ];

    /// <summary>试编码的并发上限：每个编码器是一个独立 ffmpeg 进程，4 路并发既快又不会把 GPU 初始化请求挤爆。</summary>
    private const int ProbeConcurrency = 4;

    /// <summary>待试编码条目：探测完成前先占住输出列表里的位置，保证结果顺序与发现顺序一致。</summary>
    private sealed record ProbeRequest(
        EncoderKind Kind,
        VideoCodecKind Codec,
        string CodecName,
        string Display);

    private readonly string _ffmpegPath;

    public GpuEncoderDetector(string ffmpegPath) => _ffmpegPath = ffmpegPath;

    /// <summary>执行探测。</summary>
    /// <param name="runFunctionalTest">是否执行功能性试编码（并发执行，整体 ~2 秒）。</param>
    public async Task<GpuCapabilities> DetectAsync(bool runFunctionalTest = true, CancellationToken cancellationToken = default)
    {
        // 三个枚举命令互相独立，并发执行（每个都要拉起 ffmpeg 进程，串行会白白多等几百毫秒）
        var encoderListTask = ProcessRunner.RunAsync(
            _ffmpegPath, ["-hide_banner", "-encoders"], TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);
        var hwAccelListTask = ProcessRunner.RunAsync(
            _ffmpegPath, ["-hide_banner", "-hwaccels"], TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);
        var versionTask = ProcessRunner.RunAsync(
            _ffmpegPath, ["-hide_banner", "-version"], TimeSpan.FromSeconds(15), cancellationToken: cancellationToken);

        await Task.WhenAll(encoderListTask, hwAccelListTask, versionTask).ConfigureAwait(false);

        var encoderList = encoderListTask.Result;
        var hwAccelList = hwAccelListTask.Result;
        var version = versionTask.Result;

        // 输出槽位：占位条目（GpuEncoderInfo）直接入列，待探测条目（ProbeRequest）完成后按原位回填
        var slots = new List<object>();
        var encoderNames = encoderList.Succeeded
            ? ParseVideoEncoderNames(encoderList.StandardOutput)
            : [];

        if (encoderList.Succeeded)
        {
            // 硬件后端：动态收集该后缀下所有可映射的编码器（每种编码类型取一条）
            foreach (var (kind, suffix, display) in HardwareBackends)
            {
                var discovered = encoderNames
                    .Where(name => name.EndsWith(suffix, StringComparison.Ordinal))
                    .Select(name => (Name: name, Codec: MapCodecPrefix(name)))
                    .Where(entry => entry.Codec is not null)
                    .GroupBy(entry => entry.Codec)
                    .Select(g => g.OrderBy(e => (int)e.Codec!).First())
                    .OrderBy(entry => (int)entry.Codec!)
                    .ToArray();

                if (discovered.Length == 0)
                {
                    // 该后端一个编码器都没编译：保留占位条目（VM 会按 IsAvailable 过滤掉）
                    slots.Add(new GpuEncoderInfo
                    {
                        Kind = kind,
                        Codec = VideoCodecKind.H264,
                        CodecName = $"h264{suffix}",
                        DisplayName = display,
                        IsSupported = false,
                        IsFunctional = false,
                        Message = "当前 FFmpeg 构建未包含该编码器",
                    });

                    continue;
                }

                foreach (var (name, codec) in discovered)
                {
                    slots.Add(new ProbeRequest(kind, codec!.Value, name, display));
                }
            }

            // 软件编码：白名单内、且该构建确实编译了才列出
            var softwareFound = false;

            foreach (var (name, codec, display, _) in SoftwareCandidates)
            {
                if (!encoderNames.Contains(name, StringComparer.Ordinal))
                {
                    continue;
                }

                softwareFound = true;
                slots.Add(new ProbeRequest(EncoderKind.Software, codec, name, display));
            }

            if (!softwareFound)
            {
                slots.Add(new GpuEncoderInfo
                {
                    Kind = EncoderKind.Software,
                    Codec = VideoCodecKind.H264,
                    CodecName = "libx264",
                    DisplayName = "软件编码 libx264",
                    IsSupported = false,
                    IsFunctional = false,
                    Message = "当前 FFmpeg 构建未包含该编码器",
                });
            }
        }
        else
        {
            // -encoders 都跑不起来（ffmpeg 损坏/权限问题）：为每个后端放「未编译」占位，保持下拉结构
            foreach (var (kind, suffix, display) in HardwareBackends)
            {
                slots.Add(new GpuEncoderInfo
                {
                    Kind = kind,
                    Codec = VideoCodecKind.H264,
                    CodecName = $"h264{suffix}",
                    DisplayName = display,
                    IsSupported = false,
                    IsFunctional = false,
                    Message = "无法枚举编码器（ffmpeg -encoders 执行失败）",
                });
            }

            slots.Add(new GpuEncoderInfo
            {
                Kind = EncoderKind.Software,
                Codec = VideoCodecKind.H264,
                CodecName = "libx264",
                DisplayName = "软件编码 libx264",
                IsSupported = false,
                IsFunctional = false,
                Message = "无法枚举编码器（ffmpeg -encoders 执行失败）",
            });
        }

        // 并发试编码（限流），完成后按原槽位顺序拼装结果
        var results = new List<GpuEncoderInfo>(slots.Count);

        if (runFunctionalTest)
        {
            using var gate = new SemaphoreSlim(ProbeConcurrency, ProbeConcurrency);
            var requests = slots.OfType<ProbeRequest>().ToList();
            var probeTasks = requests.Select(request => ProbeAsync(gate, request, cancellationToken));

            var probeResults = await Task.WhenAll(probeTasks).ConfigureAwait(false);
            var resultMap = requests.Zip(probeResults, (request, result) => (request, result))
                .ToDictionary(pair => pair.request, pair => pair.result);

            foreach (var slot in slots)
            {
                results.Add(slot switch
                {
                    GpuEncoderInfo placeholder => placeholder,
                    ProbeRequest request => resultMap[request],
                    _ => throw new InvalidOperationException("未知槽位类型"),
                });
            }
        }
        else
        {
            // 只做能力枚举：编译里有 ≠ 本机能跑，状态先显示成「可用（未试编码）」，
            // 启动后的后台自动验证会把它落实为「可用 / 不可用」
            foreach (var slot in slots)
            {
                results.Add(slot switch
                {
                    GpuEncoderInfo placeholder => placeholder,
                    ProbeRequest request => new GpuEncoderInfo
                    {
                        Kind = request.Kind,
                        Codec = request.Codec,
                        CodecName = request.CodecName,
                        DisplayName = request.Display,
                        IsSupported = true,
                        IsFunctional = true,
                        IsVerified = false,
                        Message = "未做试编码验证",
                    },
                    _ => throw new InvalidOperationException("未知槽位类型"),
                });
            }
        }

        var hwAccels = hwAccelList.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !line.StartsWith("Hardware", StringComparison.OrdinalIgnoreCase) && line.Length > 0)
            .ToArray();

        var versionLine = version.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;

        return new GpuCapabilities
        {
            Encoders = results,
            HardwareAccelerations = hwAccels,
            FfmpegVersion = versionLine,
        };
    }

    /// <summary>单个编码器的功能性试编码（并发限流）。</summary>
    private async Task<GpuEncoderInfo> ProbeAsync(
        SemaphoreSlim gate,
        ProbeRequest request,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var (functional, message) = await TestEncoderAsync(request.Kind, request.CodecName, cancellationToken)
                .ConfigureAwait(false);

            return new GpuEncoderInfo
            {
                Kind = request.Kind,
                Codec = request.Codec,
                CodecName = request.CodecName,
                DisplayName = request.Display,
                IsSupported = true,
                IsFunctional = functional,
                IsVerified = true,
                Message = message,
            };
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 解析 <c>ffmpeg -encoders</c> 输出中的全部视频编码器名。
    /// </summary>
    /// <remarks>
    /// 每行形如 <c> V....D h264_nvenc  NVIDIA NVENC H.264 encoder</c>：
    /// 首个标志位 V = 视频、A = 音频、S = 字幕，这里只取视频行（正则按行匹配）。
    /// </remarks>
    private static string[] ParseVideoEncoderNames(string encodersText) => EncoderLineRegex()
        .Matches(encodersText)
        .Select(m => m.Groups[1].Value)
        .Distinct(StringComparer.Ordinal)
        .ToArray();

    /// <summary>编码器名前缀 → 编码类型；无法识别的前缀返回 null（跳过，不进下拉）。</summary>
    private static VideoCodecKind? MapCodecPrefix(string encoderName) => encoderName switch
    {
        var n when n.StartsWith("h264", StringComparison.Ordinal) => VideoCodecKind.H264,
        var n when n.StartsWith("hevc", StringComparison.Ordinal) => VideoCodecKind.Hevc,
        var n when n.StartsWith("vvc", StringComparison.Ordinal) => VideoCodecKind.Vvc,
        var n when n.StartsWith("av1", StringComparison.Ordinal) => VideoCodecKind.Av1,
        var n when n.StartsWith("vp9", StringComparison.Ordinal) => VideoCodecKind.Vp9,
        _ => null,
    };

    /// <summary>用合成源做一次 6 帧试编码，验证驱动与硬件是否真正可用。</summary>
    private async Task<(bool Functional, string Message)> TestEncoderAsync(
        EncoderKind kind,
        string codec,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error",
            "-f", "lavfi",
            "-i", "color=c=black:s=256x144:r=30:d=1",
            "-frames:v", "6",
            "-c:v", codec,
        };

        arguments.AddRange(kind switch
        {
            EncoderKind.Nvenc => ["-preset", "p4"],
            EncoderKind.Qsv => ["-preset", "medium"],
            EncoderKind.Amf => ["-quality", "balanced"],
            _ => [],
        });

        arguments.AddRange(["-f", "null", "-"]);

        var result = await ProcessRunner.RunAsync(_ffmpegPath, arguments, TimeSpan.FromSeconds(25), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            return (true, "试编码通过");
        }

        var diagnostics = result.Diagnostics.Trim();

        // lavfi 缺失属于探测环境问题，不能据此判定编码器不可用
        if (diagnostics.Contains("lavfi", StringComparison.OrdinalIgnoreCase)
            || diagnostics.Contains("Unknown input format", StringComparison.OrdinalIgnoreCase))
        {
            return (true, "无法试编码（该 FFmpeg 构建缺少 lavfi 测试源），按可用处理");
        }

        if (result.TimedOut)
        {
            return (false, "试编码超时");
        }

        var firstLine = diagnostics
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("Error", StringComparison.OrdinalIgnoreCase) || line.Contains("error", StringComparison.Ordinal))
            ?? diagnostics.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            ?? "试编码失败";

        return (false, ExtractCoreMessage(firstLine));
    }

    /// <summary>从 FFmpeg 日志行中剥离前缀（文件/行号/日志级别），只留人类可读的错误。</summary>
    private static string ExtractCoreMessage(string line)
    {
        var match = LogPrefixRegex().Match(line);
        return match.Success ? line[(match.Index + match.Length)..].Trim() : line.Trim();
    }

    /// <summary>匹配 -encoders 输出里的视频编码器行，捕获组 1 = 编码器名。</summary>
    [GeneratedRegex(@"^\s*V.{5}\s+(\S+)", RegexOptions.Multiline)]
    private static partial Regex EncoderLineRegex();

    [GeneratedRegex(@"^\[[^\]]*\]\s*")]
    private static partial Regex LogPrefixRegex();
}
