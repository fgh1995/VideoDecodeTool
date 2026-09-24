FFmpeg 原生资源目录（已部署好，开箱可用）
==========================================

本目录采用「扁平布局」：共享库与命令行工具放在同一层，编译时会整体复制到输出目录的 ffmpeg\ 下。

一、当前内容
------------
共享库（供 FFmpeg.AutoGen 解码，ffmpeg.RootPath 指向本目录）
    avcodec-60.dll      73.74 MB   H.264 等解码器
    avformat-60.dll     16.03 MB   解复用（MP4/MKV/...）
    avutil-58.dll        2.09 MB   基础工具库
    swscale-7.dll        0.61 MB   YUV → BGRA 像素转换
    swresample-4.dll     0.42 MB   音频重采样（→ 48kHz 立体声）
    avdevice-60.dll      3.68 MB   （可选）
    avfilter-9.dll      37.76 MB   （可选）
    postproc-57.dll      0.07 MB   （可选）

命令行工具（转码 / 探测）
    ffmpeg.exe           0.36 MB   动态链接版，依赖上面的 DLL
    ffprobe.exe          0.18 MB   动态链接版，依赖上面的 DLL

来源：https://github.com/GyanD/codexffmpeg/releases/tag/6.0
      资产：ffmpeg-6.0-full_build-shared.zip（gyan.dev 的 FFmpeg 6.0 x64 全功能共享库构建）
      国内下载可套用 GitHub 加速前缀，例如：
      https://<加速域名>/https://github.com/GyanD/codexffmpeg/releases/download/6.0/ffmpeg-6.0-full_build-shared.zip

重要：**必须是 6.0 版本的共享库**
    FFmpeg.AutoGen 6.0.0 的绑定里 DllImport 名称固定为 avcodec-60 / avformat-60 / avutil-58 ...
    换成 FFmpeg 7.x(avcodec-61) / 8.x(avcodec-62) 的 DLL 会因名称与 ABI 不一致而无法工作。
    同样，**静态构建的 ffmpeg.exe 无法用于解码**（它不导出任何 DLL）。

二、为什么 exe 与 DLL 必须同目录
--------------------------------
ffmpeg.exe / ffprobe.exe 是动态链接版，Windows 只会在「EXE 所在目录 → 当前目录 → PATH」
中查找导入的 DLL。因此它们必须与本目录下的 av*-60.dll 放在一起。

三、程序如何定位
----------------
Interop/FfmpegRuntime.cs → NativeLibraryLocator 按以下顺序查找：
    1) <程序目录>\ffmpeg
    2) <程序目录>
    3) 从程序目录逐级向上查找名为 ffmpeg 的目录（dotnet run 开发场景）
命令行工具额外兼容 <程序目录>\ffmpeg\bin\ 布局。

四、缺失时会发生什么
--------------------
程序仍可启动，并在状态栏给出明确提示：
    - 源片解码 / 运动矢量提取 / 边转边播：不可用；
    - ffprobe 文件信息探测：仍可用；
    - GPU 编码器枚举与试编码：仍可用；
    - 转码并输出标准 MP4：仍可用（自动跳过实时预览，直接落盘）。

五、版本自检
------------
    ffmpeg.exe -hide_banner -version      # 应显示 6.0 且 libavcodec 为 60.x
    ffmpeg.exe -hide_banner -encoders | findstr /I "nvenc qsv amf"
    ffmpeg.exe -hide_banner -hwaccels
