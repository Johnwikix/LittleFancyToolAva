# NanoVSR 时序超分与 TensorRT RTX 修复

> 视频模型部分已被 [轻量 GPU 视频超分](0008-lightweight-gpu-video-sr.md) 替代；本文保留当时的实验记录，NanoVSR 已从当前视频功能与分发包移除。TensorRT RTX 提供程序发现修复继续适用于其他模型。

日期：2026-10-16。

视频超分新增 NanoVSR 644K，沿用 Windows ML / ONNX Runtime，不引入第二套原生推理库。
NVIDIA TensorRT RTX 通过 Windows ML 安装和注册，支持的 RTX 设备优先使用它；失败后
保留 DirectML 及用户配置的 CPU 回退。Linux 保留 CPU 路径。

## 模型与时间轴

- 作者源码固定在 `feab9d259977ace3be4fca659394c3043bcc34c5`，采用官方 v1.0 的
  `nanovsr_644k.pth`，48 通道、每方向 12 个传播块，MIT 许可。
- 导出脚本为 `_tools/NanoVsrExport/export_nanovsr.py`。先验证训练拓扑与融合拓扑等价，
  再导出 opset 17 并转换为 FP16，检查 ONNX 及两种尺寸的 PyTorch/ORT 数值一致性。
- 输入/输出是 RGB `[1,5,3,H,W]` / `[1,5,3,4H,4W]`。时间维固定 5，空间维动态，
  创建会话时固定为 192×192；32 像素边缘上下文限制显存占用。
- ONNX 文件随应用分发（约 1.4 MB），通过现有模型存储锁、SHA-256 和原子替换机制
  安装到可迁移的模型目录，不要求用户本机安装 Python 或下载 PyTorch。
- 解码后的真实相邻帧进入最多 5 帧的窗口。输出保留每帧原始 PTS、duration 和宽高比；
  EOF 时处理剩余帧，仅最后不足 5 帧的窗口重复最后一帧补足模型输入，不增加输出帧。
- 支持 2×（对模型原生 4× 结果做 2×2 平均）与 4×；支持已有裁剪、音频、取消和
  插帧前后顺序。图片模型列表排除 NanoVSR，避免把时序模型当作图片模型运行。
- 窗口间不传递隐藏状态，也没有跨窗口重叠或场景切分。因此仍需针对实际影片观察
  窗口边界和场景切换处的画质；这里的验证不构成内容质量排名。

模型 SHA-256：`1F4DF91EF0D7D201309FC7295A19434A4A29603C1EFA8850741D23E9B76AC03A`。
原 checkpoint SHA-256：`515EEC20FF589986FC0E0DA18B363962B931008EBD848D0C8FB7E256CE200CF7`。
版权和来源保存在 `Libraries/NanoVSR/LICENSE.txt`、`NOTICE.md`。

## 已确认的性能问题及修复

本机 Windows ML 实际注册名为 `NvTensorRTRTXExecutionProvider`。原代码精确匹配
`NvTensorRtRtxExecutionProvider`，导致已安装的 NVIDIA 提供程序被会话选择和安装筛选
同时跳过。修复为忽略大小写的统一识别，实际运行显示 `GPU · Windows ML / NvTensorRTRTX`。
启用 TensorRT RTX 的运行时缓存和 CUDA Graph，保留明确的硬件回退原因。

第二个可测瓶颈是 CPU 上的 BGRA→RGB 张量转换、RGB 输出拼接、原生 4×→2× 平均。
新增带硬件检测的 SSE 向量路径，四像素一起处理；其他平台仍使用标量路径。
输入归一化、边缘填充、通道顺序、平均顺序、舍入与钳位由逐像素对比验证。

RTX 5070 Ti 16 GB、驱动 617.14，Release 模式，同一合成 BGRA 图像，预热后完整
bitmap→张量→推理→CPU 回传→bitmap 路径。以下均不含解码/编码和首次编译时间：

| 测试 | 修复前 | 修复后 |
| --- | ---: | ---: |
| RealESRGAN general x4v3，640×360→1280×720，384 窗口，5 帧中位数 | 81.44 ms | 50.92 ms |
| LiveAction SPAN，640×360→1280×720，3 帧中位数 | 约 53 ms | 约 29 ms |
| general x4v3，1280×720→2560×1440，TensorRT，CPU 像素阶段，3 帧均值 | 47.30 ms | 16.23 ms |

