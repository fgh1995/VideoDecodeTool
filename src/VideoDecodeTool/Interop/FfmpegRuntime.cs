using FFmpeg.AutoGen;
using System.IO;
using System.Runtime.InteropServices;

namespace VideoDecodeTool.Interop;

/// <summary>
/// FFmpeg 原生运行环境的初始化：定位共享库目录、设置 ffmpeg.RootPath、提供错误码翻译。
/// </summary>
public static unsafe class FfmpegRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>共享库目录（含 avcodec-60.dll 等）。</summary>
    public static string SharedLibraryDirectory { get; private set; } = string.Empty;

    /// <summary>初始化是否成功。</summary>
    public static bool IsAvailable { get; private set; }

    /// <summary>初始化失败原因。</summary>
    public static string? FailureReason { get; private set; }

    /// <summary>探测到的 avutil 版本号（用于日志）。</summary>
    public static uint AvUtilVersion { get; private set; }

    /// <summary>
    /// 初始化 FFmpeg 原生绑定。可重复调用，仅首次生效。
    /// </summary>
    public static bool Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return IsAvailable;
            }

            _initialized = true;

            var directory = NativeLibraryLocator.ResolveSharedLibraryDirectory();
            if (directory is null)
            {
                FailureReason =
                    "未找到 FFmpeg 共享库。请把 avcodec-60.dll / avformat-60.dll / avutil-58.dll / swscale-7.dll / swresample-4.dll " +
                    "放入程序目录下的 ffmpeg 文件夹（详见 README.md）。";
                return false;
            }

            SharedLibraryDirectory = directory;
            ffmpeg.RootPath = directory;

            // AutoGen 通过 DllImport 按名称加载，把目录加入进程搜索路径可确保原生解析成功。
            NativeMethods.SetDllDirectory(directory);

            try
            {
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
                AvUtilVersion = ffmpeg.avutil_version();
                IsAvailable = true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                FailureReason = $"FFmpeg 共享库加载失败（{directory}）：{ex.Message}。" +
                                "请确认使用 x64 版本、且版本号为 6.0（*‑60.dll）。";
                IsAvailable = false;
            }

            return IsAvailable;
        }
    }

    /// <summary>把 FFmpeg 错误码翻译成可读文本。</summary>
    public static string Describe(int error, string? context = null)
    {
        if (error >= 0)
        {
            return context is null ? $"ok({error})" : $"{context}: ok({error})";
        }

        var buffer = stackalloc byte[512];
        var result = ffmpeg.av_strerror(error, buffer, 512);
        var message = result < 0
            ? $"未知错误 {error}"
            : Marshal.PtrToStringAnsi((nint)buffer) ?? $"错误 {error}";

        return context is null ? $"{message} ({error})" : $"{context}：{message} ({error})";
    }

    /// <summary>FFmpeg 调用失败时抛出带上下文的异常。</summary>
    public static void ThrowIfError(this int error, string context)
    {
        if (error < 0)
        {
            throw new FfmpegException(Describe(error, context), error);
        }
    }

    /// <summary>FFmpeg 指针为空时抛出异常。</summary>
    public static void ThrowIfNull(this nint pointer, string context)
    {
        if (pointer == 0)
        {
            throw new FfmpegException($"{context}：返回了空指针（通常表示内存不足或参数非法）");
        }
    }
}

/// <summary>FFmpeg 原生调用异常。</summary>
public sealed class FfmpegException : Exception
{
    public FfmpegException(string message, int errorCode = 0) : base(message) => ErrorCode = errorCode;

    /// <summary>原始 AVERROR 错误码。</summary>
    public int ErrorCode { get; }
}

/// <summary>定位 FFmpeg 共享库与命令行工具。</summary>
public static class NativeLibraryLocator
{
    /// <summary>
    /// 按以下顺序查找共享库目录：
    /// 1. 程序目录/ffmpeg
    /// 2. 程序目录
    /// 3. 从程序目录逐级向上查找 ffmpeg 子目录（开发期 dotnet run 场景）
    /// </summary>
    public static string? ResolveSharedLibraryDirectory()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
            AppContext.BaseDirectory,
        };

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && current is not null; depth++)
        {
            candidates.Add(Path.Combine(current.FullName, "ffmpeg"));
            current = current.Parent;
        }

        foreach (var candidate in candidates)
        {
            if (ContainsSharedLibraries(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>定位 ffmpeg.exe。</summary>
    public static string? ResolveFfmpegExecutable() => ResolveTool("ffmpeg.exe");

    /// <summary>定位 ffprobe.exe。</summary>
    public static string? ResolveFfprobeExecutable() => ResolveTool("ffprobe.exe");

    private static string? ResolveTool(string fileName)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", fileName),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
        };

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && current is not null; depth++)
        {
            candidates.Add(Path.Combine(current.FullName, fileName));
            candidates.Add(Path.Combine(current.FullName, "ffmpeg", "bin", fileName));
            current = current.Parent;
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool ContainsSharedLibraries(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                   && Directory.EnumerateFiles(directory, "avcodec-*.dll").Any();
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

internal static class NativeMethods
{
    /// <summary>把目录加入当前进程的 DLL 搜索路径，用于让 DllImport 能解析 FFmpeg 共享库。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetDllDirectory(string lpPathName);
}
