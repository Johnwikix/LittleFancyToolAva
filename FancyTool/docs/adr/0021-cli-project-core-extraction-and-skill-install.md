# 0021：CLI 独立项目、Core 抽取与首次启动技能安装

日期：2026-11-03

> 2026-11-04 补充：歌词提取功能已整体移除（[ADR 0022](0022-remove-lyrics-extraction.md)），本文中的歌词命令、Lyricify 引用与「19 个命令组 / 58 条命令」计数按 **18 组 / 56 条** 理解；视频超分 / 插帧的 CLI 端到端实测见文末补充（三）。

## 背景与约束

需求是把 GUI 的全部工具能力同时提供命令行入口，并让本机 AI agent 知道这些命令怎么用：

- CLI 必须是独立工程，公共逻辑抽取到共享项目，不能靠 GUI 工程引用或复制一份实现；
- 每一条 GUI 功能都要有对应命令，且每条命令自带 `-help`；
- MSIX 发布要携带 CLI，安装后在命令行里可直接调用；
- GUI 首次启动要询问是否把 CLI 用法写入本机的技能 / `AGENTS.md`。

约束：

- GUI 是 NativeAOT 自包含发布（ADR 0009 / ADR 0016），MSIX 路径又是托管发布，两种形态的产物目录和依赖裁剪策略不同；
- 服务层原先直接依赖 Avalonia（`Dispatcher.UIThread`、`Bitmap`）与 Lang.Avalonia（`LocalizationRegistry`），CLI 不能拉起 UI 栈；
- 进程内 FFmpeg 是 GPL shared 库（ADR 0002），必须与可执行文件同目录；
- MSIX 不能随意写系统 PATH，写入 `Program Files` 也不被包身份模型允许；
- 命令行帮助、技能文档、`AGENTS.md` 段落三者描述的是同一批命令，任何一份单独维护都必然漂移。

## 决策

### 工程拆分：Core 承载全部非 UI 逻辑

新增 `FancyTool/FancyTool.Core/FancyTool.Core.csproj`（`AssemblyName=FancyTool.Core`，`RootNamespace=FancyToolAva`，沿用 `WindowsInference.props` 的双 TFM），把算法、服务、模型、音频 DSP、FFmpeg 绑定、歌词、网络与串口整体从主工程移入；`FancyToolAva.csproj` 保留 View / ViewModel / Avalonia 装配，通过 `ProjectReference` 使用 Core。Core 的 `InternalsVisibleTo` 列出 GUI、CLI 与各烟测工程，内部类型不需要为 CLI 放宽成 public。

新增 `FancyTool/FancyTool.Cli/FancyTool.Cli.csproj`（`AssemblyName=fancy-tool`，`RootNamespace=FancyToolAva.Cli`），只引用 Core。可执行文件名固定为 `fancy-tool.exe`——MSIX 执行别名与安装包 PATH 都按这个名字注册，改名会同时破坏两处。两个工程都加进 `FancyToolAva.slnx`（x64 平台映射）。

FFmpeg shared 库、`i18n/*.json` 与 `EnsureFFmpegSharedLibraries`（缺库时 `FT1001` 中断）在 CLI 工程里各自复制一份声明，保证独立 CLI 目录也能直接运行。

### Core 去 UI 化：四个接缝

不引入条件编译分叉，改为四个明确边界：

- 调度：`Abstractions/IUiDispatcher` + Core 内的 `ImmediateUiDispatcher`（直接在当前线程执行）。GUI 侧 `Services/AvaloniaUiDispatcher` 包一层 `Dispatcher.UIThread`；CLI 注入立即执行实现，服务代码统一面向接口。
- 本地化：`LocalizationRegistry.Provider` 抽象出取词来源。GUI 继续用 Lang.Avalonia（`Services/LangLocalizationProvider`），CLI 用 `Services/JsonLocalizationProvider` 直接读随包的 `i18n/*.json`。同一份资源、同一套键，Core 的服务错误消息两侧一致。
- 图片预览：接口返回 PNG 字节而不是 `Avalonia.Media.Imaging.Bitmap`（`Base64ToPreviewPngAsync` / `LoadPreviewPngAsync`），GUI 在 `Helpers/ImagePreviewHelper` 里把字节还原成 `Bitmap`。编码链路本就走 SkiaSharp，UI 层不承担解码。
- 偏好：`ISuperResolutionPreferences` 由 GUI 的 `AppPreferences` 实现，CLI 提供读 `preferences.json` 的轻量实现，Core 不反向依赖 UI 模型。