最后一行只比较提供程序修复后、SIMD 修改前后的 CPU 像素阶段；推理和传输约 126 ms，
没有把它们的时间算作 CPU 优化收益。最初的 5 帧测量中托管分配约 2 KB/帧，Gen2 GC 为 0，
未发现 GC 是该基准主要瓶颈。不同轮次时钟和系统负载使总耗时略有波动。

NanoVSR 使用 5 个真实输入帧，3 个预热后窗口取中位数：

| 输入→输出 | 单帧耗时 | 吞吐量 |
| --- | ---: | ---: |
| 320×180→1280×720（4×） | 19.86 ms | 50.36 FPS |
| 640×360→1280×720（2×） | 34.34 ms | 29.12 FPS |

初始会话创建约 1–4 秒；首次执行还会触发编译。此集成不是论文的 15 帧 Jetson 基准，
这些结果也不能外推到高清输入、整条转码流程或 RTX Video VSR 的画质/速度。
较大分块并非一定更快：本机 general 模型 576 窗口慢于 384；未盲目扩大默认分块。
剩余主要时间在模型推理和传输；没有证明任务管理器的 25% 指标对应哪一种 GPU 引擎，
也未据此承诺 GPU 利用率达到 100%。

## 验证与复现

导出校验：两种空间尺寸的 FP16 ORT 与 PyTorch 最大绝对误差分别约 0.00124 / 0.00139。
Windows ML/TensorRT GPU 与保存的 PyTorch 浮点参考最大绝对误差约 0.00310。
参考输入和输出随 `_tools/NanoVsrExport/Reference` 保存，烟测复制到独立工具输出目录。

16 项 NanoVSR 检查通过，包括向量像素转换、模型损坏重装、GPU 数值一致性、真实时序
依赖、短窗口、CPU 路径、1/3/5/7 帧准确输出、音频/裁剪/4×、三种插帧顺序和取消清理。
原有超分 6 项 GPU/CPU 回归通过。界面检查覆盖 NanoVSR 选择、2×/4×、中英文切换、
状态持久化、两段视频批量转码、真实 TensorRT 设备提示，以及宽/窄窗口渲染。
Linux CPU 目标编译通过；没有在 Linux、AMD GPU 或 NPU 真机上执行新模型。

原生发布额外发现机器默认使用 .NET 11 RC SDK 时，主程序集缺少 Avalonia 预编译资源，
会在启动时报 `No precompiled XAML found for FancyToolAva.App`；独立构建目录也能复现。
使用已安装的稳定 .NET 10.0.402 SDK 后发布及启动恢复正常。根目录 `global.json` 将 SDK
限制在稳定 .NET 10 系列，并允许该系列的最新 feature band，避免误用预览主版本。
原生发布目录为 `.qa/nanovsr/publish-sdk10`；验证了原生程序完成应用初始化且日志无
Fatal 错误。第三方 FluentAvalonia / DataGrid 仍有已有的 trim/AOT 分析警告。

```powershell
$env:FANCYTOOL_DATA_DIRECTORY = '<workspace>/.qa/nanovsr/data'
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -c Release -- .qa/nanovsr/tests --nanovsr-only
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -c Release -- .qa/nanovsr/regression --sr-only --gpu-only
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -c Release -- .qa/nanovsr/ui --editing-ui --nano-ui
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -c Release -- .qa/nanovsr/benchmark --nanovsr-benchmark
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -c Release -- .qa/nanovsr/phases --sr-phase-benchmark
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -c Release -- .qa/nanovsr/diagnostics --sr-diagnostics --install-providers
dotnet publish FancyTool/FancyToolAva.csproj -c Release -r win-x64 --self-contained true -o .qa/nanovsr/publish
```

带 `--install-providers` 的命令安装兼容的 NVIDIA 提供程序。其他检查不隐式下载加速组件。首次运行现有
模型回归仍需要下载对应旧模型；新 NanoVSR 模型从应用分发内容安装。

高清实拍输入的速度、GPU 采样、画质和时序稳定性结果见
[720p/1080p 实际视频对比](../real-video-sr-2026-10-16.md)。

来源：[NanoVSR 作者仓库](https://github.com/filippawlicki/nanovsr)、
[ONNX Runtime TensorRT RTX 提供程序与 ABI 名称](https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider.html)、
[Windows ML 支持的提供程序](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers)。
