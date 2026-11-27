# 变更记录

## 2026-11-27（修复：TCP / UDP 连续"新建会话"从第二次起无效）

- **现象**：在下拉里选"新建会话"，第一路能建出来并自动选中；紧接着再选一次，收起框显示"新建会话"，但不创建会话，页面仍停在上一路（地址 / 端口输入框与聚合"运行中 0 / 2 会话"都不变），之后每次都无效；切到别的会话再切回来又能建一次。TCP、UDP 都如此。
- **原因**：`TerminalPageViewModelBase.SelectedChoice` 的 setter 在 ComboBox 选中变更的绑定回写里同步新建会话，并把新会话的项插进 `Choices`。集合变更发生在回写中途，`SelectingItemsControl` 的选中变更处理被重入（Avalonia 12.1.1 `SelectionModel.cs`、`SelectingItemsControl.cs` 源码核对；运行日志也记录到插入期间控件把新会话的项又推给了 ViewModel，被 `_suppressChoice` 丢弃）。外层处理结束后，控件把"上一次选中项"记成了"新建会话"项。下一次再选它时控件认为选中项没变，不发 `SelectedItem` 变更，绑定不写回源，ViewModel 的 setter 根本没有被调用（第二次选择在日志里没有任何 setter 记录）。
- **修复**：选中"新建"项时 setter 只记录选中项；创建会话、同步下拉集合与重新选中推迟到 `Dispatcher.UIThread.Post` 的回调（`AddSessionFromChoice`），同一帧内完成，界面不闪烁。回调开始时如果已经切到别的会话则放弃。串口页的"添加端口"走同一个 setter，一并受益。
- **验证**（证据在 `.qa/tcp-udp-multi-session-2026-11-26/`）：
  - 修复前（原始代码）：TCP 连续选 10 次"新建会话"只建出 1 路（`p3-probe-tcp.log`）；对照采集是"新建成功 → 再新建失败 → 切到会话 1、会话 2 → 新建成功 → 再新建失败"（`p3-probe3-tcp.log`）。
  - 第一次只推迟"把新选中项写回控件"而没有推迟创建，问题依旧：TCP 连续 5 次、UDP 连续 3 次都只有第 1 次成功（`p3fix-add-tcp.log`、`p3fix-add-udp.log`）。
  - 修复后：TCP 连续 4 次全部创建，端口 8081–8084 逐次 +1，聚合"运行中 0 / 2 … 0 / 5 会话"，应用日志 5 个服务实例，存档 5 路并选中最后一路，正常退出码 0（`p3diag2-add-tcp.log`，`p3diag2-tcp-diag.txt` 是该次的应用日志摘录）。该次构建带临时诊断日志，诊断代码之后已删除，最终构建 0 错误（`p3-fix-build.log`）。
  - **UDP 修复后没有运行结果**：唯一一次运行在第一次新建之前应用就被正常关闭（退出码 0，日志是完整的保存与释放流程，当时没有自动化动作在执行）。UDP 与 TCP 共用同一个 setter 和修复，缺的只是运行确认。
  - 鼠标点击与键盘选择没有实测（不模拟键鼠）。从源码看，它们与自动化选择走同一条 `SelectedIndex` 路径，受同一个缺陷影响，也应被同一处修复覆盖。
- **后台界面采集已停用**：这次核查发现，最小化的测试实例在下拉展开时，弹出层是屏幕左上角一个 285×86 的可见窗口，而且采样时该实例的主窗口是前台窗口（`p3vis-tcp.log`）。这不符合"不影响使用 PC"的要求，此前两次"应用进程消失"很可能就是因此被关闭的。在找到不展开下拉、不抢焦点的验证方式之前，不再运行这类采集。
- 构建：主程序 0 错误，8 条 `NU1900` 仍是还原阶段的网络警告。

## 2026-11-27（P2 / P3：TCP 与 UDP 多会话；P2 界面验证未完成，P3 只做了编译与静态核对）

- **P2 TCP 多会话**（`tcp-udp-multi-session-design.md` 第 2–6 节）：新增 `TcpSessionViewModel`（模式、地址、端口、帧间隔、`IsListening` / `IsConnected` / `IsActive`、已连接客户端与 `KickClient`、启动 / 停止 / 发送），`TcpServerViewModel` 改为派生 `TerminalPageViewModelBase<TcpSessionViewModel>`，只保留链路测试与页面级成员。`TcpServerViewState` 升 v2（`Sessions`、`SelectedSessionId`、`LinkAddress` / `LinkPort`），`Sessions` 为空即视为 v1，把旧的扁平字段转成单会话，旧端点同时作为链路测试端点。视图在连接卡顶部加会话行（"新建会话"固定在下拉末尾，默认复制当前会话、端口 +1），模式、地址、端口、选项、客户端卡、日志、发送全部指向 `CurrentSession.*`；运行中冻结模式与端点并加 ToolTip；链路测试页签改绑页面级的 `LinkAddress` / `LinkPort`。
- **P2 带来的行为变化**：
  - 运行中不能再改模式，`OnModeIndexChanged` 里"切模式即停止"的分支随之删除；`Modes` 集合、从未赋值的 `_cts`、UI 线程上的 `task.Wait(200)` 一并删除。
  - 切到链路测试页签不再停止任何会话；点击"开始测试"时停止全部运行中的会话并提示数量；切回通信调试时停止在途测试（不变）。
  - TCP 定时发送遵守所选客户端：与手动发送共用目标规则，目标在点击"定时发送"时确定；目标客户端断开或被踢出后定时发送停止并写一条日志。所选客户端离开时清空选择，避免服务端一直"只发给"一个已不存在的客户端。
  - 状态文案合并上屏（设计第 7 节，P0 记录里留给 P2）：`PostStatusText` 只保留最新一条，由页面 16ms 节拍在 UI 线程发布，不再每条状态一次 `Dispatcher.Post`。串口页走的是同一条路径，但这次没有重跑串口的界面对比。
  - 超时提示原来为警告、连接失败为错误，现在两者都经会话的出错回调弹出，显示为错误（与串口一致）；只有当前会话才弹。
  - 日志上限随基类变为分级：当前会话 20000 行、其余各 5000 行、合并日志 20000 行（TCP 原为 100000）。
- **P3 UDP 多会话**（与 P2 同构）：新增 `UdpSessionViewModel` 与 `UdpSessionViewState`，`UdpViewModel` 改为派生，`UdpViewState` 升 v2（页面级 `LinkLocalAddress` / `LinkLocalPort` / `LinkRemoteAddress` / `LinkRemotePort`，迁移时取旧会话的本地与远程端点）。运行中冻结模式、本地地址、本地端口、组播地址；远程地址 / 端口与组播端口可改。新建会话复制当前会话、本地端口 +1。冲突判定用 `EndpointConflict.Udp`：普通会话与任何运行中的会话互斥，组播会话之间放行。`FrameBreakInterval` 只留在存档模型里兼容旧文件，不进会话 VM。UDP 日志上限同样由 100000 变为分级。
- **与设计的出入**：UDP 会话的显示名把发送目标并入名称（`0.0.0.0:8080 → 127.0.0.1:9090`、`0.0.0.0:8080 → 组播 239.0.0.1:8080`）。设计 3.1 设想名称只放本地端点、目标放在摘要行，但共享的下拉项模板没有摘要行（P0 已删掉无绑定的摘要属性），且同端口上的多个组播会话只看本地端点无法区分。会话显示名随输入更新并缓存，不在每次属性通知时格式化。
- **i18n**：`Tcp` 新增 9 键（`Label_Session`、`Menu_AddSession`、`Button_CloseSession`、`Label_Aggregate`、`Msg_EndpointInUse`、`Msg_PollTargetGone`、`Button_MergedLog`、`ToolTip_LockedWhileRunning`、`Msg_LinkTestStoppedSessions`），`Udp` 新增同名的 8 键加 `Label_MulticastSummary`。删除无引用的 `Tcp.Button_Start`、`Tcp.Button_Stop`、`Tcp.Status_Ready`、`Udp.Button_Start`、`Udp.Button_Stop`。中英各 824 键。TCP 发送卡标题原来借用 `Serial.Group_Send`，改用 `Tcp.Group_Send` / `Udp.Group_Send`。
- **验证**（同 P0 环境；证据在 `.qa/tcp-udp-multi-session-2026-11-26/`）：
  - 构建：P2 的最后一次改动之后，主程序与 CLI 均 0 错误；各有 8 条 `NU1900`，是还原阶段连不上 NuGet 漏洞数据源的网络警告，不是编译器警告（`p3-build.log`、`p3-cli-build.log`）。
  - i18n 静态核对（`tools/keycheck.py`）：中英各 824 键、结构对称（唯一不对称仍是既有的 `AppFatal.*` 死键）、无 BOM、全 CRLF；XAML 与代码引用的 578 个键在两侧都存在，例外是三个与本次无关的既有引用：`AudioTranscode.Picker_SelectOutputDir`、`ImageConvert.Msg_SrModelDownloading`、`ServiceStatus.Serial_CDSignal`，两侧都没有定义，运行时回退为键名本身。
  - P2 界面（后台 UIA）只采到部分结果：运行中的服务端会话，模式单选与地址 / 端口被禁用，状态栏"运行中 1 / 1 会话"；链路测试页签下会话仍在运行，"开始测试"后会话被停止并提示"已停止 1 个运行中的会话以开始链路测试。"；合并日志的行带 `[服务器 127.0.0.1:8080]` 前缀；关闭运行中的会话后，命令行服务端立即绑定同一端口成功。另一次探索性采集里，"新建会话"得到"服务器 127.0.0.1:8081"并自动选中，状态栏"运行中 0 / 2 会话"，只有一个会话时"关闭会话"置灰。