### 命令目录是单一来源

`FancyTool.Core/Cli/CommandCatalog.cs` 用 `CliCommandSpec` / `CliOptionSpec` 记录全部命令（分组、摘要、用法、选项、示例，中英双份）。CLI 的根帮助、分组帮助、单命令帮助，`skill print` 的 markdown，写进 `SKILL.md` / `AGENTS.md` 的段落，全部由这份目录渲染。新增命令只改目录 + 注册 handler，帮助与文档自动跟随。

当前 19 个命令组、58 条叶子命令，覆盖 GUI 全部工具页：对称/非对称加密与密钥生成、哈希、Base64、文件批量加解密与文件夹比较、图片 Base64/转换/超分、视频探测/预览/转码/拼接、音频探测/转换、串口、TCP/UDP 调试与链路测试、模型目录与下载、歌词提取与导出、内置 FFmpeg 信息、技能安装。

批处理语义与 GUI 对齐：文件 / 图片 / 音频 / 视频 / 歌词接受重复 `-i` 或目录，输出用 `-o` 或 `--out-dir`（自动建目录、同名自动加序号）；`video concat` 用可重复的 `--segment file[;loops[;start;end]]` 表达 GUI 的段列表 + 每段循环 + 裁剪。`file keygen` 写出的 `{"Key","Iv","Format"}` 与 GUI 的 `KeyIvDto` 完全互通。

### 解析器手写，不引 System.CommandLine

`FancyTool.Cli/CliArgs.cs` 手写：别名规范化（`-i` → `--input`）、`--name=value`、可重复选项、保序成对选项（`--segment`）、未知选项报错。原因有二：`System.CommandLine` 仍是预览包且与 NativeAOT 的裁剪行为需要额外验证；更关键的是它的自动帮助与本仓库「帮助文本必须来自 `CommandCatalog`」冲突，包一层反而更复杂。用法错误抛 `CliUsageException`。

退出码固定：`0` 成功、`1` 运行失败、`2` 用法错误（含未知命令）、`3` 许可未通过、`130` 用户取消。进度与状态写 stderr，结果（人类可读或 `--json`）写 stdout，便于管道。

### CLI 发布为托管自包含，不跟随 GUI 的 AOT

`FancyTool.Cli.csproj` 显式 `PublishAot=false`，自包含托管发布到独立目录 `<publish>\cli`。理由：GUI 的 AOT 产物是根目录那一组文件，CLI 若共享同一目录会与 apphost / 裁剪描述冲突；若自己再跑一遍 AOT，则要为 ONNX Runtime、Windows ML 投影、FFmpeg.AutoGen 的运行时注册单独验证，收益（体积/启动）不足以抵掉风险。`publish-win.ps1` 在 GUI 发布之后追加 CLI 发布（`-SkipCli` 可跳过）。

### 2026-11-03 更新：技能安装扩展到全部主流 agent

SKILL.md 已成为跨厂商的 Agent Skills 开放标准，技能安装从"只写 Qoder"扩展为六家同装（`SkillInstaller.Agents` 注册表为单一来源）：

| agent | 技能目录 |
|---|---|
| Claude Code | `~/.claude/skills/fancy-tool/` |
| Cursor | `~/.cursor/skills/fancy-tool/` |
| Gemini CLI | `~/.gemini/skills/fancy-tool/` |
| Codex | `~/.codex/skills/fancy-tool/` |
| GitHub Copilot CLI | `~/.copilot/skills/fancy-tool/` |
| Qoder | `~/.qoder-cn/skills/fancy-tool/` |

