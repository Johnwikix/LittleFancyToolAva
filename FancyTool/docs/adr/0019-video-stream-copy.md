# 0019 视频转码流复制：编码器下拉「Copy」与 packet 直通

日期：2026-11-01

## 背景

视频转码一直是全解码→滤镜→重编码管线（`ConvertOnce`）：换容器（如 MKV→MP4）时即使源编码已与目标容器兼容也必须整条重编码，耗时且有画质损失。需求：允许不重编码直接复制流（stream copy / remux），视频与音频可独立选择。

约束：

- 进程内 FFmpeg 直调（无 ffmpeg.exe），`-c copy` 的 CLI 语义需要自己实现 packet 直通；
- `ConvertOnce` 是全矩阵烟测覆盖的热路径，直通分支必须局部化，不重构现有管线；
- 转码管线还有滤镜（缩放/裁剪/FPS/去隔行/降噪）、AI 超分、插帧、HDR 色彩转换、两通、硬件编解码、拼接模式，全部依赖解码后的帧，与流复制互斥；
- 批处理下各文件源编码不同，同一目标容器对某些文件可能无法流复制。

## 决策

### 形态：编码器下拉各加「Copy」，四组合全支持

`VideoCodec` / `AudioCodec` 枚举各加 `Copy` 成员；VM 过滤列表在非 GIF、非拼接容器时于列表末尾追加 `"Copy"` 标签（硬编码英文，与 "GIF"/"AAC"/"None" 一致）。四种组合（视频 copy/重编码 × 音频 copy/重编码）都可用——"视频 copy + 音频转 AAC"（异构 MKV 转 MP4 的最常见诉求）是真实场景。

放弃的替代方案：独立「快速重封装」入口（跳过全部解码）——无法表达混合组合，且多一个入口与现有参数区交互语义割裂。

### 服务端：ConvertOnce 内部分支 + WriteCopiedPacket

`ConvertOnce` 入口计算 `videoCopy` / `audioCopy`：

- copy 的流跳过解码器 / 滤镜图 / 编码器创建，输出流用 `avcodec_parameters_copy` 从输入流整体拷贝参数（**codec_tag 清零**，输入容器的 tag 对目标 muxer 未必合法——如 `mp4a` 进 Matroska 会被拒），`time_base` 取输入流；
- 主循环中 copy 流的包走 `WriteCopiedPacket`：时间戳平移（首包为原点，pts/dts 同减）→ `av_packet_rescale_ts` 到输出流时基 → `av_interleaved_write_frame`，进度按 packet 时间戳报告（帧计数以包计）；
- 冲洗段对 copy 流无操作（无编码器可冲）。

### 容器兼容性：预检 + muxer 兜底，批处理按文件失败报告

`avformat_query_codec` 返回 0（明确不支持）时抛 `Msg_CopyCodecUnsupported`。注意现代 FFmpeg 的 tag 表相当宽容（Vorbis 写 MP4 走 `mp4a` 私有扩展、VP9 有 riff `VP90` tag 都被放行），预检不触发时由 `avformat_write_header` / 写包阶段自然失败兜底（如 VP8→MOV 的 "muxing is currently not supported"）——两道防线都保证不产出坏文件。

批处理语义（用户确认）：无法流复制的文件**失败并在明细报告**，不做自动回退重编码（显式、可预期，与现有批处理失败明细一致）。

### 裁剪：关键帧近似，与 `-ss -c copy` 同语义

copy 支持时间段裁剪：trim 起点复用现有 seek（`max(0, start-1)` 落到关键帧），视频从读到的首个关键帧开始写，音频从 seek 点整段直通（A/V 起点天然一致），终点按绝对时间轴精确截断。输出窗口 ≈ [关键帧 ≤ start, end]，起点可能比 start 早最多一个 GOP——这是 remux 的标准近似，不额外解码对齐。

### 互斥守卫：三层

1. **UI**：选 Copy 时画质 Tab（硬件/CRF/码率/两通/HDR/超分）与滤镜 Tab（缩放/裁剪/FPS/去隔行/降噪/插帧）整体 `IsEnabled=false`（参数保留，切回重编码不丢）；音频 Copy 禁用码率与增益滑块；
2. **BuildOptions 兜底**：VM 构造 options 时强制忽略冲突项（`ScaleMode=None`、`HdrOutput=SameAsSource`、`TwoPassEnabled=false`、`HardwareBackend=Software`、`AudioGainDb=0` 等），用户参数值不动；
3. **服务端校验**：`ConvertOnce` / `ConcatTranscodeAsync` 入口对冲突组合抛 `Msg_CopyConflictingOptions` / `Msg_CopyUnsupportedInConcat`，防 API 直调绕过。

拼接模式（0018 的多段归一化管线）不支持 copy：VM 过滤列表在 `IsConcatMode` 时移除 Copy 项（`EnsureCodecSelectionValid` 自动回落），服务端拼接入口整体拒绝。GIF 容器同理（列表不含 Copy；服务端 `Container==Gif` 时 Copy 静默失效走 GIF 编码，与 GIF 强制语义一致）。

两通对视频 copy 无意义：`TranscodeAsync` 强制单通。

### 持久化

`SelectedVideoCodec`/`SelectedAudioCodec` 以字符串存入 ViewState：旧存档不含 Copy 不受影响；新存档在旧版本打开回落首项（与被移除的 "WebM" 容器同一既有行为）；还原时经 `EnsureCodecSelectionValid` 校验当前容器/模式是否仍允许 Copy。

## 后果

- 换容器 / 提取片段不再强制重编码：2 秒测试源双流 remux 3ms（对比重编码数百 ms），体积不变；
- copy 流不经过解码器：源使用罕见编码（无软解码器）时 remux 仍可用；
- copy 模式下源帧预览不可用（无解码帧）；帧率统计以包数近似（时间进度精确）；
- 预检覆盖度受 muxer tag 表影响（MKV 无 tag 表返回"未知"放行），最终以写头/写包失败为准——错误消息为 FFmpeg 原生文本，非本地化键。

## 验证

烟测 `FancyTool/_tools/FfmpegSmokeTest/CopyTests.cs`（默认随全量矩阵执行，`.qa/ffmpeg --copy-only` 单独运行），全部走 `TranscodeAsync` 真实代码路径，2026-11-01 全部通过（12/12）：

```powershell
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- .qa/ffmpeg --copy-only
```

覆盖：双流复制（同容器 remux 体积/编码断言 + 换容器 MKV）、视频 copy×音频 Opus / 视频 H.265×音频 copy 混合、trim 关键帧近似（起点对齐关键帧、终点精确）、无音源（audio copy 等同丢弃）、GIF 容器强制重编码、缩放/两通冲突拒绝、拼接拒绝、VP8→MOV 容器拒绝（预检或写头任一防线 + 输出不残留）。

未实测：UI 控件联动与语言切换（人工验证项：copy 选中时画质/滤镜 Tab 禁用、拼接开关后回落、ViewState 还原）；Linux/VAAPI 环境（copy 不涉及硬件路径，风险低）。
