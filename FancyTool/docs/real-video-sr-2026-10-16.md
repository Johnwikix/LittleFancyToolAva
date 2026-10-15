# 720p / 1080p 实际视频超分对比

2026-10-16，本机 Release 实测。NanoVSR 已通过现有 Windows ML 使用 TensorRT RTX，
但在当前集成中比两个 SPAN 模型慢：720p→1440p 全流程 **5.48 fps**，1080p→4K
**2.75 fps**。原默认 ClearReality SPAN 分别为 **9.16 / 4.90 fps**。本次没有模型达到
24 fps。NanoVSR 的保真与时序指标在四个 AI 模型中较好，但相对普通 Lanczos 缩放
的收益较小，不适合据此替换默认模型或宣称达到 RTX Video VSR 的性能。

## 测试条件

- RTX 5070 Ti，16 GB，驱动 617.14；i5-13600KF；Windows 11 专业版 10.0.26300。
- .NET 10.0.402，Windows ML 2.4.89 / ORT 1.27.1。所有 AI 测试明确显示
  `GPU · Windows ML / NvTensorRTRTX`，禁止 CPU 回退。没有同时运行其他 GPU 测试。
- 使用真实应用 `VideoTranscodeService`，并保留 NanoVSR 的时序接口；不是独立 Python
  推理脚本。NanoVSR 采用 5 帧窗口、192×192 分块；旧模型采用 384×384 分块；均有
  32 像素上下文。NanoVSR、ClearReality、general 原生 4× 后平均至 2×；LiveAction 原生 2×。