- **默认全部安装**，不检测本机是否装有该 agent：未装的 agent 目录提前创建，装上即用；`--agent <all|短名列表>` 可选子集。
- **AGENTS.md 类常驻指令文件不扩展**（各家的 CLAUDE.md / GEMINI.md / instructions.md 等价物）：它们是全量常驻上下文，写入命令清单会污染每个会话；SKILL.md 由 frontmatter 按需触发才是正确机制。Qoder AGENTS.md 受管块保留为可选附加（CLI `--agents-md`，GUI 首启次按钮）。
- CLI 参数由 `--target <qoder|agents|both>` 改为 `--agent` + `--agents-md`；安装 / 幂等 / `--force` 守卫 / 卸载清理语义逐 agent 不变，`SkillInstallResult.Target` 用 agent 短名。

### 安装后可直接调用：MSIX 用执行别名，Inno 改用户 PATH

- **MSIX**：`Package.appxmanifest` 增加第二个 `Application Id="Cli"`（`Executable="cli\fancy-tool.exe"`、`EntryPoint="Windows.FullTrustApplication"`、`AppListEntry="none"` 不出现在开始菜单），并声明 `uap5:Extension` 的 `windows.appExecutionAlias`，别名 `fancy-tool.exe`。Windows 自动把别名目录加入 PATH，不需要安装器改注册表。`FancyToolAva.csproj` 的 `PublishCliForMsix` 目标在 `FancyToolMsixBuild=true` 时把 CLI 发布到 `$(PublishDir)cli`。
- **Inno Setup**：`[Setup]` 加 `ChangesEnvironment=yes`，`[Code]` 在 `ssPostInstall` 把 `<app>\cli` 追加进 `HKCU\Environment\Path`（先查重），卸载时按同样的边界匹配删除；用 `RegWriteExpandStringValue` 保留原值中的 `%USERPROFILE%` 之类展开项。写 HKCU 而非 HKLM，避免为了 PATH 要求管理员权限（与 `{autopf}` + `PrivilegesRequiredOverridesAllowed=dialog` 的非提权安装路径一致）。

MSIX 模式下 CLI 仍带许可门：`CliLicenseGate` 在无窗口的进程里先核对 `Package.Current` 的 PFN，再走 `StoreContext.GetAppLicenseAsync()`，试用过期 / 未激活 / 查询异常一律拒绝（沿用 ADR 0016 的严格策略与 ADR 0017 的真实身份）；非 MSIX 编译直接放行。

### 首次启动询问写入技能，内容可回滚

`AppPreferences.CliSkillPromptCompleted` 保证只问一次（选择「暂不」也算完成，不重复打扰）。GUI 在许可检查通过后弹出 `FAContentDialog`：主按钮 = 只装 Qoder 技能，次按钮 = 技能 + 用户级 `AGENTS.md`，关闭 = 暂不。目标路径来自 `SkillInstaller`：`%USERPROFILE%\.qoder-cn\skills\fancy-tool\SKILL.md` 与 `%USERPROFILE%\.qoder-cn\AGENTS.md`。

`FancyTool.Core/Cli/SkillInstaller.cs` 的写入规则：

- `SKILL.md` 整体是本工具生成的内容，YAML frontmatter 之后插入一行生成标记，`--force` 才覆盖用户已有文件；
- `AGENTS.md` 只动 `<!-- BEGIN FANCY-TOOL CLI ... -->` / `<!-- END ... -->` 之间的托管块，其余用户内容原样保留；
- 写入用临时文件 + `File.Move(overwrite)` 原子替换，统一 CRLF；`UninstallAsync` 反向删除技能目录与托管块。
- 环境变量 `FANCYTOOL_SKILL_HOME` 可重定向根目录，供验证与测试使用。

CLI 侧对应 `fancy-tool skill install|print|uninstall`，这三条命令不受许可门限制（否则未授权时连「怎么关掉写入」都执行不了）。

## 替代方案

