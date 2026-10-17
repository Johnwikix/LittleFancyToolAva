# 轻量 GPU 视频超分与可选 FP16

> 执行调度、缓冲区和 RTX 5070 Ti 实测补充见 [ADR 0010](0010-bounded-video-processing-pipeline.md)。本 ADR 的模型与后端限制继续有效。

2026-10-16。替代 ADR 0007 的视频模型选择，继续使用现有 Windows ML / ONNX Runtime 与分块视频管线。

视频使用独立 `VideoSrModel` 类型，只提供 Anime4K CNN S/M、FSRCNNX s/m、RAVU Lite r2/r3。图片模型枚举保持原有六个值，移除视频专用 NanoVSR 值；旧视频模型菜单、NanoVSR 推理实现、导出工具与分发权重已移除。旧实验报告保留为历史记录。

六档均原生2×。2×输出运行一次；4×输出串联两次同一GPU会话，在第一次运行前检查最终尺寸限制，并在两次运行之间检查取消。没有使用普通插值冒充第二次模型放大。4×更慢，且中间帧会增加内存。

所有最终图使用RGB输入输出。单通道FSRCNNX、RAVU的亮度/色彩适配保存在ONNX图中，执行时也必须分配到GPU。提供十个很小的离线图：六个FP32、Anime4K和FSRCNNX四个FP16；总大小约455 KiB。安装复用现有校验、临时文件和模型目录迁移机制，位于管理根目录的 `VideoSr` 子目录。来源、许可和SHA-256见 `Libraries/VideoSr`。

FP16是可选的独立权重/张量图，默认界面选择Anime4K S并开启FP16。关闭后选择FP32图，并对OpenVINO显式设置f32计算提示；开启时使用f16提示。两种精度均要求厂商GPU提供程序。FP16图中转换器保留的少量算子/转换仍可能使用FP32，不能称为每个算子都使用FP16。性能取决于设备。

RAVU仅提供FP32：r2固定DirectML GPU，r3要求厂商GPU（本机OpenVINO）。半精度控件禁用，界面说明原因，服务层也拒绝强行请求FP16。这样避免此前RAVU半精度误差及r2在OpenVINO的FP32误差。r3在OpenVINO使用显式f32提示。

界面只显示自动GPU，不开放手动选择未通过完整回归的组合。Anime4K、FSRCNNX、RAVU r3要求厂商GPU，没有DirectML回退；RAVU r2使用DirectML。缺少兼容厂商GPU提供程序时提示安装加速组件。服务拒绝CPU、NPU和未获准的DirectML覆盖请求，会话设置 `session.disable_cpu_ep_fallback=1`。共享 `VideoSrBackend` 的CPU/NPU值继续服务图片、插帧和既有音频路径。模型计算GPU化不包括解码、编码或CPU位图转换。

DirectML路径在创建原生会话前初始化OrtEnv并完成Windows ML已安装提供程序注册，优先通过设备API选择匹配DXGI适配器的GPU，旧运行时保留原DirectML API。限制该路径的CPU调度线程数为1不改变GPU计算分配。符号维度覆盖同时兼容height/width和h/w。

诊断中部分DirectML组合在原生会话初始化处出现长时间等待，一些诊断被主动终止；未定位到唯一根因，也不把等待表述为GPU算子不支持。因此产品使用上面的限定组合。首次编译仍可能较久，初筛的推理fps不能代表首任务体验。当前机器以外的GPU/驱动尚未运行这套回归。

服务保留最多10个编译会话，按完整模型路径、窗口、后端和精度区分；每段视频取得独占租约，释放租约不销毁编译会话。这样批量视频复用编译结果，且同一服务不会并发调用原生推理。到达上限时淘汰最早的空闲会话；服务释放时统一销毁。新模型根目录产生不同缓存键，不会复用旧路径会话。RAVU r2最小窗口限制为384；其余遵循全局内存/速度档。

视图状态增加目录版本。旧版或未知版本的模型/后端索引重置为Anime4K S/自动GPU，不把旧NanoVSR或CPU索引重新解释成新模型/后端。新版本保存模型档位、倍率和FP16，旧后端索引统一归零；RAVU恢复时仍强制关闭FP16。图片选择及插帧设置独立保留。

当前Arc 140T核显、驱动32.0.101.8826下，Release构建通过，生产GPU回归17项全部通过：十个最终RGB图分别比较random/flat/edges三个FP32 CPU参考，要求最大绝对误差≤0.02且MAE≤0.001；所有图走通多块拼接和7帧带音频转码，检查尺寸、时长、帧数及音频。FP16最大误差约0.002405，阈值未放宽。其余检查覆盖损坏模型离线修复、禁止的配置、4×裁剪、帧率/最终缩放、取消清理、独占租约取消、取消后的会话复用和4×尺寸预检。

界面回归通过六档选择、FP16限制、旧状态迁移、中英文切换、宽/窄布局和两段视频独立裁剪批量转换；模型管理回归通过VideoSr子目录迁移、立即生效、恢复默认、重启、冲突回滚和取消。十个编译会话全部使用后的测试进程峰值工作集约2376 MiB，包含原生编译器、GPU运行时与位图，不是专用显存；日常只使用少数档位时不代表会达到该峰值。

复现：运行 `_tools/OnnxCandidateValidation/package.py` 后构建FfmpegSmokeTest，再使用 `--sr-only --sr-reference-root .qa/onnx-candidates`。`--editing-ui --sr-ui` 验证界面，`--settings-only` 验证目录迁移；测试数据使用独立 `FANCYTOOL_DATA_DIRECTORY`。完整记录见 [生产回归数据](../data/video-sr-integration-2026-10-16/measurements.json)，初筛另见 [ONNX候选实测](../onnx-vsr-validation-2026-10-16.md)。
