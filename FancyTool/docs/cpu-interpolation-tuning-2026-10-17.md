# CPU 运动补偿参数复核

日期：2026-10-17。环境：Windows 11、i5-13600KF（20 逻辑处理器）、RTX 5070 Ti、驱动 617.14，FFmpeg N-127579-g5cb9fcd211-20261016。

CPU 利用率低的主要原因仍是 `minterpolate` 内部没有切片线程实现；增加解码、编码或滤镜线程数不能自动并行其运动估计和像素合成。FFmpeg 的实现见 [vf_minterpolate.c](https://github.com/FFmpeg/FFmpeg/blob/5cb9fcd211/libavfilter/vf_minterpolate.c)。前轮真实管线与线程数对照见 [光流诊断](optical-flow-2026-10-17.md)。

本轮继续筛查运动补偿参数，没有找到足以替换现有默认值的可靠收益。原设置保留 `aobmc / bilat / epzs / vsbmc=1`，没有通过切换帧混合、降低分辨率或静默改变质量来宣称提速。

## 参数初筛

输入为同一自然视频的前 24 帧，1920×1080、24 fps，输出 96 fps / 96 帧。只测 FFmpeg 解码、滤镜和 null 输出，包含进程初始化，不包含编码。每项一次，属于候选筛选；测试期间模拟器进程存在，不能把小幅差异当作稳定收益。

| 单变量或候选组合 | 秒 |
| --- | ---: |
| 原质量参数，YUV444 | 14.31 |
| 仅 AOBMC 改 OBMC | 14.26 |
| 仅 EPZS 改 HEXBS | 17.22 |
| OBMC + HEXBS + 关闭 VSBMC | 16.21 |
| 仅块尺寸 8 改 16 | 13.29 |
| 仅双边估计改双向估计 | 25.93 |
| 仅 YUV444 改 YUV420 | 14.64 |

块尺寸 16 的单次差异约 8%，但会改变运动分块与结果，尚未证明等质量或稳定收益，因此没有应用。32 超过当前滤镜允许的 4–16 范围，探测失败，未纳入性能比较。不能据此声称 CPU 算法已经没有优化空间：进一步提高多核利用率需要修改原生滤镜；分段或分块并行还需验证时序历史、运动搜索边界和拼接画质。

## 复现与证据

原始命令、返回码和计时保留在仓库根目录 `.qa/cpu-interpolation-2026-10-17/screen.json`、`screen2.json`；失败参数也保留原始错误。输入使用 `.qa/optical-flow-2026-10-17/real1080-72.mp4`，SHA256 见前轮报告。

```powershell
$ffmpeg = 'G:\SoftwareProject\Avalonia\Dlss5TestDemo\runtime\ffmpeg.exe'
$inputVideo = '.qa/optical-flow-2026-10-17/real1080-72.mp4'
$params = 'mc_mode=aobmc:me_mode=bilat:me=epzs:vsbmc=1'
$filter = "trim=end_frame=24,format=yuv444p,tpad=stop_mode=clone:stop_duration=0.125,minterpolate=fps=96:mi_mode=mci:${params}:scd=none,trim=duration=1"
Measure-Command { & $ffmpeg -v error -i $inputVideo -vf $filter -fps_mode passthrough -f null - }
```

本轮另向 `G:\SoftwareProject\Avalonia\Dlss5TestDemo` 移植了 RIFE GPU 后端、校验后的两份模型及 Vulkan 回读优化，独立报告在该项目 `docs/INTERPOLATION_GPU_2026-10-18.md`。两个项目的接口、编码器与测量边界不同，速度不能直接混算。
