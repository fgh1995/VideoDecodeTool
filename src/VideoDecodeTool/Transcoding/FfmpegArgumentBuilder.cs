using System.Globalization;
using VideoDecodeTool.Models;

namespace VideoDecodeTool.Transcoding;

/// <summary>
/// 构造 FFmpeg 命令行参数。
/// </summary>
/// <remarks>
/// <para><b>输出形态（实测结论，请勿轻易改动）</b></para>
/// <list type="bullet">
/// <item><b>边转边播模式（默认）</b>：完全采用需求文档 6.x 的命令，媒体输出到 <c>pipe:1</c>，
/// 由 <see cref="Media.PlaybackPipeline.OpenStream"/> 用自定义 AVIOContext 实时解复用。
/// 该形态生成的 fMP4 其 moov 中 <c>stsd/avcC</c> 携带完整 SPS/PPS，可被正常重新解复用。</item>
/// <item><b>标准 MP4 落盘</b>：由于 MP4 复用器必须写完整个流才能确定 moov，
/// 先由 <see cref="TeeReadStream"/> 把 pipe 上的分片流镜像到临时文件，
/// 转码结束后再用 <see cref="BuildRemux"/> 以 <c>-c copy -movflags +faststart</c> 重封装为
/// moov 前置的<b>标准 MP4</b>。整个过程只编码一次。</item>
/// </list>
/// <para><b>为什么不使用 tee 复用器</b>：实测 <c>-f tee</c> 同时输出 pipe 与文件时，
/// 分片输出的 <c>avcC</c> 为空（写 moov 时编码器扩展数据尚未就绪），
/// 导致该分片流无法被 ffprobe/ffmpeg 重新打开（No start code is found）。
/// 文件输出依赖 trailer 阶段补写 moov 反而正常，但直播流不可用，故放弃 tee 方案。</para>
/// <para><b>平台差异</b>：需求文档中 QSV 的 <c>-qsv_device /dev/dri/renderD128</c> 是 Linux 路径，
/// Windows 由驱动自动枚举设备，因此仅在非 Windows 平台追加该参数。</para>
/// </remarks>
public static class FfmpegArgumentBuilder
{
    /// <summary>分片 MP4 的 movflags：moov 前置、按关键帧分片，支持边写边播。</summary>
    public const string FragmentedMp4Flags = "+frag_keyframe+empty_moov+default_base_moof";

    /// <summary>
    /// 分片时长（微秒）。必须远小于播放端的「解码超前量」（约 4.7s，见
    /// <see cref="Media.PlaybackPipeline"/> 的 MaxLeadSecondsTarget）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 为什么必须显式限制分片时长：<c>+frag_keyframe</c> 会让一片一直持续到下一个关键帧
    /// （GOP 180 @23.976fps ≈ 7.5s），而 FFmpeg 的 fMP4 复用器在每一片的 <c>mdat</c> 里
    /// <b>把该片全部视频样本排在音频样本之前</b>。于是解复用要读到该片的音频，
    /// 必须先解码完这一整片的视频（≈7.5s）；但播放端的视频超前量上限只有 ~4.7s，
    /// 音频永远够不到 —— 音频缓冲被排空、主时钟停摆、退化成墙钟，周期性复现。
    /// （实测：分片 7.5s 时每 7.5s 卡约 2s；分片 2s 时全程平滑。）
    /// </para>
    /// <para>
    /// 取 2s：分片视频(2s) + 复用器音频滞后(~1s) ≈ 3s &lt; 超前量，音频始终可达；
    /// 首帧延迟也从 ~7.5s 降到 ~2s。分片变小只增加极少量 moof 开销
    /// （实测 40s 素材仅 +2.5KB），且最终交付的标准 MP4 由 <see cref="BuildRemux"/> 流拷贝产出，
    /// 关键帧仍是 GOP 180，画质与可拖动性与原先完全一致。
    /// </para>
    /// </remarks>
    public const int FragmentDurationMicroseconds = 2_000_000;

