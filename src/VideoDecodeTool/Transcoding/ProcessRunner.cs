using System.Diagnostics;
using System.IO;
using System.Text;

namespace VideoDecodeTool.Transcoding;

/// <summary>外部进程执行结果。</summary>
public readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    /// <summary>合并后的诊断文本。</summary>
    public string Diagnostics =>
        string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : $"{StandardOutput}\n{StandardError}";
}

/// <summary>
/// 轻量外部进程执行器：用于调用 ffmpeg / ffprobe 的一次性短任务
/// （版本探测、编码器枚举、ffprobe 元数据、试编码）。
/// </summary>
public static class ProcessRunner
{
    /// <summary>启动进程并收集全部输出，超时后强杀进程树。</summary>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                standardOutput.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                standardError.AppendLine(e.Data);
            }
        };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, "进程无法启动", false);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new ProcessResult(-1, string.Empty, ex.Message, false);
        }

        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // 忽略
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);

            // 等待异步读取把剩余输出冲刷干净
            process.WaitForExit();

            return new ProcessResult(process.ExitCode, standardOutput.ToString(), standardError.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ProcessResult(-1, standardOutput.ToString(), standardError.ToString(), true);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // 已退出
        }
        catch (NotSupportedException)
        {
            // 平台不支持
        }
    }
}
