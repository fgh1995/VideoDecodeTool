using System.Runtime.InteropServices;

namespace VideoDecodeTool.Interop;

/// <summary>
/// FFmpeg.AutoGen 6.0.0 未导出的原生常量与结构体声明。
/// </summary>
/// <remarks>
/// 已通过反射核对 FFmpeg.AutoGen 6.0.0 的公开成员：
///   * <c>AVERROR_EOF</c> 存在，可直接使用；
///   * <c>AV_FRAME_FLAG_KEY</c> <b>不存在</b>（该常量自 FFmpeg 6.1 起才出现），FFmpeg 6.0 请使用 <c>AVFrame.key_frame</c>；
///   * <c>AVMotionVector</c> <b>未被导出</b>（AutoGen 未生成 libavutil/motion_vector.h），因此此处按 ABI 手工声明。
/// </remarks>
internal static class FfmpegNative
{
    /// <summary>AVERROR(EAGAIN)：解码器需要更多输入 / 暂无输出。</summary>
    public const int AVERROR_EAGAIN = -11;

    /// <summary>AVERROR(ENOSYS)：seek 回调不支持时返回值。</summary>
    public const int AVERROR_ENOSYS = -38;

    /// <summary>AVERROR(EPIPE)：IO 回调出错时返回值。</summary>
    public const int AVERROR_EPIPE = -32;

    /// <summary>AV_CODEC_EXPORT_DATA_MVS：请求解码器导出运动矢量侧数据（FFmpeg 6.0 起的主开关）。</summary>
    public const int AV_CODEC_EXPORT_DATA_MVS = 1 << 1;

    /// <summary>AVERROR_EOF 的数值（FFERRTAG('E','O','F',' ')）。</summary>
    public const int AVERROR_EOF_VALUE = -541478725;

    public static bool IsEagain(int error) => error == AVERROR_EAGAIN;

    public static bool IsEndOfStream(int error) => error == AVERROR_EOF_VALUE;

    public static int Error(int errno) => -errno;
}

/// <summary>
/// 与 <c>libavutil/motion_vector.h</c> 中 <c>AVMotionVector</c> 二进制布局完全一致的托管结构。
/// </summary>
/// <remarks>
/// C 定义（FFmpeg 6.0，sizeof == 40，8 字节对齐）：
/// <code>
/// int32_t  source;             // 0
/// uint8_t  w, h;               // 4, 5
/// int16_t  src_x, src_y;       // 6, 8
/// int16_t  dst_x, dst_y;       // 10, 12
/// uint64_t flags;              // 16 (补齐后)
/// int32_t  motion_x, motion_y; // 24, 28
/// uint16_t motion_scale;       // 32
/// </code>
/// source 语义：负值 = 参考过去（前向，LIST_0）；正值 = 参考未来（后向，LIST_1）。
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal struct AVMotionVectorNative
{
    public int source;
    public byte w;
    public byte h;
    public short src_x;
    public short src_y;
    public short dst_x;
    public short dst_y;
    public ulong flags;
    public int motion_x;
    public int motion_y;
    public ushort motion_scale;
}
