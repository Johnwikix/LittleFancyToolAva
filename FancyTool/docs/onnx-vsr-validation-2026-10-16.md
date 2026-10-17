# ONNX 视频超分候选：Arc 核显 GPU 实测（2026-10-16）

> 本文保留接入前的初筛快照。当前产品已接入六档模型及独立FP16图，后端限制与生产回归以 [ADR 0008](adr/0008-lightweight-gpu-video-sr.md) 为准；初筛通过的单次DirectML配置不等于已开放为产品配置。

已执行导出、GPU 数值校验、分块测速及短视频管线验证。当前优先候选是 **Anime4K Upscale CNN S / Windows ML OpenVINO GPU**，FSRCNNX s 可作为第二个速度候选。它们的模型计算均在禁用 CPU EP 回退的条件下通过。这里没有建立真实素材的画质排名。

本次“完全 GPU”限于模型推理图：明确选择 GPU 设备，设置 `session.disable_cpu_ep_fallback=1`，不使用 AUTO/HETERO 混合设备。CPU 仍参与会话编译、调度、像素转换及数据搬运；短视频验证使用软件编码。**不代表从解码到编码的整条链路完全运行在 GPU 上。**

机器实际为 Core Ultra 7 255H / Intel Arc 140T 核显，驱动 32.0.101.8826，Windows 11 10.0.26300。应用使用 Microsoft.Windows.AI.MachineLearning 2.4.89；导出参考环境为 Python 3.14.6、PyTorch 2.14.1+cpu、ONNX 1.23.2、CPU ONNX Runtime 1.31.0。设备名称中的“15GB”是驱动报告字符串，不能视为独立显存容量。完整配置见 [原始证据](../../.qa/git-history-cleanup-2026-10-17/evidence/onnx-vsr-validation-2026-10-16/measurements.json)。

| 保留的模型变体 | 本机通过的 GPU 配置 | 360p→720p fps | 720p→1440p fps | 1080p→2160p fps |
|---|---|---:|---:|---:|
| Anime4K CNN S | OpenVINO，f16 提示 | 70.39 | 21.60 | 10.72 |
| Anime4K CNN M | OpenVINO，f16 提示 | 38.91 | 11.67 | 5.47 |
| FSRCNNX s | OpenVINO，f16 提示 | 73.56 | 20.06 | 9.81 |
| FSRCNNX m | OpenVINO，f16 提示 | 52.65 | 12.57 | 7.51 |
| RAVU Lite r2 | DirectML | 21.40 | 7.19 | 3.69 |
| RAVU Lite r3 | DirectML | 19.75 | 6.26 | 3.09 |
| NNEDI3 nns16 win8×4 | OpenVINO，f32 提示 | 11.21 | 3.67 | 1.84 |
| NNEDI3 nns32 win8×4 | OpenVINO，f16 提示 | 6.00 | 1.96 | 0.98 |
| 对照：ClearReality V1 FP16，原生4× | OpenVINO，应用 GPU 配置 | 6.70 | 2.26 | 1.13 |
| 对照：LiveAction V1 SPAN，原生2× | OpenVINO，应用 GPU 配置 | 7.77 | 2.53 | 1.29 |

同一台机器、同样分块条件下，Anime4K S 的 1080p 输入处理速度约为 ClearReality 的 9.5 倍、LiveAction 的 8.3 倍。没有达到 1080p 输入的 24 fps；不能据此宣称 1080p 实时超分。原生倍率不同会影响计算量，这些是实际“请求2×输出”的管线代价比较，不是等画质比较。

DirectML 补测中 Anime4K S 为 33.46 / 9.42 / 6.13 fps，FSRCNNX s 为 33.71 / 10.78 / 4.98 fps；本机优先选择 OpenVINO。RAVU r3 的 OpenVINO f32 路径也通过，速度为 13.39 / 4.64 / 2.35 fps。NNEDI3 nns16 的 f16 路径通过但仅 7.37 / 2.47 / 1.22 fps；f32 更快，不能假定半精度总能加速。nns32 未补测 f32。

EDVR-M 的 OpenVINO f32 和 DirectML 都能通过纯 GPU 推理验证，但只能作为本次高质量方法的技术可行性样本：

| EDVR-M 配置 | 输入→请求输出 | 中位耗时 | fps | 进程峰值工作集 |
|---|---|---:|---:|---:|
| OpenVINO，f32 提示 | 320×180→640×360 | 7611.62 ms | 0.131 | 3810 MiB |
| DirectML | 320×180→640×360 | 9209.53 ms | 0.109 | 3695 MiB |

EDVR 模型是原生4×，输入为五帧拼接的15通道，输出中心帧。本工具先4×再缩到请求的2×，使用固定128像素窗口、32像素边缘，只保留64像素有效区域，分块开销很大。这组结果不能外推整帧 EDVR 的速度。早期640×360 DirectML尝试超过120秒并保留失败记录；后续缩小测试后通过，不把超时误判为 GPU 算子不兼容。上述内存是整个进程工作集，包含编译和测试对象，不是 GPU 专用显存。

以下配置已排除，不能进入合格配置清单：

