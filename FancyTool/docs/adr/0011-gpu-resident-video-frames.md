# 0011：NVDEC、CUDA 推理帧与 NVENC 的显存衔接

> 2026-10-17 补充：HDR 显存管线与 RIFE 保留 HDR 输出见 [ADR 0012](0012-hdr-cuda-video-pipeline.md)。本文原有算法说明与历史验证范围保留，旧 HDR 管线限制由新 ADR 更新。

日期：2026-10-17。承接 [ADR 0010](0010-bounded-video-processing-pipeline.md)。

## 背景与约束

上一轮将解码、SR、RIFE、编码与封装拆为有界队列，模型与像素前后处理已经在 GPU 执行，但各处理阶段仍以主机 BGRA 帧交接。SR 分块上传/回读、RIFE 整帧上传/回读以及 NVENC 输入上传仍然存在。

保留 Windows ML、原始模型权重、FP16/FP32 选择、插帧时钟、音频和取消语义，不引入另一份 ONNX Runtime，不放宽 CPU 执行提供程序回退。新增 CUDA 像素内核使用 NVIDIA 驱动加载嵌入 PTX，运行时不依赖 CUDA Toolkit。

## 决策

- Windows NVIDIA 编码任务在 FFmpeg 提供 CUDA 解码配置时优先尝试 NVDEC；本轮只启用已核对的 8 位 420/444 解码格式。硬件格式协商到首帧才失败时，释放全部在途资源后完整重试软件解码，包含音频和临时输出。
- 常见 8 位 4:2:0 SDR、BT.709 原色/传递函数、左置色度、无额外 CPU 滤镜/缩放的任务可以进入显存路径。支持 SR 2×/4×、RIFE Lite/Full 及原有处理顺序。RAVU R2 DirectML、高位深、HDR、其他色彩格式和额外滤镜保留原路径或 NVDEC 下载后混合处理。
- 通过 FFmpeg 公共 `AVCUDADeviceContext` 的上下文和流字段，借用已保留的 CUDA 主上下文。FFmpeg 的 primary 模式要求的调度标志与本机 Windows ML 已激活上下文冲突，因此使用 current-context 模式，拥有者存活到解码器释放后。
- NVDEC 使用独立非阻塞流。交接时只等待解码流；像素内核、ORT 绑定和 NVENC 在各自边界完成同步后才复用缓冲。不得依赖默认流隐式等待。
- SR 输入块和输出块通过同设备 EP 的实际内存信息创建 GPU I/O Binding，GPU 内核负责填边和拼接；4× 的中间帧在显存中复用。RIFE 直接借用两个 BGRA 帧，CUDA 内核完成归一化/时间平面准备及输出打包，原网络 FP32 张量留在显存，减少 ORT 会话切换和中间复制。
- 阶段间传递带引用计数的 CUDA AVFrame，NVENC 接收 NV12 硬件帧。GPU 像素比较保留重复 SR 帧复用；场景检测沿用原采样位置、整数亮度及阈值，只回传两个标志。比较先在线程块内归约，避免每个变化像素都竞争全局原子变量。
- 预览先在 GPU 缩小到最多 640×360，再下载显示所需像素。CPU 继续处理解复用、音频、时间戳、任务调度、封装及少量控制参数；显存路径不表示整个应用完全不使用 CPU。
- NVENC 任务保守关闭 CUDA Graph，以支持 GPU/主机接口切换并沿用已验证的跨会话并发防护。所有 GPU 调用及 worker 完成后才释放帧池、模型租约和主上下文；清理失败不会跳过后续资源。

## 色彩与替代方案

GPU 实现显式矩阵、范围与三角色度重采样，再将 NV12 交给 NVENC，避免编码器隐式 RGB 矩阵。模型计算与原字节接口逐像素一致，但 GPU 色彩转换没有复刻 zscale 的逐像素误差扩散。因此与旧版完整输出存在舍入、采样和后续有损编码差异，必须报告 PSNR/SSIM，不能宣称整段逐字节等价。

只接 NVDEC 后立即下载不能解决其他阶段的往返复制；把 CPU 指针当作 CUDA 指针、跨流未完成便回收帧、放宽 CPU EP 回退、引入另一份 ORT 均不采用。未支持的色彩/滤镜走现有路径；运行中改变分辨率会明确拒绝固定显存帧池，避免越界访问。

## 后果与验证范围

增加有界 GPU 帧引用、固定块缓冲和少量主机控制内存；减少处理帧的主机往返。保留软件解码和混合路径，其他品牌和 Linux 不加载 `nvcuda.dll`。性能、数值差异、测试命令及本地原始证据见 [RTX 5070 Ti 显存管线报告](../video-cuda-pipeline-2026-10-17.md)。不宣称 GPU 100%、零分配或数学意义上的不可继续优化。

FFmpeg 接口依据：[CUDA 公共上下文](https://github.com/FFmpeg/FFmpeg/blob/master/libavutil/hwcontext_cuda.h)、[CUDA 硬件帧](https://github.com/FFmpeg/FFmpeg/blob/master/libavutil/hwcontext_cuda.c)、[NVENC 实现](https://github.com/FFmpeg/FFmpeg/blob/master/libavcodec/nvenc.c)。具体捆绑版本仍以 `Libraries/FFmpeg/BUILD_INFO.md` 和实际集成验证为准。
