# GPU 图片 / 视频复测与 SIMD 优化（2026-10-23）

本次在移除非商业图片模型后，重新测试四款保留的图片模型和现有视频 GPU 管线。透明图片的 FP16 回读和透明度合成改用 SIMD，输入张量改为有明确租借边界的池化缓冲；RIFE 上下文复用 RunOptions。没有新增依赖、模型或执行提供程序。

## 环境与测量边界

- Windows 11（10.0.26300）、Intel Core i5-13600KF（20 逻辑处理器）、RTX 5070 Ti（16 GB）、NVIDIA 驱动 617.14。
- .NET SDK 10.0.402、测试运行时 .NET 10.0.13、Windows ML 2.4.89；实际 ORT 原生版本 `1.27.20260711.1.7b7c0e2`。Release 烟测为托管应用，未测安装包与 Satori 自包含发布性能。
- 基线为 `f6295a31d344f30fe099dd3e98a91dff19cf1168` 对应的生产服务；开始测试时另存全部 Services 源码、工作区差异、环境与模型 SHA-256。基线和候选使用相同烟测入口、独立二进制目录及同一模型目录。
- 图片：确定性 640×360 BGRA，原生 4 倍模型请求 2 倍输出，平衡档（384 窗口、320 有效分块）。分别测试不透明和预乘透明度输入。每模型先执行一次，再重复 3 次取中位数，计时包含分块、张量转换、GPU 推理、回读、合成和输出 bitmap，排除输入生成、PNG 编码、落盘与比较。
- 视频：同一段 1920×1080 / 24 fps / 72 帧 H.264 High 测试片，包含音频；FSRCNNX M FP16、2 倍超分、RIFE 4.25 Lite / 4 倍帧率、NVIDIA H.264 输出。组合顺序为插帧后超分。每模式首次后重复 3 次，计时为完整 TranscodeAsync。fps 均指**输出帧率**；组合为 3840×2160 / 288 帧，约 92 输出 fps 对应约 23 输入 fps。
- 单独启动进程做交替复测，GPU 性能测试之间串行执行。初次进程执行保留驱动和 EP 缓存，不能解释为清空缓存后的冷启动。
- 托管分配通过 GC.GetTotalAllocatedBytes 记录，包含同时运行的本进程后台工作，不包含 Skia、ORT 和显存。GC 计数不能推导 STW 时长、原生内存峰值或“零分配”。

原始证据均在仓库根目录 `.qa/gpu-inference-2026-10-23/`，包括 `environment.json`、`cpu.json`、`gpu.txt`、`model-sha256.json`、`baseline-source/`、`candidate-source/`、源码校验值、`baseline-bin/`、`candidate-bin/`、各次 `measurements.json` 和日志。验证汇总为 `verification-summary.json`。它们已忽略，不随源码分发。

## 图片结果

下表采用交替复测后的基线 `baseline-image-alpha-repeat/`，对照终版 `candidate-image-alpha-final/`；每格耗时为 3 次重复的中位数。

| 透明图片模型 | 基线 ms | SIMD / 池化 ms | 耗时减少 | 平均托管分配：基线 → 优化后 |
| --- | ---: | ---: | ---: | ---: |
| RealESRGAN_x4plus | 451.260 | 374.809 | 16.9% | 3,552,392 → 11,064 B |
| RealESRGAN_x4plus_anime | 317.794 | 221.836 | 30.2% | 3,551,760 → 11,064 B |
| realesr-general-x4v3 | 252.445 | 180.851 | 28.4% | 3,552,776 → 12,040 B |
| ClearReality V1 | 225.064 | 160.657 | 28.6% | 3,551,784 → 10,808 B |

以上四款均实际报告 `GPU · Windows ML / NvTensorRTRTX`，未回退 CPU。平均托管分配约减少 99.7%，但原生 bitmap 与显存仍有占用。前次候选采样为约 402 / 227 / 183 / 161 ms，终版再次运行得到上表，说明耗时本身存在波动。第一次基线中的 general 模型约 372 ms，交替复测后约 252 ms，因此不用首次值夸大提速，也不承诺其他素材的固定收益。

不透明图片沿用已有固定输入输出张量路径，本次没有改其算法。四款模型本次约为 251 / 91 / 46 / 27 ms，前后差异约 -5%～+3%，不认定为稳定提速或退化。详见 `image-comparison.json`；其中不透明基线复跑期间曾有构建活动，因此这些数字仅作为路径回归和波动记录，不作为严格性能结论。

正确性对照共 12 幅：四模型 ×（640×360 不透明 2 倍、透明 2 倍、641×363 透明 4 倍）。最终 BGRA 均与基线逐字节一致，覆盖透明边缘、非整块尺寸和部分尾块；同一输入重复结果一致。四模型在奇数尺寸 4 倍路径上均通过分块推理期间取消和随后重试，验证池化缓冲不会污染下一次结果。详见 `image-comparison.json`、`image-odd-comparison.json`、`candidate-image-odd-4x.log`。

