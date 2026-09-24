using System.Runtime.InteropServices;

namespace VideoDecodeTool.Models;

/// <summary>
/// 渲染层使用的运动矢量：坐标与位移均已换算为像素单位，避免渲染时反复做定点除法。
/// </summary>
/// <remarks>
/// FFmpeg 定义：src = dst + motion / motion_scale，
/// 其中 motion_scale = 1 &lt;&lt; (1 + quarter_sample)，H.264 一般为 4。
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct MotionVector
{
    public MotionVector(short dstX, short dstY, short dx, short dy, byte blockWidth, byte blockHeight, sbyte direction)
    {
        DstX = dstX;
        DstY = dstY;
        Dx = dx;
        Dy = dy;
        BlockWidth = blockWidth;
        BlockHeight = blockHeight;
        Direction = direction;
    }

    /// <summary>矢量所属块在当前帧中的横坐标（像素）。</summary>
    public readonly short DstX;

    /// <summary>矢量所属块在当前帧中的纵坐标（像素）。</summary>
    public readonly short DstY;

    /// <summary>水平位移（像素，已除以 motion_scale）。</summary>
    public readonly short Dx;

    /// <summary>垂直位移（像素，已除以 motion_scale）。</summary>
    public readonly short Dy;

    /// <summary>块宽（像素）。</summary>
    public readonly byte BlockWidth;

    /// <summary>块高（像素）。</summary>
    public readonly byte BlockHeight;

    /// <summary>-1 = 参考过去（前向，LIST_0）；+1 = 参考未来（后向，LIST_1）。</summary>
    public readonly sbyte Direction;

    /// <summary>块中心横坐标，绘制箭头时使用。</summary>
    public float CenterX => DstX + BlockWidth * 0.5f;

    /// <summary>块中心纵坐标，绘制箭头时使用。</summary>
    public float CenterY => DstY + BlockHeight * 0.5f;
}
