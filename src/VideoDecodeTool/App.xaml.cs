using System.Windows;
using System.Windows.Threading;
using VideoDecodeTool.Interop;

namespace VideoDecodeTool;

/// <summary>
/// 应用入口。负责在创建任何窗口前完成 FFmpeg 原生绑定初始化，并兜底未处理异常。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 必须在任何解码调用之前设置 ffmpeg.RootPath 并把目录加入 DLL 搜索路径
        FfmpegRuntime.Initialize();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                ShowFatal(exception);
            }
        };

        base.OnStartup(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception);
        e.Handled = true;
    }

    private static void ShowFatal(Exception exception)
    {
        MessageBox.Show(
            $"发生未处理的异常：\n\n{exception.GetType().Name}: {exception.Message}\n\n{exception.StackTrace}",
            "视频解码分析工具",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