## 优化依据与实现

`--trace` 将图片分解为输入准备、原生 Run、回读、透明度处理和最终缩放。ClearReality 透明图片的一次优化后采样为：4 块输入准备 3.841 ms、原生 Run 18.392 ms、回读 10.845 ms、透明度缩放与合成 103.128 ms、最终缩放 22.268 ms，总推理路径 163.459 ms。原生 Run 包含执行与传输，不是独立 GPU 内核计时。诊断 CSV 的 Wall 还包含计时外的输出校验/PNG，不能当作图片推理耗时。

据此保留以下改动：

1. `ImageTensorPixels` 逐行用 TensorPrimitives 转换 Half，AVX2 每次量化 8 个像素，SSE2 每次 4 个，剩余部分标量处理。保留原有 clamp 后截断为字节的规则，没有改成四舍五入；处理不同 RowBytes。
2. 透明度预乘以整数 SIMD 处理。对 0～65025 的乘积使用精确的除以 255 变换，保持原来的向下取整；Alpha 通道也保持原值。
3. 输入租借 Half / float 数组，仅向 ORT 暴露实际有效长度；同步原生执行和输出读取结束、OrtValue 释放后，才解除固定并归还缓冲。异常、取消同样归还；输出转换异常时释放已创建的 bitmap。
4. GPU RIFE 在独占上下文中复用 RunOptions，每轮释放旧取消注册后，下轮重置 Terminate，保持 GPU 完成同步和取消后的安全复用。

像素契约测试实际调用生产实现，分别在 AVX2、禁用 AVX2 的 SSE2、禁用硬件指令的标量配置运行，每种 15,450,672 项比较全部通过。覆盖全部 65,536 种 Half 位模式、浮点字节阈值及相邻值、NaN/无穷、1/2/3 通道、向量尾部、行填充和全部 256×256 颜色/透明度组合。证据为 `pixels-avx2.log`、`pixels-sse2.log`、`pixels-scalar.log`。

另测了 Alpha8 缩放：只改目标格式收益很小；先抽取 Alpha8 源再高质量缩放，在 384 窗口内部样本反而从约 40 ms 增至 52 ms，因此没有纳入生产代码。实验源码和日志保留为 `ImageAlphaResizeProbe.cs`、`alpha-resize-probe.log`、`alpha-resize-probe-source.log`。保持原有 Skia 高质量滤镜，未以降画质换速度。

## 视频结果与 GPU 工作范围

| 模式 | 首轮基线输出 fps | 首轮候选输出 fps | 交替复测基线输出 fps | 交替复测候选输出 fps |
| --- | ---: | ---: | ---: | ---: |
| 2 倍超分 | 110.89 | 114.69 | 117.64 | 114.77 |
| 4 倍 RIFE 插帧 | 127.83 | 135.83 | 134.26 | 135.23 |
| 插帧后超分 | 83.07 | 90.14 | 89.78 | 92.23 |

未改动的纯超分也有相近波动，所以**本次不认定视频获得稳定 fps 提升**。RunOptions 复用减少每次 RIFE 创建执行对象；完整管线的后台分配有波动，不用其微小差值承诺固定内存收益。

三种模式的 SR / RIFE 实际使用 Windows ML / TensorRT RTX，像素 GPU 标记为 true。交替复测的纯 RIFE 第 3 次中，72 帧使用 CUDA 解码，DecodeDownload 为 0；213 次插值的 RifeNetwork 合计 1952.898 ms，占该阶段墙钟 2026.460 ms 的约 96%。因此该场景主要受模型执行和 GPU 同步支配，继续优化 CPU 像素循环的收益有限。

现有 NVIDIA 路径已使用 NVDEC → CUDA 像素处理 / SR / RIFE → NVENC。输入输出文件、复用调度、部分预览和不支持格式的软解回退仍需要 CPU。图片透明路径也仍保留 CPU 合成与最终回读。把更多计算搬入 GPU，需要让中间结果留在显存、减少传输并保持原有滤镜与量化语义；GPU 利用率达到 100% 本身不是吞吐或延迟改善的证据。本次没有放宽硬件回退守卫、去掉必要同步或增加并发 CUDA Graph。

三种视频的最终一轮输出已全部解码比较：超分 72 帧、插帧 288 帧、组合 288 帧，所有像素哈希与时间戳逐帧一致；同时通过输出尺寸、帧数、音轨存在和时间单调检查。证据见 `video-quality.log`、`video-quality/decoded-comparison.json`、`video-comparison.json`。

真实 GPU 回归还包括：