- **未完成与未查清**：
  - 上面那轮完整场景作废：脚本把端口写进了错误的参数，服务端实际起在默认端口，后续步骤的前提都不成立。
  - 用户取消了测试。P2 的其余界面场景（页内环回互发、未读徽标、定时发送定点与目标断开即停、冲突提示、重启恢复、英文界面）与 P3 的全部界面验证都没有执行。
  - 作废的那一轮里，连续"新建会话"时应用进程消失，日志没有任何退出流程记录；应用日志里 7 次选择只出现 1 次服务实例创建。原因未查清：可能是崩溃，也可能是被外部结束；Windows 应用事件日志的查询没有返回结果，不能据此排除崩溃。
  - 设计第 12 节的验收要点，P2 只覆盖了上面列出的几项，P3 一项都没有运行过。
- 新增 `docs/adr/0024-terminal-multi-session-model.md`，把串口与 TCP / UDP 多会话的行为决策、验证范围与未验证项记在一处。

## 2026-11-27（P0：终端会话 / 页面公共基类抽取，串口取消会话上限；同日修复五处既有问题）

- **公共基类抽取**（`tcp-udp-multi-session-design.md` 6.1 甲方案，P0）：新增会话级 `TerminalSessionViewModelBase`（日志、计数、未读、计时、状态文案、发送选项、定时发送循环）、页面级泛型 `TerminalPageViewModelBase<TSession>`（会话集合、当前会话、下拉项同步、关闭并选邻、合并日志、聚合文案、"全部会话"发送）、`SessionChoiceItem`（取代 `SerialChoiceItem`，后者删除）和单例节拍 `TerminalTicker`（16ms / 1s，`App.axaml.cs` 注册）。`SerialSessionViewModel` / `SerialPortViewModel` 改为派生，串口页布局不变。选择器两个 `DataTemplate` 提到 `CommonStyles.axaml`（`SessionChoiceItemTemplate`、`SessionChoiceBoxTemplate`），后续 TCP / UDP 共用。
- **定时发送是会话级的**：每一路各自一个任务和取消令牌，开始时在 UI 线程取发送目标快照（抽象 `CreatePollingSender()`），之后切换会话、离开页面都不影响进行中的定时发送。串口原本就是会话级，行为不变；这次由基类承载，TCP / UDP 迁移时直接继承。
- **取消串口 8 路上限**：删除 `MaxSessions`、`CanAddSession`、超限提示和恢复存档时的截断（`Take(MaxSessions)`）。下拉项同步由"每个会话在下拉项里线性查找"改为字典。
- **"广播"改为"全部会话"**：勾选框改用公共键 `Session.Label_SendAllSessions`，行为不变。
- **i18n**：新增 `Session.Label_SendAllSessions`，删除 `Serial.Label_Broadcast`、`Serial.Msg_MaxSessions`，中英各 808 键（同日修复后为 811 键）。
- **随搬移产生的四处非搬移变化**（均已逐行对照旧实现）：其一，按设计 6.2 订阅 `CultureChanged`，切换语言后聚合文案、下拉项名称与状态文字、会话状态文字重发通知，旧实现没有这一段。其二，TX 文本解码与 RX 共用带回退的 `DecodeBytes`，旧实现 TX 直接 `GetString`、没有异常回退。其三，定时发送任务不再把取消令牌传给 `Task.Run`，并删除从未被读取的 `_pollTask` 字段。其四，删除没有绑定的 `RxTxSummary` / `ShortCount`（会话）和 `Summary` / `RxTxSummary`（下拉项）。
- **验证**（Windows 11 26300 x64，SDK 11.0.100，Debug 构建，FluentAvalonia 3.0.2；GUI 以"最小化且不激活的窗口 + UI Automation"驱动，不模拟鼠标键盘；虚拟串口 VSerial 两两互连 COM1↔COM2 … COM7↔COM8；证据在 `.qa/tcp-udp-multi-session-2026-11-26/`）：
  - 构建：GUI 与 CLI 均 0 警告 0 错误。泛型基类上的 `[RelayCommand]` 与编译绑定（`CurrentSession.*`）按预期可用，设计第 11 节的第二条风险没有出现。
  - GUI 基线对比：改动前的构建副本与改动后的构建按同一组步骤各采集 9 个状态（默认、下拉展开、两路、两路下拉、合并日志、全部会话、输入发送内容、关闭会话、8 路）逐元素比对，每个状态只有 4 行差异，全部是勾选框"广播"→"全部会话"。
  - 存档恢复：同一份 10 路存档，旧构建只恢复 8 路（"已连接 0 / 8 会话"），新构建恢复 10 路。切换会话时参数与发送内容整体切换，两构建一致。
  - 真实串口（COM1↔COM2 两路互连）：A 手动发送后 B 的 RX 为 1。A 启动定时发送后切到 B，B 的"定时发送"可用、"停止"禁用，B 的 RX 持续增长（1 → 49 → 74）；切回 A，"停止"仍可用，TX 6 → 25 → 98。B 也启动定时发送后 A 的 RX 增长到 31。"全部会话"发送与停止正常。
  - 英文启动：串口页 9 个状态的文字无裸键、无残留中文（`All sessions`、`Add port COMx`、`0 of N sessions connected` 等）。
  - CLI：`fancy-tool serial list` 列出 COM1–COM8；`serial send --port COM3 --data cli-p0` 与 `serial listen --port COM4` 环回收到 `RX> cli-p0`。
  - 正常退出：2 路会话（均未连接）下关闭窗口，`SerialPortHub` 与各服务按序释放，日志无错误。
- **未执行**：运行时切换语言（后台最小化窗口下 UIA 无法驱动侧栏导航项，旧构建相同，进不了设置页）；真实鼠标键盘点击、关闭窗口时有定时发送在途的退出路径、超过 10 路会话的内存占用均未测；P1–P3 未开始。
- **同日修复五处既有问题**（用户确认后修改）：
  - 合并日志保存提示显示裸键：原前缀 `SerialMerged` 在 i18n 里没有对应分区。新增公共分区 `MergedLog`（`Msg_NoDataToSave`、`Msg_SaveDialogTitle`、`Msg_SaveSuccess`，中英同步），前缀改为页面基类里的常量，不再由各页面提供。
  - 恢复存档后总是选中第一路：存档里的 `SelectedSessionId` 对不上新建会话的 `Id`。会话基类新增 `RestoreId`，串口会话的 `ApplyState` 用存档里的 `Id` 覆盖新建的标识；存档没有有效标识时保留新建的。
  - 会话出错回调可能在非 UI 线程发布通知：对话框本身原本就会切回 UI 线程（`ContentDialogNotificationService`），越线程的是页面处理器里的聚合状态与命令通知。页面基类改为不在 UI 线程时先切回，再刷新与弹提示；"是否当前会话"在触发时刻取值。
  - `ParameterSummary` 没有任何绑定（下拉摘要行早已注释掉）：连同它的五处通知一起删除，四个索引属性保留原有的变更通知。
  - 未读计数在串口接收线程上直接更新（核对上一项时发现，同属越线程发布，且"读—加一—写回"与 UI 线程的清零之间没有互斥）：接收线程只累加待发布计数，由页面 16ms 节拍在 UI 线程发布；`MarkRead()` 同时清空未发布部分，切到本会话瞬间迟到的记账直接丢弃（当前会话的未读没有意义）。`UnreadCount` 改为私有 setter。
