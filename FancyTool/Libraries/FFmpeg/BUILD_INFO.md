# FFmpeg 捆绑库（进程内调用）

应用通过 FFmpeg.AutoGen 在进程内直接调用下列 shared 库，不再依赖外部 ffmpeg.exe/ffprobe.exe。

- 版本：FFmpeg n9.0.1-84-g946fcce07b-20260918（release/9.0 分支，ABI 9.0.x）
- 构建：BtbN FFmpeg-Builds `autobuild-2026-09-18-13-22`
  - win64: `ffmpeg-n9.0.1-84-g946fcce07b-win64-gpl-shared-9.0.zip`
  - linux64: `ffmpeg-n9.0.1-84-g946fcce07b-linux64-gpl-shared-9.0.tar.xz`
  - https://github.com/BtbN/FFmpeg-Builds/releases/tag/autobuild-2026-09-18-13-22
- 许可：GPL-3.0（gpl full 构建，含 libx264/libx265/libaom/libsvtav1/libvpx 等 GPL 组件），见 LICENSE.txt
- 仅拷贝转码管线实际依赖的六个库：
  avformat→avcodec→avutil、avfilter→swscale→swresample→avutil；
  avdevice 未使用，不随包分发。exe（ffmpeg/ffprobe/ffplay）不分发。

## Win-x64 SHA-256

- `avcodec-63.dll`: a262c589cb818006ae6f33182666bf2cdea690ea0219062b77b1834cc61df171
- `avfilter-12.dll`: 18e1e48015bf815be6931cf8f2290c2c1bdb7d0ebe23dc1da932751f7f3d7a7b
- `avformat-63.dll`: 1f42d0f445467cdb9adf62eed15184f018e7adb93f295bb6c445c59673db584b
- `avutil-61.dll`: f055a75616b12a49c741876723a459235852ad657e9e1d04e442163eba5b1b2f
- `swresample-7.dll`: f185d89c41c0a3fdf0801b9ae5c8df3f5e1033e6d03901b9699e2d6d427a64dd
- `swscale-10.dll`: ea8d02bb10314c4372cf484fa7822aec49cb37c8011fd8e5a2028ecab89015d2

## 升级须知

目录通配符意味着升级 FFmpeg 解进新 DLL/so 会被静默带上包，需人工核对：
版本仍须为 FFmpeg 9.0.x ABI（avcodec-63/avformat-63/avutil-61/swresample-7/swscale-10/avfilter-12），
与 FFmpeg.AutoGen 9.0.1.1 绑定版本一致；升级 AutoGen 时需同步替换本目录并更新本文件。

## NVDEC / CUDA / NVENC 管线验证（2026-10-17）

未替换 FFmpeg 二进制。新增硬件解码、CUDA 帧池和 NVENC 硬件输入后，使用进程内真实转码路径验证：

```powershell
$env:FANCYTOOL_DATA_DIRECTORY = (Resolve-Path .qa/video-pipeline/data).Path
pwsh -File FancyTool/_tools/FfmpegSmokeTest/ValidatePipeline.ps1 -OutputDirectory .qa/video-cuda/validation -DataDirectory .qa/video-pipeline/data
```

该入口包含 FFmpeg 全矩阵、普通队列及 CUDA 压缩输入集成验证；原始记录保留在已忽略的 `.qa/`。架构、回退边界、画质差异及测速结果见 [显存管线实测](../../docs/video-cuda-pipeline-2026-10-17.md)。
