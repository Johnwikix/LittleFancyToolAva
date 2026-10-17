# CUDA 视频像素内核

`pixels.cu` 是本项目的像素搬运、边界填充、色彩转换、帧比较及预览内核，`hdr.cu` 负责 P010、HDR 浮点 LUT 与预览；两个 `.ptx` 由 NVRTC 11.8 生成并嵌入主程序。运行时仅通过 NVIDIA 驱动 `nvcuda.dll` 装载，不分发 CUDA Toolkit、NVRTC 或另一份 ONNX Runtime。

重建命令（Windows，Python 3）：

```powershell
python FancyTool/_tools/FfmpegSmokeTest/CompileVideoCuda.py --nvrtc 'C:/Program Files/NVIDIA GPU Computing Toolkit/CUDA/v11.8/bin/nvrtc64_112_0.dll'
```

编译参数固定为 `--gpu-architecture=compute_75 --fmad=false --std=c++14`，以便驱动为 RTX 5070 Ti JIT 编译，同时保留像素计算的舍入边界。源码、PTX 与重建脚本同步提交，PTX 统一 CRLF；无需在常规 .NET 构建时安装 CUDA 编译器。

硬件帧、主上下文与同步约定见 [ADR 0011](../../docs/adr/0011-gpu-resident-video-frames.md)，实际设备验证见 [实测报告](../../docs/video-cuda-pipeline-2026-10-17.md)。

HDR LUT 在任务初始化或静态亮度变化时由现有 FFmpeg 色彩图烘焙，最多缓存 8 份主机数据；显存上传使用固定页缓冲和自有流，完成后才允许像素内核读取。处理帧的查表、重采样和 P010 打包在 GPU 执行。具体范围见 [ADR 0012](../../docs/adr/0012-hdr-cuda-video-pipeline.md)。
