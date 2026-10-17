# RTX 5070 Ti 视频超分与补帧管线实测

日期：2026-10-16 至 2026-10-17。架构决策见 [ADR 0010](adr/0010-bounded-video-processing-pipeline.md)。

原管线存在明显的 CPU 供料、像素转换与整帧复制空隙。修复后，在相同模型、精度、倍率、处理顺序和编码设置下，主要场景的端到端吞吐提升见下表。最终输出与原版完整解码后逐帧比较，像素和时间戳全部一致。RAVU R3 的特殊数值验证单独说明，不能混入逐字节一致的结论。

## 环境与测量边界

- Intel i5-13600KF，14 核 / 20 逻辑处理器；RTX 5070 Ti，16303 MiB，驱动 617.14；Windows 11 专业版 10.0.26300。
- .NET SDK 10.0.402，Windows ML 2.4.89，Avalonia 12.1.1；随仓库分发的 FFmpeg `n9.0.1-84-g946fcce07b-20260918` GPL shared。
- 已加载 ORT 原生运行时 `1.27.20260711.1.7b7c0e2`；NVIDIA EP 框架 `2.30.49.0`，提供程序 DLL `0.4.0`，TensorRT RTX `1.6.1.120`。见 [锁定版本](data/video-pipeline-2026-10-17/provider-versions.json)和 [模型哈希](data/video-pipeline-2026-10-17/model-sha256.json)。
- 基线提交 `8fe63616fbe66426389ada61ef61b2c132965142`。基准脚本只在隔离构建中替换八个原始生产文件，并插入整段墙钟计时；未改写工作区或复制业务逻辑自证。
- 1920×1080、24 fps、72 帧、3 秒的确定性 BGR24 AVI + PCM。FSRCNNX M FP16、超分 2×、RIFE 4.25 Lite 补帧 4×，NVENC H.264 p4 / Fast、QP 18、AAC，场景检测关闭，384 推理窗口。仅超分输出 72 帧；补帧和组合输出 288 帧。AVI 场景输出为 yuv444p，另测 H.264 yuv420p 输入输出。
- 每个独立进程先运行一次，再重复三次，表格取重复运行中位数。首次运行仍可能命中已安装 EP 的编译缓存，不等于清空缓存的冷启动。
- 端到端计时包围真实 `VideoTranscodeService.TranscodeAsync`，含会话租用、滤镜/编码器初始化、收尾；不含测试输入生成和事后逐帧校验。管线计时从上下文准备完成后开始，包含处理与收尾。
- `nvidia-smi` 每 200 ms 采样，按管线实际活动窗口截取，再对三次均值取中位数。它是整张设备的利用率，包含系统桌面等其他活动；短于一秒的 SR 窗口样本很少，不能当成内核占用率。
- 所有模型与缓存均使用隔离的 `.qa/video-pipeline/data`。进程峰值工作集包含约 1 GiB 的测试视频生成开销及原生缓存，不能用作显存需求或生产峰值结论。

## 主场景结果

单位为**输出帧/秒**。不同倍率的 fps 不能直接当作源视频处理速度比较。

| 场景 | 原版端到端 fps | 优化后 fps | 吞吐倍率 | 管线 fps：原版 → 优化 | GPU 活动窗口均值 |
|---|---:|---:|---:|---:|---:|
| 仅 FSRCNNX M 2× | 13.06 | 74.28 | 5.69× | 13.90 → 101.29 | 9.6% → 39.5% |
| 仅 RIFE Lite 4× | 32.84 | 101.41 | 3.09× | 42.56 → 107.50 | 36.2% → 66.3% |
| 先 RIFE Lite 4×，后超分 2× | 9.42 | 60.64 | 6.44× | 10.13 → 65.04 | 20.9% → 83.2% |

[完整测量与阶段耗时](data/video-pipeline-2026-10-17/main/measurements.json)；[原版原始记录](data/video-pipeline-2026-10-17/main/baseline/measurements.json)；[优化版原始记录](data/video-pipeline-2026-10-17/main/candidate/measurements.json)。同目录保留每次 CSV、GPU 采样和日志。完整质量比较保留在验证目录。

组合的 60.64 fps 相当于约 15.16 源帧/秒；24→96 fps 的视频仍未达到实时生成速度。不得把吞吐提升解释为所有场景都已满载或实时。

