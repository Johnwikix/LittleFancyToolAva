# 0012：10 位 HDR 显存管线与 RIFE HDR 输出

日期：2026-10-17。承接 [ADR 0011](0011-gpu-resident-video-frames.md)，色调映射算法沿用 [ADR 0005](0005-model-storage-and-hdr-output.md)。

## 背景与约束

上一版只让常见 8 位 SDR 在显存中交接。10 位 HDR 软件解码，色调映射使用 CPU zscale/tonemap/lut3d；RIFE 拒绝保留 HDR 输出。继续保留原始模型、Windows ML、现有 GPU 执行提供程序策略、时钟、音频与取消语义。

## 决策

- Windows NVIDIA 任务增加 10 位 4:2:0 NVDEC，软件格式映射为 P010。硬解协商失败仍完整清理并重试软件解码；10 位 SDR、特殊色度、其他色彩及额外 CPU 滤镜等继续使用混合路径。
- 常见 BT.2020 NCL、PQ/HLG、左置色度、固定尺寸/色彩且无额外滤镜的任务进入 HDR 显存路径。GPU 完成 P010 重建、四面体 LUT 查表、超分/RIFE、色度低通和 P010 打包，NVENC 直接接收硬件帧。H.265、AV1 的 10 位输出与能力检查沿用原编码策略。
- HDR 曲线由现有 FFmpeg 浮点色彩图在初始化时烘焙为 65³ RGB LUT，复用 Mobius、BT.2446、PQ/HLG 和色域转换，避免维护第二套传递函数。按源/目标色彩及有效静态亮度缓存，主机最多 8 份，约 33.5 MiB；显存绑定由任务持有。静态亮度变化会重建/重新上传，处理期间不下载整帧。
- LUT 上传使用固定页临时缓冲，在自有 CUDA 流复制并等待完成后释放。分页内存的同步 HtoD 返回不代表 DMA 已结束，非阻塞计算流也不会隐式等待；不得把这种上传交接留给默认流碰巧完成。
- GPU 模型输出显式标记 BT.709 全范围 RGB。源、模型域及编码域分别管理色彩信息；运行中改变色彩或尺寸明确拒绝，避免按旧曲线解释新帧。
- RIFE 允许 HDR 输入保留 HDR 输出；CPU/混合回退同样先做现有 SDR 映射再恢复 HDR，避免硬解失败改变功能可用性。其他插帧方式仍保留显式强制 SDR 限制。
- 无神经处理且色彩不变时直接转交解码 P010 帧，不经过 8 位模型边界；HLG→PQ 等纯色彩转换保持浮点查表与 10 位打包。预览只生成并下载最多 640×360 的 SDR 像素。

## HDR 输出的含义

现有超分与 RIFE 接口在 SDR BGRA 字节域工作。HDR 经色调映射后送入模型，再按原 BT.2446 路径扩展到 1000 尼特目标，输出 10 位 PQ/HLG。它保留 HDR 输出类型，**不保证保留源高光、宽色域或原始逐像素亮度**；本轮没有引入原生 HDR 神经模型，也不宣称无损 HDR 处理。

生成 HDR 写入 1000 尼特 mastering/MaxCLL，未知 MaxFALL 保持 0；无神经处理的透传保留原静态信息。SDR 输出移除 HDR 信息，过时 HDR10+/Dolby Vision 动态信息按既有策略移除。

## 替代方案与后果

没有直接放开 HDR 开关并把 P010 当成 NV12，也没有为 HDR 放宽 CPU EP 回退或分发 CUDA Toolkit。独立手写 PQ/HLG/Mobius 数学会增加算法偏差和维护面，因此使用可对照原色彩图的 LUT。65³ 查表与 GPU 重采样仍有近似和舍入差异，必须比较完整视频，不能仅靠标签或预览宣称画质相同。

增加有界 LUT 缓存、每任务显存与冷路径曲线生成成本，减少处理帧的主机交接。设备证据、性能、画质、复现命令和未验证范围见 [HDR 显存管线实测](../video-hdr-cuda-2026-10-17.md)。

依据：[CUDA 复制同步语义](https://docs.nvidia.com/cuda/archive/13.2.1/cuda-driver-api/api-sync-behavior.html)、[FFmpeg 色调映射](https://github.com/FFmpeg/FFmpeg/blob/master/libavfilter/vf_tonemap.c)、[FFmpeg 静态峰值选择](https://github.com/FFmpeg/FFmpeg/blob/master/libavfilter/colorspace.c)。具体行为以捆绑版本及设备测试为准。
