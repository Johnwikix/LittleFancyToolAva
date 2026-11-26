# 串口调试多端口 UI 重设计（任务状态）

## 任务要求

### 用户原始 QUERY
> 重新设计串口调试的ui，需要其支持同时调试多个串口，先给出设计

### 明确调整
- 2026-11-26 评审：保留现有布局与串口 ComboBox，在 ComboBox 上加连接状态标识；放弃 v1 会话轨方案。
- 2026-11-26 09:16：用户指令“执行”，按 v2 设计进入实现。
- 2026-11-26 13:22：用户已自行修改样式与布局，要求在当前基础上继续；后续改动须适配其新样式，不覆盖其修改。
无

### 需求清单
- r1（must，content_quality）：交付可评审的串口调试 UI 重设计方案，而非直接改代码；本轮先设计后实现。
- r2（must，content_quality）：设计必须支持同时调试多个串口——多端口可并发连接、并发收发、各自独立参数与日志。
- r3（must，content_quality）：设计落到现有工程约束：Avalonia + FluentAvalonia、CommunityToolkit MVVM、编译绑定、i18n 双语键、ViewState 持久化、NativeAOT、Singleton 页面与显式释放。
- r4（ideal，content_quality）：说明改动范围（服务层、ViewModel、View、ViewState、CLI 兼容）、分期顺序与风险。

## 环境理解

- 现实现为单端口单例：`FancyTool.Core/Services/SerialPortService.cs` 内部仅一份 `_serialPort`/`_receiveBuffer`/`_frameTimer`；`App.axaml.cs:248,261` 以 `AddSingleton` 注册服务与 VM。
- 现有 UI：`Views/SerialPortView.axaml` 左 320px 设置列 + 右 3:2 上下分栏（日志/发送）+ 底部 `TerminalStatusBar`。
- 令牌与控件：`Views/Themes/CommonStyles.axaml`、`Views/Controls/{ToolPageHeader,StatusPill,LogBufferView,TerminalStatusBar}`。
- `Utils/LogBuffer.cs` 每实例自带 16ms `DispatcherTimer`、`MaxLines` 默认 100000。
- `SerialPortViewState` 扁平单端口；`ViewStatesRoot.cs` 源生成 JSON 上下文。
- CLI 也消费 `ISerialPortService`（`CliContext.cs:187`、`NetworkCommands.cs:30,81`，命令 `serial list/send/listen`）。
- 主窗口 `MinWidth=900`（`MainWindow.axaml:18`），决定分屏降级阈值。

## 规划及进度

### 当前计划
- [x] 读取现有串口实现、UI、日志缓冲、状态持久化、CLI 与依赖注册
- [x] 输出重设计方案（信息架构、布局、交互、状态模型、服务层拆分、持久化 v2、性能线程、i18n、分期与风险）
- [x] 输出界面线框（对话内渲染，单栏 + 双分屏）
- [x] 按评审意见修订为 v2（ComboBox 即会话选择器 + 状态标识）
- [x] P0 服务层拆分：SerialPortSession + Hub + Catalog + DI + CLI
- [x] P1 VM/ViewState v2/i18n 键
- [x] P2 ComboBox 改造（模板、状态点、未读、添加端口、关闭）
- [x] P3 广播发送 + 聚合文案（合并日志未做，留待后续）
- [x] 回归：主工程与 CLI 均 0 警告 0 错误，CLI serial list 实跑通过
- [x] 修复“添加端口后 ComboBox 收起框为空”（增量同步 + 选中项重推 + SelectionBoxItemTemplate）
- [ ] GUI 交互与真实串口多路收发待人工验证
- [x] 对齐用户新样式/布局：确认其改动集中在 `SerialPortView.axaml`（ComboBox 模板缩进、参数摘要注释掉、关闭按钮移入 ActionBar），现有 VM 绑定全部仍有效
- [x] 合并日志（跨端口时间线）：`LogBuffer` 加 `EntryEnqueued`，会话 `LogSink` 转发，页面 `MergedLog` + 开关/清空/保存
- [x] 文档同步：`docs/changelogs.md` 与设计文档 §14 的“合并日志未实现”已改为已实现，并补记收起框模板与用户布局差异
- [x] 仅剩单会话时关闭按钮置灰：`CloseSessionCommand` 加 `CanExecute=CanCloseSession`（`Sessions.Count > 1`），XAML 另绑 `IsEnabled` 双保险；编译 0 错误 0 警告