| 排除配置 | 实测原因 |
|---|---|
| NNEDI3 nns16、nns32 / DirectML | 会话创建报存在分配给默认 CPU EP 的节点；禁用回退后拒绝创建。保留其已通过的 OpenVINO GPU 配置。 |
| RAVU r2、r3 / OpenVINO f16 提示 | 虽可在 GPU 运行，但随机样本最大误差分别约0.08249、0.04594，超出阈值。 |
| RAVU r2 / OpenVINO f32 提示 | 最大误差0.0226053，MAE约0.000002714；仍超过0.02最大误差门槛，未因均值较低放行。 |
| EDVR-M / OpenVINO f16 提示 | 随机样本最大误差0.1200063、MAE0.0059112，数值不合格。 |

因此筛选单位必须是“模型权重 + 图适配 + GPU后端 + 精度提示”，不能因为一个后端失败就把整个模型家族排除，也不能把一个后端的通过结果推广到另一台 GPU。数值失败的 RAVU 路径可能涉及方向/类别边界对精度的敏感性，但本次没有定位具体算子，不作为已证实原因。

本次共有九个候选变体、两项速度对照、30条运行记录。候选来源为固定提交的 [HWEnc-onnx-models 导出工具](https://github.com/rigaya/HWEnc-onnx-models/tree/909fc391b5584702892081adb98f93ea2db16e73)：Anime4K S/M、FSRCNNX s/m、NNEDI3 nns16/nns32、RAVU Lite r2/r3、EDVR-M。FRVSR、RLSP、RRN、TecoGAN、EGVSR、VRT、RVRT、Enhanced RAISR 未进入本次已定位的可复现 ONNX 集合，不对它们做“无法导出”或“无法纯 GPU”的绝对判断，也没有把它们列为已验证可用项。[VRT](https://github.com/JingyunLiang/VRT) 和 [RVRT](https://github.com/JingyunLiang/RVRT) 的官方仓库另行保留为后续研究来源。

验证细节与边界：

- 八个轻量变体各使用 random / flat / edges 三个样本做 PyTorch→CPU ONNX 比较，24项全部通过，最大误差不超过1.014e-6。NNEDI3、RAVU 上游导出器另有 NumPy 独立实现比较。EDVR 导出器单独比较 PyTorch 与 CPU ONNX，最大误差6.525e-5，隔离环境的重导出日志已存档。
- GPU 对照 CPU ONNX 的三个样本要求输出有限、形状一致、最大绝对误差≤0.02且 MAE≤0.001，同时验证预先取消的调用。EDVR OpenVINO f32 的随机样本最大误差约9.373e-5；DirectML约8.261e-5。阈值是本次工程筛选条件，不是画质指标。
- 对单通道模型，新增RGB ONNX适配图：全范围BT.709权重提取Y，模型放大Y，RGB和Y双线性放大后叠加亮度残差并截断。适配图也在严格GPU会话中执行，没有把三个RGB通道分别送入Y模型。这是一种应用适配，未证明与原始mpv/GLSL的色彩、采样相位和完整渲染管线逐像素相同。数值对照针对原始模型图；RGB适配图验证了GPU执行、尺寸与转码，未单独进行完整CPU/GPU逐像素对照。
- 速度使用确定性BGRA合成图（渐变、细线、圆形、纹理），每个尺寸一次冷运行、三次热运行取中位数。包含CPU位图处理、张量拷贝、GPU推理及输出拼装；排除会话建立、解码、编码和PNG保存。模型原生2×时使用384窗口、32边缘、320有效块。不同后端的编译等待不计入表中fps，原始日志另存。
- 八个轻量变体都至少有一条合格GPU配置走通未修改的 `VideoTranscodeService`：160×120、7fps、1秒、带音频AVI→320×240 MP4，检查7帧、时长约1秒及音频流存在。只证明短管线连通，不证明长视频无丢帧、音画同步质量或编码实时性能。
- EDVR 使用五张不同位移的合成邻帧。CPU参考中只改变一个邻帧就使中心输出 MAE变化约0.005289，确认依赖邻帧；未接入产品时序调度、边界补帧、切镜和长视频队列。现有 NanoVSR 五进五出契约不能直接替代它的五进一出契约。
- 未测真实视频的PSNR/SSIM/VMAF、主观画质、时间闪烁、长时间功耗或多设备驱动覆盖。三个热样本只用于初筛；提高画质的选型还需要同素材盲比。FSRCNNX的训练权重许可与导出脚本许可不同，应保留上游许可来源；本次没有发布权重。

产物包括 [完整测量](../../.qa/git-history-cleanup-2026-10-17/evidence/onnx-vsr-validation-2026-10-16/measurements.json)、[仅合格GPU配置](../../.qa/git-history-cleanup-2026-10-17/evidence/onnx-vsr-validation-2026-10-16/eligible-configurations.json) 和 [复现脚本说明](../_tools/OnnxCandidateValidation/README.md)。清单区分张量验证、位图验证与转码验证，未通过配置仍留在诊断数据中。新增代码只进入 `_tools/FfmpegSmokeTest` 的显式验证入口，产品模型菜单与默认推理策略未变。

环境操作记录：首次安装依赖受到本机全局pip的 `target=D:\pip` 配置影响，向共享目录增加了部分包。随后重建工作区隔离环境、显式指定安装target并使用Python `-I`，EDVR已在隔离环境重导出；没有为回滚而删除来源可能混杂的共享包。后续复现命令已规避该全局配置。