## GPU 空闲来源与修复

原实现中，解码、RIFE、SR、滤镜、编码、封装逐项等待。SR 每个块还要等待 CPU 准备三平面浮点张量、FP16 转换、GPU 同步和 CPU 拼接；RIFE 在 CPU 上构建七平面张量并将三平面浮点输出读回。原始 RIFE 本身已经运行在 GPU，低利用率并不证明模型在 CPU 上推理。

当前实现将各个视频阶段解耦，队列有背压且传递原生引用；复用可写 FFmpeg 帧，减少整帧分配和无用复制；NVIDIA SR 使用 GPU 像素图与独立分块会话，RIFE 三个 GPU 阶段通过 GPU 张量交换中间结果。固定输入输出和会话缓存进一步减少了每次初始化与内存管理成本。NVIDIA EP 的 `HOST_ACCESSIBLE` 分配器选择性用于 SR 输入输出主机缓冲；SR 单块两轮交替对照约 360→290 ms / 1000 次，完整管线仅超分约 61→70 fps。后端不支持该内存时保留原缓冲路径，未新增 CUDA 运行库。成对复核显示 RAVU R3 使用普通主机缓冲更快；RIFE 保留普通主机缓冲，避免把单块传输收益推广到复用输入的网络阶段。

RIFE 的通道转换、归一化、时间平面展开、网络推理、量化与打包现在均在 GPU。CPU 仍处理视频解码、原帧及最终字节复制、场景切换检测、输出时间戳、音频、滤镜、队列和编码提交。输出仍需交给现有的编码路径。NVENC 使用独立编码引擎，计算利用率 100% 不是这条链路的必要条件。

超分还逐字节检查上一帧输入，只对已知无状态模型复用完全相同像素的结果，逐帧更新时钟。快速采样只用于排除变化帧，命中必须完整比较；仅保留一个源帧和结果的引用。重复片段的组合从 78.71→110.70 fps，这是优化后管线“关闭/开启超分复用”的对照，不是原版串行管线对照。完整解码为零差异，见 [消融记录](data/video-pipeline-2026-10-17/repeated-frames/measurements.json)。

## 其他模型、倍率与顺序

640×360、6 帧的完整矩阵覆盖六种 SR 模型、可用的 FP16/FP32、RIFE Lite/Full、2×/4× SR、2×/4×/6× 补帧、两种执行顺序和 CPU 运动补偿。共 31 组配置，29 组可运行、合计 79 种模式。原版可运行的 73 种模式全部通过完整解码像素与时间戳对照。

NVIDIA/AMD 光流的两组配置为环境不支持：此 FFmpeg 缺少相应插帧滤镜，本机也没有 AMD 设备；原版和新版均不能运行，未伪装成优化成功。RAVU R3 的六种模式在原版 NVIDIA EP 编译失败，修复后可运行，单独由原始 CPU 图按既有容差验证。

额外 1080p 对照使用相同 NVENC 设置、24 源帧、先一次再重复三次，输出为 yuv444p。下表记录统一启用 EP 主机缓冲时的阶段，最终主配置与 RAVU R3 的收窄策略另列；不能把不同采样长度的数值直接与主表比较。

| 超分 / 补帧模型 | 仅超分 fps | 先补帧后超分 fps | 先超分后补帧 fps |
|---|---:|---:|---:|
| Anime4K S / RIFE Lite | 13.17 → 46.50 | 9.08 → 59.06 | 8.11 → 27.12 |
| Anime4K M / RIFE Lite | 11.68 → 44.30 | 7.60 → 52.23 | 6.96 → 26.95 |
| FSRCNNX S / RIFE Lite | 11.97 → 46.42 | 8.14 → 56.36 | 7.25 → 27.00 |
| FSRCNNX M / RIFE Full | — | 9.36 → 53.90 | 7.77 → 23.85 |
| RAVU R3 FP32 / RIFE Lite | 新增可用：34.74 | 新增可用：40.03 | 新增可用：26.42 |
| RAVU R2 FP32 / RIFE Lite | 8.51 → 23.82 | 6.11 → 25.78 | — |