- **在 GUI 工程里加一个 `args.Length > 0` 分支**：省一个工程，但 CLI 会被迫继承 Avalonia 依赖与 GUI 的发布形态，且 AOT/托管两条发布路径互相牵制。
- **CLI 也上 NativeAOT**：启动更快、体积更小，但要为 ONNX Runtime / Windows ML / FFmpeg.AutoGen 单独验证运行时绑定，本次收益不明确。
- **CLI 直接安装到 GUI 输出目录**：省一个 `cli\` 子目录，但两 apphost 同目录时裁剪清单与依赖消歧不可控。
- **MSIX 里也改注册表 PATH**：包外写入需要提权且升级后残留，执行别名是官方机制。
- **首次启动直接写入不问**：往用户主目录写文档属于副作用，必须显式同意。
- **把用法一次性塞进 `AGENTS.md`**：`AGENTS.md` 是用户手写的长期上下文，塞 58 条命令既难维护也难以卸载；托管块 + 技能文件让 agent 能发现、用户能删除。
- **命令帮助与技能文档各写一份**：一定会漂移，故由 `CommandCatalog` 单源渲染。

## 后果

- 仓库多一个 Core 工程，主工程文件数下降但三工程引用关系需要在 `slnx` 与 `global.json` 下同时维护；新增服务要注册两次（GUI 的 `App.axaml.cs` 与 CLI 的 `CliServices.Build`），CLI 的容器只装配自己用到的服务。
- CLI 与 GUI 共享 `%LocalAppData%\FancyTool`（模型目录、`preferences.json`、日志）。`models dir set` 会改到 GUI 也读取的模型根目录，属预期的单一来源。
- 帮助、技能、`AGENTS.md` 三处文本永远与命令实现同步；新增命令只改目录和 handler。
- CLI 独立自包含目录，安装体积明显增加（GUI 产物之外再有一份 .NET 运行时 + FFmpeg 库）。
- MSIX 的执行别名要求包已注册才能解析，开发期 `bin` 目录里的 `fancy-tool.exe` 与别名指向的是不同实例。

## 验证

构建与帮助覆盖（2026-11-03，本机全部通过）：

```powershell
dotnet build FancyTool/FancyTool.Cli/FancyTool.Cli.csproj -c Debug   # 0 警告 0 错误
# 58 条命令逐条 -help，退出码 0 且输出非空：中英双语各一轮
dotnet run --project FancyTool/_tools/BatchSmokeTest -- .qa/batch-smoke   # runs=21 failures=0
```

`dotnet build FancyToolAva.slnx` 中 Core / CLI / GUI / Lyricify 均通过，唯一失败是既有的 `FancyToolAva.Msix.wapproj`（`MSB4019`，dotnet SDK 不带 `Microsoft.DesktopBridge.props`，与本次改动无关，MSIX 仍需在 Visual Studio 里生成）。

FFmpeg 相关代码整体搬进 Core，因此重跑全矩阵确认迁移没有改变行为：`dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- .qa/ffmpeg-core` → `total=127 failures=0 skips=2`（NVENC / AMF 因本机缺 `nvcuda.dll` / `amfrt64.dll` 按惯例 SKIP，日志 `.qa/ffmpeg-core-smoke.log`）。

`skill install --target both` 在 `FANCYTOOL_SKILL_HOME` 指向的临时目录下写入 `%HOME%\.qoder-cn\skills\fancy-tool\SKILL.md` 与 `%HOME%\.qoder-cn\AGENTS.md`；重复执行两条都报告「未更改」；已有用户内容的 `AGENTS.md` 只增删受管区块，卸载后确认 `# My Agents` / `handwritten note` 原文保留、`SKILL.md` 与其目录删除；`--lang zh|en` 下安装与卸载消息两侧均为本地化文本。

功能实测走真实服务（`FfmpegService` / `VideoTranscodeService` / `AudioConvertService` / `IFileEncryptionService` / 网络服务），命令与产物留在 `.qa/`：

