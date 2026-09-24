using System.IO;

namespace VideoDecodeTool.Transcoding;

/// <summary>
/// 只读流：顺序读取一个<b>正在被写入</b>的文件。读到当前写入位置时阻塞等待新数据，
/// 生产者显式标记结束后，读到末尾即返回 0（EOF）。
/// </summary>
/// <remarks>
/// <para>
/// 用途：把「转码写盘」与「实时预览读流」解耦。原先两者共用 FFmpeg 的 stdout 管道，
/// 而预览必须按播放速率限流读取，管道随即写满、FFmpeg 被阻塞 —— 转码速度被硬拖到实时
/// （1x），与「尽快转码」的目标冲突。
/// </para>
/// <para>
/// 现在写侧（<see cref="TeeReadStream"/> 的全速抽干）把 FFmpeg 的分片流落到镜像文件，
/// 读侧（本流）按自己的节奏跟随该文件：写侧全速跑完，读侧仍按实时把内容放完，
/// 两者互不阻塞；镜像文件本身就是这块“缓冲”。
/// </para>
/// <para>
/// 与管道语义一致：不支持 seek / 长度查询（FFmpeg 侧只用到顺序读），读到 0 即 EOF。
/// </para>
/// </remarks>
internal sealed class GrowingFileReadStream : Stream
{
    /// <summary>等待新数据时的轮询间隔（毫秒）。足够小以免影响解码吞吐，又不至于空转烧 CPU。</summary>
    private const int PollMilliseconds = 15;

    private const int ReadBufferSize = 1 << 16;

    private readonly string _path;

    private FileStream? _file;
    private long _position;
    private volatile bool _producerDone;
    private volatile bool _disposed;

    public GrowingFileReadStream(string path) => _path = path;

    /// <summary>是否已被释放（停止播放会释放预览读流；再次播放需要换一个新的）。</summary>
    public bool IsDisposed => _disposed;

    /// <summary>
    /// 标记生产者已结束（不会再有新数据）。调用后，读到当前末尾即视为 EOF。
    /// </summary>
    public void MarkProducerDone() => _producerDone = true;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _file?.Length ?? 0;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // 懒打开：镜像文件由 TeeReadStream 构造时创建，此处必然已存在
            _file ??= new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                // 允许写侧继续追加、也允许收尾阶段删除该文件（预览继续读已被删除但打开的句柄）
                FileShare.ReadWrite | FileShare.Delete,
                ReadBufferSize,
                FileOptions.SequentialScan);

            var available = _file.Length - _position;

            if (available > 0)
            {
                if (_file.Position != _position)
                {
                    _file.Position = _position;
                }

                var read = _file.Read(buffer);

                if (read > 0)
                {
                    _position += read;
                    return read;
                }
            }

            // 生产者结束且已读到末尾 → 真正的流末尾
            if (_producerDone)
            {
                return 0;
            }

            Thread.Sleep(PollMilliseconds);
        }
    }

    public override void Flush()
    {
        // 只读流，无需冲刷
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // 先置位再关文件：阻塞中的 Read 会在下一次轮询时看到并抛出 ObjectDisposedException，
            // 上层（StreamAvioContext）据此把阻塞中的 av_read_frame 视作流结束。
            _disposed = true;

            try
            {
                _file?.Dispose();
            }
            catch (IOException)
            {
                // 忽略
            }

            _file = null;
        }

        base.Dispose(disposing);
    }
}