### 未解决项与下一步
- 关闭按钮在仅剩 1 个会话时置灰，最后一个会话不会被误关；换端口需先“添加端口”建新会话再关旧会话。
- 会话上限定为 8 路（SerialPortViewModel.MaxSessions）。
- 合并日志已实现：日志卡标题栏 ToggleButton，缓冲 20000 行且常驻采集（隐藏时也写入，切回即可看到历史），清空/保存按当前视图分流。
- 关闭会话不做确认弹窗：`INotificationService` 无确认 API。
- 无真实串口设备，收发链路未实机验证；GUI 交互需人工点一次。
- 风险提示：`LogBuffer` 新增 autoFlush 构造参数与 `EntryEnqueued` 事件；TCP/UDP 页面仍走旧构造（自建定时器、无订阅者），需一次收发回归。
- 合并日志常驻采集，隐藏时也写入（上限 20000 行、16ms 统一刷），长时运行内存占用需留意。

## 关键证据及证据分析

- 单端口硬约束：`SerialPortService.cs:16-24`（接收缓冲与帧定时器为实例单份），多端口必须拆会话。
- 线程模型：`SerialPortViewModel.cs:267-292`（线程池入队 + Interlocked 计数）、`417-435`（16ms 汇总）。
- 计时器数量：`SerialPortViewModel.cs:397-409`（每连接 1s 计时器），`LogBuffer.cs:25-30`（每缓冲 16ms）。
- CLI 依赖点：`NetworkCommands.cs:30,81`；已证实保留 `GetPortNames()` 会话语义后 CLI 无需改命令。
- 实现结果：`dotnet build FancyTool/FancyToolAva.csproj` 与 CLI 工程均 0 错误 0 警告（约 19s / 3s）。
- 实现结果：`fancy-tool serial list` 实跑输出 COM1–COM8，证明新 DI（Transient 会话 + Hub）可解析。
- i18n 校验：zh/en 各 808 键对称；新增 6 键均存在。
- 缺陷根因：`RebuildChoices` 对 `PortChoices` 整表 `Clear()`，ComboBox 将 `SelectedItem` 置空，而重建后的新实例不在旧集合中，收起框停在未选中态；另 Fluent 的 ComboBox 模板只用 `SelectionBoxItemTemplate` 渲染收起框，不回退 `ItemTemplate`。
- 修复：改为增量 `SyncChoiceItems`（会话项插在“添加端口”段前、被占用端口项就地移除），选中项变更后补一次 `Dispatcher` 重推；补 `SelectionBoxItemTemplate`；`SerialChoiceItem` 加 `ToString()` 兜底。已重新编译 0 错误 0 警告。
- 合并日志实现结果：主工程编译 0 错误 0 警告（约 5.7s）；静态核对 XAML 落点（202–219 行：ToggleButton + 双 LogBufferView + BufferUsage 绑定）与 i18n（`Serial.Button_MergedLog` zh「合并日志」/ en「Merged log」，Serial 段两端键数一致）。

## 工具使用与问题反馈

- 设计文档初稿以 `write` 写到了 `FancyTool/docs/serial-multi-port-ui-design.md`，与工作文档同路径发生覆盖；已将工作文档迁至本文件，设计文档保持为交付物。
- 已用 bash 校验 v2 设计文档 CRLF：352 个 CRLF、0 个孤立 LF。
- `visualized-output` skill 已加载并按其契约（`html.md`/`core.md`/`mockup.md`）渲染线框。

## 中间产物与文档

- 交付物：`FancyTool/docs/serial-multi-port-ui-design.md`（v2，ComboBox 方案，已导出）
- 本状态文档：`.workbuddy/serial-multi-port-ui-redesign.md`。
- 代码：新增 `SerialPortSession/SerialPortHub/ISerialPortHub/SerialPortCatalog`、`SerialSessionViewModel`、`SerialChoiceItem`；重写 `SerialPortViewModel`、`SerialPortView.axaml`；`LogBuffer` 加 `autoFlush` 构造；`TerminalStatusBar` 加 `MiddleDetail`；`SerialPortViewState` 升 v2；`App.axaml.cs`/`CliContext.cs` 改注册；i18n 双语各 +6 键；`docs/changelogs.md` 加条目。