RAVU R2 的 DirectML 路径：仅超分 8.51→23.62 fps，组合 6.11→25.18 fps；双会话与四会话已接近，默认选双会话。RIFE Full 单独 1080p 的对应对照为 24.01→85.19 fps。详见 [额外 1080p 数据](data/video-pipeline-2026-10-17/models1080/measurements.json)、[矩阵](data/video-pipeline-2026-10-17/matrix/matrix.json)与 [DirectML 并发消融](data/video-pipeline-2026-10-17/experiments/ablations/measurements.json)。

## 收益平台与否决实验

以下均保留实际记录。只比较吞吐时，不能忽略数值一致性、取消正确性和内存代价。

| 实验 | 仅超分 fps | 仅补帧 fps | 组合 fps |
|---|---:|---:|---:|
| workers-1 | 43.08 | 93.63 | 44.55 |
| workers-2 | 60.00 | 97.12 | 53.68 |
| workers-4 | 61.44 | 97.55 | 59.39 |
| workers-8 | 62.00 | 98.61 | 59.01 |
| tile-0 | 31.50 | — | 29.59 |
| tile-1 | 42.61 | — | 50.18 |
| tile-2 | 52.79 | — | 52.09 |
| tile-3 | 62.51 | — | 56.75 |
| ort-thread1 | 56.96 | 98.09 | 52.58 |
| rife-workers-1 | — | 96.77 | 56.36 |
| rife-workers-2 | — | 94.35 | 51.94 |
| rife-workers-4 | — | 90.36 | 48.18 |

这些实验在相应实现阶段依次执行，并非同一时刻的成对测试；不能把相邻小幅差异当成稳定收益。单进程各场景先一次再重复三次；SR 并发 4/8 已出现平台，RIFE 2/4 会话整体下降，因此最终保留四个 SR 上下文和一个 RIFE 网络。原始 CSV、遥测、版本与快照见 [消融证据](data/video-pipeline-2026-10-17/experiments/ablations/measurements.json)。

H.264 420 输入也进行了同样三次重复：原版/最终版仅超分为 14.47/64.94 fps、仅补帧 33.39/97.91 fps、组合 10.33/60.16 fps。该场景完整解码对照为零差异，见 [压缩输入原始数据](data/video-pipeline-2026-10-17/compressed420/measurements.json)。EP 主机内存之前的 384/576 对照另存于消融记录。

最终 EP 主机内存路径复核：四/八会话仅超分为 72.27/72.43 fps、组合 59.08/58.30 fps；576 窗口仅超分 69.20、组合 58.96 fps，没有稳定超过现有 384 窗口。因此保留现有块大小与四会话策略。

两个捕获 CUDA Graph 的 NVIDIA 会话直接并发曾导致黑块。最小复现移除 AVFrame 和视频队列，使用独立会话、固定地址、自有输入，480 次调用中仍出现不一致；显式流和同步也没有修复。关闭 Graph 的相同并发对照为零差异。因此采用关闭捕获的安全并发与保留捕获路径的互斥保护。此结论只针对本机版本组合，见 [ORT 的配置说明](https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider.html)与原生并发实验记录。早期错误方案的数据单独保留并明确标为无效，不能作为性能收益。

RIFE 全图融合会改变极少量 FP32 舍入结果，因此最终保留原始网络会话边界；更大 batch 的早期模型实验同样改变部分字节，未交付。GPU 量化单独用临界半整数、NaN、无穷等输入验证。

这里的停止条件是：在当前模型、精度、处理语义、Windows ML 和编码接口下，已试的并发数、块大小、线程设置与批处理方案没有进一步稳定收益，或不能保持结果一致。不能证明“所有未来架构都没有优化空间”。当前没有 NVDEC→GPU 色彩转换→SR/RIFE→GPU NVENC 的全链路共享设备内存，也没有 GPU 内核时间线证据；这两点不能用队列计时或设备利用率冒充已完成。

追加的 GPU 原生调用对照中，四个 SR 会话执行 1600 次，默认流两轮约 568/570 ms，独立流约 602/599 ms，均零差异；独立流未增加吞吐。降低 FFmpeg 滤镜线程数也下降：1/2/4 线程组合约 26.18/41.81/53.83 fps，保留自动线程调度。没有把单项微基准替代完整管线收益。

RIFE 采样缓存原型虽然通过数值验证，真实视频路径已有 `SamePixels` 跳过相同帧对，重复片段的 RIFE 推理仅六次。新增采样缓存不再减少这些调用，已撤回，避免额外内存与状态；保留的是下游 SR 结果复用。

