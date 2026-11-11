# 升级到 .NET 11 并移除 SDK 版本固定

日期：2026-11-11。

## 背景与约束

- 原仓库根目录 `global.json` 固定 SDK 为 `10.0.100`（`rollForward=latestFeature`、`allowPrerelease=false`）。开发机仅安装 9.0.321 与 11.0.100，不含 10.0.100，因此删除前 `dotnet --version` 以退出码 155 失败，仓库无法还原或构建。
- TFM 由 `FancyTool/WindowsInference.props` 按 RID 决定：Windows 为 `net10.0-windows10.0.19041.0`（WinRT / Windows ML 投影），Linux 为 `net10.0`。该 TFM 字符串还出现在包引用条件、许可证 ItemGroup、发布脚本、发布配置、烟测脚本路径与文档中，必须同步修改。
- `PublishWithSatoriGC` MSBuild SDK 钉在 `10.0.13-satori.38707290103.1`，其 Runtime / AotSdk / AotTools 包与 .NET 主版本绑定，不能与 `net11.0` 混用；只改 TFM 而不升级该 SDK 会产出主框架与 GC 运行时包不匹配的发布产物。
- [ADR 0007](0007-nanovsr-and-tensorrt-rtx.md) 记录：机器默认使用 .NET 11 RC SDK 时，Avalonia 预编译 XAML 资源缺失，启动报 `No precompiled XAML found for FancyToolAva.App`，当时的处置是用 `global.json` 把 SDK 限制在 .NET 10 稳定系列。本次升级必须复核 GA SDK 是否仍存在该问题。

## 决策

1. **删除 `global.json`**，SDK 解析回退到 .NET 默认规则：选用本机已安装的最高版本。开发机解析结果为 `11.0.100`。
2. **TFM 全量升级**：`net10.0` → `net11.0`、`net10.0-windows10.0.19041.0` → `net11.0-windows10.0.19041.0`；覆盖 `WindowsInference.props`、GUI / Core / CLI 三工程、7 个烟测工程、基于 TFM 字符串的包引用条件与许可证 ItemGroup、`publish-win.ps1`、`FolderProfile.pubxml`、烟测脚本路径、`AGENTS.md` 与 README 双语。历史实测报告中的旧 TFM 路径作为原始证据保留不改。
3. **Satori SDK 同步升级**：`10.0.13-satori.38707290103.1` → `11.0.0-satori.42242472725.2`。SDK 内部 `$(SatoriPackageVersion)` 解析出的 Runtime / AotSdk / AotTools 包为 `11.0.0-satori.42242472725.1`，与主框架同为 11.x，[ADR 0014](0014-satori-runtime-pack-rid.md) 的 RID 结论继续有效。该版本 NuSpec 的 license 表达式为 MIT，与 `docs/commercial-license-audit-2026-10-23.md` 对 10.x 的审计结论一致。
4. **移除 Core 中 `Microsoft.Extensions.Logging.Abstractions` 的显式 PackageReference**：.NET 11 SDK 报 NU1510（框架已自动提供），移除后构建恢复 0 警告。
5. **修复 Core 的 ProjectReference TFM 协商**：SDK 在 `GetTargetFrameworks` 阶段通过 `RemoveProperties` 移除 `RuntimeIdentifier`（`Microsoft.Common.CurrentVersion.targets:1867` 与 `:1883`），`SetPlatform="Platform=AnyCPU"`（2026-11-03 为 MSIX 中间目录隔离引入）又使引用工程不再继承 RID，导致 Linux 交叉构建时 Core 被判为 `net11.0-windows...`，`net11.0` 主工程无法引用。改用 SDK 官方 escape hatch：`SkipGetTargetFrameworkProperties="true"` + `SetTargetFramework="TargetFramework=$(TargetFramework)"`，把引用方 TFM 作为全局属性钉给 Core；`SetPlatform` 保留以维持 MSIX 隔离链。

## 替代方案

| 方案 | 未采用原因 |
| --- | --- |
| 保留 `global.json`，改为 `11.0.100` + `latestFeature` | 构建可重复性最好，但仍是版本固定，不满足"去掉限制"的要求；且未来只装 11.0.2xx 时会受同 feature band 规则限制 |
| `rollForward=latestMajor` + `allowPrerelease=false` | 保留文件约束，可在缺 10.0.100 时滚到 11.0.100，但预览主版本的选取行为仍需单独管理 |
| Satori 选 `11.0.0-satori.38707303131.1`（与原 10.0.13 同批构建） | 更接近原实验状态，但缺少后续修复；实际验证选择的是 11.0.0 系列最新构建 |
| 移除 Satori SDK、回退默认 GC | 改动最小，但会改变已验证的自包含 NativeAOT 发布形态与 ADR 0014 的结论，未采用 |

## 后果

### 影响面：移除 `global.json`