- 六类视频超分的全部 10 个 FP16 / FP32 图，以及两款 RIFE，适配器对照共 170 项通过。除 RAVU r3 使用既有 CPU 参考容差（最大差异 ≤5、平均 ≤0.255 字节）外，其他原始图与适配器均逐字节一致；实际生产候选都使用 GPU。两款 RIFE 的 1080p 取消后，同一上下文重试均最大像素差异为 0。证据为 `video-adapters-validated/gpu-adapters.json`。
- CUDA 显存管线 16 项通过，包含双处理顺序、2/4/6 倍插帧、尾帧、裁剪、预览、统计、音轨、GIF、两通编码、行跨度、故障/取消清理、会话复用和重复帧时间戳。证据为 `video-cuda-integration.log`。
- 强制使用不支持 NVDEC 的 H.264 4:4:4 输入，再次运行同一集成矩阵，16 项通过，验证真实软件解码回退。预期的硬件格式协商失败日志保留于 `video-cuda-fallback.log`。

RIFE 取消用例持续执行真实 GPU 调用直到取消送达，不假设单次调用必然慢于系统 2 ms 定时器。最初“一次调用后立即判定”的测试曾因调用先完成而误报，已修正测试时序；该诊断日志仍保留在 `video-adapters-final.log`。

## 复现

在仓库根目录执行，先按项目要求安装 FFmpeg shared 库并在隔离目录准备固定版本模型；图片入口会通过真实下载服务校验缓存，并拒绝临时联网下载。视频模型与双 RIFE 模型的版本/哈希见 `model-sha256.json`。

```powershell
$gpuRoot = Join-Path (Get-Location) '.qa/gpu-inference-2026-10-23'
$env:NUGET_SCRATCH = Join-Path $gpuRoot 'nuget-scratch'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $gpuRoot 'http-cache'
dotnet build FancyTool/_tools/FfmpegSmokeTest -c Release --no-restore -m:1 -p:BuildInParallel=false "-p:BaseOutputPath=$gpuRoot/candidate-bin/"
$runner = Join-Path $gpuRoot 'candidate-bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe'
$env:FANCYTOOL_DATA_DIRECTORY = Join-Path $gpuRoot 'data'

& $runner "$gpuRoot/retest-alpha" --image-gpu --alpha --repeats 3
& $runner "$gpuRoot/retest-opaque" --image-gpu --repeats 3
& $runner "$gpuRoot/retest-odd" --image-gpu --alpha --width 641 --height 363 --scale 4 --repeats 1 --cancel-in-flight
& $runner "$gpuRoot/retest-phases" --image-gpu --alpha --image-model SpanClearRealityV1 --trace --repeats 3

& $runner "$gpuRoot/retest-pixels" --image-pixels-only
$env:COMPlus_EnableAVX2 = '0'
& $runner "$gpuRoot/retest-pixels-sse2" --image-pixels-only
$env:COMPlus_EnableHWIntrinsic = '0'
& $runner "$gpuRoot/retest-pixels-scalar" --image-pixels-only
Remove-Item Env:COMPlus_EnableAVX2, Env:COMPlus_EnableHWIntrinsic

# 首次生成固定 H.264 测试片；后续基准均使用这一个输入文件。
& $runner "$gpuRoot/fixture" --pipeline-benchmark --width 1920 --height 1080 --frames 72 --modes plain --repeats 0 --profile high
& $runner "$gpuRoot/retest-video" --pipeline-benchmark --input "$gpuRoot/fixture/plain-0/output.mp4" --width 1920 --height 1080 --frames 72 --multiplier 4 --modes sr,rife,both --repeats 3 --profile high
& $runner "$gpuRoot/retest-quality" --pipeline-compare --before "$gpuRoot/baseline-video-encoded-repeat" --after "$gpuRoot/candidate-video-encoded-repeat"
& $runner "$gpuRoot/retest-adapters" --pipeline-adapters
& $runner "$gpuRoot/retest-integration" --pipeline-cuda-integration
& $runner "$gpuRoot/retest-fallback" --pipeline-cuda-fallback
```

基线二进制、对应服务源码及已有输入均保存在上述证据目录；不要以候选重新构建并覆盖基线二进制。图片计时入口 `ImageGpuBenchmark.cs`、像素契约入口 `ImagePixelTests.cs` 位于 `_tools/FfmpegSmokeTest/`。正式基准使用硬件指令默认配置，三个 SIMD 配置测试各在独立进程运行。

## 已知限制

- 本机无 NPU，未验证 NPU 性能；未验证 AMD / Intel GPU、Linux 实机、安装包内 EP 安装和完整桌面交互性能。
- 本次使用合成图和短测试片，不能据此判断真实电影、字幕、人像、透明素材的主观画质或长时间稳定吞吐。原有模型、分块策略和滤镜未更换。
- 没有采集 GC 暂停 trace，不给出 STW 或零分配结论；原生峰值内存不能由托管分配数字替代。
- 完整商业许可待办仍以 [商业许可核查报告](commercial-license-audit-2026-10-23.md) 为准；本次性能验证不代表依赖授权事项已经解决。
