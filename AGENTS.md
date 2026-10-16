# 项目约定

## 文件格式

- 代码、XAML、配置和文档统一使用 Windows CRLF (\r\n) 行尾。`FancyTool/.gitattributes` 已启用 `* text=auto`，不要混用 LF。
- 文档与注释的默认语言为中文；`docs/adr/` 下历史 ADR 允许英文，新增内容优先中文。

## Avalonia 本地化（Lang.Avalonia）：避免资源键直出

- 界面文本统一走 Lang.Avalonia：
  - XAML 使用 `{lan:I18n 分类.键名}`，例如 `{lan:I18n Settings.Label_Language}`；命名空间为 `xmlns:lan="clr-namespace:Lang.Avalonia.MarkupExtensions;assembly=Lang.Avalonia"`。
  - 代码侧使用 `LocalizationRegistry.Get("分类.键名")`（缺键时回退返回键名本身），不要硬编码可见文案。
- 资源键即 JSON 路径：`i18n/zh-CN.json` 与 `i18n/en-US.json` 中嵌套的 `"Settings": { "Label_Language": ... }` 对应调用 `Settings.Label_Language`。
- **新增界面文案必须在 `i18n/zh-CN.json` 与 `i18n/en-US.json` 同步新增同名键**；两侧结构不一致时，缺失语言会直接显示键名。构建成功不能证明运行时资源解析正确，提交前静态检查新增键与两个语言文件是否匹配。
- 运行时语言切换：XAML 的 `lan:I18n` 自动刷新；代码构造的字符串不会自动刷新。设置页等需要即时切换的页面，把键注册到 `Services/LocalizationStrings.cs`（`IReadOnlyDictionary`），并在 `NotifyAllTextChanged()` 中补 `OnPropertyChanged`；遗漏会出现切换语言后部分界面停留在旧语言。
- 转换器、`LocalizedOption` 等间接取词处同样复用 `LocalizationRegistry.Get`，不要自制平行的字符串表。

## UI 风格（FluentAvalonia）

- 以 `Views/Themes/CommonStyles.axaml` 的设计令牌为准（`PageMargin`、`CardPadding`、`CardCornerRadius`、`FieldSpacing`、`MonoFontFamily` 等），新页面优先复用，不要另起一套间距/字号/圆角。
- 统一控件位于 `Views/Controls/`：`ToolPageHeader`、`StatusPill`、`LogBufferView`、`TerminalStatusBar`、`SymmetricCipherView` 等；同类界面优先复用现有控件、转换器（`Helpers/`）与交互语义，不制造平行版本。
- 跟随系统 / 浅色 / 深色三套主题必须同步可用；FluentAvalonia 的 `FANavigationView` / `Frame` / `ContentDialog` 体系是导航与弹窗入口，不绕过它自建窗口或消息层。
- `AvaloniaUseCompiledBindingsByDefault=true`：绑定使用编译绑定，新增/改动 XAML 要保证 `x:DataType` 与绑定路径一致；`ViewState` 等序列化模型按现有源生成（source-gen）方式声明，不引入运行期反射序列化。

## MVVM 与页面注册

- 保持 `CommunityToolkit.Mvvm` 源生成：`[ObservableProperty]`、`[RelayCommand]`，设置状态与命令放在 ViewModel，视图只负责布局和交互转发。
- 页面生命周期：ViewModel 实现 `IViewLifecycle`（`OnNavigatedTo` / `OnNavigatedFrom`）；瞬时页面（`AddTransient`）离开时由 `NavigationFactory.GetPageFromObject` 触发 `Dispose`，长驻页面（串口 / TCP / UDP / 歌词 / 设置等 `AddSingleton`）不会被导航释放，资源清理必须显式管理。
- 新增工具页：新建 ViewModel → 在 `App.axaml.cs` 的 `ConfigureServices` 注册（默认 `AddTransient`，需保持长连接的用 `AddSingleton` 并写清释放路径）→ 新建 View（`*View.axaml`，`DataContext` 由 `NavigationFactory.Register<TViewModel, TView>()` 注入）→ 在 `MainWindow`/`NavigationFactory` 注册导航入口 → 补齐 `i18n` 双语言键。
- 页面状态持久化：实现 `IViewState` 并注册到 `IViewStateService`（`Register/Unregister`），由 `ApplicationHostService` 在启动 / 退出时 `LoadViewStates` / `SaveState`；窗口与页面首帧布局依赖 ViewState 恢复时，注意 `OnNavigatedTo` 与 `Register` 的先后顺序。
- 应用偏好统一走 `AppObserveModel / AppPreferences`（theme、backdrop、语言、模型目录等），不要新增平行的配置文件。

