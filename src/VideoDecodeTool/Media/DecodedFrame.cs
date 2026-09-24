using System.Runtime.InteropServices;
using VideoDecodeTool.Models;

namespace VideoDecodeTool.Media;

/// <summary>
/// 一帧可渲染的画面：BGRA 像素缓冲 + 运动矢量 + 编码参数。
/// </summary>
/// <remarks>
/// 像素缓冲使用非托管内存（<see cref="NativeMemory.AlignedAlloc"/>），原因：
/// <list type="bullet">
/// <item>1080p BGRA 单帧 8MB，落在 LOH 上，反复分配会造成大对象堆碎片；</item>
/// <item>SkiaSharp 通过 <c>SKImage.FromPixels(IntPtr, ...)</c> 直接引用这块内存，无需每帧 pin 托管数组；</item>
/// <item>配合 <see cref="FrameBufferPool"/> 复用，稳态下零分配。</item>
/// </list>
/// </remarks>
public sealed unsafe class DecodedFrame : IDisposable
{
    private byte* _pixels;
    private int _vectorCount;

    internal DecodedFrame(
        int bufferWidth,
        int bufferHeight,
        int stride,
        int sourceWidth,
        int sourceHeight,
        int maxVectors)
    {
        Width = bufferWidth;
        Height = bufferHeight;
        Stride = stride;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        Vectors = new MotionVector[maxVectors];
        _pixels = (byte*)NativeMemory.AlignedAlloc((nuint)(stride * bufferHeight), 64);
    }

    /// <summary>像素缓冲宽度（预览尺寸，可能小于源分辨率）。</summary>
    public int Width { get; }

    /// <summary>像素缓冲高度（预览尺寸）。</summary>
    public int Height { get; }

    /// <summary>源视频宽度。运动矢量坐标即定义在该坐标系下。</summary>
    public int SourceWidth { get; }

    /// <summary>源视频高度。</summary>
    public int SourceHeight { get; }

    public int Stride { get; }

    /// <summary>BGRA 像素起始地址（非托管）。</summary>
    public byte* PixelPointer => _pixels;

    /// <summary>像素字节数。</summary>
    public int PixelLength => Stride * Height;

    /// <summary>本帧运动矢量数组（可能长于实际数量，用 <see cref="VectorCount"/> 界定）。</summary>
    public MotionVector[] Vectors { get; }

    /// <summary>本帧实际写入的运动矢量数量。</summary>
    public int VectorCount
    {
        get => _vectorCount;
        internal set => _vectorCount = value;
    }

    /// <summary>本帧的编码参数。</summary>
    public FrameInfo Info { get; internal set; } = null!;

    /// <summary>显示时间（秒）。</summary>
    public double Time => Info.Time;

    /// <summary>由渲染端持有期间置为 false，归还池时不再重置。</summary>
    internal bool InUse { get; set; }

    internal void Reset()
    {
        _vectorCount = 0;
        Info = null!;
        InUse = false;
    }

    public void Dispose()
    {
        if (_pixels != null)
        {
            NativeMemory.AlignedFree(_pixels);
            _pixels = null;
        }
    }
}

/// <summary>
/// 解码帧缓冲池。解码线程 <see cref="Rent"/>，渲染完成后 <see cref="Return"/>；
/// 池耗尽时返回 null，由调用方丢帧，从而把内存占用钉死在“池容量 × 单帧大小”。
/// </summary>
public sealed class FrameBufferPool : IDisposable
{
    /// <summary>单帧最多缓存的运动矢量数量（超出部分按等差采样，避免极端场景下内存膨胀）。</summary>
    public const int MaxVectorsPerFrame = 24_000;

    private readonly Stack<DecodedFrame> _idle = new();
    private readonly List<DecodedFrame> _all = new();
    private readonly object _gate = new();
    private readonly int _bufferWidth;
    private readonly int _bufferHeight;
    private readonly int _stride;
    private readonly int _sourceWidth;
    private readonly int _sourceHeight;
    private readonly int _capacity;
    private bool _disposed;

    /// <param name="sourceWidth">源视频宽度（用于运动矢量坐标系）。</param>
    /// <param name="sourceHeight">源视频高度。</param>
    /// <param name="bufferWidth">预览像素缓冲宽度。</param>
    /// <param name="bufferHeight">预览像素缓冲高度。</param>
    /// <param name="capacity">池容量（应 ≥ 帧队列上限 + 渲染端持有的 1 帧）。</param>
    public FrameBufferPool(int sourceWidth, int sourceHeight, int bufferWidth, int bufferHeight, int capacity = 20)
    {
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _bufferWidth = bufferWidth;
        _bufferHeight = bufferHeight;
        _stride = bufferWidth * 4;
        _capacity = capacity;
    }

    public int Width => _bufferWidth;

    public int Height => _bufferHeight;

    public int Stride => _stride;

    /// <summary>当前已借出且未归还的帧数。</summary>
    public int RentedCount
    {
        get
        {
            lock (_gate)
            {
                return _all.Count - _idle.Count;
            }
        }
    }

    /// <summary>借出一帧；池已满且无空闲时返回 null。</summary>
    public DecodedFrame? Rent()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (_idle.Count > 0)
            {
                var reused = _idle.Pop();
                reused.Reset();
                reused.InUse = true;
                return reused;
            }

            if (_all.Count >= _capacity)
            {
                return null;
            }

            var created = new DecodedFrame(
                _bufferWidth,
                _bufferHeight,
                _stride,
                _sourceWidth,
                _sourceHeight,
                MaxVectorsPerFrame) { InUse = true };

            _all.Add(created);
            return created;
        }
    }

    /// <summary>归还一帧（null 安全，便于渲染端直接透传可能为空的引用）。</summary>
    public void Return(DecodedFrame? frame)
    {
        if (frame is null)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            frame.Reset();
            _idle.Push(frame);
        }
    }

    /// <summary>
    /// 释放池。这里遍历“全部”而非仅空闲帧，
    /// 保证即使渲染端持有未归还的帧也不会泄漏非托管内存。
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _idle.Clear();

            foreach (var frame in _all)
            {
                frame.Dispose();
            }

            _all.Clear();
        }
    }
}
