# RTX 5070 Ti 光流补帧诊断（2026-10-17）

## 原因与修复

本机显卡和驱动可用。`vulkaninfo` 在 RTX 5070 Ti / 617.14 上暴露 `VK_NV_optical_flow` 和 `QUEUE_OPTICAL_FLOW_BIT_NV`；用隔离的新版 FFmpeg 已真实完成光流补帧。当前不可用的原因有两层：

1. 原捆绑 `n9.0.1-84-g946fcce07b-20260918` 是 release/9.0 构建，实际导出查询中没有 `fruc_vulkan`。连续两次调用生产转码入口均被 `Interp_FilterMissing fruc_vulkan` 拒绝，尚未进入设备能力校验。
2. 仅更新 DLL 后，生产入口仍失败：旧 `BindGraphAndConfigure` 调用 `avfilter_graph_parse_ptr` 后才绑定 `hw_device_ctx`，而 `hwupload` 在解析/初始化时已经需要设备。使用 segment API 分离创建和初始化，在初始化前给滤镜持有设备引用，再连接 buffersrc/buffersink；所有引用由图和调用方分别释放，失败路径也会释放 segment 和端点。

Windows/Linux 六库统一更新为固定提交 `5cb9fcd211`、BtbN `autobuild-2026-10-16-13-04` 的 GPL shared 构建，保留 avcodec 63、avformat 63、avutil 61、avfilter 12、swscale 10、swresample 7 的 ABI 主版本，未修改 AutoGen 9.0.1.1、Windows ML 或全局 SDK。来源及逐库 SHA-256 见 [BUILD_INFO](../Libraries/FFmpeg/BUILD_INFO.md)，下载归档校验见本地 `native-manifest.json`。

CPU 运动补偿的主瓶颈在 `minterpolate` 算法：实际随包滤镜 `flags=0`，没有 `AVFILTER_FLAG_SLICE_THREADS`；上游实现也没有调度切片工作。应用未把解码/编码线程限制为单核，增加这些线程不会使运动估计并行。1/16 滤镜线程的辅助单次对照耗时 5.11/4.78 秒，仅用于排除线性扩展，不能据此宣称性能收益。CPU 路径保留既有 AOBMC/VSBMC 画质设置，没有用降质参数替代。

## 相同输入实测

机器：i5-13600KF（14 核 / 20 线程）、RTX 5070 Ti、驱动 617.14、Windows 11、.NET SDK 10.0.402。输入由既有自然视频 `input1080.mp4` 截取并统一为 1920×1080、24 fps、72 帧、3 秒、8 位 4:2:0；SHA-256 为 `0c4035d1c88e8a36b3a381811e7533ff5764ddf884416f86d9b0d86441f28ecd`。仅补帧，不开启超分和预览，关闭场景检测，4× 输出 288 帧，NVENC H.264 p4/Fast、QP 18；三种算法均使用更新后的库。

每种算法单独启动进程，首次运行一次，再重复两次，重复结果取中位数；计时包围真实 `TranscodeAsync`，包括初始化和排空，排除输入生成、输出探测和逐帧时间戳检查。驱动/模型缓存未清空，首次不是纯冷启动。CPU 每 200 ms 采样，只统计重复运行的计时区间。

| 算法 | 首次输出 fps | 重复输出 fps 中位数 | 平均逻辑核心数 | 平均总 CPU 占用 |
|---|---:|---:|---:|---:|
| CPU 运动补偿 | 8.12 | 8.07 | 1.26 | 6.30% |
| NVIDIA 光流原布局 | 10.94 | 11.51 | 1.04 | 5.18% |
| NVIDIA 光流线性布局（最终默认） | 28.84 | 30.89 | 未采样 | 未采样 |
| RIFE 4.25 Lite GPU | 37.32 | 139.44 | 1.05 | 5.24% |

这组输入下 RIFE 仍最快。原布局 NVIDIA 光流约 11.51 fps，优先探测线性布局后约 30.89 fps（同场景约 2.68 倍）。线性布局每种倍率实际生成帧；失败或倍率不全时再探测原有最优平铺布局，优先保留更多已验证倍率，避免根据型号强行开启。NVIDIA 光流仍通过 Vulkan 上传/回读，尚未接入 CUDA/Vulkan 外部内存共享。

