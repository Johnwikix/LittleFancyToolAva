# RTX 5070 Ti：NVDEC 到 NVENC 的显存管线实测

> 后续 HDR 实现与测试见 [HDR 显存管线报告](video-hdr-cuda-2026-10-17.md)；本报告的数字仍仅代表当时的 SDR 场景。

日期：2026-10-17。设计见 [ADR 0011](adr/0011-gpu-resident-video-frames.md)，上一轮 CPU 帧边界优化见 [原报告](video-pipeline-2026-10-17.md)。原始结果统一放本地 `.qa/video-cuda/`，不纳入 Git。

## 环境与测量边界

- i5-13600KF，14 核 / 20 线程；RTX 5070 Ti，16303 MiB，驱动 617.14；Windows 11 26300；.NET SDK 10.0.402，Windows ML 2.4.89，Avalonia 12.1.1。
- 捆绑 FFmpeg `n9.0.1-84-g946fcce07b-20260918`；ORT 原生运行时 `1.27.20260711.1.7b7c0e2`，NVIDIA EP Framework `2.30.49.0`、提供程序 `0.4.0`、TensorRT RTX `1.6.1.120`。原始模型未修改，哈希保留在 `.qa/video-pipeline/git-evidence-previous/model-sha256.json`。
- 同一 H.264 yuv420p + AAC 输入，1920×1080、24 fps、72 帧、3 秒；FSRCNNX M FP16 2×、RIFE 4.25 Lite 4×，先补帧后超分，NVENC H.264 p4/Fast、QP 18，场景检测关闭，384 推理窗口。
- 基线为上轮生产代码 `5ff27272dfa99d110fa9aaf42297823bedae2412`（原 `8d92501` 移除历史测试产物后的等价源码），通过隔离源码覆盖构建，未在基线启用本轮 NVDEC/CUDA 路径。正式前后对照在用户停止 MAA 后重新执行；更早 MuMu/MAA 高负载下的数据仅作实验记录。
- 每种模式先运行一次，再重复三次，取重复中位数。计时包围真实 `TranscodeAsync`，含初始化、处理、收尾，不含输入生成和输出验证。已有模型/驱动编译缓存未清空；不把首次运行称为纯冷启动。
- `nvidia-smi` 每 200 ms 采样。遥测属于整张设备；NVDEC、NVENC 与计算引擎不同，SM 利用率不是唯一速度指标。后台桌面/模拟器仍可能影响短片测速，结论只适用于本机本场景。

## 正式结果

单位是**输出帧/秒**；SR 输出 72 帧，补帧及组合输出 288 帧。

| 场景 | 上轮基线 fps | 显存管线 fps | 吞吐倍率 | 处理阶段 fps：基线 → 新版 |
|---|---:|---:|---:|---:|
| 仅 FSRCNNX M 2× | 55.00 | 113.98 | 2.07× | 70.37 → 143.26 |
| 仅 RIFE Lite 4× | 83.15 | 136.66 | 1.64× | 88.20 → 144.27 |
| 先 RIFE Lite 4×，后超分 2× | 53.33 | 84.12 | 1.58× | 56.55 → 88.01 |

组合输出 84.12 fps 约合 21.03 源帧/秒；源视频 24→96 fps，尚未达到该组合的完整实时速度。没有将结果解释为 GPU 必须 100% 或数学上不可再优化。

正式数据：`.qa/video-cuda/idle-baseline/`、`.qa/video-cuda/native-final/`；汇总为 `.qa/video-cuda/final-evidence/measurements.json`。输入、CUDA 源码及 PTX 哈希见 `.qa/video-cuda/artifact-sha256.json`。

## 找到的空闲与修复

1. 主机 BGRA 阶段边界使 GPU 推理后先回读，再为下一个阶段上传。现在 NVDEC 输出 CUDA 帧，GPU 完成色彩转换、SR 填边/拼接、RIFE 前后处理，NVENC 直接接收 NV12 硬件帧。
2. FFmpeg 的 primary-context 调度标志与本机 Windows ML 活跃上下文冲突；改为借用已保留的主上下文，并把 NVDEC 放到专用非阻塞流，只等待实际生产流。
3. 未等待解码表面复制就跨流读取曾产生异常帧；加入明确交接同步后，整段对照不再出现该异常。帧引用保持到所有 GPU 工作完成，预览引用先于设备拥有者释放。
4. GPU 比较原先逐像素竞争全局原子变量；改为每线程块归约后写标志。重复帧复用与场景判断保留，只回读控制标志。
5. RIFE 的 GPU 前处理和打包改为原生 CUDA 内核，直接衔接原始网络的 FP32 张量，移除两次 ORT 执行及额外中间图像复制；归一化查表与量化边界保持一致。CPU 字节接口继续保留。
6. UI 预览先在 GPU 缩小到最多 640×360，避免仅为预览下载整幅 4K 帧。CUDA 原生工作与 EP 的 CUDA Graph 捕获共用并发门。

组合最后一次正式处理记录为 72 次 CUDA 解码、288 次 GPU NV12 转换、0 次解码帧下载；SR/RIFE 的模型与像素路径不再经主机整帧交接。该计数不包括初始化时的兼容性暖机，也不表示音频、封装、时间戳和任务调度不使用 CPU。启用预览时仍需下载缩小后的显示像素。

## 画质与验证

原始模型网络及 GPU 地址接口对照保持像素一致。GPU 色彩转换使用显式矩阵、范围和三角重采样，没有复刻 zscale 的误差扩散，因此完整输出与上轮存在差异。下表比较全部解码帧，不只抽取预览：