## 异步、线程与生命周期

- UI 属性、绑定集合（`ObservableCollection` / `RangeObservableCollection`）和命令通知必须在 UI 线程发布；后台服务跨线程更新时切回 `Dispatcher.UIThread`，不要依赖 UI 控件的内部封送。
- 取消等待不等于底层工作已停止：转码 / 超分 / 提取的取消、重试、在途回调、临时文件清理都要按真实调用链核对；退出时服务释放顺序（`ServiceProvider.Dispose` → 保存 ViewState → `Log.CloseAndFlush`）不可打乱。
- 清理应幂等，单项失败不能跳过其他资源；后台任务必须有明确的异常观察与收尾责任，不产生未观察的 `Task`。

## 平台与运行时差异

- 主项目 TFM 由 `WindowsInference.props` 控制：Windows 使用 `net10.0-windows10.0.19041.0`（WinRT / Windows ML 投影），Linux 保留 `net10.0`。新增 Windows 专属代码必须置于相应条件编译或 `OperatingSystem.IsWindows()` 守卫内，保证 Linux 构建仍是干净的平台无关代码。
- 发布形态是**自包含托管应用，NativeAOT 关闭**（FFmpeg.AutoGen 使用反射式动态绑定，不能用 AOT）；`csproj` 中 `PublishAot=true` 仅作为默认属性，实际发布以脚本为准。
- 不要重复引入 ONNX Runtime 或替换 Windows ML（`Microsoft.Windows.AI.MachineLearning`），冲突的 `onnxruntime.dll` 会静默移除硬件执行提供程序；GPU/后端策略（如 `session.disable_cpu_ep_fallback=1`）沿用 `Services/` 中已验证的组合，不私自放宽。
- FFmpeg 为进程内 GPL shared 库（随应用分发，来源与校验见 `Libraries/FFmpeg/BUILD_INFO.md`）。改动 FFmpeg 相关代码或库后，必须重新跑对应烟测并在文档中保留证据。

## 构建与发布

- 常用验证构建：
  ```powershell
  # 快速调试运行
  dotnet run -c Debug

  # Windows x64 自包含发布（脚本会调用 dotnet publish）
  pwsh -File FancyTool\publish-win.ps1 -Configuration Release

  # Linux .deb 打包
  pwsh -File FancyTool\publish-linux-deb.ps1

  # Inno Setup 安装包（内部先执行 publish-win）
  pwsh -File FancyTool\pack-win-installer.ps1
  ```
- RID 在 `csproj` 中按 OS 自动选择（Windows→`win-x64`，其它→需显式指定）；`NativeAOT` 与单文件发布不使用于本仓库的正式发布。
- MSIX 打包使用独立输出目录（`FancyToolMsixBuild=true` → `bin/msix` / `obj/msix`），生成与验证方式见 `docs/adr/0009-msix-entry-point-output-metadata.md`；MSIX 下 `%LocalAppData%` 由系统重定向到包内 LocalCache。
- 不要改动 `global.json` 固定的 .NET SDK 版本与 `rollForward` 策略；SDK 升级需要单独说明影响面。

## 验证证据：烟测与实测记录

- 独立烟测工程位于 `FancyTool/_tools/`，不参与主程序编译。常用入口：
  ```powershell
  # 进程内 FFmpeg 全矩阵转码（容器×视频编码×音频编码）与特殊场景
  dotnet run --project FancyTool/_tools/FfmpegSmokeTest -- .qa/ffmpeg

  # 歌词格式回读检查
  dotnet run --project FancyTool/_tools/LyricsSmokeTest -- formats .qa/lyrics/formats

  # 网络歌词源对照（测试专用，不覆盖识别结果）
  dotnet run --project FancyTool/_tools/LyricsSmokeTest -- network .qa/lyrics/network D:\music\歌曲.mp3
  ```
- 测试数据隔离：`FANCYTOOL_DATA_DIRECTORY` 隔离应用数据，`FANCYTOOL_LYRICS_MODELS` / `FANCYTOOL_LYRICS_DATA` 隔离模型与歌词队列。下载模型、音频缓存和输出统一放已忽略的 `.qa/`，不随源码分发。
- 烟测必须走真实代码路径（`VideoTranscodeService`、`FfmpegService`、ViewModel 的过滤/守卫逻辑等），不能只在测试里复制一份逻辑自证；涉及 UI、GPU/Windows ML、文件监视或原生资源的改动，桩测试通过不能代替集成验证。
- 性能与内存结论必须注明环境（机器、GPU、驱动、版本）、场景、测量边界和重复次数；未执行的设备/UI 验证要明确列出，没有测量不得宣称零分配或具体倍数收益。

