# 0018 视频转码拼接模式：多段单编码管线

日期：2026-11-01

## 背景

视频转码页此前只支持逐文件独立转码（`Parallel.ForEachAsync`，各自输出）。需求：勾选"拼接模式"后把列表中的视频按当前顺序合并为一个文件；列表支持拖拽排序；每个片段可独立设置循环次数（该片段在成片中重复出现 N 次）。

约束：

- 转码是 100% 进程内 FFmpeg（`FFmpeg.AutoGen` 直调 shared 库），没有 ffmpeg.exe 子进程，不能直接套 `-f concat` 的 CLI 方案；
- 既有单文件管线 `ConvertOnce`（解复用 → 解码 → 滤镜图 → 编码 → 封装）是全矩阵烟测覆盖的热路径，不应为拼接重构它；
- 用户素材常为异构（不同分辨率 / 帧率 / 编码 / 有无音轨），拼接必须对这些差异健壮。

## 决策

### 管线：单遍多段解码、一次编码（而非中间文件 + concat remux）

新增 `IVideoTranscodeService.ConcatTranscodeAsync`（实现于独立分部 `Services/VideoTranscodeService.Concat.cs`，完全不改动 `ConvertOnce`）：以**第一段为基准段**建立一套共享的滤镜图 / 视频编码器 / 封装器（以及基准段有音轨时的音频编码器与供料器），随后按顺序逐段（每段按循环次数重复解码）把帧喂入同一管线，输出单文件。

放弃的替代方案：先逐段转码成规格统一的中间文件、再用 concat demuxer 无损 remux——双倍磁盘占用与双次写入，且 concat demuxer 对流参数（编解码器 / 分辨率 / 帧率 / SAR / 时基）一致性要求苛刻，任何偏差在全部编码完成后才暴露，失败模式隐蔽。单遍方案对异构输入天然兼容，且只编码一次。

### 异构段归一化：基准画布 + letterbox

- 共享 buffersrc 的参数固定为基准段的宽高 / 像素格式 / SAR；基准段解码为 PAL8（GIF 源）时画布改用 BGRA（sws 不支持以 PAL8 为目标格式）。
- 与基准画布不一致的段：`sws_getCachedContext` 等比缩放（保持显示宽高比）居中写入画布，空余区域用 `av_image_fill_black` 预填的黑底（子采样平面偏移按像素格式描述符换算）。参数与 DAR 一致的段直通，无转换开销。
- **时间轴固定 1/90000**（对常见帧率整除性好），不用基准段自身的流时基——粗时基会让高帧率段逐帧重标定后 pts 重复，muxer 报非单调 DTS（烟测实测）。编码器标称帧率取**全部段的最高帧率**，保证最细帧距 ≥ 1 tick；输出按各段真实时间戳构成 VFR（MP4/MKV 均支持）。
- 音频：共享编码器参数由基准段派生；每段用自己的解码器参数独立建 `SwrContext` 重采样到共享编码器格式。段内裁剪复用 `TrimTimeline`；进入新段时经 `EncoderFrameFeeder.AddStartSamples` 把音频定位到全局时间轴。缺音轨的段该区间静默（时间轴留空）。

### UI / VM

- `IsConcatMode`（VM 属性，随 ViewState 持久化；每项 `LoopCount` 与 Trim 一样不持久化）；
- 拖拽排序：Avalonia `ListBox` 无内置 reorder，新增附加行为 `Helpers/ListBoxDragReorder.cs`（进程内私有 `DataFormat` 与 Explorer 文件拖入区分；DragOver 按命中项中线上/下实时重排），仅拼接模式且非转码中启用；
- 拼接路径把服务的全局进度按"各段有效时长（裁剪后 × 循环次数）"还原为当前段 + 段内进度，统计与预览帧路由到当前段对应的列表项；
- 拼接是单次编码会话，段间无暂停粒度：隐藏暂停按钮（停止仍可用，取消后当前段回 Pending、临时文件由服务清理）。

## 已知限制（v1）

- 拼接模式不支持 AI 超分与插帧（其管线从单一输入派生）；VM 与服务双侧拒绝；
- 拼接解码固定走软件路径（NVDEC 不参与）；硬件**编码**不受影响；
- 混合 HDR/SDR 段以基准段的色彩决策为准（色调滤镜串只按基准段构建），跨段色彩偏差不做逐段色调映射；
- 段间 A/V 边界按各自流的自然时长对齐，极端情况下边界处有毫秒级间隙/重叠（与 concat filter 行为一致）。

## 验证

烟测 `FancyTool/_tools/FfmpegSmokeTest/ConcatTests.cs`（默认随全量矩阵执行，`--concat-only` 单独运行），全部走 `ConcatTranscodeAsync` 真实代码路径，2026-11-01 全部通过（14/14）：

```powershell
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- --concat-only .qa/ffmpeg
```

覆盖：同构 3 段拼接时长、单段循环 ×3、裁剪 × 循环组合、异构分辨率/帧率/宽高比（输出画布 = 首段 + letterbox 像素断言：黑边行暗、内容区亮）、有音/无音段混合（音频样本数只计基准段）、GIF、MKV/VP9/Opus、两通、非零时间戳源、进度单调且终值 1、预取消（异常类型 + 输出与临时文件不残留）、非法输入（空列表 / 超分选项 / 缺文件）。

未实测：Linux/VAAPI 后端拼接、真实 NVENC 硬编拼接（Windows 本机无对应驱动环境时烟测按既有惯例记 SKIP）、MSIX 打包下的 UI 行为。
