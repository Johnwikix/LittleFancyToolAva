# RTX 5070 Ti：10 位 HDR 显存管线实测

日期：2026-10-17。设计见 [ADR 0012](adr/0012-hdr-cuda-video-pipeline.md)，此前 SDR 结果见 [原报告](video-cuda-pipeline-2026-10-17.md)。原始媒体、日志和采样均在本地 `.qa/video-hdr/`，不纳入 Git。

## 场景和计时

- i5-13600KF，14 核 / 20 线程；RTX 5070 Ti 16303 MiB，驱动 617.14，Windows 11 26300；.NET SDK 10.0.402、Windows ML 2.4.89、Avalonia 12.1.1。捆绑 FFmpeg 为 `n9.0.1-84-g946fcce07b-20260918`，ORT/NVIDIA EP 版本与前述 SDR 报告相同，没有更新模型权重或运行时。
- 合成 BGR 图案与音频经原色彩实现生成 1920×1080、24 fps、72 帧、3 秒的 HEVC Main10 / PQ / BT.2020 素材；不是自然电影样本。FSRCNNX M FP16 2×、RIFE 4.25 Lite 4×、384 窗口、4 个 SR 会话、先补帧后超分，输出 NVENC H.265 p4/Fast、QP 18。场景检测关闭，速度测试不打开预览。
- 每模式先运行一次，再重复三次，取重复中位数。每次计时包围真实 `TranscodeAsync`，包括初始化、LUT 上传、处理及收尾，排除输入生成和输出校验。进程内复用模型/曲线缓存，未清空驱动编译缓存；不是纯冷启动成绩。首次 LUT 烘焙使用 CPU，处理帧在 GPU 查表。
- 同一模型与精度，参考路径仅通过 `FANCYTOOL_VIDEO_NVDEC=0` 保留软件解码、CPU 色彩和主机帧交接，**推理仍在 GPU**。整卡遥测每 200 ms 采样，短片及后台桌面/模拟器会产生波动，SM 利用率不是判定性能的唯一依据。
- 原版 `4f18fc6`（历史清理后的等价生产源码）的独立基线仅支持 HDR 转码/超分，分别约 203.41/25.94 fps，数据在 `baseline/`。原版拒绝保留 HDR 的 RIFE，因此下表 RIFE/组合列是本轮新增功能的等价 CPU 色彩参考，不能称为旧版 HDR 补帧成绩。先前等价提交的隔离构建在 `repro-657/` 验证；非测试源码一致性见 `baseline-code-equivalence.json`。

## 正式速度

单位为**输出帧/秒**，转码和超分各 72 帧，补帧和组合各 288 帧。

| 场景 | 软件解码/CPU 色彩，GPU 推理 | 显存管线 | 吞吐倍率 |
|---|---:|---:|---:|
| 仅 HDR 转码 | 203.72 | 252.39 | 1.24× |
| FSRCNNX M FP16 2× | 26.05 | 105.30 | 4.04× |
| RIFE Lite 4× | 70.14 | 134.78 | 1.92× |
| RIFE Lite 4× → 超分 2× | 33.36 | 80.43 | 2.41× |

组合约合 20.11 源帧/秒，仍未达到 24→96 fps 的完整实时速度。数据见 [汇总](../../.qa/video-hdr/summary.json)、`reference/measurements.json`、`resident-final/measurements.json` 与各目录 GPU CSV。基线与新版没有并行测速。

组合最后一次记录为 72 次 CUDA 解码、72 次 HDR→SDR GPU 映射、288 次 P010 转换、0 次解码帧下载。该计数不包含兼容性暖机或初始化 LUT 上传，但这些成本计入端到端时间。预览另有缩小后的像素下载；音频、解复用、封装与控制仍使用 CPU。

## 画质和 HDR 证据

全部解码帧比较，按实际 10 位最大码值 1023 计算 PSNR，同时检查时间戳、帧数及色彩/位深一致；回归门槛为全局 PSNR ≥40 dB、最小单帧 SSIM ≥0.98。它是码值域回归检查，不等同于绝对亮度误差或主观 HDR 评价。

| 场景 | 帧数 | PSNR dB | 平均 SSIM | 最小单帧 SSIM |
|---|---:|---:|---:|---:|
| 仅 HDR 转码 | 72 | 一致 | 1.000000 | 1.000000 |
| FSRCNNX M FP16 2× | 72 | 43.94 | 0.986221 | 0.983888 |
| RIFE Lite 4× | 288 | 43.86 | 0.987516 | 0.985116 |
| RIFE Lite 4× → 超分 2× | 288 | 44.38 | 0.987337 | 0.984496 |

原始结果见 `quality-final/quality.json`、每帧 PSNR/SSIM 文本。另用 BT.2020 通道色条检查窄色域之外的边界：24 帧 HDR 超分 PSNR 50.27 dB、平均 SSIM 0.997393，数据在 `bars-quality/`；该组仅用于画质，不作为吞吐成绩。

