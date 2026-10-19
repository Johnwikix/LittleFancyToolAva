# FFmpeg 捆绑库（进程内调用）

应用通过 FFmpeg.AutoGen 在进程内直接调用下列 shared 库，不再依赖外部 ffmpeg.exe/ffprobe.exe。

- 版本：FFmpeg N-127579-g5cb9fcd211-20261016（固定 master 提交，保持 AutoGen 9.0.1.1 使用的六库 ABI 主版本）
- 构建：BtbN FFmpeg-Builds `autobuild-2026-10-16-13-04`
  - win64: `ffmpeg-N-127579-g5cb9fcd211-win64-gpl-shared.zip`
  - linux64: `ffmpeg-N-127579-g5cb9fcd211-linux64-gpl-shared.tar.xz`
  - https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-10-16-13-04
- 许可：GPL-3.0（gpl full 构建，含 libx264/libx265/libaom/libsvtav1/libvpx 等 GPL 组件），见 LICENSE.txt
- 仅拷贝转码管线实际依赖的六个库：
  avformat→avcodec→avutil、avfilter→swscale→swresample→avutil；
  avdevice 未使用，不随包分发。exe（ffmpeg/ffprobe/ffplay）不分发。

## 获取方式（仓库不收录二进制）

2026-10-19 起 `win-x64/`、`linux-x64/` 不再入库：FFmpeg 六库单文件超过 GitHub 的 100 MB 限制，
推送会被直接拒绝。首次构建或发布前按下方记录下载并校验：

```powershell
# 当前平台（或 -Platform all / win / linux）下载、解出六库并逐文件校验 SHA-256
pwsh -File FancyTool/Libraries/FFmpeg/fetch-ffmpeg.ps1 -Platform all

# 只校验本地已有文件的 SHA-256
pwsh -File FancyTool/Libraries/FFmpeg/fetch-ffmpeg.ps1 -VerifyOnly

# 离线：把上方 release 里的两个发布包放进同一目录后指定
pwsh -File FancyTool/Libraries/FFmpeg/fetch-ffmpeg.ps1 -ArchiveDirectory D:\downloads
```

脚本从本文件读取 release tag、发布包名与 SHA-256（单一来源）；下载缓存与解包临时目录位于已忽略的
`.qa/ffmpeg-fetch/`。缺少对应平台的库时 `FancyToolAva.csproj` 会以 `FT1001` 中断构建，避免产出装不上解码库的应用。

## 共享库 SHA-256

### win-x64

- `avcodec-63.dll`: 776d673738f99c6fc7499404056b017e1ecf8e5c3f6ae918013dca62d7256398
- `avfilter-12.dll`: 9fc7f99c04d94e38354c91efdea471629b11524392993a09508fbcbe4c39cc81
- `avformat-63.dll`: e9dbab861b5095bb098f7a027ea0a846347a9886a6d77d35234a5c7decd0d71c
- `avutil-61.dll`: 8fc87fb2cb1abdd16780ace8cff98c7392858650314f5a77f7393497cec9fa61
- `swresample-7.dll`: a050a845f24882c45bb162d4f2b34377c15297632cfb04c4c1570145813f3157
- `swscale-10.dll`: 62b03d1fd6e4dfb05f830ce4430123de27c96089b9a4beaa0b7b09aaa4b3779d

### linux-x64

- `libavcodec.so.63`: 62b1e8dd224135bda6202e3528a5101713d5ca38061671048b67e6c1ad7d45ca
- `libavfilter.so.12`: 6df2e3fcb76fe051c1a17d5cd4f663d4f05f00846f817a1227d3619c372cbccc
- `libavformat.so.63`: f21e43be2bc20411cc866bae73d57310d3cdd33cf43b8a3758f6fd0e18c3bf08
- `libavutil.so.61`: 5433570c5a2e6dc0762621b7ee875f44bd685613778fdf256f8daad69759b4d1
- `libswresample.so.7`: b08da71b0bc7310e6c057a7e87bd6e06e0e06af48e430b47b0e1575c6b47c56a
- `libswscale.so.10`: 00ea4fc1a13273ef2db0a0f9b33853c4bcb44a330995a5fb38cab0e0466d20ee

## 升级须知

升级流程：先更新本文件的版本、发布包名与全部 SHA-256，再执行
`pwsh -File FancyTool/Libraries/FFmpeg/fetch-ffmpeg.ps1 -Force` 重新获取（目录通配符会把解出的新库带上包，需人工核对）。
六库 ABI 主版本必须保持兼容（avcodec-63/avformat-63/avutil-61/swresample-7/swscale-10/avfilter-12），
当前 libavutil 61.14、avcodec 63.20、avformat 63.10、avfilter 12.5、swscale 10.2、swresample 7.3，保持 FFmpeg.AutoGen 9.0.1.1 的 ABI 主版本；不得只混换单个库，升级后必须执行完整转码、HDR 与 GPU 烟测。

> 下列 NVDEC/HDR 条目记录本次更新之前的旧库验证；当前库的结果以末尾光流诊断链接为准。

## NVDEC / CUDA / NVENC 管线验证（2026-10-17）

未替换 FFmpeg 二进制。新增硬件解码、CUDA 帧池和 NVENC 硬件输入后，使用进程内真实转码路径验证：

```powershell
$env:FANCYTOOL_DATA_DIRECTORY = (Resolve-Path .qa/video-pipeline/data).Path
pwsh -File FancyTool/_tools/FfmpegSmokeTest/ValidatePipeline.ps1 -OutputDirectory .qa/video-cuda/validation -DataDirectory .qa/video-pipeline/data
```

该入口包含 FFmpeg 全矩阵、普通队列及 CUDA 压缩输入集成验证；原始记录保留在已忽略的 `.qa/`。架构、回退边界、画质差异及测速结果见 [显存管线实测](../../docs/video-cuda-pipeline-2026-10-17.md)。

## 2026-10-17：HDR 显存管线追加验证

FFmpeg DLL 未更新；新增 10 位 NVDEC/P010、GPU LUT 与 NVENC 交接使用本目录现有共享库。真实路径验证：

```powershell
& $exe .qa/video-hdr/hdr-check --hdr-cuda
pwsh -File FancyTool/_tools/FfmpegSmokeTest/ValidatePipeline.ps1 -OutputDirectory .qa/video-hdr/validation -DataDirectory .qa/video-pipeline/data
```

`$exe` 指向 Release 的 FfmpegSmokeTest；需按 [HDR 实测报告](../../docs/video-hdr-cuda-2026-10-17.md) 设置隔离模型目录。57 项 HDR GPU 扩展、21 项插帧、101 项 FFmpeg 烟测及原矩阵结果见本地 `.qa/video-hdr/verification.json`，两项无对应设备的编码烟测跳过。

## 2026-10-17：启用 NVIDIA Vulkan 光流

旧 release/9.0 构建缺少 `fruc_vulkan`；本次固定到上述提交，Windows/Linux 同步更新六库，未修改 FFmpeg.AutoGen 包版本。Windows RTX 5070 Ti / 617.14 暴露 `VK_NV_optical_flow`，并已真实生成 2×/4×/6× 帧；硬件滤镜使用 segment API，在滤镜初始化前绑定设备；NVIDIA 优先探测线性图像以降低 Windows 回读开销，失败时继续验证原布局。归档下载 SHA-256 与各库校验见本地 `.qa/optical-flow-2026-10-17/native-manifest.json`；复现与完整验证见 [光流诊断](../../docs/optical-flow-2026-10-17.md)。
