# MSIX AI 许可门控验证（2026-10-23）

策略与范围见 [ADR 0016](adr/0016-msix-ai-feature-license.md)。实现参照 music_player 的应用许可规则；该项目只读参考，未改动。

## 环境与证据

Windows 11 10.0.26300、Intel Core i5-13600KF、RTX 5070 Ti / 驱动 617.14、.NET SDK 10.0.402、运行时 10.0.13、Avalonia 12.1.1、Windows ML 2.4.89。原始日志、状态结果、截图与模型 / 输出数据均在仓库根目录 `.qa/msix-ai-license-2026-10-23/`，不进入源码分发。

## 已执行

- 普通 Windows Release：主项目及真实 UI 烟测工程构建通过，0 错误，UI 烟测有 3 个既有截图 API 警告；最终许可烟测构建为 0 警告。`core-final/results.json` 的 16 组生产许可 / 服务回归全部通过。
- MSIX 形态：使用 `FancyToolMsixBuild=true`、自包含托管设置构建通过，实际程序集包含 Windows Store 后端；`msix-final/results.json` 的同一 16 组回归全部通过。`build-msix-final.log` 为 0 错误、0 警告；首次完整重建依赖时存在既有 Lyricify 分析器警告。
- Linux `net10.0 / linux-x64` 主项目与许可烟测完整交叉构建通过，0 错误、0 警告（`build-linux-final.log`）。此检查还特意携带 MSIX 标记，确认 WINDOWS 条件不会让 Linux 引用 Store 后端。Windows .NET host 加载实际 Linux 编译程序集，仅执行渠道静态检查及默认构造：Store 后端不存在，AI 立即放行，`linux-policy-final/results.json` 为 1/1；这不代替 Linux 原生运行。此前缺少 FFmpeg 六库触发 FT1001，已使用仓库固定下载脚本补齐并逐文件通过 SHA-256，记录在 `fetch-linux-ffmpeg.log`。
- 真实 Avalonia 三页面：`ui-confirmed/ui-checks.json` 的 14 项检查通过，涵盖受限时禁止 AI 开始、直接调用命令的执行守卫、不进入忙状态、普通转换与运动补偿放行、设置保留、三页共享购买恢复、普通渠道隐藏以及新增文案即时切换英文。保存宽窗浅色、窄窗深色和英文截图。
- lvt / UIA：识别真实 Avalonia 12.1.1 窗口，通过界面切换到受限状态，定位并点击实际 `AiLicensePurchase` 按钮，确认购买替身返回后按钮隐藏、进程存活；保留带标注截图。结果为 `uia-summary.json`。**没有真实付款。**
- 缓存页面往返：实际调用生产 `NavigationFactory`，复用同一图片 / 视频 ViewModel，9 项检查通过（`navigation-final/navigation-checks.json`），覆盖普通任务可启动、受限 AI 拒绝、许可恢复后的命令通知、再次失效。修正新加的导航生命周期接口会让缓存实例离开时被释放的问题；现在由应用 DI 退出时清理。
- 普通构建 GPU 管线回归：`gpu-regression.log` 中 16 项全部通过，包含 GPU 超分 / RIFE 三种组合顺序与 2/4/6 倍、裁剪尾帧、音频 / 时间戳 / 预览、取消与失败后会话复用。测试使用同机已校验的现有模型缓存，每项执行一次，边界为实际生产转码路径；本次仅验证兼容性，不据此比较性能。

16 组回归直接编译并调用生产 LicenseService、图片超分、视频超分、RIFE 和转码服务，仅替换 Store、时钟及 UI 调度边界。覆盖默认渠道旁路、同步缓存、初查等待与取消、首次失败放行、后续失败保留、后台许可事件、刷新合并补查、购买与旧查询交错、取消购买不解锁、试用天数、释放与迟到结果；实际入口拒绝前断言零下载、零设备事件、零输出。通过真实图片推理锁与视频会话 semaphore 控制排队，验证等待期间失效后拒绝缓存 / 新建会话。