- **五处修复的验证**（同上环境与驱动方式，证据目录同上）：修复后重跑 9 个状态的采集，与修复前逐元素一致（0 行差异）。同一份 `SelectedSessionId=s03` 的 10 路存档，新构建恢复后选中第 3 路（COM3），切到 `dup3` 后正常关闭窗口，存档写回 `s09`；旧构建恢复后选中第 1 路，存档里的 `Id` 全部是新生成的。合并日志为空时点"保存数据"，新构建显示"没有可保存的合并日志。"，英文为 "No merged log to save."，旧构建显示裸键 `SerialMerged.Msg_NoDataToSave`。命令行占住 COM3 时在界面连接，弹出"连接失败: Access to the path 'COM3' is denied."，走的是出错回调在 UI 线程上的分支。未读计数：COM1↔COM2 互连，A（当前会话）定时发送时下拉里 COM2 项显示未读徽标（8）；切到 COM2 后徽标清空，它继续接收也不再出现（RX 涨到 31）。该场景的最后一步"切回 COM1 后徽标重新出现"没有采到：应用在此时被正常关闭（日志显示按序保存与释放），与前面"COM2 作为非当前会话出现徽标"是同一条路径。
- **五处修复里没有运行期证据的部分**：出错回调在线程池线程上触发的分支（服务层的 `SendAsync` 自己吞掉异常，当前只有订阅者抛异常才会走到那条路径）；合并日志非空时的保存对话框标题与成功提示（会弹出系统文件对话框，打扰用户），只做了"键存在且中英对称"的静态检查。
- **P1 服务层（界面不变）**：新增通用会话编队 `ISessionHub<T>` / `SessionHub<T>`（工厂委托创建实例，不经根容器解析服务接口，关闭会话后对象可回收；`ReleaseSession` 可重复调用，`Dispose` 幂等）；新增 `BindEndpoint`（TCP / UDP 的绑定地址解析与端点重叠判定，回退规则与两个服务现状一致）和冲突规则 `EndpointConflict`（TCP 只有服务端会话互斥；UDP 普通会话与任何会话互斥，组播会话之间放行）。`TcpServerService` / `UdpService` 改用它解析绑定地址，行为不变；两个服务接口继承 `IDisposable`。GUI 的 DI 移除两个服务的单例注册，改由两个 Hub 创建会话，页面 VM 从 Hub 取会话。CLI 的容器与命令不变。
- **踢出客户端的事件类型修正**：`DisconnectClient(endpoint)` 改发带 `Endpoint` 的 `ClientDisconnected`，`Disconnected` 只表示本会话的链路停止。旧实现把踢出当成"服务端已停止"：状态胶囊变为未连接、客户端卡隐藏、"停止"按钮置灰，而监听仍在继续。
- **P1 验证**（同上环境，证据目录同上）：新增烟测 `_tools/NetworkSessionSmokeTest`（`dotnet run --project FancyTool/_tools/NetworkSessionSmokeTest`，45 项全过），覆盖端点解析回退与 10 组重叠组合、TCP / UDP 规则的真值表、两个服务端会话同端口时系统 bind 拒绝第二个、同一 Hub 内服务端与客户端环回互发、踢出后监听仍在且能接受新客户端、回收后端口立即可被外部绑定、Hub 释放幂等、UDP 同端口双绑定绕过规则时确实成功、两个组播会话同端口都能启动。CLI：`tcp server` + `tcp client`、`udp bind` + `udp send` 环回通过。界面（后台 UIA，新旧构建同一组步骤）：TCP 页启动服务端、命令行客户端连入并出现在列表、选中后"断开选中客户端"——新构建仍为已连接、客户端卡保留、新客户端可再次连入；旧构建变为未连接且"停止"按钮被置灰（UIA 调用被拒）。UDP 页启动、停止正常。
- **顺带发现（未修改）**：`UdpService` 给 socket 设了 `MulticastLoopback=false`，而 Windows 上接收端关闭环回后收不到本机发出的组播包（独立实验 `.qa/…/mcastloop.txt`：同一组播组同端口的三个接收端，只有设为 false 的那个收不到）。因此"同端口两个组播会话各收一份"只在数据来自其它主机时成立，本机自发时收不到；这是既有行为，是否允许环回需要产品决定。

## 2026-11-26（TCP / UDP 多会话设计文档）

- 新增 `docs/tcp-udp-multi-session-design.md`（待评审，未实现）：沿用串口多端口的会话模型，针对"端点是文本框输入"设计三处差异。其一，下拉末尾放固定的"新建会话"项，默认复制当前会话并把端口 +1。其二，运行中冻结绑定身份，启动前按"TCP 服务端监听端点 / UDP 本地端点"做冲突检查：UDP 因 `ReuseAddress` 同端口双绑定会成功且投递未定义，必须在应用层拦截，组播会话之间放行。其三，链路测试保持页面级并改用独立端点输入，开始测试时才停止全部会话，切页签不再停。
- 服务层建议泛型 `SessionHub<T>` 加 Core 内 `BindEndpoint`，CLI 不受影响。并建议先从串口实现抽出会话级 / 页面级公共基类（单独一期、先留 GUI 基线），否则选择器同步、合并日志、定时发送等逻辑会在三页各留一份。
- 列出迁移时不再带走的现状问题（TCP `Modes` 无绑定、`_cts` 从未赋值、UI 线程 `task.Wait(200)`、定时发送忽略所选客户端）、分期与 6 项待确认事项。仅文档，未改动代码，未重新构建。
- **同日评审，文档更新为 v1.1**：第 1–3 项同意——先抽公共基类并改串口、链路测试用独立端点且开始测试时才停全部会话、运行中冻结绑定身份。第 4–6 项按评审意见改写：TCP 定时发送必须遵守所选客户端，做法是与手动发送共用目标规则、目标在开始时确定、目标客户端断开即停止；发送勾选的文案统一为"全部会话"，串口同步修改；取消全部会话上限，包括串口现有的 8 路。
- 核对第 4 项的调用链时发现一处现状缺陷（静态阅读，未运行复现）：服务端踢出单个客户端时 `TcpServerService` 发的是 `Disconnected`，页面据此把 `IsRunning` 置为 false，状态胶囊变为未连接、"已连接客户端"卡隐藏，而监听仍在继续。已写入设计 6.4，计划在 P1 改为发 `ClientDisconnected`。
- 在 `serial-multi-port-ui-design.md` 文首补后续决策说明，指向新设计。仍仅文档，未改动代码，未重新构建。

## 2026-11-26（串口调试多端口：ComboBox 会话选择器）

- **串口调试支持多端口并发**：单例 `SerialPortService`（实例内只有一份 `SerialPort`/接收缓冲/帧定时器）拆为会话级 `SerialPortSession` + 单例 `SerialPortHub`，端口枚举收口到 `SerialPortCatalog`；GUI 侧 `ISerialPortService` 改 `AddTransient`、新增 `AddSingleton<ISerialPortHub, SerialPortHub>`，CLI 用临时会话，`serial list / send / listen` 命令名与参数不变。
- **保留现有布局，ComboBox 升级为会话选择器**：下拉项带连接状态点（复用 `ConnectionStatusToBrushConverter`）、状态文字、未读徽标、参数摘要；末尾追加"添加端口 COMx"段，选中即新增会话（默认断开、不自动连接）；ComboBox 右侧新增"关闭"按钮。左栏参数/选项、右栏日志/发送、头部状态胶囊全部改绑当前会话，切换会话整组一起换。
- **状态与资源**：`SerialPortViewState` 升到 v2（`Sessions[]` + `SelectedSessionId`），旧扁平结构读到即迁移为单会话且不自动重连；`LogBuffer` 新增 `autoFlush=false` 构造，串口会话改由页面级 16ms 统一刷新（每会话不再各自起定时器），非当前会话日志上限降为 5000 行、当前会话 20000 行。
- **新增行为**：发送区"广播"（按已连接会话逐个下发、失败只影响该路）、`TerminalStatusBar.MiddleDetail` 显示"已连接 N / M 会话"、同一 COM 被其他已连接会话占用时拒绝连接、RTS/DTR 改为真实写 `SerialPort.RtsEnable/DtrEnable`（原实现只弹提示）。
- **i18n**：`Serial.Button_CloseSession`、`Label_Broadcast`、`Label_Aggregate`、`Menu_AddPort`、`Msg_MaxSessions`、`Msg_PortInUseBySession` 六键中英同步。
- **修复（同日）**：添加端口后 ComboBox 收起框为空。原因是 `RebuildChoices` 对 `PortChoices` 整表 `Clear()`，ComboBox 会先把 `SelectedItem` 置空，而重建出来的新实例又不在旧集合里，收起框就停在未选中态；改为增量 `SyncChoiceItems`（会话项插到"添加端口"段之前、被占用端口项就地移除），选择变更后补一次 `Dispatcher` 重推，并给 ComboBox 补 `SelectionBoxItemTemplate`（Fluent 模板只用它渲染收起框，不回退 `ItemTemplate`），`SerialChoiceItem` 另加 `ToString()` 兜底。重新编译 0 错误 0 警告。
- **关闭会话按钮随会话数置灰**：仅剩 1 个会话时不可关闭——`CloseSessionCommand` 加 `CanExecute = CanCloseSession`（`Sessions.Count > 1`），XAML 另绑 `IsEnabled="{Binding CanCloseSession}"` 双保险；`RefreshAggregate` 内只在状态翻转时 `NotifyCanExecuteChanged`，避免每秒刷新命令状态。
- 验证：GUI 与 CLI `dotnet build` 均 0 错误 0 警告；`fancy-tool serial list` 实跑列出 8 个端口；双语键 808/808 对称。**未实测**：GUI 点击交互、真实串口多路收发（无设备）、TCP/UDP 页面日志刷新回归（`LogBuffer` 新构造对旧调用方保留了自建定时器）。合并日志（跨端口时间线）已实现：`LogBuffer` 增加 `EntryEnqueued` 事件，会话 `LogSink` 汇聚到页面 `MergedLog`（20000 行、常驻采集），日志卡标题栏 `ToggleButton` 切换，行前缀为 `[别名]`，清空/保存按当前视图分流。

## 2026-11-11（升级 .NET 11 · 移除 global.json）

