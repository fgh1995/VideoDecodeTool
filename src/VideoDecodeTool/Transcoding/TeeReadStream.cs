using System.IO;

namespace VideoDecodeTool.Transcoding;

/// <summary>
/// 只读的“三通”流：从 <paramref name="source"/>（FFmpeg 的 stdout）顺序读取的同时，
/// 把同样的字节镜像写入一个文件，用于在“边转边播”过程中同步留存分片 MP4。
/// </summary>
/// <remarks>
/// <para>
/// 之所以这样做，是因为 MP4 复用器只有写完整个流才能确定 moov：
/// 直播必须用分片形态（moov 前置），而标准 MP4 需要另一次封装。
/// 镜像留存后再做一次 <c>-c copy</c> 重封装，就可以同时满足
/// “边转边播” 与 “输出标准 MP4” 两个需求，且全程只编码一次。
/// </para>
/// <para>
/// <b>由谁读</b>：本流由转码会话的「全速抽干」线程读取（不按播放速率限流），
/// 于是 FFmpeg 的 stdout 不再被阻塞、转码以最快速度跑完；预览侧则改读镜像文件
/// （见 <see cref="GrowingFileReadStream"/>）。
/// </para>
/// </remarks>
internal sealed class TeeReadStream : Stream
{
    private readonly Stream _source;
    private readonly FileStream _mirror;
    private readonly bool _ownsSource;
    private long _mirrored;
    private bool _disposed;

    public TeeReadStream(Stream source, string mirrorPath, bool ownsSource = true)
    {
        _source = source;
        _ownsSource = ownsSource;

        var directory = Path.GetDirectoryName(mirrorPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // FileShare.ReadWrite | Delete：
        //   ReadWrite —— 预览侧（GrowingFileReadStream）同时打开该文件顺序读取；收尾重封装也会读它
        //   Delete    —— 收尾产出标准 MP4 后要删除镜像文件，而预览可能仍在读它
        // 缓冲区取最小（1 → 实际 8 字节）：保证写入的数据立刻对另一个句柄可见，
        // 否则预览侧要等到缓冲区攒满（默认 4KB/64KB）才看得到，首帧会被白白推迟。
        _mirror = new FileStream(
            mirrorPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            1,
            FileOptions.SequentialScan);
    }

    /// <summary>已镜像写入的字节数。</summary>
    public long MirroredBytes => _mirrored;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = _source.Read(buffer, offset, count);

        if (read > 0)
        {
            _mirror.Write(buffer, offset, read);
            _mirrored += read;
        }

        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = _source.Read(buffer);

        if (read > 0)
        {
            _mirror.Write(buffer[..read]);
            _mirrored += read;
        }

        return read;
    }

    public override void Flush()
    {
        _mirror.Flush();
        _source.Flush();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        // 幂等：抽干线程与收尾流程都可能走到这里，重复 Dispose 不能再动已释放的文件句柄
        if (disposing && !_disposed)
        {
            _disposed = true;

            try
            {
                _mirror.Flush();
                _mirror.Dispose();
            }
            catch (IOException)
            {
                // 磁盘写满等异常不应阻断播放侧释放
            }
            catch (ObjectDisposedException)
            {
                // 已释放
            }

            if (_ownsSource)
            {
                try
                {
                    _source.Dispose();
                }
                catch (IOException)
                {
                    // 忽略
                }
            }
        }

        base.Dispose(disposing);
    }
}