- SDK 不再由仓库锁定，构建可重复性下降。其他机器 / CI 必须预装支持 `net11.0` 的 SDK（>= 11.0.100），否则报 NETSDK1045；未来若安装更高主版本 SDK（含预览版），会被自动选中。需要可复现构建时，在 CI 显式安装 11.0.100，或临时补一份 `global.json`。
- 本机对照：删除前 `dotnet --version` 退出码 155 并提示缺少 10.0.100；删除后解析为 `11.0.100`。
- [ADR 0004](0004-windows-ml-and-efficient-sr.md) 与 [ADR 0007](0007-nanovsr-and-tensorrt-rtx.md) 文首已加指向本 ADR 的说明；其中旧 TFM 与旧 SDK 结论作为历史证据保留。

### 已验证范围（2026-11-11，Windows x64，SDK 11.0.100）

| 场景 | 命令 | 结果 |
| --- | --- | --- |
| SDK 解析 | `dotnet --version` | `11.0.100` |
| GUI Debug | `dotnet build FancyTool/FancyToolAva.csproj -c Debug --disable-build-servers` | 0 警告 0 错误，产物 `net11.0-windows10.0.19041.0/win-x64` |
| CLI Debug | `dotnet build FancyTool/FancyTool.Cli/FancyTool.Cli.csproj -c Debug --disable-build-servers` | 0 警告 0 错误 |
| 7 个烟测工程 Debug | `dotnet build FancyTool/_tools/*/*.csproj -c Debug` | 全部 0 错误；`FfmpegSmokeTest` 3 个既有 CS0618（`Bitmap.Save(string,int?)` 过时重载，非本次引入） |
| Linux 交叉构建 | `dotnet build FancyTool/FancyToolAva.csproj -c Release -r linux-x64 -p:PublishAot=false -p:SelfContained=true --disable-build-servers` | 0 警告 0 错误，产物 `net11.0/linux-x64` |
| Windows NativeAOT 发布 | `dotnet publish FancyTool/FancyToolAva.csproj -c Release -f net11.0-windows10.0.19041.0 -r win-x64 -p:PublishSelfContained=true -o .qa/net11-aot-2026-11-11` | 成功，`FancyToolAva.exe` 99 MB；产物在 `.qa/net11-aot-2026-11-11/` |
| AOT 启动复核（ADR 0007 风险） | `FANCYTOOL_DATA_DIRECTORY=<.qa>/data timeout -k 3 15s <publish>/FancyToolAva.exe` | exit 124（持续运行至超时），stdout/stderr 为空，Serilog 日志无 `precompiled XAML` / `Fatal` / `Unhandled` |
| CLI 自包含发布与托管运行时 | `dotnet publish FancyTool/FancyTool.Cli/... -c Release -f net11.0-windows10.0.19041.0 -r win-x64 -p:PublishSelfContained=true -p:PublishAot=false -o <publish>/cli` | 发布成功（72 KB apphost）；`fancy-tool.exe --help` 退出码 0，命令目录正常渲染 |

AOT 发布保留既有告警：`SATORI002`（Satori GC 实验性）、FluentAvalonia 与 Avalonia.Controls.DataGrid 的 `IL2104` / `IL3053` trim 分析、Avalonia DComposition 的 ILC 提示；均非本次引入。

### 已知限制

- ADR 0007 的 XAML 问题属运行时症状。本次限时启动验证只证明 **.NET 11 GA SDK 下 AOT 应用能完成初始化、未触发 XAML 加载失败**，不等于 GUI 全流程、窗口渲染与交互的实机验证（未做窗口截图 / UIA 点选）。
- 未验证：MSIX 打包与安装、`.deb` 打包、`publish-win.ps1` 脚本整体的串联执行（GUI AOT 与 CLI 发布已分别验证）、Inno 安装包、Linux 真机运行、Windows ML / GPU / NPU 推理、FFmpeg 全矩阵烟测的实际执行（仅编译通过）。
- 历史文档（ADR 0004 / 0007、各 2026-10 实测报告、商业许可审计）中的旧 TFM 与 SDK 版本为当时的原始证据，保留不改。

## 复现命令

```powershell
# 前提：本机安装 .NET SDK 11.0.100（仓库不再提供 global.json）
dotnet --version
dotnet build FancyTool/FancyToolAva.csproj -c Debug --disable-build-servers
dotnet build FancyTool/FancyTool.Cli/FancyTool.Cli.csproj -c Debug --disable-build-servers
dotnet build FancyTool/FancyToolAva.csproj -c Release -r linux-x64 -p:PublishAot=false -p:SelfContained=true --disable-build-servers
dotnet publish FancyTool/FancyToolAva.csproj -c Release -f net11.0-windows10.0.19041.0 -r win-x64 -p:PublishSelfContained=true -o .qa/net11-aot-2026-11-11
dotnet publish FancyTool/FancyTool.Cli/FancyTool.Cli.csproj -c Release -f net11.0-windows10.0.19041.0 -r win-x64 -p:PublishSelfContained=true -p:PublishAot=false -o .qa/net11-aot-2026-11-11/cli
& .qa/net11-aot-2026-11-11/cli/fancy-tool.exe --help

# Satori 版本与还原资产的核对
# FancyTool/obj/project.assets.json 中应为 PublishWithSatoriGC.Runtime.win-x64/11.0.0-satori.42242472725.1
```