## 文档：ADR 与验证报告

- 架构与行为决策记录在 `FancyTool/docs/adr/`，文件名 `NNNN-短横线主题.md`（如 `0008-lightweight-gpu-video-sr.md`），内容包含：日期、背景约束、决策、替代方案、后果（含已验证范围与已知限制）。
- 实测报告使用 `FancyTool/docs/<主题>-<日期>.md`（如 `onnx-vsr-validation-2026-10-16.md`），原始数据放 `FancyTool/docs/data/<同一主题-日期>/`（measurements.json、日志、锁定版本等），并在报告中互相链接。
- **新增数字、模型、后端或界面行为结论时，必须保留可复现命令与原始证据**；被后续决策取代的旧文档在文首加说明并链接新 ADR，不静默删除历史记录。
- README.md / README.en.md 中的功能说明（模型、后端、支持格式、发布方式）与 ADR 冲突时以 ADR 为准，并同步更新 README。

## 默认代码质量与审查标准

后续功能实现、修复、重构和 code review 默认执行以下高标准，无需用户重复提醒。审查必须覆盖功能正确性、重构方案设计、性能、内存分配和代码可读性；按风险投入验证，不以“能编译”或“已拆分类”作为完成标准。

- **功能正确性**：沿真实调用链核对行为与兼容性，覆盖正常流程、空状态、失败、取消、重试、并发及启动中退出。多入口命令应统一可用条件和执行守卫；显式 Start/Stop、暂停/恢复等用户意图不能因去重或单飞机制被静默丢弃。批处理（转码、加密、提取）要覆盖失败明细、部分成功与队列恢复。
- **重构设计**：先明确职责、依赖方向、状态来源和资源所有权，再确定拆分边界。保持 MVVM 和单一状态源；避免只搬移代码却继续隐藏依赖或副作用。构造函数保持轻量，服务启动与停止显式管理；优先构造注入，必要的延迟解析集中在明确边界并说明原因。
- **异步与生命周期**：UI 属性、绑定集合和命令通知必须在 UI 线程发布。取消等待不等于底层工作已停止（含原生 FFmpeg / ONNX 会话与临时文件）；退出需要处理在途任务、迟到回调、订阅解绑和依赖释放顺序。清理应幂等，单项失败不能跳过其他资源；后台任务必须有明确的异常观察与收尾责任。
- **性能**：检查 UI 线程阻塞、重复 I/O、扫描复杂度、事件风暴、队列背压和不必要的启动工作（`App` 中过早 touch 服务的代价）。关闭功能时核对其后台活动是否也停止。区分冷路径与热路径，优先解决可感知延迟和规模增长问题，不为微优化引入不必要的复杂度。
- **内存分配**：检查热路径的闭包、装箱、LINQ/匿名对象、字符串和集合复制，以及定时器、CTS、事件订阅、原生句柄和缓冲区的生命周期。Span/Memory/ArrayPool 仅在收益明确且所有权、线程与异步边界安全时使用；池化缓冲区必须可靠归还。区分短期分配、长期保留与实际泄漏，没有测量不得宣称零分配或具体收益比例。
- **可读性**：命名准确、控制流清楚、方法职责集中；避免在一行压缩多个状态变更或清理操作。注释解释约束和原因，并随实现同步更新；移除迁移后无效的状态、重复守卫、过时注释和无用依赖。
- **验证证据**：运行与改动风险相称的构建、烟测或最小复现。测试桩应保留真实边界的异步时序、线程切换、属性/集合通知与失败行为；涉及 Avalonia UI、Windows ML / GPU、FFmpeg 或原生资源时，桩测试通过不能代替集成验证。性能结论注明场景与测量依据，未执行的设备/UI 验证明确列出。
- **审查输出**：按严重性报告可定位、可复现或有完整调用链证据的问题，给出文件位置、触发条件、影响和修复方向。区分本次引入的回归、既有问题与设计建议；不得为了覆盖维度而凑问题。说明已执行验证及剩余盲区。仅请求 review 时默认交付审查结果，生产代码修复按用户授权范围执行。
