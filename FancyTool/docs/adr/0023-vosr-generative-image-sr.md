# VOSR-1.4B 生成式图片超分接入

2026-11-11。在图片页新增 `VOSR-1.4B`（[cswry/VOSR](https://github.com/cswry/VOSR)，CVPR 2026 一步扩散式超分），权重取自 `AllGPTORG/VOSR_1.4B_Mobile` 的 ONNX 分级图。

## 背景与约束

现有图片超分（RealESRGAN ×3、ClearReality SPAN）都是**单图、定倍率、tile-in/tile-out** 的卷积网络：一次 `InferenceSession.Run` 输入 `[1,3,W,H]`、输出 `[1,3,4W,4H]`，服务层负责切块与拼回。VOSR 不符合该形态：

- 它是**九图链**：DINOv2-L（第 17 层语义特征）→ Qwen-Image VAE 编码 → DiT prepare → 四段连续 DiT blocks → DiT final（velocity）→ Qwen-Image VAE 解码。
- 所有图都是**静态形状**，`lq_image` 固定 `[1,3,512,512]`，DiT 固定 1024 token，输出 latent `[1,16,64,64]`。
- 它是**分辨率保持**的生成式复原：官方要求先把低分图双三次放大到目标分辨率，再让网络在该分辨率上恢复细节。因此"倍率"是预处理参数，不是网络属性。
- 1.4B 参数、静态 512 窗口，host 侧必须自己负责分块、单步 `z = z - v` 更新与重叠混合。

## 量化版本的实际形态（重要）

用户提供的 `.../tree/main/onnx` 目录**不是**量化图，而是 FP16 分级图（合计约 3.35 GB）。仓库内的量化产物有三类：

| 目录 | 形态 | 本应用能否使用 |
|---|---|---|
| `onnx/` | FP16 分级图，9 个文件 | 是，已实测 |
| `pi/w8a16/` | ONNX Runtime `MatMulNBits` INT8 权重 / FP16 激活，1.66 GB，面向 8 GB 树莓派 CPU | 否：实测 ORT 1.22.1 CPU EP 直接抛 `Only 4b quantization is supported for unpacked compute`；模型卡要求 ORT ≥ 1.28 的 ARM64 kernel |
| `sd8g3/` | Qualcomm QNN EPContext（.bin + ONNX wrapper） | 否：只能在骁龙 8 Gen 3 HTP 上加载 |

因此本次接入固定使用 `onnx/` 的 FP16 分级图，并按文件逐一钉住 SHA-256。

## 决策

1. **模型编号**：`SuperResolutionModel.Vosr1_4B = 5`，保留编号 3 的历史空位，旧页面状态不会被重新解释。
2. **按需下载**：3.35 GB 不进入启动预热。`ISuperResolutionService.EnsureModelAsync` 在用户选中该模型时触发下载；启动预热仍只处理四个 ESRGAN 家族模型。`IsDownloading` 改为并发计数，预热与按需下载可并存。
3. **独立管线**：新增 `VosrImagePipeline`，由 `SuperResolutionService.UpscaleAsync` 在 `Vosr1_4B` 时委派。九张图对应九个常驻会话；`ReleaseSessions`/`Dispose` 一并释放。
4. **分块与混合**：512 窗口、步长 384（重叠 128）。重叠带用余弦窗交叉淡化，左右按列累积、逐列与上一列混合，内存上界为 `512 × 目标高 × 4` 个 float；上下方向由二维权重归一化自然融合。目标小于 512 时补齐工作画布再裁回。
5. **确定性**：VAE 编码的 `posterior_noise` 传 0（对应参考实现的默认 `posterior_mode`），初始 latent 用"图块坐标 + 固定基种子"的高斯采样，重复运行结果一致。
6. **后端顺序**：厂商 GPU（OpenVINO / TensorRT-RTX）→ DirectML → CPU；`SR_FORCE_CPU=1` 或非 Windows 直接 CPU。不把 DirectML 作为首选的原因见下。

## 后果与已验证范围

实测环境：Intel Core Ultra 7 255H / Arc 140T（Windows 11，驱动 32.0.101.8991），Debug 构建，模型目录位于本地 `Models`。

- 九图冷加载约 20 s，常驻权重约 3.3 GB。
- 端到端 `480×320 → 1920×1280`（15 块）40–45 s，约 2.5–2.9 s/块（含 DINOv2 与 VAE 编解码），后端 `GPU · Windows ML / OpenVINO`。
- CPU 路径（`SR_FORCE_CPU=1`）：约 29–35 s/块，仅作可用性兜底，不作为推荐路径。
- 输出经人工比对：渐变、1 px 细线与文字均保持并可辨，重叠带无可见接缝；早期"取窗口内部 384 裁剪"的方案在同样素材上能看到每 384 px 一条垂直亮/暗带，已被混合方案取代。

已知问题：

- **DirectML 连续创建会话存在原生崩溃风险**：在本机驱动上，进程内连续创建第 3 个 DirectML 会话时出现过 `InferenceSession::Init` 的 `0xC0000005`（原生 AV，进程直接退出，托管 `try/catch` 无法接管）；仓库内早期探针（独立 ORT 1.22.1 DirectML 包）与应用运行时（Windows ML 2.4.89）都复现过，同一组图在厂商 EP 路径下连续创建 9 个会话正常。因此首选厂商 EP，DirectML 仅作回退；若用户环境只有 DirectML，首次加载存在崩溃可能，需要单独跟踪（怀疑与 ORT DirectML EP 的多会话生命周期/驱动有关）。
- 视频页未接入：单块约 3 s 的吞吐对逐帧视频不现实（`IVideoSuperResolutionService` 仍是轻量卷积模型）。
- 生成式模型会引入源图不存在的细节，界面文案标注"生成式"，不承诺还原真实内容。
- 许可：`AllGPTORG/VOSR_1.4B_Mobile` 与上游 `CSWRY/VOSR` 均为 Apache-2.0，商用无附加限制；DINOv2 以权重形式封装在图内，发布材料需保留来源说明。

## 下载与续传（2026-11-11 修复）

首版接入后出现"已下载的 VOSR 每次进入图像页都重新下载、且始终装不完"。根因是**清单里两张图的 SHA-256 转录错误**：`vosr_dinov2l_layer17.onnx` 与 `vosr_dit_blocks_00_12.onnx` 的期望值与实际字节只差一个字符，下载完成后必然判为损坏并删除重下。取证方式：让下载器保留被判"损坏"的文件，与 curl 直连下载的已知正常文件逐字节比较完全一致，仅哈希字符串不同；远端 API 的 LFS sha256 与本地字节一致，是手抄清单时写错。

叠加的工程缺陷让问题更难自愈：下载层无断点续传、整体 15 分钟超时、失败即删 `.partial`；页面恢复上次选择就触发下载；每次进入都要全量哈希 3.3 GB；校验器把"文件被临时占用"也当作内容不符（`File.OpenRead` 抛 IOException 被 `catch` 吞成 false）。

修复：

- **修正两处哈希**，并用九张图的实际字节与 HF API 双重复核（清单校验已纳入诊断入口）。
- **HTTP Range 续传**：`.partial` 以 `FileShare.None` 独占打开后再取偏移并据此请求（GUI 与 CLI 并发不会重复追加）；`206` 校验 `Content-Range.From`，`200`（不支持 Range）回退整下，`416` 或内容不符时清空片段并对同一地址从头重下一次（仅一次）。
- **空闲超时替代整体超时**：`HttpClient.Timeout` 设为无限，改由 90 s 无数据超时判定，每次读到数据即重置；未收满声明长度视为可续传的中断并保留片段。
- **校验器容错**：以 `FileShare.ReadWrite | Delete` 打开、IO 异常重试 3 次，并按（长度, 最后写入时间）缓存结果，避免把被杀毒/索引器短暂占用的正确文件判成损坏。
- **不再由页面恢复触发下载**：`IViewState.RestoreState` 期间抑制自动下载，只有用户主动改选模型才调用 `EnsureModelAsync`；提示文案改为"重新选择该模型即可继续下载（支持断点续传）"。
- **按模型单飞**：同一模型的进行中任务被复用，重复选择或并发入口不会并发拉取。

回归入口：`FfmpegSmokeTest --model-resume`（13 项，覆盖续传偏移、重复调用零请求、并发去重、忽略 Range、截断保留片段、垃圾片段自愈）；`--image-models-only` 断言"恢复页面状态不触发下载"；`--model-live <url> <file> <sha> <dir>` 用真实网络验证单条目落盘哈希。`hf-mirror.com` 实测返回 `206` 与 `Content-Range`，前缀 + 续段拼接的 SHA-256 与整体下载一致。

## 复现

```powershell
# 九张图（约 3.35 GB）放在任意目录，指向该目录运行
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- .qa/vosr-smoke --image-vosr <模型目录>
```

CLI 亦可直接调用：`fancy-tool image <in> <out> --sr --sr-model vosr-1.4b`（首次使用需 `models install --image vosr`）。