- 片源为 [Blender Foundation《Tears of Steel》官方 4K MOV](https://download.blender.org/demo/movies/ToS/tearsofsteel_4k.mov.zip)，
  [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/)。截取原片 25–29 秒的人物、衣物、
  树木及砖墙，以及 43–47 秒的城市、字幕和全息图，拼接成 8 秒、24 fps、192 帧。
  电影本身包含实拍和 CGI；这是一部影片的短样本，不覆盖动画、体育或长时间温度稳定性。
- 母版画面为 3840×1714，在上方加 222、下方加 224 行黑边，得到 3840×2160。
  因此测速处理的是完整 16:9 栅格，但有效画面不是满幅 2160 行；其他满幅影片可能更慢。
- 用 Lanczos 从同一高分辨率来源生成 1280×720 / 1920×1080，H.264 CRF23。
  1440p 参考用 Lanczos 从母版生成并无损保存；4K 参考保留母版解码像素。
- 所有输出统一 H.264 NVENC、p4、constQP18、SDR 8bit、无音频，不改帧率。
  八个输出均验证为 192 帧及目标分辨率。
- 每组单独启动进程、串行执行，保留已有 TensorRT 磁盘缓存。每组一次；没有重复轮次
  或置信区间。全流程包括会话创建、首次推理、解码、像素转换、超分、编码及封装，
  不含模型安装、进程启动和探测文件。

## 速度与 GPU

“预热推理”排除第一次调用，计入 upscaler 内部的像素转换、分块、推理、CPU 回传和
拼接，不含调用外的解码及编码，不能直接视作长片转码速度。全流程 fps = 192 / 总秒数。

| 输入→输出 | 模型 | 全流程 fps | 预热推理 fps | GPU 平均 / P95 | 整卡峰值显存 MiB | 平均功耗 W |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 720p→1440p | NanoVSR 644K | 5.48 | 6.87 | 49.3% / 54% | 2465 | 194.6 |
| 720p→1440p | ClearReality SPAN（原默认） | 9.16 | 11.18 | 31.4% / 33% | 2447 | 131.0 |
| 720p→1440p | LiveAction SPAN | 9.16 | 11.07 | 57.0% / 60% | 2537 | 217.7 |
| 720p→1440p | RealESRGAN general x4v3 | 5.95 | 6.88 | 54.6% / 58.9% | 2385 | 220.7 |
| 1080p→4K | NanoVSR 644K | 2.75 | 3.06 | 49.1% / 55% | 2653 | 195.0 |
| 1080p→4K | ClearReality SPAN（原默认） | 4.90 | 5.63 | 31.5% / 35% | 2635 | 131.7 |
| 1080p→4K | LiveAction SPAN | 4.91 | 5.63 | 58.3% / 65% | 2725 | 223.2 |
| 1080p→4K | RealESRGAN general x4v3 | 3.06 | 3.36 | 53.6% / 59% | 2573 | 219.6 |

GPU 数据来自 `nvidia-smi` / NVML，200 ms 读取一次，仅统计首次推理完成到最后一次
推理完成的区间。busy 表示采样期间有内核工作的时间比例，**不是 Tensor Core 使用率、
SM occupancy 或理论算力达成率**；整卡显存包含桌面和其他常驻进程，不能当作模型独占显存。
驱动底层统计窗口也未必为 200 ms，各次读取并非独立样本。测试前整卡 busy 为 0%，
约 1698 MiB / 25.2 W。测试中温度最高 65°C，编码器平均占用仅 0.6%–2.5%。

NanoVSR 两组总耗时 35.07 / 69.90 秒，其中 upscaler 调用占 28.05 / 62.87 秒；
会话创建占 4.99 / 3.22 秒。当前主要时间仍在超分调用内部，而非编码器。仅凭这些数据
不能再把内部分成 GPU 运算、同步、传输或 CPU 处理的准确占比，也不能将低 busy 等同于
“有 50% 算力可直接补满”。

当前分块结构可解释一部分额外工作量：NanoVSR 的有效块为 128×128，720p 每个 5 帧窗口
需要 60 次推理、1080p 需要 135 次；旧模型有效块为 320×320，分别每帧 12 / 24 次。
NanoVSR 还计算原生 4× 后缩至 2×，并且窗口内有真实的双向时序传播。这是代码层面的
额外工作，不是已测得的各项瓶颈占比。此轮没有为提高 busy 而盲目并行会话或扩大分块。

此前已修复的提供程序名称大小写误匹配和 CPU 像素转换瓶颈见
[ADR 0007](adr/0007-nanovsr-and-tensorrt-rtx.md)。本报告测的是修复后的版本。

## 画质

比较的是最终编码视频，而不是无损模型输出。PSNR-Y 越高越接近参考亮度；SSIM-Y
越高越接近参考结构。先去掉黑边，再在有效画面四周排除约 4 像素，避免黑边稀释误差。
逐帧使用 FFmpeg PSNR/SSIM；PSNR 汇总以平均 MSE 计算，而非平均 dB。

| 模型 | 720p→1440p PSNR-Y / SSIM-Y | 1080p→4K PSNR-Y / SSIM-Y |
| --- | ---: | ---: |
| Lanczos 对照 | 33.341 dB / 0.92646 | 33.752 dB / 0.92684 |
| NanoVSR | 33.521 dB / 0.92487 | 33.860 dB / 0.92532 |
| ClearReality SPAN | 31.820 dB / 0.90677 | 32.381 dB / 0.91341 |
| LiveAction SPAN | 32.667 dB / 0.91919 | 33.011 dB / 0.92013 |
| RealESRGAN general | 31.218 dB / 0.90431 | 31.936 dB / 0.90982 |

抽查第 50 帧的人脸、发丝、衣物和第 145 帧的城市字幕：NanoVSR 比 Lanczos 略锐，
但没有恢复出母版的细密颗粒和发丝；两个 SPAN 与 general 的边缘强化更明显，部分皮肤、
背景纹理偏平滑，城市台阶和字幕边缘更硬。它们的更强锐化不等于更接近原片。
NanoVSR 相对 Lanczos 仅提高约 0.11–0.18 dB，SSIM 反而略低，不应据此声称所有画质更好。

![720p 人脸局部](data/real-video-sr-2026-10-16/720p-face.png)

![1080p 人脸局部](data/real-video-sr-2026-10-16/1080p-face.png)

![1080p 城市字幕与纹理](data/real-video-sr-2026-10-16/1080p-city-texture.png)

## 闪烁与切镜

直接比较相邻帧差异会把运动、原片颗粒和全息图本身的闪烁算入缺陷。这里先将有效画面
缩到 512×228 灰度，在参考帧上估计当前→上一帧的 Farneback 光流，再将上一帧的
重建误差 `输出−参考` 对齐，计算其与当前重建误差的 MAE。只保留映射在画面内、且
对齐后参考亮度误差小于 15 的像素，排除遮挡和不可靠运动；这是辅助指标，不是主观
闪烁评分。单位是 8bit 灰度值，越低表示相对参考的时间误差越小；模糊也可能降低此指标。

FFmpeg 在缩至 320 像素宽的参考视频上以 `scene > 0.05` 检测切镜，确认在第 96 帧
（4 秒，人为拼接）及第 159 帧（6.625 秒，原片切镜）。这两个转场不进入 MAE 统计。
空间画质分数仍包含所有 192 帧。边界比为 `第 n%5==0 帧的 MAE均值 / 其他转场均值`；
旧单帧模型使用同一分组作为内容变化对照。

| 模型 | 720p 平均时序 MAE / P95 | 1080p 平均时序 MAE / P95 | 720p / 1080p 五帧边界比 |
| --- | ---: | ---: | ---: |
| Lanczos | 0.948 / 1.256 | 0.855 / 1.047 | 1.013 / 1.012 |
| NanoVSR | 0.965 / 1.358 | 0.875 / 1.156 | 1.018 / 1.014 |
| ClearReality SPAN | 1.018 / 1.466 | 0.910 / 1.231 | 1.012 / 1.013 |
| LiveAction SPAN | 0.998 / 1.477 | 0.892 / 1.203 | 1.014 / 1.013 |
| RealESRGAN general | 1.028 / 1.496 | 0.916 / 1.228 | 1.013 / 1.013 |

NanoVSR 在四个 AI 模型中时间误差最低，但仍略高于 Lanczos。五帧交界的增幅接近对照组，
本片段没有显示明显的固定五帧周期异常。检查第 48–51 帧人脸连续裁图，以及两次切镜前后
四帧，未见明显的跨镜头轮廓残留或窗口切换时脸部突变。检查依据为连续帧、差异曲线和
同步对照视频的导出；没有完成长片实时主观观看，不能保证所有片源没有闪烁、重影或接缝。

![时序误差曲线](data/real-video-sr-2026-10-16/temporal-residuals.png)

![五帧边界连续裁图](data/real-video-sr-2026-10-16/1080p-window-boundary.png)

![人为切镜](data/real-video-sr-2026-10-16/1080p-scene-cut-96.png)

![原片自然切镜](data/real-video-sr-2026-10-16/1080p-scene-cut-159.png)

所有画面对照图均由上述 CC BY 3.0 片源裁剪、缩放、超分及编码而来，署名 Blender Foundation。
各格使用相同的解码帧编号与区域；不依赖 OpenCV 在无损 MKV 上不准确的随机定位。

## 复现与产物

工具位于 `_tools/RealVideoSrBenchmark`，依赖 PATH 上的 FFmpeg / ffprobe / nvidia-smi、
Python 3.11、requests、numpy、OpenCV、matplotlib。实测版本为 NumPy 1.26.4、
OpenCV 4.9.0、matplotlib 3.8.3；外部 FFmpeg 9.0.1-8，仅用于造样和分析。
应用转码使用其随包的进程内 FFmpeg 9.0.1-84。

```powershell
python FancyTool/_tools/RealVideoSrBenchmark/prepare_suite.py .qa/real-video-sr/test
dotnet build FancyTool/_tools/FfmpegSmokeTest -c Release
python FancyTool/_tools/RealVideoSrBenchmark/run_benchmarks.py .qa/real-video-sr/test `
  --runner FancyTool/_tools/FfmpegSmokeTest/bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe `
  --models-directory .qa/nanovsr/data/Models
python FancyTool/_tools/RealVideoSrBenchmark/analyze_quality.py .qa/real-video-sr/test
python FancyTool/_tools/RealVideoSrBenchmark/export_results.py .qa/real-video-sr/test `
  FancyTool/docs/data/real-video-sr-2026-10-16
```

模型目录须先由应用或烟测安装四个模型，并通过应用的“安装加速组件”安装 NVIDIA 提供程序。
`run_benchmarks.py` 会跳过已有 `result.json`，故重复测量应使用新目录并复制输入文件，
避免误把旧结果当成第二轮。造样脚本从官方 ZIP 通过 Range 读取所需的 MOV 数据，
不下载整个 6.7 GB 压缩包。不同 FFmpeg 版本的编码字节可能不同，输入 SHA-256 已归档。

小型原始结果与逐帧时间误差保存在
[measurements.json](data/real-video-sr-2026-10-16/measurements.json)，同目录包含整次 GPU
采样 CSV 与对照图。完整视频留在工作区 `.qa/real-video-sr/test`，不提交数百 MB 的片源：

- `runs/<720或1080>p-<模型>/output.mp4`：各模型的完整 8 秒输出。
- `720p-face-comparison-2x2.mp4`、`1080p-face-comparison-2x2.mp4`：4 秒同步人脸局部视频。
  左上参考、右上 NanoVSR、左下 ClearReality SPAN、右下 LiveAction SPAN。
- 每组 `result.json`、`phases.jsonl`、`psnr.log`、`ssim.log`、`quality.json`：原始结果。

此次重新执行 NanoVSR 16 项和原模型 6 项回归，均为 0 失败；Python 语法与 `git diff --check`
通过。原生发布、Linux 构建及 Avalonia UI 验证见 ADR 0007。本次没有更改默认模型、
新增推理并发或调整生产分块参数。