追加的单变量定位使用相同 288 帧、相同 perf=medium/grid=1：GPU 图到空输出约 2.28 秒；加入 hwdownload 后约 25.28 秒；线性布局加回读约 9.71 秒。以上各一次，仅用于定位回读成本，不作为正式吞吐中位数。另测 grid=4 的纯 GPU 图约 1.06 秒，但未修改产品的 perf/grid 参数。avoid_host_import=1 对本次慢回读无明显帮助（约 24.73 秒）。原始命令/输出在 readback-*.log、readback-control.json、linear-images.json 和 avoid-host-import.json。

布局优化不是逐像素一致：288 帧全部解码和时间戳/色彩元数据匹配，但 146 帧编码输出校验和不同（原布局的两次重复为 0 差异帧）；相对原布局 PSNR 52.93 dB、SSIM 0.998572、最差帧 SSIM 0.995094，通过现有差异检查。结果只能说明此输入下差异较小，不能据此宣称无损或画质提升；未改变算法 perf/grid 设置，也未把低 SM 占用等同于专用光流引擎空闲。

## 复现与证据

全部原始文件位于 [本地证据目录](../../.qa/optical-flow-2026-10-17/)。`nvidia-probe*.log` 是旧库拒绝记录；`native-optical1080.log` 是仅换库仍失败的记录；`native-fixed-optical1080.log` 是初始化顺序修复后的真实输出；`vulkan-full.txt`、`bundled-capabilities.json`、`performance-summary.json`、`formal-*/measurements.json`、`formal-*-cpu.json`、`linear-quality/quality.json` 及 `linear-pixel-comparison.json` 保留设备、线程能力与每次采样。

```powershell
$env:FANCYTOOL_DATA_DIRECTORY = (Resolve-Path .qa/video-pipeline/data).Path
dotnet build FancyTool/_tools/FfmpegSmokeTest/FfmpegSmokeTest.csproj -c Release --disable-build-servers -v:q
$exe = 'FancyTool/_tools/FfmpegSmokeTest/bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe'
# NVIDIA 将按真实能力探测优先使用线性布局；最终结果见 formal-NvidiaLinear
foreach ($mode in 'MotionCompensation','NvidiaOpticalFlow','Rife') {
    & $exe ".qa/optical-flow-repro/$mode" --pipeline-benchmark --input .qa/optical-flow-2026-10-17/real1080-72.mp4 --width 1920 --height 1080 --frames 72 --modes rife --interpolation-mode $mode --multiplier 4 --repeats 2
}
pwsh -File FancyTool/_tools/FfmpegSmokeTest/ValidatePipeline.ps1 -OutputDirectory .qa/optical-flow-repro/validation -DataDirectory .qa/video-pipeline/data
& $exe .qa/optical-flow-repro/ui --editing-ui --interpolation-ui
dotnet build FancyTool/FancyToolAva.csproj -c Release -r linux-x64 -p:PublishAot=false -p:SelfContained=true --disable-build-servers -v:q
```

`--modes rife` 在该测量入口表示只执行补帧、不做超分，实际算法由 `--interpolation-mode` 指定并记录在 JSON 的 `interpolation` 字段。该输入文件不随源码分发；从已有自然样本重建的命令见本地 `reproduce.ps1`。

完整 14 组回归全部通过（`verification.json`、`validation/validation.json`）：101 项 FFmpeg 矩阵无失败、2 项无设备的硬件编码跳过；57 项 HDR CUDA 检查、23 项补帧检查（含 NVIDIA 2×/4×/6× 真实转码）、CUDA/CPU 像素适配与队列集成均通过。真实 Avalonia 页面已验证异步探测后光流选项和倍率可选，截图为 `ui/interpolation-NvidiaOpticalFlow.png`，并通过原有批量转码、语言切换及宽/窄布局烟测。线性布局优化后另复测 23 项补帧及真实页面（`linear-validation.log`、`linear-ui.log`），验证 NVIDIA 三种倍率仍可用。其余未受设备选项影响的组未重复执行。Windows 构建 4 项既有警告、Linux 构建 2 项既有警告，均为 0 错误。Linux 目前完成交叉构建，未在 Linux GPU 上执行；AMD/VAAPI 缺少对应设备，不宣称设备验证通过。NVIDIA 光流保留现有 HDR 守卫：需要显式强制 SDR，保留 HDR 请使用 RIFE；改变算法不会自动改变输出策略。

依据：[FFmpeg FRUC 实现](https://github.com/FFmpeg/FFmpeg/blob/5cb9fcd211/libavfilter/vf_fruc_vulkan.c)、[minterpolate 实现](https://github.com/FFmpeg/FFmpeg/blob/5cb9fcd211/libavfilter/vf_minterpolate.c)、[segment API](https://ffmpeg.org/doxygen/trunk/group__lavfi.html)。