## 回归与限制

- 直接 GPU 像素对照 168 项：164 项逐字节一致；RAVU R3 四项使用 CPU 参考，最大像素差不超过既有测试容差。包括重复调用和采样网格之外的单像素变化。
- GPU 量化 1284 个边界/特殊值，零差异；CPU AVX2、SSE2、标量三条路径均通过原始算法参考。
- 16 项真实 GPU/FFmpeg 集成检查：Auto 和两种顺序、2/4/6 倍、音频和时钟、裁剪、单帧尾部、奇数对齐步长、GIF、两通、场景切换、VFR、生产者/编码回调失败、启动/在途取消、取消后复用，以及重复帧的像素、PTS 和推理次数。
- 进程内 FFmpeg 矩阵 101 项，零失败；Intel QSV、AMD AMF 两项按环境跳过。另有 FSRCNNX M 两项完整 GPU 烟测。
- Windows Release 与 Linux 交叉编译通过；Linux 保留原有 Satori 重复 PackageReference 两条警告，未升级 SDK。

原始日志和命令见 [验证记录](data/video-pipeline-2026-10-17/validation/)。

本轮未进行真实桌面长时交互验证，预览验证通过真实服务回调检查尺寸、最终帧和时钟；Linux 只进行编译验证，未声称其他设备、驱动和 EP 版本获得相同收益。

## 复现

在仓库根目录运行；模型必须使用目录中锁定的 SHA256，不能把相同名字的其他权重替换进去。

```powershell
$measure = 'FancyTool/_tools/FfmpegSmokeTest/MeasurePipeline.ps1'
& $measure -OutputDirectory .qa/video-pipeline/repro-baseline -DataDirectory .qa/video-pipeline/data -BaselineCommit 8fe63616fbe66426389ada61ef61b2c132965142
& $measure -OutputDirectory .qa/video-pipeline/repro-candidate -DataDirectory .qa/video-pipeline/data
& 'G:/Program Files/python/python.exe' FancyTool/_tools/FfmpegSmokeTest/SummarizePipeline.py --baseline .qa/video-pipeline/repro-baseline --candidate .qa/video-pipeline/repro-candidate --output .qa/video-pipeline/repro-summary

$binary = 'FancyTool/_tools/FfmpegSmokeTest/bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe'
& $binary .qa/video-pipeline/repro-compare --pipeline-compare --before .qa/video-pipeline/repro-baseline --after .qa/video-pipeline/repro-candidate --modes sr,rife,both
& FancyTool/_tools/FfmpegSmokeTest/ValidatePipeline.ps1 -OutputDirectory .qa/video-pipeline/repro-validation -DataDirectory .qa/video-pipeline/data

# 并发数消融：分别设置 1、2、4、8，其他参数不变。
$env:FANCYTOOL_VIDEO_SR_WORKERS = '4'
& $measure -OutputDirectory .qa/video-pipeline/repro-workers4 -DataDirectory .qa/video-pipeline/data -SkipBuild
Remove-Item Env:FANCYTOOL_VIDEO_SR_WORKERS
```

更多压缩输入、模型矩阵、负向实验和隔离快照命令见证据目录的 README；嵌入模板可用 `ExportVideoPixelAdapters.py` 重生成，开发环境锁定 onnx 1.17.0。原始模型来源与许可证沿用 [VideoSr NOTICE](../Libraries/VideoSr/NOTICE.md)及服务中的 RIFE 固定版本清单。

## 最终主机内存范围复核

72 源帧、三次重复的成对测试中，RAVU R3 关闭 EP 主机缓冲为仅超分 50.96 fps、组合 50.13 fps；仅对 SR 启用则为 46.52/43.23 fps。因此该模型使用普通缓冲。FSRCNNX M 保留 SR 的 EP 主机缓冲、RIFE 使用普通缓冲时，单独 RIFE 约 101.45 fps、组合约 60.3 fps；RIFE Full 对应单独/组合/反序为 84.84/53.23/23.90 fps，与启用 EP 主机缓冲接近。最终选择简单且实测更稳定的范围。原始配对记录与隔离变体同存消融目录；此处不把不同帧数的结果混成同一个收益倍率。
