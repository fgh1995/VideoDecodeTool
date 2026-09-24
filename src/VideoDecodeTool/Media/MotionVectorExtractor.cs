using FFmpeg.AutoGen;
using System.Runtime.InteropServices;
using VideoDecodeTool.Interop;
using VideoDecodeTool.Models;

namespace VideoDecodeTool.Media;

/// <summary>一帧运动矢量的统计结果。</summary>
public readonly record struct MotionVectorStatistics(
    int Total,
    int Forward,
    int Backward,
    double MeanPixels,
    double MaxPixels,
    double ForwardMeanPixels,
    double BackwardMeanPixels);

/// <summary>
/// 从 <c>AV_FRAME_DATA_MOTION_VECTORS</c> 侧数据中提取运动矢量。
/// </summary>
/// <remarks>
/// 前置条件：解码器需开启运动矢量导出（<c>AV_CODEC_EXPORT_DATA_MVS</c>），
/// 该 side data 由 <c>ff_print_debug_info2()</c> 写入，仅对带运动补偿的编解码器（H.264 / MPEG-2 / VP9 ...）有效。
/// <para>
/// AVMotionVector.source 语义（FFmpeg 官方定义）：
/// <c>-1</c> = 参考过去的帧（前向 / LIST_0），<c>+1</c> = 参考未来的帧（后向 / LIST_1）。
/// </para>
/// </remarks>
internal static unsafe class MotionVectorExtractor
{
    private static readonly int NativeStructSize = Marshal.SizeOf<AVMotionVectorNative>();

    /// <summary>
    /// 提取运动矢量。所有矢量都会参与统计；写入 <paramref name="destination"/> 的数量受其长度限制，
    /// 超出时按等差采样抽取，保证叠加层绘制散布均匀。
    /// </summary>
    public static MotionVectorStatistics Extract(AVFrame* frame, MotionVector[] destination, out int written)
    {
        written = 0;

        var sideData = ffmpeg.av_frame_get_side_data(frame, AVFrameSideDataType.AV_FRAME_DATA_MOTION_VECTORS);
        if (sideData == null || sideData->data == null || sideData->size == 0)
        {
            return default;
        }

        var total = (int)(sideData->size / (ulong)NativeStructSize);
        if (total <= 0)
        {
            return default;
        }

        var source = (AVMotionVectorNative*)sideData->data;

        // 采样步长：>0 时每隔 step 个矢量写入一个，仅在数量超出绘制容量时生效
        var step = destination.Length > 0 && total > destination.Length
            ? (int)Math.Ceiling(total / (double)destination.Length)
            : 1;

        var forward = 0;
        var backward = 0;
        double sum = 0;
        double max = 0;
        double forwardSum = 0;
        double backwardSum = 0;
        var index = 0;

        for (var i = 0; i < total; i++)
        {
            var mv = source[i];

            // motion_scale = 1 << (1 + quarter_sample)，H.264 通常为 4；防御性处理 0 值
            var scale = mv.motion_scale == 0 ? 1 : mv.motion_scale;
            var dx = mv.motion_x / (double)scale;
            var dy = mv.motion_y / (double)scale;
            var length = Math.Sqrt(dx * dx + dy * dy);

            sum += length;
            if (length > max)
            {
                max = length;
            }

            if (mv.source < 0)
            {
                forward++;
                forwardSum += length;
            }
            else if (mv.source > 0)
            {
                backward++;
                backwardSum += length;
            }

            if (i % step != 0 || index >= destination.Length)
            {
                continue;
            }

            destination[index++] = new MotionVector(
                mv.dst_x,
                mv.dst_y,
                ClampToShort(dx),
                ClampToShort(dy),
                mv.w == 0 ? (byte)16 : mv.w,
                mv.h == 0 ? (byte)16 : mv.h,
                (sbyte)Math.Sign(mv.source));
        }

        written = index;

        // 两个方向各给一份平均长度：图表的 MOTION 行用「同一帧位置上叠加的 fwd / bwd 两根柱」
        // 表达运动来自过去还是未来（参考实现那一行的含义），所以需要分方向的均值。
        return new MotionVectorStatistics(
            total,
            forward,
            backward,
            sum / total,
            max,
            forward == 0 ? 0 : forwardSum / forward,
            backward == 0 ? 0 : backwardSum / backward);
    }

    private static short ClampToShort(double value)
    {
        if (value > short.MaxValue)
        {
            return short.MaxValue;
        }

        return value < short.MinValue ? short.MinValue : (short)Math.Round(value);
    }
}