| 场景 | 帧数 | PSNR dB | 平均 SSIM | 最小单帧 SSIM |
|---|---:|---:|---:|---:|
| 仅 FSRCNNX M 2× | 72 | 44.20 | 0.987699 | 0.986237 |
| 仅 RIFE Lite 4× | 288 | 43.89 | 0.992777 | 0.990898 |
| 先 RIFE Lite 4×，后超分 2× | 288 | 44.60 | 0.989259 | 0.987250 |

全部输出帧数、音频、尺寸和时间戳已检查。原始画质结果见 `.qa/video-cuda/idle-quality/quality.json`。RIFE 内核合并后与前一显存版再做完整解码对照，72/288/288 帧的像素与时间戳全部一致，证据在 `.qa/video-cuda/native-equivalence/decoded-comparison.json`。

验证入口包含 CPU SIMD/标量路径、原 GPU 适配器 168 项、CUDA 接口 90 项、1284 个量化边界及非有限值、GPU/Graph 并发隔离、真实原生管线取消/重试/行跨度/尾帧/预览、FFmpeg 101 项及现有 SR 烟测。最终执行结果以 `.qa/video-cuda/final-validation/validation.json` 为准；FFmpeg 的 QSV/AMD 两项因本机设备能力跳过。Windows 烟测工程和 Linux 主工程构建均为 0 错误，日志分别为 `.qa/video-pipeline/cuda-final-build.log`、`.qa/video-pipeline/cuda-linux-build.log`；既有依赖和弃用 API 警告未在本轮处理。

## 并发对照与限制

仅 RIFE 的最后一次正式记录中，处理阶段耗时 1996.249 ms，网络执行及同步累计 1923.501 ms，占约 96.4%；原生前处理和打包分别为 23.718 ms、9.729 ms。剩余耗时主要在原模型网络边界，这些是主机计时的执行/同步耗时，不是 GPU 内核占用率。后续仍可研究网络或执行提供程序，不能据此宣称已没有任何优化空间。

在合并 RIFE 原生像素内核之前，2/4/8 个 SR 上下文的端到端中位数分别为：仅 SR 101.62/114.60/115.29 fps，组合 81.68/81.24/82.36 fps。8 个上下文对 4 个仅约 1% 差异，默认保留 4 个以控制启动和资源成本。记录位于 `.qa/video-cuda/idle-workers-2/`、`idle-resident/`、`idle-workers-8/`。

高位深、HDR、额外去噪/去隔行/裁剪/缩放、其他色彩或色度位置、DirectML RAVU R2 等保留既有或混合路径。RIFE 仍受原有 4K 像素上限约束。固定显存帧池拒绝运行中分辨率改变；本轮设备实测覆盖 Windows RTX 5070 Ti 的 H.264 8 位输入，其他 GPU、Linux 原生设备和其他硬解码格式未作设备验证。

## 复现命令

```powershell
# 在仓库根目录；已有隔离模型目录 .qa/video-pipeline/data
$exe = 'FancyTool/_tools/FfmpegSmokeTest/bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe'
$env:FANCYTOOL_DATA_DIRECTORY = (Resolve-Path .qa/video-pipeline/data).Path
dotnet build FancyTool/_tools/FfmpegSmokeTest/FfmpegSmokeTest.csproj -c Release --no-restore --disable-build-servers -v:q

# 生成同规格 H.264 4:2:0 输入；首次生成与编译不计入正式处理耗时
& $exe .qa/video-cuda/fixture420 --pipeline-benchmark --width 1920 --height 1080 --frames 72 --modes plain --profile high --repeats 0
$inputFile = '.qa/video-cuda/fixture420/plain-0/output.mp4'

# 隔离构建上一轮源码，再测当前代码；不改写工作区生产文件
pwsh -File FancyTool/_tools/FfmpegSmokeTest/MeasurePipeline.ps1 -OutputDirectory .qa/video-cuda/repro-baseline -DataDirectory .qa/video-pipeline/data -InputFile $inputFile -BaselineCommit 5ff27272dfa99d110fa9aaf42297823bedae2412
pwsh -File FancyTool/_tools/FfmpegSmokeTest/MeasurePipeline.ps1 -OutputDirectory .qa/video-cuda/repro-candidate -DataDirectory .qa/video-pipeline/data -InputFile $inputFile

# 完整输出画质与真实代码路径回归
& $exe .qa/video-cuda/repro-quality --pipeline-quality --before .qa/video-cuda/repro-baseline --after .qa/video-cuda/repro-candidate
pwsh -File FancyTool/_tools/FfmpegSmokeTest/ValidatePipeline.ps1 -OutputDirectory .qa/video-cuda/repro-validation -DataDirectory .qa/video-pipeline/data

# Linux 仅验证构建，未替代设备验证
dotnet build FancyTool/FancyToolAva.csproj -c Release -r linux-x64 -p:FancyToolMsixBuild=true -p:PublishAot=false --disable-build-servers -v:q
```

诊断时可分别设置 `FANCYTOOL_VIDEO_NVDEC=0`、`FANCYTOOL_VIDEO_CUDA=0` 或 `FANCYTOOL_VIDEO_SR_WORKERS=1..8`；这些仅用于对照，不是界面偏好。原始日志、GPU 采样、实验源码与媒体保留在 `.qa/`，历史测试产物不再放入 Git。
