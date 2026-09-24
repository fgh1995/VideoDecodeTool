using System.Windows;
using VideoDecodeTool.ViewModels;

namespace VideoDecodeTool.Views;

/// <summary>
/// 「停止转码」三选一询问框：取消（继续转码）/ 停止并封装已转码 / 停止并删除。
/// </summary>
public partial class StopTranscodeDialog : Window
{
    public StopTranscodeDialog() => InitializeComponent();

    /// <summary>用户选择。直接关闭窗口（右上角 X / Esc）时按「取消」处理，即不停止转码。</summary>
    public StopTranscodeChoice Choice { get; private set; } = StopTranscodeChoice.Continue;

    private void OnContinueClick(object sender, RoutedEventArgs e) => CloseWith(StopTranscodeChoice.Continue);

    private void OnKeepClick(object sender, RoutedEventArgs e) => CloseWith(StopTranscodeChoice.KeepTranscoded);

    private void OnDeleteClick(object sender, RoutedEventArgs e) => CloseWith(StopTranscodeChoice.DeleteTranscoded);

    private void CloseWith(StopTranscodeChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}
