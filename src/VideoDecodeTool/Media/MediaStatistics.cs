using VideoDecodeTool.Models;

namespace VideoDecodeTool.Media;

/// <summary>
/// 累计帧统计。由解码线程写入、UI 线程按秒读取快照，内部用锁保证一致性。
/// </summary>
public sealed class MediaStatistics
{
    private readonly object _gate = new();

    private long _frameCount;
    private long _totalBytes;
    private long _iFrames;
    private long _pFrames;
    private long _bFrames;
    private long _otherFrames;
    private long _iBytes;
    private long _pBytes;
    private long _bBytes;
    private long _otherBytes;
    private double _qpSum;
    private long _qpCount;
    private int _qpMin = int.MaxValue;
    private int _qpMax;
    private long _motionVectors;
    private double _motionMeanSum;
    private long _motionMeanCount;
    private double _motionMaxPeak;
    private long _forwardVectors;
    private long _backwardVectors;
    private long _droppedFrames;

    /// <summary>累加一帧。</summary>
    public void AddFrame(FrameInfo info)
    {
        lock (_gate)
        {
            _frameCount++;
            _totalBytes += info.PacketSize;

            switch (info.Kind)
            {
                case FrameKind.I:
                    _iFrames++;
                    _iBytes += info.PacketSize;
                    break;
                case FrameKind.P:
                    _pFrames++;
                    _pBytes += info.PacketSize;
                    break;
                case FrameKind.B:
                    _bFrames++;
                    _bBytes += info.PacketSize;
                    break;
                default:
                    _otherFrames++;
                    _otherBytes += info.PacketSize;
                    break;
            }

            // QP 有效范围约定为 [1, 63]，其余（0 = 未提供）不计入统计
            if (info.Qp is > 0 and <= 63)
            {
                // 累计平均也用「min-max 中点」这一个定义，和面板读数是同一套口径
                _qpSum += info.QpAverage;
                _qpCount++;

                // min / max 必须取本帧「宏块级」的极值，不能用逐帧平均值：
                // 逐帧平均都集中在 23~27 这种窄区间，拿它当 min/max 会得到一条几乎不动的读数
                // （例如 min 23 / max 27），而真实的宏块级跨度是 5~42 这种量级。
                var frameMin = info.QpMin > 0 ? info.QpMin : info.Qp;
                var frameMax = info.QpMax > 0 ? info.QpMax : info.Qp;

                if (frameMin < _qpMin)
                {
                    _qpMin = frameMin;
                }

                if (frameMax > _qpMax)
                {
                    _qpMax = frameMax;
                }
            }

            if (info.VectorCount > 0)
            {
                _motionVectors += info.VectorCount;
                _motionMeanSum += info.MotionMeanPixels;
                _motionMeanCount++;
                if (info.MotionMaxPixels > _motionMaxPeak)
                {
                    _motionMaxPeak = info.MotionMaxPixels;
                }
            }

            _forwardVectors += info.ForwardVectors;
            _backwardVectors += info.BackwardVectors;
        }
    }

    /// <summary>记录一次丢帧（渲染跟不上）。</summary>
    public void AddDroppedFrame()
    {
        lock (_gate)
        {
            _droppedFrames++;
        }
    }

    /// <summary>取一份不可变快照。</summary>
    public StatisticsSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new StatisticsSnapshot
            {
                FrameCount = _frameCount,
                TotalBytes = _totalBytes,
                IFrames = _iFrames,
                PFrames = _pFrames,
                BFrames = _bFrames,
                OtherFrames = _otherFrames,
                IKeyBytes = _iBytes,
                PKeyBytes = _pBytes,
                BKeyBytes = _bBytes,
                OtherKeyBytes = _otherBytes,
                QpAverage = _qpCount == 0 ? 0 : _qpSum / _qpCount,
                QpMin = _qpMin == int.MaxValue ? 0 : _qpMin,
                QpMax = _qpMax,
                MotionVectorCount = _motionVectors,
                MotionMeanPixels = _motionMeanCount == 0 ? 0 : _motionMeanSum / _motionMeanCount,
                MotionMaxPixels = _motionMaxPeak,
                ForwardVectors = _forwardVectors,
                BackwardVectors = _backwardVectors,
                DroppedFrames = _droppedFrames,
            };
        }
    }

    /// <summary>清空统计（重新开始播放时调用）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _frameCount = 0;
            _totalBytes = 0;
            _iFrames = _pFrames = _bFrames = _otherFrames = 0;
            _iBytes = _pBytes = _bBytes = _otherBytes = 0;
            _qpSum = 0;
            _qpCount = 0;
            _qpMin = int.MaxValue;
            _qpMax = 0;
            _motionVectors = 0;
            _motionMeanSum = 0;
            _motionMeanCount = 0;
            _motionMaxPeak = 0;
            _forwardVectors = 0;
            _backwardVectors = 0;
            _droppedFrames = 0;
        }
    }
}
