# 运行时升级到 .NET 11

2026-11-11。项目从 .NET 10 升级到 .NET 11（SDK `11.0.100`、运行时 `Microsoft.NETCore.App 11.0.0`）。

## 背景与约束

- 本机 .NET 10 SDK 已不可用：`C:\Program Files\dotnet\sdk\10.0.*` 下的 `dotnet.dll` 缺失，只剩 `11.0.100` SDK；`global.json` 固定 `10.0.100` / `10.0.300` 且 `rollForward: latestFeature` 不会跨大版本，仓库因此完全无法构建。
- 发布形态是**自包含 NativeAOT**，由实验性的 `PublishWithSatoriGC` SDK 接管运行时打包；SDK 版本与其 `SatoriPackageVersion` 必须成对升级，否则 AOT 还原取不到对应 RID 的运行时包。
- Windows TFM 必须带 `-windows10.0.19041.0` 才能保留 WinRT / Windows ML 投影；Linux 保持平台无关 TFM。
- 依赖兼容性：FFmpeg 为进程内 GPL shared 库（ABI 与 TFM 无关）；`Microsoft.ML.OnnxRuntime` 1.22.1、`Microsoft.Windows.AI.MachineLearning` 2.4.89、Avalonia 12.1.1 的程序集为 netstandard2.0 / net10.0，可被 net11.0 消费。

## 决策

| 项 | 变更 |
|---|---|
| TFM | `net10.0` → `net11.0`；`net10.0-windows10.0.19041.0` → `net11.0-windows10.0.19041.0`（`WindowsInference.props` 仍是唯一开关） |
| SDK 固定 | 根目录与 `FancyTool/` 的 `global.json` 均为 `11.0.100` + `rollForward: latestFeature` |
| Satori | `10.0.13-satori.38707290103.1` → `11.0.0-satori.42242472725.2`（自带 `SatoriPackageVersion=11.0.0-satori.42242472725.1`） |
| 扩展包 | `Microsoft.Extensions.DependencyInjection`、`Microsoft.Extensions.Http`、`Microsoft.Extensions.Logging`、`System.IO.Ports`、`System.Numerics.Tensors` → `11.0.0` |
| 内置包 | 移除 Core 的 `Microsoft.Extensions.Logging.Abstractions` 引用：.NET 11 起该程序集在共享框架内，显式引用触发 NU1510 |
| 项目引用 | GUI 与 CLI 对 Core 的 `ProjectReference` 增加 `SetTargetFramework="TargetFramework=$(TargetFramework)"`（原因见下） |

**为什么显式传 TFM**：Core 的 TFM 由 `$(RuntimeIdentifier)` 决定，而 SDK 在项目引用协商时不把 `RuntimeIdentifier` 传给引用方，因此交叉发布 `linux-x64` 时 Core 会被评估成 `net11.0-windows10.0.19041.0`，与 `net11.0` 入口直接冲突（`Microsoft.Common.CurrentVersion.targets` 的 TFM 兼容性检查）。最小复现：`.qa/rid-probe`（`net11.0` 入口 + 由 RID 决定 TFM 的库）在 SDK 11 下必然报同一错误。显式传递后两侧 TFM 始终一致，Windows / Linux / MSIX 三条路径都不再依赖 RID 传播。

## 替代方案

- **保留 net10.0 并加 net11.0 多目标**：AOT 发布与 MSIX 负载翻倍；本仓库只发布自包含产物，没有旧运行时消费者，收益为零。
- **重装 .NET 10 SDK**：与用户正在进行的 Visual Studio 更新冲突，且只是推迟升级。
- **把 Core 改成多目标、靠 P2P 协商选 TFM**：可行，但要为每个条件加 TFM 判断且构建时间翻倍；`SetTargetFramework` 一行即可达到同样效果。

## 后果（已验证）

环境：Intel Core Ultra 7 255H / Arc 140T，Windows 11，SDK `11.0.100`，运行时 `11.0.0`。

- **构建**：Core、GUI、CLI 与 `_tools/` 下七个烟测工程全部 0 错误 0 警告（移除内置包引用后 NU1510 消失）。
- **AOT 发布**：`publish-win.ps1 -Configuration Release` 成功，`FancyToolAva.exe` 98.9 MB（自包含原生）；CLI 托管自包含发布到 `cli\`，`fancy-tool --version` → `1.1.0.0+7518298`。
- **运行时行为**：GUI 启动日志完整（偏好载入 → ViewState 载入 → TCP/UDP/串口服务 → FFmpeg in-process `N-127579` → 超分模型就绪），无异常；AOT 发布版同样起窗正常。
- **超分链路**：VOSR 480×320 → 1920×1280 用时 **42.1 s**（GPU · Windows ML / OpenVINO），与 .NET 10 下的 43.6 s 同量级。
- **回归**：`FfmpegSmokeTest --image-models-only`、`--model-resume`（13/13）、`--image-vosr`（4/4）全部通过。
- **Linux 交叉发布**：`dotnet publish -r linux-x64 -p:PublishAot=false -p:SelfContained=true` 产出 271 个文件（ELF 入口 + `libavcodec.so.63` + `libSkiaSharp.so`）。

## 已知限制

- **MSIX 未重新打包**：本机没有 Visual Studio DesktopBridge，`FancyToolAva.Msix.wapproj` 只做了属性层核对（`FancyToolMsixBuild=true` 时入口与 Core 的 TFM 一致）。
- **Linux 只验证交叉编译与产物落盘**，未在 Linux 设备上运行；`.deb` 打包（`publish-linux-deb.ps1`）未重跑。
- **Satori GC 仍是实验特性**（构建期 `SATORI002` 警告），与运行时版本无关，随 SDK 升级一并保留。
- 最低系统版本不变：`SupportedOSPlatformVersion` 仍为 `10.0.18362.0`。