- 加解密链路互操作：`aes`/`des`/`sm4` keygen→encrypt→decrypt 往返一致；`file keygen` 产物的键文件可被 GUI 与 `file encrypt/decrypt` 双方使用；`hash md5/sha*/sm3` 与已知向量一致；`base64` 与 GUI 行为一致（Git Bash 会把 `+/…` 参数当路径转换，验证时改用 `--stdin`，不是 CLI 缺陷）。
- 媒体：`video probe` / `preview`（PNG 落盘）/ `transcode`（含 `--out-dir` 批量）/ `concat`（多段 + 循环 + 裁剪）/ `audio probe` / `convert`。
- 网络：`serial list`；`tcp server` + `tcp client` 回环收到 `70 69 6E 67` / `ping`，连接与断开消息已本地化（此前直出 `ServiceStatus.Tcp_ClientConnected` 键名，已通过初始化 `LocalizationRegistry.Provider` 修复）；`udp bind` + `udp send`；`linktest` 四种模式产出汇总 JSON。
- 技能：`skill install --target both` 在 `FANCYTOOL_SKILL_HOME` 指向的临时主目录下生成 `SKILL.md`（frontmatter 完整、标记在 `---` 之后）与带托管块的 `AGENTS.md`；重复执行报告「无变化」；`skill uninstall` 两处都清掉且不动用户段落。
- GPU 图片超分：`video preview` 取帧 → `image convert --max-dimension 160` → `image convert --sr-model realesr-general-x4v3 --sr-scale 2` 输出 320×240，实测 4.9 秒，走 `ISuperResolutionService` 真实推理路径（`.qa/cli-sr/`）。
- GUI 首次启动对话框经 lvt 非侵入检查确认渲染与主按钮路径；「技能 + AGENTS.md」次按钮路径由 CLI 同一 `SkillInstaller` 覆盖验证。
- 打包：MSIX 模式发布产物含 `cli\fancy-tool.exe`，`makeappx pack` 通过清单与别名校验；Inno 脚本 `iscc` 编译通过，`[Code]` 的 PATH 追加/删除逻辑经脚本审查（Git Bash 需 `MSYS_NO_PATHCONV=1` 才能把 `/D` 开关传给 `iscc`）。

### 2026-11-03 补充：真实 wapproj 打包打通与两处构建修复

本机装上 VS 18 Community 后首次真实运行 wapproj 打包，暴露两个问题并已修复（详见 changelogs 2026-11-03）：

