namespace VideoDecodeTool.ViewModels;

/// <summary>
/// 点击「停止转码」后用户对「已转码部分」的处理选择。
/// </summary>
public enum StopTranscodeChoice
{
    /// <summary>不停止，继续转码。</summary>
    Continue,

    /// <summary>停止，但把已转码部分重封装为标准 MP4 保存下来。</summary>
    KeepTranscoded,

    /// <summary>停止，并删除已转码的临时文件（不产出任何输出）。</summary>
    DeleteTranscoded,
}