- **全量升级目标框架到 .NET 11**：`net10.0` → `net11.0`、Windows `net10.0-windows10.0.19041.0` → `net11.0-windows10.0.19041.0`；覆盖 `WindowsInference.props`、GUI / Core / CLI 三工程、7 个烟测工程、TFM 条件包引用、`publish-win.ps1`、`FolderProfile.pubxml`、烟测脚本路径、AGENTS.md 与 README 双语。历史实测报告保留旧 TFM 作为原始证据，ADR 0004 / 0007 文首补指向新 ADR 的说明。
- **删除 `global.json`**（原固定 10.0.100 / `latestFeature`）：本机缺 10.0.100 时 `dotnet --version` 直接 exit 155，删除后解析为 11.0.100。代价是构建 SDK 不再由仓库锁定，CI 需显式安装 >= 11.0.100；未来安装更高主版本（含预览）会被自动选中。决策、替代方案与完整影响面见 ADR 0023。
- **`PublishWithSatoriGC` 同步升到 11**：`10.0.13-satori.38707290103.1` → `11.0.0-satori.42242472725.2`，运行时包由 SDK 解析为 `11.0.0-satori.42242472725.1`，与主框架同为 11.x（NuSpec 仍为 MIT）。10.x 运行时包不能与 `net11.0` 混用，只改 TFM 不够。
- 清理 .NET 11 新增的 NU1510：Core 移除 `Microsoft.Extensions.Logging.Abstractions` 显式引用（框架自动提供）。
- 修复 Linux 交叉构建：SDK 在 TFM 协商阶段会移除 `RuntimeIdentifier`，`SetPlatform` 后 Core 误判为 Windows TFM 导致 `net11.0` 主工程无法引用；GUI / CLI 的 Core 引用改用 `SkipGetTargetFrameworkProperties` + `SetTargetFramework` 把引用方 TFM 钉给 Core。属 2026-11-03 `SetPlatform` 修复遗留的传递缺口，本次验证发现并修复。
- 验证：GUI Debug / CLI Debug / Linux 交叉 Release（`net11.0/linux-x64`）/ 7 个烟测工程均 0 错误；GUI 与 CLI 0 警告，FfmpegSmokeTest 3 个既有 CS0618。Windows NativeAOT Release 发布成功（`FancyToolAva.exe` 99 MB，产物 `.qa/net11-aot-2026-11-11/`），仅既有 SATORI002 与 FluentAvalonia / DataGrid trim 告警。AOT 产物限时启动 15s 持续运行（exit 124），日志无 `precompiled XAML` / Fatal，未复现 ADR 0007 记录的 .NET 11 RC XAML 缺失问题。CLI 自包含发布成功（72 KB apphost），`fancy-tool.exe --help` 退出码 0、命令目录正常渲染。
- 未实测：GUI 窗口渲染与交互、MSIX / `.deb` / Inno 安装包、`publish-win.ps1` 脚本整体串联执行（GUI AOT 与 CLI 发布已分别验证）、Linux 真机运行、Windows ML / GPU 推理、FFmpeg 全矩阵烟测的实际执行（仅编译通过）。

## 2026-11-04（移除歌词提取功能 · 视频超分/插帧 CLI 实测）

- **整体移除歌词提取功能**（用户决策：本地模型效果不达预期且不再需要；GUI 入口自 2026-10-01 起已关闭，CLI 却仍暴露该命令，两侧行为不一致）：删除 `LyricsExtractionView/ViewModel`、`FancyTool.Core/Services/Lyrics/`（10 文件）、`LyricsModels.cs`、CLI `lyrics extract/export`、`models install --lyrics/--lyrics-high`、`Resources/LyricsModels/`（Qwen3 清单）、`_tools/LyricsSmokeTest`、**`External/Lyricify.Lyrics.Helper`（约 100 文件，唯一使用方即歌词功能）** 与 `NOTICE-Lyrics.txt`；同步清理 GUI 服务注册/导航/磁贴、CLI 分发与 CommandCatalog 渲染（**19 组/58 条 → 18 组/56 条**）、i18n 双语约 70 键、Core/Ava csproj 与 slnx 的 Lyricify 引用及许可拷贝（SharpZipLib 随之消失）。决策与范围见 ADR 0022。
- **视频超分 / 插帧 CLI 端到端实测通过**（补上 ADR 0021 的未验证项）：Intel Arc 140T，`--sr` ×2 四模型（ravu-r2 DirectML 6.2s、anime4k-cnn-s 2.7s、fsrcnnx-s 3.1s、ravu-r3 5.3s，均 160×120→320×240；anime4k fp16 亦通过）、RIFE lite/full ×2 → 20fps、lite ×4 → 40fps、组合 SR+插帧 320×240@20fps；rife425 首次下载遇瞬时网络失败，重试成功，错误传播正确。证据 `.qa/cli-e2e-2026-11-04/measurements.json`，详见 ADR 0021 补充（三）。
- 烟测适配：`FfmpegSmokeTest --settings-only` 的嵌套迁移 fixture 由 `Qwen3Lyrics/asr` 改为中性的 `nested/asr`，歌词队列恢复校验块随功能删除；AGENTS.md 去掉 LyricsSmokeTest 命令与 `FANCYTOOL_LYRICS_*` 环境变量说明；README 双语 CLI 能力行改为 18 组/56 条。
- 验证：Core/Cli/Ava 三工程 `dotnet build` 0 错误（slnx 仅剩既有 wapproj MSB4019）；BatchSmokeTest 21/21；`FfmpegSmokeTest --settings-only` 全矩阵通过；CLI 根帮助 / `models list` / `skill print` 无 lyrics 残留，`lyrics` 命令返回未知命令；源码 grep 仅剩 `.vs` 缓存（不入库）。本机已安装的各 agent `SKILL.md` 含旧版 lyrics 小节，`fancy-tool skill install --force` 刷新。既有遗留（非本次引入）：i18n 存在 `AppFatal.*`（zh）/`App.FatalError.*`（en）死键，无代码引用。

## 2026-11-03（MSIX 打包修复 · 二：VS IDE 生成路径）

