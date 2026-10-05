# 视频解码分析工具（VideoDecodeTool）

一款 Windows 桌面视频分析与转码工具。在播放视频的同时，实时在画面上叠加显示**运动矢量（MV）**、**帧类型（I/P/B）**、**QP** 等编码参数，
底部以时序图表逐帧展示码率 / 帧类型分布 / QP / 运动矢量长度 / GOP / 重排，并支持 GPU 硬件加速转码、**边转边播**（转码的同时预览已转码部分）。

界面采用 QCTools 风格的多面板布局：左侧视频预览 + 叠加层，右侧文件信息 / 帧参数 / 帧统计 / 转码设置 / 播放控制，底部六联时序图表。
<img width="1914" height="1011" alt="UI" src="https://github.360967.xyz/https://github.com/user-attachments/assets/1a616a6a-29bb-4c9f-b230-7d6368bc43c0" />
---

## 一、这是什么 / 能做什么

| 能力 | 说明 |
|---|---|
| 实时编码参数可视化 | 播放时画面内叠加运动矢量箭头、帧类型徽标、QP 等；底部六联图表逐帧呈现码率、I/P/B 分布、QP 区间、运动矢量、GOP、重排深度 |
| GPU 硬件转码 | 自动探测本机可用的 NVIDIA（NVENC）/ Intel（QSV）/ AMD（AMF）编码器，也可用软件编码（x264/x265/SVT-AV1 等），输出标准 MP4 |
| 边转边播 | 转码过程中实时预览已转码的内容，最终产出可拖动、moov 前置的标准 MP4

适用人群：需要观察视频编码细节（运动估计、帧结构、量化参数）的开发者、多媒体分析人员、编码调优人员。

---

## 二、运行环境

| 项 | 要求 |
|---|---|
| **操作系统** | Windows 10 1607（build 14393）及以上，**64 位**；不支持 Windows 7 / 32 位系统 |
| **运行时** | .NET 8 Desktop Runtime（`Microsoft.WindowsDesktop.App 8.x`）。未安装时程序无法启动，下载：<https://dotnet.microsoft.com/download/dotnet/8.0>（选 **Desktop Runtime**，x64） |
| **磁盘** | 程序本体 + FFmpeg 运行库约 150 MB |
| **GPU 转码（可选）** | 取决于机器是否具备对应硬件与驱动：NVIDIA 需较新驱动、Intel 需核显驱动、AMD 需 Adrenalin 驱动；无对应硬件时自动回退到软件编码 |

> 本程序为 64 位（x64）构建，只能运行在 64 位 Windows 上。

---

## 三、安装与运行

1. 安装 **.NET 8 Desktop Runtime**（x64）：<https://dotnet.microsoft.com/download/dotnet/8.0>（下载页里选 **Desktop Runtime** 的 x64 安装包）。
2. 下载 Releases 中的压缩包并解压到任意目录。
3. 解压后目录中应包含：
   ```
   VideoDecodeTool.exe
   ffmpeg\
   ├─ avcodec-60.dll / avformat-60.dll / avutil-58.dll / swscale-7.dll / swresample-4.dll …   解码运行库（必需）
   └─ ffmpeg.exe / ffprobe.exe                                                              转码与探测（必需）
   ```
   ⚠ **`ffmpeg\` 目录必须与 `VideoDecodeTool.exe` 放在一起**，缺了它解码/转码功能不可用（详见下节）。
4. 双击 `VideoDecodeTool.exe` 启动，或把视频文件拖到窗口上、用「打开方式」关联。

也可在命令行直接带上片源路径：
```powershell
VideoDecodeTool.exe  D:\movie.mp4
```

---

## 四、功能依赖与降级说明

程序依赖一套 **FFmpeg 6.0 共享库**（`ffmpeg\` 目录里的 `av*-60.dll` 等）做视频解码。Releases 压缩包已内置，正常解压即可使用。

若 `ffmpeg\` 目录缺失或被删除，程序**不会崩溃**，但功能会降级：

| 功能 | 缺库时 |
|---|---|
| 源片播放 / 边转边播 / 运动矢量叠加 | ❌ 不可用，状态栏会提示 |
| 文件信息探测（ffprobe） | ✅ 仍可用 |
| GPU 编码器检测与试编码 | ✅ 仍可用 |
| 转码输出标准 MP4 | ✅ 仍可用（无实时预览，转完直接落盘） |

> 如自行补库，必须使用 **FFmpeg 6.0** 的共享库构建（来源：gyan.dev 的 `ffmpeg-6.0-full_build-shared.zip`）。
> 7.x / 8.x 的库文件名（如 `avcodec-61`）与本程序不兼容。

---

## 五、功能说明

- **视频预览**：逐帧解码渲染，支持叠加层开关（运动矢量、帧类型徽标、QP 图例），矢量可放大显示。
- **底部图表**：六联时序图共享一条 4 秒滑动时间轴、居中播放头，逐帧采样：
  - BITRATE 码率、FRAME TYPE 帧类型（I/P/B 着色）、QP 量化参数区间、MOTION 运动矢量、GOP 距关键帧、REORDER 重排深度
- **转码**：自动枚举本机编码器（硬件 + 软件）并实测可用性；可选编码类型（H.264/H.265/AV1/H.266 等，依本机支持而定）、码控、配置文件、速度档、帧率等；转码进度实时显示。
- **播放控制**：播放 / 暂停（当前为顺序播放，暂不支持拖动跳转）。

---

## 六、已知限制

- 暂不支持播放中拖动跳转（seek），为顺序播放。
- 源片模式下逐帧 QP 通常不可用（多数解码器不提供，显示 `n/a`）；转码模式的 QP 正常显示。
- 运动矢量仅 H.264 / MPEG-2 / VP9 等带运动补偿的解码器可用，其余格式叠加层为空。
- 预览分辨率上限 1280 宽（性能取舍，不影响转码输出的原始分辨率）。

---

## 七、反馈

运行库缺失、编码器异常等情况会在窗口底部状态栏给出提示；如遇崩溃，可将现象与片源信息反馈给维护者。

# 赞助二维码1：
<img width="1242" height="1692" alt="mm_facetoface_collect_qrcode_1791196704492" src="https://github.360967.xyz/https://github.com/user-attachments/assets/d40e4173-e9a3-4cf7-bde9-d6c9b077847b" />
# 赞助二维码2：
<img width="1242" height="1692" alt="mm_facetoface_collect_qrcode_1791196704492" src="https://github.com/user-attachments/assets/d40e4173-e9a3-4cf7-bde9-d6c9b077847b" />
