using System.Globalization;
using System.IO;
using System.Text.Json;
using VideoDecodeTool.Models;
using VideoDecodeTool.Transcoding;

namespace VideoDecodeTool.Probing;

/// <summary>
/// 通过 <c>ffprobe -print_format json</c> 一次性读取容器/流元数据，对应界面右侧的“文件信息面板”。
/// </summary>
public static class FfprobeService
{
    /// <summary>探测媒体文件。</summary>
    public static async Task<MediaFileInfo> ProbeAsync(string ffprobePath, string filePath, CancellationToken cancellationToken = default)
    {
        var arguments = new[]
        {
            "-hide_banner",
            "-loglevel", "error",
            "-print_format", "json",
            "-show_format",
            "-show_streams",
            "-i", filePath,
        };

        var result = await ProcessRunner.RunAsync(ffprobePath, arguments, TimeSpan.FromSeconds(30), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException($"ffprobe 探测失败：{result.Diagnostics.Trim()}");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        return Parse(document.RootElement, filePath);
    }

    private static MediaFileInfo Parse(JsonElement root, string filePath)
    {
        var format = root.TryGetProperty("format", out var formatElement) ? formatElement : default;

        var streams = root.TryGetProperty("streams", out var streamArray)
            ? streamArray.EnumerateArray().ToArray()
            : [];

        var video = streams.FirstOrDefault(s => GetString(s, "codec_type") == "video");
        var audio = streams.FirstOrDefault(s => GetString(s, "codec_type") == "audio");

        var frameRate = ParseRational(GetString(video, "avg_frame_rate"));
        if (frameRate <= 0.01)
        {
            frameRate = ParseRational(GetString(video, "r_frame_rate"));
        }

        var durationSeconds = ParseDouble(GetString(format, "duration"));
        if (durationSeconds <= 0)
        {
            durationSeconds = ParseDouble(GetString(video, "duration"));
        }

        var frameCount = (long)ParseDouble(GetString(video, "nb_frames"));
        if (frameCount <= 0 && durationSeconds > 0 && frameRate > 0)
        {
            frameCount = (long)Math.Round(durationSeconds * frameRate);
        }

        var bitRate = (long)ParseDouble(GetString(format, "bit_rate"));

        return new MediaFileInfo
        {
            FilePath = filePath,
            FileName = Path.GetFileName(filePath),
            FormatName = GetString(format, "format_name"),
            FormatLongName = GetString(format, "format_long_name"),
            SizeBytes = (long)ParseDouble(GetString(format, "size")),
            Duration = TimeSpan.FromSeconds(durationSeconds),
            BitRate = bitRate,
            FrameCount = frameCount,

            VideoCodec = GetString(video, "codec_name"),
            VideoProfile = GetString(video, "profile"),
            VideoLevel = (int)ParseDouble(GetString(video, "level")),
            Width = (int)ParseDouble(GetString(video, "width")),
            Height = (int)ParseDouble(GetString(video, "height")),
            PixelFormat = GetString(video, "pix_fmt"),
            SampleAspectRatio = Or(GetString(video, "sample_aspect_ratio"), "1:1"),
            DisplayAspectRatio = Or(GetString(video, "display_aspect_ratio"), "unknown"),
            FrameRate = frameRate,
            Progressive = GetString(video, "field_order") is not ("tt" or "bb" or "tb" or "bt"),
            ColorPrimaries = Or(GetString(video, "color_primaries"), "unknown"),
            ColorTransfer = Or(GetString(video, "color_transfer"), "unknown"),
            ColorSpace = Or(GetString(video, "color_space"), "unknown"),

            AudioCodec = GetString(audio, "codec_name"),
            AudioSampleRate = (int)ParseDouble(GetString(audio, "sample_rate")),
            AudioChannels = (int)ParseDouble(GetString(audio, "channels")),
            AudioChannelLayout = GetString(audio, "channel_layout"),
            AudioSampleFormat = GetString(audio, "sample_fmt"),
        };
    }

    private static string GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.ToString(),
            _ => string.Empty,
        };
    }

    private static string Or(string value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    /// <summary>解析 "30000/1001" 形式的有理数。</summary>
    private static double ParseRational(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var parts = value.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return ParseDouble(value);
    }
}