- 修复 **VS IDE 内生成 wapproj** 仍报 APPX0703（清单引用 `cli\fancy-tool.exe` 不在负载）：DesktopBridge 有两条负载收集通道——命令行打包（`_IsGeneratingAppxPackage=true`）消费 `DesktopBridgePublishItemsOutputGroup`（前次已注入 ✓）；**VS IDE 生成**（`BuildingInsideVisualStudio=true` → `_IsGeneratingAppxPackage=false`）改用 `PackageOutputGroups`（`BuiltProjectOutputGroup` 等构建输出组），CLI 的 Exec 发布产物不在其中。CLI 本身一直有构建（`PublishCliForMsix` 正常执行），缺的是这条通道的清单项。
- 修复：`FancyToolAva.csproj` 新增 `AddCliToBuiltOutputForIdePackaging`，把 CLI 发布输出注入 `BuiltProjectOutputGroupOutput`。两个关键约束：**必须 `BeforeTargets`**——MSBuild 的 `AfterTargets` 修改的项不会进入被挂 target 的 `Returns`（Returns 在本体完成时捕获，早于钩子），IDE 模式直接消费该 Returns 就拿不到；命令行模式 `PublishDir` 被 DesktopBridge 改写为 msixpublish 而 IDE 模式保持默认 `$(OutputPath)publish\`，两个落点都探测。包内路径仍由 wapproj 的 `RebaseCliPayloadToPackageRoot` 统一到包根 `cli\`。
- 验证（VS 18 MSBuild 模拟 IDE 全局属性 `-p:BuildingInsideVisualStudio=true`）：构建 exit 0，`FancyToolAva.Msix.build.appxrecipe` 生成，负载 584 项含 cli 250 项（`PackagePath=cli\...` 包根、`cli\fancy-tool.exe` 就位、无 `FancyToolAva\cli` 重复）；GUI 入口 `FancyToolAva\FancyToolAva.exe`。命令行打包模式回归通过（msix + msixbundle + appxupload 完整出包，包内清单与负载一致）。诊断日志 `.qa/msix-ide-*.log`。

## 2026-11-03（技能安装扩展到全部主流 agent）

- 技能安装从"只写 Qoder"扩展为六家 agent 同装（SKILL.md 已是跨厂商开放标准，一份内容多处落盘）：Claude Code `~/.claude/skills`、Cursor `~/.cursor/skills`、Gemini CLI `~/.gemini/skills`、Codex `~/.codex/skills`、Copilot CLI `~/.copilot/skills`、Qoder `~/.qoder-cn/skills`。默认全部安装（未装的 agent 目录提前创建、装上即用）；AGENTS.md 类常驻指令文件（CLAUDE.md / GEMINI.md / instructions.md）**不扩展**——常驻上下文写命令清单会污染每个会话，Qoder AGENTS.md 受管块保留为可选附加。
- CLI 参数 `--target <qoder|agents|both>` 改为 `--agent <all|短名列表>` + `--agents-md`；`SkillInstallTarget` 枚举删除，改为 `SkillInstaller.Agents` 注册表（`SkillAgentTarget` 记录）+ 参数化 `Install/UninstallAsync`，安装/幂等/`--force`/卸载清理语义逐 agent 不变。GUI 首启对话框主按钮改为「安装到全部 agent」，次按钮「技能 + AGENTS.md」，`SkillPrompt` 九键中英文同步更新。
- 验证：`FANCYTOOL_SKILL_HOME` 指向 `.qa/skill-multi` 下，六家 `skill install` 全部写入（frontmatter 首行、生成标记在其后、CRLF）；重复执行全部「未更改」；`--agent claude,qoder` 只处理指定两家；未知名退出码 2 并列出合法值；预置用户内容的 Qoder `AGENTS.md` 经 `--agents-md` 只增受管块，`skill uninstall --agents-md` 后六家技能目录清空、用户段落原样保留。GUI 首启对话框文案经静态核对（i18n 键与 `App.axaml.cs` 占位参数一致），未做 lvt 实机点选。见 ADR 0021 更新小节。

## 2026-11-03（MSIX 打包修复）

- 修复 MSIX 构建两处失败（本机已装 VS 18 Community，可真实跑 DesktopBridge 打包）：
  - **CS0006 找不到 `FancyTool.Core\obj\x64\Release\...\ref\FancyTool.Core.dll`**：Core / Lyricify 未声明 `Platforms`，x64 评估的引用方（sln 的 Release|x64、MSIX 隔离链）把 `Platform=x64` 传染给它们，期望 `obj\x64` 中间目录；该路径只在"恰好有人以 x64 构建过库"时存在，VS IDE 内构建 wapproj 时依赖不随隔离链构建即报错。修复：三处 `ProjectReference`（Ava→Core/Lyricify、Cli→Core、Core→Lyricify）钉 `SetPlatform="Platform=AnyCPU"`，slnx 平台映射同步改为 AnyCPU，库在任何引用方平台下只有单一中间目录。
  - **APPX0703 清单引用 `cli\fancy-tool.exe` 不在负载**：DesktopBridge 负载不是枚举发布目录，而是消费入口工程 `DesktopBridgePublishItemsOutputGroup` 的项清单（源自 `ResolvedFileToPublish`），`PublishCliForMsix` 用外部 dotnet 进程写入的 CLI 文件不在其中。修复分两层：`FancyToolAva.csproj` 新增 `AddCliToDesktopBridgePublishItems`（在 `PublishItemsOutputGroup` 后把 `$(PublishDir)cli\**` 投影进输出组，注意先在临时项上通配再投影，直接写 `%(RecursiveDir)` 触发 MSB4120 自引用污染）；wapproj 新增 `RebaseCliPayloadToPackageRoot`（`_ConvertItems` 后把 CLI 项从 `FancyToolAva\cli\` 重定位到包根 `cli\` 并同步改写 `_FileToCopy`/`_UploadFileToCopy`，否则 CreateWapProjPackageFiles 给入口工程输出统一加的 `FancyToolAva\` 前缀会与清单字面值、Upload 变体的剥前缀行为互相矛盾）。
- 验证：清空全部 `obj`/`bin` 的 x64、msix 中间产物后冷启动，`MSBuild FancyToolAva.Msix.wapproj -restore -p:Configuration=Release -p:Platform=x64` 完整出包（msix + msixbundle + appxupload），包内 `cli/fancy-tool.exe` 与三份生成的 AppxManifest（主 / ForBundle / Upload）的 `Executable="cli\fancy-tool.exe"` 一致，无 `FancyToolAva/cli` 重复残留；GUI 入口为 `FancyToolAva\FancyToolAva.exe`。slnx Release|x64 回归通过。诊断日志与中间证据在 `.qa/msix-*.log`。
- 未实测：真机安装后 `fancy-tool` 别名调用（包未安装，仅静态校验清单与负载一致）。

## 2026-11-03

- 修复视频转码文件列表在拼接模式下的横向错位：状态 / 进度列文案长度逐行变化，原排在固定宽度的 ↑↓ 与循环次数控件右侧，借助 `*` 列吸收宽度导致每行控件位置不同；现将状态列移到排序与循环控件左侧，固定控件逐行对齐。
- 验证：Debug 构建 0 错误；lvt 实际窗口运行拼接模式，首行转码中进度文案（182 px）与其余行「等待中」（33 px）、失败长文案并存时，↑↓ / 循环输入框 / 删除按钮的横向坐标逐行一致（`x=1195/1216/1243/1401`）；截图与 UIA dump 见 `.qa/videolist-align/`。
- 调整许可失败语义：已由 Store 确认的正式许可 / 试用 / 非活跃结论，在后续查询失败或超时后继续沿用，已购用户不再因 Store 或网络临时故障被锁定；首次查询失败、包身份不匹配、初始化或监控启动失败仍锁定，试用保留受本地 UTC 到期与进程内单调时钟约束，Store 明确返回非活跃时仍立即撤权。保留仅限当前进程内存，重启重新验证。见 ADR 0020。
- 验证：`LicenseSmokeTest` 普通与 MSIX 配置回归通过（覆盖失败保留、超时保留、迟到结果作废、试用到期转锁定、明确非活跃撤权、首次失败仍锁定），结果见 `.qa/license-transient-2026-11-03/`。
- 新增 CLI 模式并抽取公共层：算法 / 服务 / 模型 / 音频 DSP / 进程内 FFmpeg / 命令目录移入新建的 `FancyTool.Core`，GUI 工程只保留 View / ViewModel 与 Avalonia 装配，`FancyTool.Cli`（`fancy-tool.exe`）仅引用 Core。Core 经四个接缝与 UI 解耦：`IUiDispatcher`（GUI 包 `Dispatcher.UIThread`，CLI 用立即执行）、`LocalizationRegistry.Provider`（GUI 走 Lang.Avalonia，CLI 直读随包 `i18n/*.json`）、图片预览返回 PNG 字节而非 `Bitmap`、`ISuperResolutionPreferences`。
- CLI 覆盖 GUI 每一项功能：19 个命令组 / 58 条命令（对称与非对称加解密及密钥生成、哈希、Base64、文件批量加解密与文件夹比较、图片 Base64 / 转换 / 超分、视频探测 / 预览 / 转码 / 拼接、音频探测与转换、串口、TCP/UDP 调试与链路测试、模型管理、歌词提取与导出、内置 FFmpeg 信息、技能安装）。根帮助、分组帮助、单命令 `-help`、`skill print`、`SKILL.md`、`AGENTS.md` 段落全部由 `FancyTool.Core/Cli/CommandCatalog.cs` 单源渲染（中英双语），进度写 stderr、结果写 stdout（`--json`），退出码固定 0/1/2/3/130；手写解析器不引 System.CommandLine。`file keygen` 产出的 `Key/Iv/Format` JSON 与 GUI 互通。
- 打包与首次启动：`publish-win.ps1` 在 GUI 之后发布 CLI 到 `<输出>\cli`（`-SkipCli` 跳过），MSIX 清单新增第二个 Application（`AppListEntry=none`）并以 `uap5` 应用执行别名注册 `fancy-tool.exe`，Inno 安装包 `ChangesEnvironment=yes` + `[Code]` 在装 / 卸时增删当前用户 `HKCU\Environment\Path` 的 `cli` 项；MSIX 下 CLI 走 `StoreContext` 无窗口许可校验，`skill` 三条命令不受许可限制。GUI 首次启动在许可通过后弹 `FAContentDialog` 询问是否把用法写入本机：主按钮只写 `%USERPROFILE%\.qoder-cn\skills\fancy-tool\SKILL.md`，次按钮同时写用户级 `AGENTS.md` 的受管区块，「暂不」不写；`AppPreferences.CliSkillPromptCompleted` 保证只问一次，后续用 `fancy-tool skill install | print | uninstall` 刷新或移除。i18n 中英各新增 `SkillPrompt` 9 键（两侧键集一致）。
- 验证：GUI / Core / CLI Debug 构建 0 错误 0 警告；58 条命令中英文 `-help` 逐条执行退出码 0；加解密往返、`file keygen` → `file encrypt` → `file decrypt` 一致性、`video probe/preview/transcode/concat`、`audio`、`serial list`、`tcp server`+`tcp client` 回环（收 `ping`，状态消息已本地化）、`udp`、`linktest`、`models`、`skill install/uninstall` 实测通过（`FANCYTOOL_SKILL_HOME` 指向临时目录，重复安装报「未更改」，带用户内容的 `AGENTS.md` 只动受管区块）；`BatchSmokeTest` 适配 Core 新接口后 runs=21 failures=0；FFmpeg 代码整体迁入 Core 后重跑全矩阵 total=127 failures=0 skips=2（NVENC/AMF 缺驱动按惯例 SKIP，日志 `.qa/ffmpeg-core-smoke.log`）；GUI 首启对话框经 lvt 确认渲染与写入。MSIX 模式发布含 `cli\fancy-tool.exe` 且 `makeappx pack` 通过，Inno 脚本 `iscc` 编译通过。`dotnet build` 整个 `slnx` 仍只有既有的 `FancyToolAva.Msix.wapproj` 报 `MSB4019`（dotnet SDK 无 DesktopBridge，需在 VS 内生成）。见 ADR 0021。
- 未实测：真机 MSIX 安装后的别名调用与安装器 PATH 增删（本机缺 VS 18 / DesktopBridge 工具链，仅做到 `makeappx` / `iscc` 校验）；CLI 侧视频超分 / 插帧与歌词端到端（本机未安装 VideoSr / RIFE / 歌词模型，需先下载数 GB）；`.deb` 暂未打包 CLI。CLI 图片超分已实测：`image convert --sr-model realesr-general-x4v3 --sr-scale 2` 把 160×120 帧放大到 320×240（4.9 秒，`.qa/cli-sr/`）。
- 验证中发现并修掉两处 CLI 一致性问题：`file keygen` 的输出参数只认 `--out`，与其余命令的 `-o, --out` 不统一（命令目录补别名）；`skill uninstall` 的结果消息硬编码英文，未按已有 `zh` 开关本地化（`SkillInstaller.UninstallAsync` 增加 `zh` 参数，六条消息双语）。

## 2026-11-01

- 视频转码新增流复制（stream copy）：视频 / 音频编码器下拉各加「Copy」选项，不重编码直接复制流（四组合全支持，如视频 copy + 音频转 AAC），换容器 remux 毫秒级完成且无损。`ConvertOnce` 内加直通分支（`avcodec_parameters_copy` + codec_tag 清零 + packet 时间戳平移重标定），裁剪按关键帧近似（与 `-ss -c copy` 同语义）；容器兼容性经 `avformat_query_codec` 预检 + muxer 写头兜底，批处理中不兼容文件失败并进明细报告（不自动回退）。互斥三层守卫：copy 时画质 / 滤镜 Tab 禁用（参数保留）、`BuildOptions` 兜底忽略冲突项、服务端入口拒绝（防 API 直调）；拼接模式与 GIF 容器不支持 copy（列表回落 + 服务端拒绝）。i18n 中英 4 键同步。
- 验证：新增 `_tools/FfmpegSmokeTest/CopyTests.cs` 12/12 通过（双流 remux 体积 / 编码断言、换容器 MKV、混合组合、trim 关键帧近似、无音源、GIF 强制重编码、缩放 / 两通冲突拒绝、拼接拒绝、VP8→MOV 容器拒绝且输出不残留），FFmpeg 全量烟测 127 用例 0 失败（QSV/AMF 驱动缺失按惯例 SKIP）。见 ADR 0019。
- 视频转码新增拼接模式：勾选后按列表顺序把多个视频合并为一个文件（单次编码、无中间文件），分辨率不一致的片段以第一个视频为基准等比缩放并加黑边，帧率保留各段原始时间戳（VFR）。新增 `IVideoTranscodeService.ConcatTranscodeAsync` 多段单编码管线（独立分部文件，未改动 `ConvertOnce`）；拼接时间轴固定 1/90000、编码器标称帧率取全段最高帧率，避免高帧率段 pts 重复。拼接模式下 AI 超分 / 插帧禁用（VM 与服务双侧拒绝），暂停按钮隐藏（单次会话无段间暂停粒度）。
- 顺序调整为每行 ↑↓ 按钮（经 `IVideoFileItemOwner.MoveUp/MoveDown` 执行 `FileItems.Move`，转码中锁定）：曾实现 `DragDrop` 拖拽实时重排（附加行为 `ListBoxDragReorder`），实测手感不佳后移除；每项循环次数数字框去掉文字标签并加宽至 150；状态列进度行按内容显隐（`HasProgressDisplay`），修复"等待中"因空进度行占位而偏上、无法垂直居中的问题。中英文资源同步（净增 12 键）。
- 同步修复烟测工程既有编译错误：`SettingsTests` 补上 `SettingsViewModel` 新增的 `LicenseViewModel` 依赖（未解锁授权桩）。
- 验证：拼接烟测 14/14 通过（顺序 / 循环 / 裁剪×循环 / 异构 letterbox 像素断言 / 有音无音混合 / GIF / MKV·VP9·Opus / 两通 / 非零时间戳 / 进度单调 / 取消清理 / 非法输入），FFmpeg 全量烟测 115 用例 0 失败（NVENC/AMF 驱动缺失按惯例 SKIP）。见 ADR 0018 与 `_tools/FfmpegSmokeTest/ConcatTests.cs`。

## 2026-10-31

- 修复已购电脑在 VS MSIX Release 中仍被锁定：真实商品包身份是 `SennpaiStudio.---modbus`，清单及商店关联由错误的 `FancyTool.FancyTool` 更正；运行期核对 PFN，错误身份 / 查询异常不再误报为未购买。保留严格门控，无签名或调试放行。
- 设置页新增当前授权状态、试用截止时间及非完整授权购买提醒，复用唯一许可服务并随授权 / 语言变化刷新。实际开发部署已取得 Store `IsActive=true, IsTrial=false` 并显示完整版；普通 / MSIX 各 23/23 回归通过，真实 MSIX 打包和 Linux 交叉编译通过。证据见 ADR 0017 与 `docs/license-owned-validation-2026-10-31.md`。

- MSIX 新增全应用收费门控：参考已投入使用的 music_player Store 查询和购买流程，只有有效正式许可或未到期试用开放全部工具；初始化、身份、查询或监控失败均锁定，不保留签名例外、首次失败放行、旧权限回退或 DEBUG 覆盖。非 MSIX Windows / Linux 发布完全解锁。
- 初查通过前延迟创建工具及后台服务；到期弹框购买，关闭提示仍锁定。运行中失去授权后结束旧工具进程并重新核验，保证通信与后台任务不会继续使用；在途输出可能不完整。锁定退出保留已有 ViewState，中英文资源同步。
- 验证：普通与 MSIX 配置各 20/20 回归通过（含真实 App 依赖图和进程重启 / 原生句柄释放），MSIX Release 与 Linux 交叉编译通过，lvt 验证实际无包身份窗口的提示、关闭和重试仍锁定。隐藏窗口截图未成功；真实 Store 交易、签名安装包试用、GPU / 转码中途撤权未实测。见 ADR 0016 与 `docs/msix-license-validation-2026-10-31.md`。

- 修复 README 中英文各 16 张截图的颜色通道和截取区域：在独立隐藏桌面采集完整窗口，不切换前台或操作用户鼠标键盘；设置页由仅调整启动页的隔离构建采集。已核对双语页面、链路测试和图片尺寸（2160×1350），脚本及联系表位于 `.qa/readme-shots-isolated/`。
- 商店文案二次修订（仓库外 `H:\download\listingData-9P543GHQQKVK-1152921505702283998.csv`）：Description 尾段移除「基于 MIT 协议开源 / 可自由用于个人与企业场景」宣称，保留源代码与问题反馈地址；README 与项目本身不变。回读校验 454 行不变、仅 Description 中英两处变更。
- 重新截取 README 软件截图（`FancyTool/docs/img/{zh,en}/1-16.png`，各 16 张，覆盖导航全部页面并新增 TCP/UDP 链路测试模式截图）：构建 Debug 版并用 lvt（UIA）驱动导航、Win32 PrintWindow 干净截窗，应用数据经 `FANCYTOOL_DATA_DIRECTORY` 隔离在 `.qa/readme-shots/data`；截图自动化脚本保留在 `.qa/readme-shots/`（shot.py / drive.py）。
- 更新 README.md 与 README.en.md 过时描述：头部简介补音视频；新增「音频转码」小节并规范「视频转码 / 歌词与字幕提取」标题；功能矩阵补图片超分、视频转码、音频转码、歌词与字幕提取行，TCP/UDP 行补链路测试；歌词章节移除「FFmpeg 动态绑定不支持 NativeAOT、保持托管发布」过时表述；「技术架构-发布」改为自包含 NativeAOT（MSIX 路径例外托管）；构建脚本路径由「仓库根目录」更正为 `FancyTool/` 目录；跨平台分发移除不存在的 `publish-msix.ps1` 流程，改为 `FancyToolAva.Msix` 项目 + ADR 0009，`.deb` 产物路径更正为 `FancyTool\dist\`；数据存储的模型目录说明对齐现行模型管理（图像超分启动后台下载、RIFE/歌词按需校验下载、视频超分随应用分发）。截图区扩展为 16 张（新增音视频处理与链路测试页面）。
- 歌词功能已废弃（用户确认）：README.md / README.en.md 移除「歌词与字幕提取」整节、功能矩阵歌词行与数据存储中歌词模型（`Qwen3Lyrics`）说明；商店 CSV（仓库外）移除 Description 歌词段落、ShortDescription / ReleaseNotes 歌词字样，Feature11-13 重排为文件夹比较、Base64 与图片转 Base64、空。回读校验 CSV 454 行不变、中英文全文歌词关键词为 0；代码本身未动（`LyricsExtractionEnabled = false` 维持入口关闭）。
- 更正发布形态记录：NativeAOT 已启用（`csproj` 默认 `PublishAot=true`，FFmpeg shared 库经静态 P/Invoke 随应用分发，加载引导见 `Services/FFmpegNative.cs`，兼容 AOT）。AGENTS.md 原文「自包含托管应用，NativeAOT 关闭」与「NativeAOT 与单文件发布不使用于正式发布」更正为「自包含 NativeAOT 发布，MSIX 打包路径（`FancyToolMsixBuild=true`）例外走托管发布」；同步修正 `publish-win.ps1` 与 `publish-linux-deb.ps1` 过时的首行注释（后者注明未覆盖 `PublishAot`，跨 OS AOT 需目标平台工具链，可用 `-p:PublishAot=false` 回退托管发布）。
- 更新 Microsoft Store 商店文案（仓库外 `H:\download\listingData-9P543GHQQKVK-1152921505702283998.csv`，原文件备份为同目录 `*_backup.csv`）：ReleaseNotes 移除 modbus 字样并改为本次功能说明；Description / ShortDescription 在保留原有正确内容与 AOT 宣称的基础上，补充图片 AI 超分（RealESRGAN / ClearReality）、视频/音频转码（HDR、AI 视频超分、RIFE 与 NVIDIA/AMD 光流插帧、DSP 升频）、歌词与字幕提取（Qwen3-ASR）以及 TCP/UDP 链路测试；新增 Feature7-13 与中英文 SearchTerm7。功能表述与 README 及导航结构逐一核对，未宣称未实现的能力。
- 验证：CSV 回读 454 行不变，仅 22 个目标单元格变更，Field/ID/Type/default 结构列与其它字段逐字节一致，UTF-8 BOM 与 CRLF 记录分隔保持，全文 modbus 为 0；AGENTS.md 与两个脚本为纯注释/文档修改，无行为变更。

## 2026-10-22

- TCP / UDP 调试页新增「链路测试」独立模式（页面功能切换）：TCP 带宽（默认 10s / 64KB 块，流尾 16 字节标记区分正常完成与中止）、UDP 带宽 + 丢包率（默认 1400 字节包、10 Mbps 目标速率，FIN/FIN-ACK 协议让两端都能看到丢包统计）；使用专属 Socket 服务（`TcpLinkTestService` / `UdpLinkTestService`），与通信调试互不占用，进入测试自动停止当前通信；参数与功能选择持久化到 ViewState，i18n 双语言 52 键同步。
- 新增烟测 `_tools/LinkTestSmokeTest`（真实服务代码、环回）：17 项全部通过，含 TCP 字节一致性（≈8.8 Gbps）、UDP 0 丢包 + 统计回传、双端中止语义、接收端先停、无数据停止（必有空中止回执，界面不卡「测试中」）；期间修复三个真实缺陷：TCP 中止被接收端误报为正常完成、UDP 接收端中止时回传过期统计、无数据停止时无事件回执。协议与证据见 `docs/linktest-2026-10-22.md` 与 `.qa/linktest-2026-10-22/`。
- 验证：`dotnet build -c Debug` 0 错误（仅既有 NU1504 警告）；i18n 键静态检查中英一致。未做实机 GUI 操作验证，已列入文档「已知限制」。
- 修复 MSIX 打包报错 `Restore the Satori Runtime pack with the same RID used for publish.` 与随之而来的 NU1504 重复 `PackageReference` 告警：`FancyToolAva.csproj` 去掉与 `RuntimeIdentifier` 重叠的 `RuntimeIdentifiers`（Satori SDK 组合 RID 列表时 `KeepDuplicates` 对展开项列表不生效，同一包被引用两次），并在 `PublishAot=true` 时显式补 `PublishWithSatoriGC.Runtime.$(RuntimeIdentifier)`，使任何一次还原都带上与发布 RID 一致的 Satori 运行时，MSIX 的托管发布（`PublishAot=false`）因此总能取到它。根因、备选方案与证据见 `docs/adr/0014-satori-runtime-pack-rid.md`。
- 验证：修复前以「按 `PublishAot=true` 还原 + 按 `PublishAot=false` 发布」复现出同样的报错与 NU1504，修复后同一场景还原与发布均退出 0，托管发布产物的 `coreclr.dll` 与 Satori Runtime 包内文件哈希一致；AOT 发布产物不变（仍为原生 exe、无托管 DLL、未混入 CoreCLR 运行时文件）；Debug 构建与 `-r win-x64` / `-r linux-x64` 还原均无 NU1504。本机无 Visual Studio/DesktopBridge，MSIX 收尾打包（生成并签名 .msix）未验证，需在装有 VS 的机器上补跑。
- 修复 MSIX 签名打包的商店保留名校验报错「Properties 元素的 DisplayName 元素必须具有应用程序名称值」：`Package.appxmanifest` 的 `Properties/DisplayName` 改用 `Package.StoreAssociation.xml` 预留的第一条名字（`妙妙小工具---串口、modbus、网络调试、图像转换、文本文件加密解密工具`），`Package.StoreAssociation.xml` 的 `Publisher` 由占位 DN 改回与清单 `Identity/Publisher` 一致的 `CN=CF259542-70C0-4852-9523-066A80212473`（这些保留名在该 DN 名下预留）。开始菜单显示的 `uap:VisualElements/DisplayName` 不变。约束与背景见 `docs/adr/0009-msix-entry-point-output-metadata.md` 末尾补充。
- 验证：脚本逐项比对清单与商店关联文件的 Identity 名、Publisher、保留名包含关系全部通过，XML 可解析、UTF-8 BOM 与 CRLF 未变；本机无 Visual Studio/DesktopBridge，真实签名打包未执行。

## 2026-10-20

- 修复安装/更新 GPU·NPU 组件时 UI 卡死：`WindowsMlAccelerator.InstallAsync` 的目录枚举（`FindAllProviders`）、`TryRegister`（加载厂商 EP 原生 DLL）与已安装组件同步完成的 `EnsureReadyAsync` 原先落在 UI 线程，现整体移入线程池（与 `RegisterInstalledAsync` 一致）；三个安装入口（视频超分/插帧/图片转换）共用该路径。
- 图片转换页两处 `ReleaseSessions()`（持推理锁同步 Dispose 原生 DML 会话）改为 `Task.Run` 执行：安装硬件组件完成后、批量转换结束后，均不再阻塞 UI 线程。
- 验证：`dotnet build -c Debug` 0 错误（仅既有 NU1504/外部库警告）；线程封送依赖既有 `Dispatcher.UIThread.Post`/`_uiContext.Post` 进度回调，行为不变。未做实机 GPU 安装回归，待下次组件更新时观察 UI 响应。

## 2026-10-17

- 上轮视频管线改为异步有界队列，增加独立 GPU 会话并发、像素前后处理和帧缓冲复用，修复并发 CUDA Graph 的画面损坏。
- 接入 NVDEC → CUDA 超分/RIFE → NVENC 显存帧传递；合并 RIFE 像素内核，GPU 缩小预览，补齐取消、回退及原生资源释放。
- RTX 5070 Ti 的 1080p 对照：超分 55.00→113.98 fps，补帧 83.15→136.66 fps，组合 53.33→84.12 fps；色彩转换差异和验证范围见显存管线报告。
- 更新 AGENTS.md，要求每次变更追加简要日志；从 Git 历史移除上一轮 1,633 个测试产物，原始证据保留本地 `.qa/`。

- HDR：接入 10 位 NVDEC、GPU 色调映射和 P010→NVENC，RIFE 支持映射后输出 PQ/HLG；补齐帧色彩标记、LUT 上传同步、元数据、预览及软解回退验证。模型仍在 SDR 域推理，非无损 HDR 处理，速度与画质见 HDR 实测报告。

- 1080p HDR 实测（相同 GPU 模型，CPU 色彩参考→显存管线）：超分 26.05→105.30 fps，补帧 70.14→134.78 fps，组合 33.36→80.43 fps；57 项 HDR GPU 扩展通过，画质和限制见 HDR 报告。

- 彻底清理旧测试产物的本地及远端分支历史，1,648 个现有文件与历史版本归档到 `.qa/`；同步证据链接和复现基线，忽略旧产物目录，使用精确租约强制推送。
- 勾选 AI 视频超分后显示持续可见的 HDR 有损警告，说明 SDR 模型处理后的高光与色域无法完整恢复；支持中英文即时切换与主题切换，取消勾选后隐藏。
- 验证：Release 构建、真实转码页面烟测和 9 项桌面 UI 检查通过；警告在宽/窄窗口勾选后全文可见，支持浅色、深色和系统主题。日志、截图及清理映射见 `.qa/git-history-cleanup-2026-10-17/`。
- 修复 NVIDIA 光流不可用：固定更新含 fruc_vulkan 的 FFmpeg Windows/Linux 六库，保持 AutoGen ABI；改为初始化滤镜前绑定硬件设备，RTX 5070 Ti 实际补帧通过。补充 CPU 运动补偿单核瓶颈与复现记录。
- 验证：Windows/Linux 构建、14 组回归及真实补帧页面通过；同一 1080p 4× 输入重复中位数：CPU 8.07、NVIDIA 光流线性布局 30.89、RIFE Lite GPU 139.44 输出 fps，场景与限制见光流诊断报告；原始证据保留 `.qa/optical-flow-2026-10-17/`。
- 光流优先探测线性图像，回读优化使本机场景 11.51→30.89 fps；保留原布局能力回退。288 帧差异 PSNR 52.93 dB、SSIM 0.998572，非逐像素一致；23 项插帧与真实 UI 追加回归通过。

- 复核 CPU 运动补偿参数：未找到经过等质量验证的稳定收益，保留原默认值；记录单线程限制、7 组参数初筛及复现命令，原始数据放 `.qa/cpu-interpolation-2026-10-17/`。

## 2026-10-18

- 补充 CPU 运动补偿参数复核及 Dlss5VideoEnc 移植报告链接；CPU 默认值保持不变，未宣称等质量提速。
- 将从本目录运行外部烟测留下的 40 个未跟踪文件移到 Dlss5TestDemo 的 `.qa/interpolation-port-2026-10-17/main-cwd-evidence/legacy-build/`，增加根目录 `/build/` 忽略规则；核实当前索引与历史均未收录该目录。

## 2026-10-19

- 把 DesktopBridge 打包缓存 `FancyToolAva.Msix/FancyToolAva.Msix.assets.cache` 移出版本库，并在 `FancyToolAva.Msix/.gitignore` 增加 `*.assets.cache` 忽略规则：该二进制缓存记录本机绝对路径（如 `C:\Users\<用户>\.nuget\packages\...`），一次打包会话就会被改写，历史上一共产生 11 次无意义的二进制提交。
- 验证：删掉该文件后 `FancyToolAva.Msix.wapproj` 的 Debug 构建退出码 0，且构建过程不会重建缓存，说明普通构建不依赖它；文件保留在本机磁盘，下次打包会重新生成。
- 遗留（与本次改动无关）：Release 打包在当前提交上仍因 `Package.appxmanifest` 的 `Properties/DisplayName`（`FancyTool`）不在 `Package.StoreAssociation.xml` 的保留名列表中而报 APPX1607 失败；该回归来自 2026-08-28 的“布局调整”提交，与 ADR 0009 中“显示名已改为第一个保留名”的结论不一致，缓存存在与否都会复现。
- 为解决 GitHub 拒收超大文件（`GH001`，单文件上限 100 MB）：把 FFmpeg 六库 `FancyTool/Libraries/FFmpeg/{win-x64,linux-x64}` 移出版本库，并只重写 `ffmpeg` 分支在 `2c348f1..` 区间的 40 个提交（`master`/`i18n`/`opt` 哈希不变），旧历史保留为标签 `pre-github-purge-2026-10-19`。待推送内容由 766 MiB 降到约 35 MiB，FFmpeg 二进制原本占 731 MiB（95%）。
- 新增 `FancyTool/Libraries/FFmpeg/fetch-ffmpeg.ps1`：从 `BUILD_INFO.md` 读取固定的 BtbN release tag、发布包名与逐文件 SHA-256（单一来源），支持 `-Platform win|linux|all`、`-VerifyOnly`、`-ArchiveDirectory`（离线）与 `-Force`；下载缓存与解包临时目录在已忽略的 `.qa/ffmpeg-fetch/`。
- `FancyToolAva.csproj` 新增 `EnsureFFmpegSharedLibraries` 目标：对应平台缺库时以 `FT1001` 中断构建并给出获取命令，避免产出装不上解码库的应用；`.gitignore`、`BUILD_INFO.md`、README 与 AGENTS.md 同步说明。
- 验证：隐藏 `avcodec-63.dll` 后构建报 `FT1001`（含修复提示），恢复后正常；两个平台目录分别移走后由脚本真实下载固定发布包（win zip 84.7 MB / linux tar.xz 67.2 MB）解出六库，重新生成的十二个文件与原有文件 SHA-256 逐一相同，`-VerifyOnly -Platform all` 退出码 0。
- 验证（全新克隆）：从 GitHub 以 `--depth 1` 克隆 `ffmpeg` 分支后 `FancyTool/Libraries/FFmpeg/win-x64` 为空，构建被 `FT1001` 中断；运行 `fetch-ffmpeg.ps1 -Platform win` 后构建成功（0 错误），六个 DLL 与 `FancyToolAva.exe` 均出现在 `bin\Debug\net10.0-windows10.0.19041.0\win-x64`。

## 2026-10-23

- 新增依赖与模型商业许可核查报告：列出 UltraSharp / LiveAction 非商业限制、GPL/LGPL 收费分发义务，以及 CHTCHSConv 和 Windows ML / 厂商 EP 的待核授权问题。
- 验证：静态核对现有还原图的 82 个包、模型清单与已有发布目录；在线核对上游许可，只读提取微软原始组件 EULA。原始证据保存在 .qa/license-audit-2026-10-23/；未改动生产代码，未重新构建发行包。
- 按 GPL 开源收费目标移除 UltraSharp / LiveAction SPAN：清理下载、服务调用与页面双语入口，图片超分保留三款 RealESRGAN 和 ClearReality；旧选择按稳定模型编号恢复，退役编号回退到 ClearReality，避免下标错位；同步候选/测速工具、README 与 ADR 0015。
- 验证：Debug 构建 0 错误（3 个既有截图 API 警告）；真实下载服务的离线请求、退役编号及旧缓存守卫、8 种旧状态迁移和源生成保存回读、真实 Avalonia 绑定与三主题渲染通过；lvt UIA 展开确认仅 4 款保留模型。本次涉及的双语资源与 CRLF 静态检查通过；未执行 GPU/NPU 推理，其他依赖授权待办未据此关闭。证据见 .qa/model-removal-2026-10-23/。
- 图片 / 视频 GPU 复测：透明图片 FP16 回读、截断量化和透明度预乘改用 AVX2 / SSE2（保留标量回退），池化输入张量并在原生使用结束后归还；RIFE 上下文复用 RunOptions，保留取消注册和 GPU 同步。
- RTX 5070 Ti / 640×360 透明图片请求 2 倍输出，多轮采样的四模型热身后 3 次中位耗时减少约 11%～30%，平均托管分配由约 3.55 MB 降至 11～12 KB；12 幅 2/4 倍图片与原版逐字节一致，四模型在途取消后重试通过，AVX2 / SSE2 / 标量各 15,450,672 项像素比较通过。
- 1080p 固定视频交替复测：2 倍超分约 115 输出 fps、4 倍插帧约 135 fps、组合约 92 fps，未证明本次视频有稳定提速；三模式全部输出帧像素和时间戳与基线一致。环境、命令、范围与证据见 docs/gpu-inference-validation-2026-10-23.md 和 .qa/gpu-inference-2026-10-23/。
- 验证：Release 构建 0 错误（3 个既有截图 API 警告），视频 GPU 适配器 170 项、CUDA 管线及软解回退各 16 项通过，含双 RIFE 取消后复用；CRLF 与差异静态检查通过。未测 NPU、其他 GPU 厂商、Linux 实机及安装包性能。

## 2026-10-26

- 修复 Windows 10 默认 Mica 导致窗口异常透明：Mica 仅在 Windows 11 及更新版本显示，Windows 10 将已有或默认 Mica 偏好映射为亚克力且不覆盖原偏好；设置页选项索引改为按可见模式映射，说明文字支持换行并同步中英文回退说明。
- 验证：`dotnet build FancyTool/FancyToolAva.csproj -c Debug` 0 错误；隔离应用数据启动 Windows 11 实机，确认设置页说明完整换行且默认仍选中 Mica，截图保存在 `.qa/settings-backdrop-2026-10-26/`。Windows 10 实机回退与隐藏 Mica 尚未验证。
- 修复 Visual Studio 文件夹发布配置：`FolderProfile.pubxml` 的无版本 `net10.0-windows` 会把 `TargetPlatformVersion` 推导为 7.0，覆盖 WindowsInference.props 的完整 TFM；现统一为 `net10.0-windows10.0.19041.0`，并移除主项目重复设置的 `SupportedOSPlatformVersion`，保留 10.0.18362.0 最低系统版本。该无凭据发布配置通过 .gitignore 例外纳入版本控制。
- 验证：MSBuild 属性求值确认目标/最低平台版本分别为 10.0.19041.0 / 10.0.18362.0；`dotnet publish FancyTool/FancyToolAva.csproj -c Release -p:PublishProfile=FolderProfile` 发布成功，仅既有 Satori 实验性与 Avalonia 裁剪/AOT 分析警告。

## 2026-10-31

- 商用许可审计收尾两项修复：其一，移除 CHTCHSConv 1.0.0（微软 VS International Pack 的 ChineseConverter.dll，经 Lyricify.Lyrics.Helper 引入）——其 EULA 禁止在非 Windows 平台分发且禁止组件受 GPL 类许可约束，与本仓库 GPL-3.0 冲突；`ChineseHelper.S2T/T2S` 改用文件内 ChineseConverter 字典做字符级转换（词级歧义仍由 ToSC force 分支修正表覆盖），External/NOTICE-Lyrics.txt 记录移除原因。
- 其二，补齐 FFmpeg GPLv3 第 6 条对应源码书面 offer：`Libraries/FFmpeg/LICENSE.txt` 头部新增中英文 offer（FFmpeg commit 5cb9fcd211 源码 + BtbN/FFmpeg-Builds autobuild-2026-10-16-13-04 构建脚本 + 失效时经仓库 issue 索取），GPL 全文保持不变；README 与 README.en 许可证段同步补对应源码指引。
- 验证：`dotnet build -c Debug` 0 警告 0 错误；`obj/project.assets.json` 无 chtchsconv，bin（Debug 与 msix）输出无 ChineseConverter.dll（旧残留已清理）；`dotnet run --project FancyTool/_tools/LyricsSmokeTest -- formats .qa/lyrics/formats` 全部 PASS（10 项，覆盖 ToSC 匹配路径、GBK 与日文分词）。AI 模型许可同日复核：RIFE 4.25 系经上游 README 确认 MIT、Qwen3-ASR/Aligner ONNX 为 Apache-2.0、RealESRGAN BSD-3、ClearRealityV1 Apache-2.0，商用收费均无限制。
- 修复商店包接受验证失败：`Package.appxmanifest` 与 `Package.StoreAssociation.xml` 的 PublisherDisplayName 由 `FancyTool` 改为账号发布者显示名 `Sennpai Studio`（Identity/Name 与 Publisher CN 未动），清单内加注释说明该值必须与 Partner Center 账号一致。
- 验证：`dotnet run --project FancyTool/_tools/LicenseSmokeTest -- .qa/msix-publisher-2026-10-31` 23 项全部 PASS（退出码 0，含包身份与商店许可门控全矩阵）；门控代码 `StoreAppIdentity` 仅校验 PackageName/PackageFamilyName，不受本次改动影响。
