# 0022：移除歌词提取功能

日期：2026-11-04

## 背景与约束

- 歌词提取（Qwen3-ASR 本地识别 + 对齐 + 多格式导出）自 2026-10-01 起已因「本地模型效果不佳」关闭 GUI 入口（提交 831c5d8，`MainWindowViewModel.LyricsExtractionEnabled = false`），页面与服务代码保留待观察。
- 2026-11-03 的 CLI（ADR 0021）把 `lyrics extract` / `lyrics export` 作为正式命令暴露，与 GUI 的刻意隐藏形成不一致；同日实测端到端前，用户确认不再需要该功能，决定整体移除。
- 移除范围包含仅在歌词功能中存在的依赖：`External/Lyricify.Lyrics.Helper`（文件格式加解密与生成、网易/QQ 网络对照）与 `_tools/LyricsSmokeTest`。

## 决策

彻底移除歌词提取功能，不留隐藏开关：

- **整删**：`Views/LyricsExtractionView.*`、`ViewModels/LyricsExtractionViewModel.cs`、`FancyTool.Core/Services/Lyrics/`（10 文件）、`FancyTool.Core/Models/LyricsModels.cs`、`FancyTool.Cli/Commands/LyricsCommands.cs`、`Resources/LyricsModels/*.json`（Qwen3 ASR/Aligner 下载清单与日文分词字典）、`_tools/LyricsSmokeTest/`、`External/Lyricify.Lyrics.Helper/`、`External/NOTICE-Lyrics.txt`。
- **工程引用**：Core 与 Ava 两个 csproj 的 Lyricify `ProjectReference`、两份 `Licenses\Lyricify-*.txt` 拷贝项、Core 的歌词清单 `EmbeddedResource` 与 `InternalsVisibleTo LyricsSmokeTest`、slnx 工程项一并移除；SharpZipLib 随 Lyricify 消失（主工程无直接引用）。
- **装配与目录**：GUI 服务注册、导航注册、首页磁贴映射、CLI 分发表（`CliHandlers`）、`ModelsCommands` 的 `--lyrics/--lyrics-high`、`CommandCatalog` 的 lyrics 命令组与 SKILL.md/AGENTS.md 渲染全部移除。命令目录变为 **18 组 / 56 条**。
- **i18n**：`Lyrics.*` 约 70 键、磁贴/导航键删除；`Settings.Caption_ModelsDirectory` 去掉「默认歌词模型」表述（模型目录仍服务图像超分与视频超分/插帧）。
- **烟测**：`FfmpegSmokeTest --settings-only` 的嵌套迁移 fixture 由 `Qwen3Lyrics/asr` 改为中性的 `nested/asr`；歌词队列恢复校验块（依赖 `LyricsQueueState`/`LyricsExtractionViewModel`）随功能删除。
- **模型数据**：环境变量 `FANCYTOOL_LYRICS_MODELS` / `FANCYTOOL_LYRICS_DATA` 不再被读取；已下载的 Qwen3 模型（约 5 GB）对用户是本地数据，卸载升级不会主动删除，可手动清理 `%LocalAppData%\FancyTool\Models\Qwen3Lyrics`。

## 替代方案

- **保留死代码**：隐藏入口的方案已试运行一个月，模型质量未达可开放标准，继续维护（GPL 合规审计、构建告警、csproj 纠缠）成本大于价值。
- **只删 CLI 命令、保留 GUI 页面**：两侧行为仍不一致，且 GUI 入口本身处于关闭状态，无意义。
- **仅移除入口**：Core 服务与 Lyricify 留作死代码会继续参与构建与许可审计（1076 条 CA 告警全部来自 Lyricify 工程），违背「移除迁移后无效的状态与无用依赖」。

## 后果

- 仓库减少一个外部工程与一个烟测工程；Core 不再引用 Lyricify，`LyricsJsonContext` 等 source-gen 上下文随 `LyricsModels.cs` 删除，构建告警显著下降。
- `audio convert` 的歌词**标签**写入（vorbis LYRICS comment / mp3 TXXX）属音频元数据处理，与本次移除无关，行为不变。
- 已安装在本机各 agent 技能目录的 `SKILL.md` 含 lyrics 小节，属于旧版 CLI 的落盘产物；用户运行 `fancy-tool skill install --force` 后刷新为新目录（18 组 / 56 条）。
- ADR 0005 中 `Qwen3Lyrics` 目录约定、ADR 0021 的 19 组 / 58 条命令计数由本文取代。

## 验证

- `dotnet build` Core / Cli / Ava 三工程 0 错误；slnx 下仅剩既有的 wapproj `MSB4019`（dotnet CLI 不带 DesktopBridge，见 ADR 0021，与本次无关）。
- `BatchSmokeTest` 21/21 通过；`FfmpegSmokeTest --settings-only` 迁移全矩阵通过（含嵌套目录迁移、冲突、回滚、重置与双语绑定）。
- CLI 实测：根帮助 / `models list` / `skill print` 均无 lyrics 内容；`lyrics extract` 返回未知命令；源码 grep 仅剩 `.vs` IDE 缓存（不入库）与 `AudioConvertService.cs` 的歌词标签注释（保留是有意的）。
- 视频超分 / 插帧 CLI 端到端实测（2026-11-04）详见 [ADR 0021 验证补充](0021-cli-project-core-extraction-and-skill-install.md)；原始数据 `.qa/cli-e2e-2026-11-04/measurements.json`。

## 已知限制

- 未重新打包 MSIX / Inno 验证许可文件清单（`Licenses\Lyricify-*.txt` 不再随包分发由 csproj 条目移除保证，下次正式打包时复查）。
- 移除前歌词功能的最后可用版本封存在 git 历史（本 ADR 对应提交之前），如需考古按文件路径检索。