1. **库工程平台传染（CS0006）**：Core / Lyricify 未声明 `Platforms`，x64 引用方把 `Platform=x64` 传染给它们，期望 `obj\x64\...\ref\` 引用程序集；该目录只在恰好有人以 x64 构建过库时存在。与 ADR 0009 当年 Lyricify 的 CS0006 同构，本次在普通（非隔离）链上复发。修复为三处 `ProjectReference` 钉 `SetPlatform="Platform=AnyCPU"` + slnx 映射同步 AnyCPU。
2. **DesktopBridge 负载不收 Exec 产物（APPX0703）**：负载来自入口工程 `DesktopBridgePublishItemsOutputGroup` 项清单（`ResolvedFileToPublish`），不枚举发布目录。修复：`AddCliToDesktopBridgePublishItems` 注入输出组 + wapproj `RebaseCliPayloadToPackageRoot` 把 CLI 项重定位到包根 `cli\`（CreateWapProjPackageFiles 给入口工程全部输出加 `FancyToolAva\` 前缀，与清单字面值和 Upload 变体的剥前缀行为冲突；CLI 因此落在包根 `cli\`，GUI 仍在 `FancyToolAva\`）。

### 2026-11-03 补充（二）：VS IDE 生成走另一条负载通道

上述修复只覆盖命令行打包（`_IsGeneratingAppxPackage=true` 消费 `DesktopBridgePublishItemsOutputGroup`）。**VS IDE 内生成 wapproj**（`BuildingInsideVisualStudio=true` → `_IsGeneratingAppxPackage=false`）时 DesktopBridge 改用 `PackageOutputGroups`（`BuiltProjectOutputGroup` 等构建输出组）收集负载，CLI 依旧缺席而报 APPX0703。补充修复：`AddCliToBuiltOutputForIdePackaging` 把 CLI 发布输出注入 `BuiltProjectOutputGroupOutput`，**必须 `BeforeTargets="BuiltProjectOutputGroup"`**——MSBuild 的 `AfterTargets` 修改的项不会进入被挂 target 的 `Returns`（Returns 在本体完成时捕获，早于钩子执行），而 IDE 模式的 wapproj 直接消费该 Returns。两种模式的 CLI 发布落点不同（命令行=msixpublish、IDE=默认 `$(OutputPath)publish\`），注入时两个候选都探测。验证：IDE 模拟构建（`-p:BuildingInsideVisualStudio=true`）exit 0，appxrecipe 负载含 cli 250 项且包内路径为包根 `cli\`；命令行打包模式回归不受影响。

冷启动（清空 x64 / msix 中间产物）验证 `MSBuild FancyToolAva.Msix.wapproj -restore -p:Configuration=Release -p:Platform=x64` 完整出包，主 / ForBundle / Upload 三份生成的 AppxManifest 中 CLI `Executable="cli\fancy-tool.exe"` 与包内文件一致；slnx Release|x64 回归通过。

### 2026-11-04 补充（三）：视频超分 / 插帧 CLI 端到端实测；歌词功能移除

- **视频超分 / 插帧实测通过**（Intel Arc 140T iGPU，DirectML + OpenVINO 厂商路径；Debug 构建 CLI，`FANCYTOOL_DATA_DIRECTORY` 沙盒，走 `VideoTranscodeService` 真实推理；输入 160×120@10fps 4s 测试片）：
  - `--sr` ×2：ravu-lite-r2（DirectML，6.2s）、anime4k-cnn-s（厂商，2.7s）、fsrcnnx-s（3.1s）、ravu-lite-r3（fp32，5.3s）均输出 320×240；`anime4k-cnn-s --sr-fp16` 在 Intel 厂商 EP 上同样通过。
  - `--interpolate rife`：rife425-lite ×2（3.5s）与 rife425 ×2（3.0s）输出 20fps，rife425-lite ×4 输出 40fps。
  - 组合 `--sr anime4k-cnn-s ×2 + rife425-lite ×2` 输出 320×240@20fps（4.9s）。
  - rife425 首次下载报 `All download endpoints failed`（与歌词模型下载并发时的瞬时网络问题），curl 验证两端点 206，重试 5.5s 成功；错误传播与退出码正确。
  - 原始数据与命令记录：`.qa/cli-e2e-2026-11-04/measurements.json`。显式 `--sr-backend` / `--interpolation-backend` 覆盖组合未测（沿用 ADR 0008 的产品默认 auto）。
- **歌词提取功能整体移除**（用户决策，本地模型效果不达预期且不再需要）：命令目录变为 18 组 / 56 条，详见 [ADR 0022](0022-remove-lyrics-extraction.md)。下文「未实测」清单中的歌词项由此关闭。

## 已知限制与未验证项

- **未做**：真机 MSIX 安装后的 `fancy-tool` 别名调用（wapproj 真实打包已于 2026-11-03 在 VS 18 Community MSBuild 下通过并校验包内清单与负载一致，但包未安装到系统）；Inno 安装/卸载后 `HKCU\Environment\Path` 的实际增删（只做到编译与逻辑审查）；Linux `.deb` 尚未打包 CLI（`publish-linux-deb.ps1` 未接入，需要单独决定 `.deb` 的 `cli` 落点与 `/usr/bin` 链接）。
- **未实测**：视频超分 / 插帧（`video transcode --sr` / `--interpolate`）与歌词提取的 CLI 端到端运行——本机模型目录只有四款图片超分模型，VideoSr / RIFE / 歌词模型均未安装（`models list` 全部 `[ ]`），跑真实推理需要先下载数 GB 模型；命令与参数映射已接通并复用 GUI 同一服务，结论只到「代码路径存在」。图片超分已实测，见上。→ 2026-11-04 更新：视频超分 / 插帧已实测通过、歌词功能已移除，见补充（三）与 ADR 0022。
- 首次启动对话框的次按钮（技能 + `AGENTS.md`）未在 UI 上点选验证：lvt 合成按键的 Tab 顺序落在主按钮；该分支经 CLI `skill install --target both` 覆盖。
- `AGENTS.md` 托管块只保证不覆盖用户内容，不保证与其他工具的同名托管块兼容。