    /// <summary>标准 MP4 的 movflags：把 moov 整体搬到文件头部（faststart）。</summary>
    public const string StandardMp4Flags = "+faststart";

    /// <summary>构造转码参数表。</summary>
    /// <param name="request">转码请求。</param>
    /// <param name="useLiveStream">true = 媒体写入 stdout（边转边播）；false = 直接写标准 MP4 文件。</param>
    public static IReadOnlyList<string> Build(TranscodeRequest request, bool useLiveStream)
    {
        var args = new List<string>(48)
        {
            // 全局：不打印 banner、不读取按键、压制日志噪声、只输出机器可读进度
            "-hide_banner",
            "-nostdin",
            "-loglevel", "warning",
            "-nostats",
        };

        if (request.Overwrite)
        {
            args.Add("-y");
        }

        // ---- 解码侧硬件加速 ----
        args.AddRange(BuildHardwareAcceleration(request.Encoder));

        // ---- 输入 ----
        args.Add("-i");
        args.Add(request.InputPath);

        // ---- 流映射（音频缺失时用 ? 容错）----
        args.Add("-map");
        args.Add("0:v:0");
        args.Add("-map");
        args.Add("0:a:0?");

        // ---- 视频编码 ----
        args.AddRange(BuildVideoEncoder(request));

        // ---- 目标画幅（可选）：null / 空 = 保持源分辨率 ----
        if (!string.IsNullOrEmpty(request.TargetResolution))
        {
            args.Add("-vf");
            args.Add(BuildScaleFilter(request.TargetResolution, request.ResizeMode, request.ScaleQuality));
        }

        // ---- 固定 GOP，防止分片边界漂移 ----
        args.Add("-g");
        args.Add(request.GopSize.ToString(CultureInfo.InvariantCulture));
        args.Add("-keyint_min");
        args.Add(request.GopSize.ToString(CultureInfo.InvariantCulture));

        if (request.Encoder != EncoderKind.Amf)
        {
            // h264_amf 不支持 sc_threshold 参数
            args.Add("-sc_threshold");
            args.Add("0");
        }

        // ---- 输出帧率（可选）：不指定则保持源帧率 ----
        if (!string.IsNullOrEmpty(request.OutputFrameRate))
        {
            args.Add("-r");
            args.Add(request.OutputFrameRate);
        }

        // ---- 音频编码 ----
        args.AddRange(BuildAudioEncoder(request));

        if (request.LimitSeconds > 0)
        {
            // 限制输出时长（调试用：只转一小段快速验证链路）
            args.Add("-t");
            args.Add(request.LimitSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        // ---- 进度输出到 stderr，避免污染 stdout 的媒体数据 ----
        args.Add("-progress");
        args.Add("pipe:2");

        // ---- 输出 ----
        args.Add("-f");
        args.Add("mp4");

        if (useLiveStream)
        {
            args.Add("-movflags");
            args.Add(FragmentedMp4Flags);
            // 显式限制分片时长：纯 frag_keyframe 会产出 ~7.5s 的长片，
            // 令每片音频排在整片视频之后、超出播放端超前量而永远读不到（详见常量注释）
            args.Add("-frag_duration");
            args.Add(FragmentDurationMicroseconds.ToString(CultureInfo.InvariantCulture));
            args.Add("pipe:1");
        }
        else
        {
            args.Add("-movflags");
            args.Add(StandardMp4Flags);
            args.Add(request.OutputPath);
        }

        return args;
    }

    /// <summary>
    /// 构造“分片 MP4 → 标准 MP4”的重封装参数（流拷贝，不做二次编码）。
    /// </summary>
    public static IReadOnlyList<string> BuildRemux(string fragmentedInputPath, string outputPath) =>
    [
        "-hide_banner",
        "-nostdin",
        "-loglevel", "warning",
        "-nostats",
        "-y",
        "-i", fragmentedInputPath,
        "-c", "copy",
        "-movflags", StandardMp4Flags,
        "-f", "mp4",
        outputPath,
    ];

    /// <summary>解码侧硬件加速参数（显著降低 CPU 占用，不影响输出内容）。</summary>
    private static IEnumerable<string> BuildHardwareAcceleration(EncoderKind kind) => kind switch
    {
        EncoderKind.Nvenc => ["-hwaccel", "cuda"],
        EncoderKind.Qsv => OperatingSystem.IsWindows()
            ? ["-hwaccel", "qsv"]
            : ["-hwaccel", "qsv", "-qsv_device", "/dev/dri/renderD128"],
        EncoderKind.Amf => ["-hwaccel", "d3d11va"],
        _ => [],
    };

    /// <summary>视频编码器参数，对应需求文档 6.1 / 6.2 / 6.3 三套命令。</summary>
    /// <remarks>
    /// <para>码控：指定 <see cref="TranscodeRequest.VideoBitrateKbps"/> 时切换为指定码率（VBR）模式，
    /// 否则保持原质量模式（NVENC cq / QSV global_quality / AMF cqp / x264 crf）。</para>
    /// <para>速度档 / 调谐 / profile 都按当前编码器映射成它自己的参数名；
    /// 未指定时用各家默认值（与历史命令行完全一致：nvenc p4+hq、qsv/x264 medium、amf balanced）。</para>
    /// </remarks>
    private static IEnumerable<string> BuildVideoEncoder(TranscodeRequest request)
    {
        var quality = request.Quality.ToString(CultureInfo.InvariantCulture);
        var bitrate = request.VideoBitrateKbps is { } kbps
            ? $"{kbps.ToString(CultureInfo.InvariantCulture)}k"
            : null;
        // 缓冲区取 2 倍码率，给 VBR 留抖动空间
        var bufsize = request.VideoBitrateKbps is { } v
            ? $"{(v * 2).ToString(CultureInfo.InvariantCulture)}k"
            : null;

        var args = new List<string>(16);
        var codec = request.VideoCodec;
        // 优先用探测结果里的真实编码器名（动态枚举 -encoders 得到）；未探测到时按（后端 × 编码类型）推断
        var encoderName = request.VideoEncoderName is { Length: > 0 } name
            ? name
            : ResolveEncoderName(request.Encoder, codec);

        switch (request.Encoder)
        {
            case EncoderKind.Nvenc:
                args.AddRange(["-c:v", encoderName]);
                args.AddRange(["-preset", NvencPreset(request.SpeedPreset)]); // p1(最快) ~ p7(最慢)
                args.AddRange(["-tune", NvencTune(request.Tune)]);            // 高质量 / 低延迟

                if (bitrate is not null)
                {
                    args.AddRange(["-rc", "vbr", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize!]);
                }
                else
                {
                    // 与 cq 搭配时用 -b:v 0 禁用固定码率
                    args.AddRange(["-rc", "vbr", "-cq", quality, "-b:v", "0"]);
                }

                break;

            case EncoderKind.Qsv:
                args.AddRange(["-c:v", encoderName, "-preset", QsvPreset(request.SpeedPreset)]);

                if (bitrate is not null)
                {
                    args.AddRange(["-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize!]);
                }
                else
                {
                    args.AddRange(["-global_quality", quality]);
                }

                break;

            case EncoderKind.Amf:
                args.AddRange(["-c:v", encoderName, "-usage", "transcoding"]);
                args.AddRange(["-quality", AmfQuality(request.SpeedPreset)]);

                if (bitrate is not null)
                {
                    args.AddRange(["-rc", "vbr_peak", "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize!]);
                }
                else
                {
                    // AMF 恒定 QP 模式
                    args.AddRange([
                        "-rc", "cqp",
                        "-qp_i", quality,
                        "-qp_p", (request.Quality + 1).ToString(CultureInfo.InvariantCulture),
                        "-qp_b", (request.Quality + 3).ToString(CultureInfo.InvariantCulture),
                    ]);
                }

                break;

            default:
                args.AddRange(["-c:v", encoderName, "-preset", X264Preset(request.SpeedPreset)]);

                if (X264Tune(request.Tune) is { } tune)
                {
                    args.AddRange(["-tune", tune]);
                }

                if (bitrate is not null)
                {
                    args.AddRange(["-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize!]);
                }
                else
                {
                    // libvvenc 没有 crf，质量模式用 -qp 表达
                    args.AddRange([codec == VideoCodecKind.Vvc ? "-qp" : "-crf", quality]);
                }

                break;
        }

        // 双步（多步）编码：各家实现不同，不支持的编码器忽略（界面 Tooltip 已说明）
        if (request.TwoPass)
        {
            switch (request.Encoder)
            {
                case EncoderKind.Nvenc:
                    // 首遍全分辨率分析，二遍按分析结果编码
                    args.AddRange(["-multipass", "fullres"]);
                    break;
                case EncoderKind.Qsv:
                    args.AddRange(["-look_ahead", "1"]);
                    break;
            }
        }

        // profile：未指定则交给编码器默认（H.264 通常为 High）。
        // ⚠ 按编码类型裁剪：AV1 / VP9 / VVC 没有 -profile:v 的 baseline/main/high 这套档位
        //   （传了会报错），不传；HEVC 没有 baseline；h264_amf 的 -profile 只接受 main / high
        //   （没有 baseline，实测传 baseline 会直接报「Undefined constant」），这些组合不传，退回编码器默认。
        if (request.Profile is { Length: > 0 } profile
            && codec is not (VideoCodecKind.Av1 or VideoCodecKind.Vp9 or VideoCodecKind.Vvc)
            && !(profile == "baseline"
                && (codec == VideoCodecKind.Hevc || request.Encoder == EncoderKind.Amf)))
        {
            args.AddRange(["-profile:v", profile]);
        }

        return args;
    }

    /// <summary>（编码器后端 × 编码类型）→ FFmpeg 编码器名。</summary>
    /// <remarks>
    /// 编码类型下拉只列探测到的组合，正常不会走到未探测的分支；
    /// 万一走到（如该后端未编译 hevc），退回 H.264 保证命令行仍然合法。
    /// </remarks>
    private static string ResolveEncoderName(EncoderKind kind, VideoCodecKind codec) => (kind, codec) switch
    {
        (EncoderKind.Nvenc, VideoCodecKind.Hevc) => "hevc_nvenc",
        (EncoderKind.Nvenc, VideoCodecKind.Av1) => "av1_nvenc",
        (EncoderKind.Qsv, VideoCodecKind.Hevc) => "hevc_qsv",
        (EncoderKind.Qsv, VideoCodecKind.Av1) => "av1_qsv",
        (EncoderKind.Amf, VideoCodecKind.Hevc) => "hevc_amf",
        (EncoderKind.Amf, VideoCodecKind.Av1) => "av1_amf",
        (EncoderKind.Software, VideoCodecKind.Hevc) => "libx265",
        (EncoderKind.Software, VideoCodecKind.Av1) => "libsvtav1",
        (EncoderKind.Software, VideoCodecKind.Vvc) => "libvvenc",
        _ => kind switch
        {
            EncoderKind.Qsv => "h264_qsv",
            EncoderKind.Amf => "h264_amf",
            EncoderKind.Software => "libx264",
            _ => "h264_nvenc",
        },
    };

    /// <summary>速度档 → nvenc preset（p1 最快 ~ p7 最慢）；未指定用 p4。</summary>
    private static string NvencPreset(string? speed) => speed switch
    {
        "fastest" => "p1",
        "fast" => "p3",
        "slow" => "p6",
        "slowest" => "p7",
        _ => "p4",
    };

    /// <summary>
    /// 构造「目标画幅」滤镜：按适配方式组合缩放 / 补边 / 裁剪，并带上缩放质量对应的采样算法。
    /// </summary>
    /// <remarks>
    /// <c>fit</c>（默认）= 保持比例缩到放得下，再补黑边居中（letterbox / pillarbox）；
    /// <c>stretch</c> = 直接拉伸铺满（宽高比会变）；<c>crop</c> = 保持比例放大到铺满后裁掉溢出。
    /// 采样算法：fast=bilinear、best=lanczos、其余=bicubic。
    /// </remarks>
    private static string BuildScaleFilter(string target, string? mode, string? quality)
    {
        var flags = quality switch
        {
            "fast" => "bilinear",
            "best" => "lanczos",
            _ => "bicubic",
        };

        var parts = target.Split('x', 'X', '×');
        var size = parts.Length == 2 ? $"{parts[0]}:{parts[1]}" : null;

        return mode switch
        {
            // 拉伸铺满：无视原始宽高比
            "stretch" => $"scale={target}:flags={flags}",

            // 裁剪铺满：先按短边放大到铺满，再居中裁掉溢出部分
            "crop" when size is not null =>
                $"scale={target}:force_original_aspect_ratio=increase:flags={flags},crop={size}",

            // 保持比例加框：按长边缩小到放得下，再补黑边居中
            _ when size is not null =>
                $"scale={target}:force_original_aspect_ratio=decrease:flags={flags}," +
                $"pad={size}:(ow-iw)/2:(oh-ih)/2:black",

            _ => $"scale={target}:flags={flags}",
        };
    }

    /// <summary>调谐 → nvenc tune；默认 hq（高质量）。</summary>
    private static string NvencTune(string? tune) => tune switch
    {
        "lowlatency" => "ll",
        _ => "hq",
    };

    /// <summary>速度档 → QSV preset；未指定用 medium。</summary>
    private static string QsvPreset(string? speed) => speed switch
    {
        "fastest" => "veryfast",
        "fast" => "fast",
        "slow" => "slow",
        "slowest" => "veryslow",
        _ => "medium",
    };

    /// <summary>速度档 → AMF quality；未指定用 balanced。</summary>
    private static string AmfQuality(string? speed) => speed switch
    {
        "fastest" or "fast" => "speed",
        "slow" or "slowest" => "quality",
        _ => "balanced",
    };

    /// <summary>速度档 → x264 preset；未指定用 medium。</summary>
    private static string X264Preset(string? speed) => speed switch
    {
        "fastest" => "ultrafast",
        "fast" => "fast",
        "slow" => "slow",
        "slowest" => "veryslow",
        _ => "medium",
    };

    /// <summary>调谐 → x264 tune；未指定不传（QSV / AMF 无此参数）。</summary>
    private static string? X264Tune(string? tune) => tune switch
    {
        "film" => "film",
        "animation" => "animation",
        "lowlatency" => "zerolatency",
        _ => null,
    };

    private static IEnumerable<string> BuildAudioEncoder(TranscodeRequest request)
    {
        yield return "-c:a";
        yield return request.AudioCodec;

        // 直接复制源音频时不重编码，码率 / 采样率参数无意义
        if (request.AudioCodec == "copy")
        {
            yield break;
        }

        yield return "-b:a";
        yield return $"{request.AudioBitrate}k";

        // 采样率 / 声道数：未指定（自动）则保持源
        if (request.AudioSampleRate is { } sampleRate)
        {
            yield return "-ar";
            yield return sampleRate.ToString(CultureInfo.InvariantCulture);
        }

        if (request.AudioChannels is { } channels)
        {
            yield return "-ac";
            yield return channels.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>把参数表还原成可复制粘贴的命令行文本（用于界面显示与排障）。</summary>
    public static string ToCommandLine(string ffmpegPath, IReadOnlyList<string> arguments)
    {
        var builder = new System.Text.StringBuilder($"\"{ffmpegPath}\"");

        foreach (var argument in arguments)
        {
            builder.Append(' ');

            if (argument.Length == 0 || argument.Any(c => char.IsWhiteSpace(c) || c is '|' or '"'))
            {
                builder.Append('"').Append(argument.Replace("\"", "\\\"")).Append('"');
            }
            else
            {
                builder.Append(argument);
            }
        }

        return builder.ToString();
    }
}
