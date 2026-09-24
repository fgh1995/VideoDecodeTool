using FFmpeg.AutoGen;
using System.IO;

namespace VideoDecodeTool.Interop;

/// <summary>
/// 把任意 .NET <see cref="Stream"/>（例如 FFmpeg 子进程的 StandardOutput）包装成 FFmpeg 的
/// <see cref="AVIOContext"/>，用于“边转边播”场景下的流式解复用。
/// </summary>
/// <remarks>
/// <para>设计要点：</para>
/// <list type="number">
/// <item>读回调直接阻塞在 <see cref="Stream.Read(Span{byte})"/> 上，FFmpeg 需要数据时才拉取，
/// 天然形成背压，不会因为播放端跟不上而无限缓存；</item>
/// <item>不提供 seek / write 回调（管道不可回退），配合 fragmented MP4（moov 前置）可正常解复用；</item>
/// <item>FFmpeg.AutoGen 6.0.0 把回调参数生成成 <c>*_func</c> 结构体（内含函数指针 + 从委托的隐式转换），
/// 因此这里持有真正的委托类型 <see cref="avio_alloc_context_read_packet"/> 以防止 GC 回收，
/// 传入时由隐式转换包装成结构体；</item>
/// <item>AVIO 缓冲区由 av_malloc 分配，其生命周期归 AVIOContext 所有，释放时只需 avio_context_free。</item>
/// </list>
/// </remarks>
internal sealed unsafe class StreamAvioContext : IDisposable
{
    /// <summary>AVIO 内部缓冲区大小，64KB 足以摊薄跨进程读取开销。</summary>
    private const int DefaultBufferSize = 64 * 1024;

    private readonly Stream _source;
    private readonly avio_alloc_context_read_packet _readCallback;

    private AVIOContext* _context;
    private byte* _buffer;
    private bool _disposed;

    public StreamAvioContext(Stream source, int bufferSize = DefaultBufferSize)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));

        // 委托必须保持强引用，否则原生侧回调会指向已回收对象
        _readCallback = ReadPacket;

        _buffer = (byte*)ffmpeg.av_malloc((ulong)bufferSize);
        if (_buffer == null)
        {
            throw new OutOfMemoryException("av_malloc 分配 AVIO 缓冲区失败");
        }

        // 参数顺序（AutoGen 6.0.0）：buffer, buffer_size, write_flag, opaque, read_packet, write_packet, seek
        // write / seek 传 default 即空函数指针（管道不可写、不可回退）
        _context = ffmpeg.avio_alloc_context(
            _buffer,
            bufferSize,
            0,
            null,
            _readCallback,
            default,
            default);

        if (_context == null)
        {
            ffmpeg.av_free(_buffer);
            _buffer = null;
            throw new FfmpegException("avio_alloc_context 创建自定义 IO 上下文失败");
        }
    }

    /// <summary>原生 AVIOContext 指针。</summary>
    public AVIOContext* Context => _context;

    /// <summary>已读取的字节数，用于诊断。</summary>
    public long BytesRead { get; private set; }

    /// <summary>读回调：返回读取字节数；0 表示 EOF；负值表示错误。</summary>
    private int ReadPacket(void* opaque, byte* buffer, int bufferSize)
    {
        try
        {
            var span = new Span<byte>(buffer, bufferSize);
            var read = _source.Read(span);

            if (read > 0)
            {
                BytesRead += read;
                return read;
            }

            // Stream.Read 返回 0 即到达末尾；FFmpeg 侧同样以 0 表示 EOF
            return 0;
        }
        catch (OperationCanceledException)
        {
            return FfmpegNative.AVERROR_EOF_VALUE;
        }
        catch (IOException)
        {
            return FfmpegNative.AVERROR_EPIPE;
        }
        catch (ObjectDisposedException)
        {
            // 主动释放流以打断阻塞读取时会发生，视为流结束
            return FfmpegNative.AVERROR_EOF_VALUE;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_context != null)
        {
            // avio_context_free 会一并释放内部由 av_malloc 分配的缓冲区，不能再单独释放
            var context = _context;
            ffmpeg.avio_context_free(&context);
            _context = null;
            _buffer = null;
        }
        else if (_buffer != null)
        {
            ffmpeg.av_free(_buffer);
            _buffer = null;
        }
    }
}