- PQ/HLG 保留源传递函数，强制 HDR 输出 PQ；NVENC H.265/AV1 输出为 10 位。生成 HDR 的解码帧实际携带 MaxCLL=1000 与 mastering max=1000 尼特，SDR 输出不保留 HDR 静态信息。
- 1024 像素宽灰阶中，直接 PQ 转码保留 873 个亮度码值，HLG→PQ GPU 转换保留 651 个；这两条非神经路径没有先降至 8 位。原始证据在 `hdr-expanded/hdr-cuda.json`。
- 曾发现 LUT 分页主机复制返回后 DMA 未完成，导致 RIFE 首帧局部异常。改用固定页缓冲和同流上传/同步；暖机后交替 LUT 的 12 次 1080p 逐像素检查与完整视频复测通过。失败实验保留在 `quality.log`，修复对照在 `lut-sync-quality/`；早期 `resident/` 不用于正式结论。

## 验证与边界

最终 14 组状态见 [验证索引](../../.qa/video-hdr/verification.json)。包含原 GPU 适配器 168 项、CUDA 接口 90 项、1284 个量化边界、HDR 原烟测 11 项、HDR GPU 扩展 57 项、插帧 21 项、三组真实管线各 16 项、CPU SIMD/标量、FFmpeg 101 项（QSV/AMD 两项按设备能力跳过）及既有 SR 烟测。HDR 扩展覆盖六种超分模型、2×/4×、RIFE Lite/Full、两种顺序、2×/4×/6×补帧、非对齐行跨度、预览、元数据、取消重试与软解/混合回退。

首次插帧烟测错误要求 PreferNpu 在无 NPU 的 13600KF 上必须执行 NPU；已修正为验证既定 GPU 回退及原因，显式 CPU 仍单独验证，未改变生产后端策略。原失败记录保留在 `final-validation/`，补测在 `interpolation-final.log`。本机没有 NPU，不能据此宣称验证了 NPU 硬件。

**保留 HDR 输出不等于原生 HDR 神经处理。** 现有模型/接口仍在 SDR BGRA 字节域推理，映射回 HDR 使用原 1000 尼特目标；不能无损恢复源高光、宽色域或逐像素亮度。无需神经处理的同色彩 P010 转码则直接保留解码帧，正式样本的全部像素一致。

GPU 主路径限 Windows NVIDIA、8/10 位 4:2:0、PQ/HLG BT.2020 NCL、左置色度及已支持的滤镜组合；RAVU R2 DirectML、裁剪等保留混合路径。过小 HEVC 帧触及本机 NVDEC 尺寸限制时完整重试软件解码。其他 GPU、Linux 原生设备、自然电影 HDR 观感、HDR 显示器 UI、Dolby Vision/HDR10+动态元数据及其他硬解格式未在本轮完成设备验证。Windows/Linux 构建结果见 `windows-final-build.log`、`linux-final-build.log`。

## 复现

```powershell
$exe = 'FancyTool/_tools/FfmpegSmokeTest/bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe'
$env:FANCYTOOL_DATA_DIRECTORY = (Resolve-Path .qa/video-pipeline/data).Path
dotnet build FancyTool/_tools/FfmpegSmokeTest/FfmpegSmokeTest.csproj -c Release --no-restore --disable-build-servers -v:q
& $exe .qa/video-hdr/repro-fixture --pipeline-benchmark --hdr-fixture --hdr-output SameAsSource --codec H265 --width 1920 --height 1080 --frames 72 --modes plain --repeats 0
$inputFile = '.qa/video-hdr/repro-fixture/pipeline-hdr-source.mp4'
$env:FANCYTOOL_VIDEO_NVDEC = '0'
pwsh -File FancyTool/_tools/FfmpegSmokeTest/MeasurePipeline.ps1 -OutputDirectory .qa/video-hdr/repro-reference -DataDirectory .qa/video-pipeline/data -InputFile $inputFile -Codec H265 -HdrOutput SameAsSource -Multiplier 4 -Modes plain,sr,rife,both
Remove-Item Env:FANCYTOOL_VIDEO_NVDEC
pwsh -File FancyTool/_tools/FfmpegSmokeTest/MeasurePipeline.ps1 -OutputDirectory .qa/video-hdr/repro-resident -DataDirectory .qa/video-pipeline/data -InputFile $inputFile -Codec H265 -HdrOutput SameAsSource -Multiplier 4 -Modes plain,sr,rife,both
& $exe .qa/video-hdr/repro-quality --pipeline-quality --before .qa/video-hdr/repro-reference --after .qa/video-hdr/repro-resident --modes plain,sr,rife,both
pwsh -File FancyTool/_tools/FfmpegSmokeTest/ValidatePipeline.ps1 -OutputDirectory .qa/video-hdr/repro-validation -DataDirectory .qa/video-pipeline/data
# 原版仅支持 HDR 转码/超分；不要把原版不支持的 HDR RIFE 当作速度基线
pwsh -File FancyTool/_tools/FfmpegSmokeTest/MeasurePipeline.ps1 -OutputDirectory .qa/video-hdr/repro-original -DataDirectory .qa/video-pipeline/data -InputFile $inputFile -Codec H265 -HdrOutput SameAsSource -Multiplier 4 -Modes plain,sr -BaselineCommit 4f18fc6
```

模型与原生库沿用前述 SDR 报告的锁定版本；本次输入、CUDA 源码、PTX 及关键代码哈希见 `.qa/video-hdr/artifact-sha256.json`。Windows/Linux 构建日志、验证索引和失败实验均留在本地 `.qa/`，本次 Git 提交仅新增代码、简要变更记录与结论文档。2026-10-17 已按用户要求清理旧测试产物的本地及远端分支历史；原始证据与提交映射保存在 `.qa/git-history-cleanup-2026-10-17/`，复现基线已更新为清理后的等价提交。