初轮 UI 测试发现新增 LicenseViewModel 直接切换 Culture 时读取的统一字符串表尚未刷新，已在语言事件的 UI 回调中 Reload 并通知绑定；确认轮通过。没有增加平行字符串表。

## 复现

在仓库根目录执行。已有包可加 `--no-restore`；测试不需要真实 Store 账号。

```powershell
$licenseRoot = Join-Path (Get-Location) '.qa/msix-ai-license-2026-10-23'
$env:NUGET_SCRATCH = Join-Path $licenseRoot 'nuget-scratch'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $licenseRoot 'http-cache'

# 普通 Windows：实际默认构造与所有服务守卫。
dotnet run --project FancyTool/_tools/LicenseSmokeTest -c Release -- "$licenseRoot/core-retest"

# MSIX 编译形态：测试 exe 也须自包含，避免 NETSDK1151。
dotnet build FancyTool/_tools/LicenseSmokeTest -c Release -p:FancyToolMsixBuild=true -p:SelfContained=true -p:PublishAot=false "-p:BaseOutputPath=$licenseRoot/msix-bin/"
& "$licenseRoot/msix-bin/Release/net10.0-windows10.0.19041.0/win-x64/LicenseSmokeTest.exe" "$licenseRoot/msix-retest" --expect-msix

# Linux 主项目：Windows 上交叉构建，未运行 Linux 安装包。
pwsh -File FancyTool/Libraries/FFmpeg/fetch-ffmpeg.ps1 -Platform linux
dotnet build FancyTool/FancyToolAva.csproj -c Release -r linux-x64 -p:TargetFramework=net10.0 -p:FancyToolMsixBuild=true -p:SelfContained=true -p:PublishAot=false "-p:BaseOutputPath=$licenseRoot/linux-bin/"
dotnet build FancyTool/_tools/LicenseSmokeTest -c Release -r linux-x64 -p:TargetFramework=net10.0 -p:FancyToolMsixBuild=true -p:SelfContained=true -p:PublishAot=false "-p:BaseOutputPath=$licenseRoot/linux-bin/"

# 真实三页 UI，--inspect 保留烟测窗口供 UIA；购买仅使用测试替身。
dotnet build FancyTool/_tools/FfmpegSmokeTest -c Release "-p:BaseOutputPath=$licenseRoot/windows-bin/"
& "$licenseRoot/windows-bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe" "$licenseRoot/ui-retest" --license-ui --inspect
& "$licenseRoot/windows-bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe" "$licenseRoot/navigation-retest" --license-navigation-only

# Linux 编译程序集的纯托管渠道检查，宿主仍是 Windows，不调用 Linux 原生库。
dotnet exec --runtimeconfig "$licenseRoot/windows-bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.runtimeconfig.json" "$licenseRoot/linux-bin/Release/net10.0/linux-x64/LicenseSmokeTest.dll" "$licenseRoot/linux-policy-retest" --build-policy-only

# 普通构建真实 GPU 兼容性；复用当天 GPU 复测中已校验的模型缓存。
$env:FANCYTOOL_DATA_DIRECTORY = Join-Path (Get-Location) '.qa/gpu-inference-2026-10-23/data'
& "$licenseRoot/windows-bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe" "$licenseRoot/gpu-retest" --pipeline-integration
Remove-Item Env:FANCYTOOL_DATA_DIRECTORY
```

`--license-ui` 的控制按钮和可变许可只存在于独立烟测工程。正式包未引入可由环境变量、文件或偏好设置伪造的授权状态。

本次未新增依赖包。新增 `License.*` 共 10 键，中英文 JSON、统一字符串表与生产调用的键集合一致；修改文件已检查 CRLF 与 `git diff --check`。验证摘要与最终源文件 SHA-256 留在证据目录。

## 未覆盖

尚未用 Store 签名且已关联当前应用的真实包验证购买、取消真实付款、恢复购买、商店缓存许可和试用发放；也未生成 / 安装新的 MSIX、修改商店定价或创建增购商品。Linux 为交叉构建检查，未做 Linux 实机 UI / GPU 验证。本次不据此声明新的 GPU 性能或商业许可结论。
