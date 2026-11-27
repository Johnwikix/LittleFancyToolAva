# TCP / UDP 调试多会话设计（v1.1）

- 日期：2026-11-26
- 状态：已评审，6 项待确认事项均有结论，见第 11 节。P0（公共基类抽取 + 6.6 的两处串口调整）已于 2026-11-27 实施，见第 13 节；P1–P4 未实现
- 修订：v1.1 按评审结论更新三处——TCP 定时发送遵守所选客户端（4.6）、"全部会话"文案同步到串口（6.6）、取消全部会话上限（4.1、6.6）
- 前置：串口多端口已落地，见 `serial-multi-port-ui-design.md`（第 14 节为实现记录）。本文沿用其结论，只展开 TCP / UDP 与串口的差异；会话选择器、未读徽标、合并日志、ViewState v2 迁移、日志分级上限等通用机制不再重复论证。
- 范围：`FancyTool.Core/Services/` 下的 TCP / UDP 服务与新增的 Hub、端点规则；`ViewModels/TcpServerViewModel.cs`、`ViewModels/UdpViewModel.cs` 及对应 View、ViewState、`App.axaml.cs`、`i18n/*.json`。若采纳 6.1 的甲方案，还包含串口 VM / XAML 的基类迁移。不改：CLI 命令、链路测试协议与服务、`LogBuffer`、`TerminalStatusBar`、页面导航与生命周期（三个页面仍是单例）。

## 1. 目标与核心差异

### 1.1 目标

与串口一致：多路并发、每路独立的参数与日志、页面一次只显示一路、下拉里能看到全部会话的状态。TCP / UDP 下有三类典型用法，都是现在单会话做不到的：

1. 自环调试：同一页面里一个服务端会话加若干客户端会话互相收发，不必再开第二个程序实例或用 CLI 充当对端。
2. 多设备：UDP 绑定多个本地端口分别对应不同设备，或单播与组播各占一个本地端口同时观察。
3. 并发观察：多个客户端会话同时连到同一个服务端。

### 1.2 与串口的关键差异

| 维度 | 串口 | TCP / UDP |
| --- | --- | --- |
| 会话身份的来源 | 从系统枚举出的端口清单里选（`COM3`） | 文本框输入。TCP 为模式 + 地址 + 端口；UDP 为模式 + 本地地址/端口 + 远程或组播地址/端口 |
| "可添加项" | 枚举结果减去已添加端口，列在下拉末尾 | 没有可枚举的集合，下拉无法预置"可添加项" |
| 身份是否可变 | 创建后不变 | 随文本框输入实时变化，显示名跟着变 |
| 独占性 | 一个 COM 口同时只能被一个会话打开 | TCP：服务端监听端点互斥，客户端会话不互斥。UDP：`ReuseAddress` 让同端口双绑定成功，但之后的投递行为未定义 |
| 接入动作 | 连接 / 断开 | 启动 / 停止 |
| 页面上的其它功能 | 无 | 链路测试。独立功能页签，目前与调试共用地址/端口输入 |
| 服务端下挂对象 | 无 | TCP 服务端会话有"已连接客户端"列表、按所选客户端发送、踢出 |

结论：页面骨架和会话模型照搬串口，需要单独设计的是三件事——**新建入口**（没有端口清单可列）、**会话身份与冲突**（身份是文本）、**链路测试的归属**（页面级，不能进会话）。

## 2. 选型

### 2.1 新建会话入口

| 方案 | 说明 | 结论 |
| --- | --- | --- |
| A. 下拉末尾固定一项"＋ 新建会话" | 与串口的"＋ 添加端口"是同一个交互：选中即按默认值创建会话并切换过去 | **采用** |
| B. 连接卡上放一个"＋"按钮 | 更显眼，但下拉只管切换、添加要另找按钮，与串口是两套操作心智 | 备选 |
| C. 可编辑 ComboBox，输入 `host:port` 回车新建 | 需要解析输入（IPv6 字面量、主机名），"模式"也没有来源 | 不采用 |

新会话的默认值取"复制当前会话，端口 +1"：

- TCP：模式、地址取当前会话，端口取当前端口 + 1（非数字或已是 65535 时原样）。
- UDP：模式、本地地址、组播/远程地址与端口取当前会话，本地端口 + 1。
- 没有存档时的首个会话与现状默认值相同：TCP 服务端 `127.0.0.1:8080`；UDP 普通 `0.0.0.0:8080`，远程 `127.0.0.1:9090`。

这样新会话立即可启动，显示名不会与来源会话重名，用户通常只需再改一个字段。用户之后可以把端点改成任何值（包括与别的空闲会话相同），冲突只在启动时判定（见 4.4）。

### 2.2 链路测试保持页面级

`TcpLinkTestService` / `UdpLinkTestService` 是进程内单例，一次只能跑一个测试，"每会话一份链路测试"没有意义。因此 `FunctionIndex`、角色、时长、块大小 / 包大小 / 限速、`LinkTest` 呈现状态都不动。

需要处理的是端点输入。现状是链路测试与通信调试共用同一组地址/端口（TCP 共用 `Address`/`Port`；UDP 接收端用 `LocalAddress`/`LocalPort`，发送端用 `RemoteAddress`/`RemotePort`），所以进入测试页签时要先把通信停掉（`TcpServerViewModel.cs:720`、`UdpViewModel.cs:689`）。地址/端口变成会话属性之后：

| 方案 | 结论 |
| --- | --- |
| (a) 链路测试使用"当前会话"的端点 | 否。隐式耦合：切换会话会改变测试目标，而测试页签上又看不到会话选择器 |
| (b) 链路测试使用自己的页面级端点输入 | **采用**。新增 `LinkAddress`/`LinkPort`（UDP 为 `LinkLocalAddress`/`LinkLocalPort`、`LinkRemoteAddress`/`LinkRemotePort`）；迁移时用旧的 `Address`/`Port` 初始化，现有单会话用户第一次打开时行为不变 |

互斥规则随之调整：

- 切到链路测试页签**不再停止任何会话**。现状会停，多会话下"只是看一眼"就停掉几路不可接受。
- 点击"开始测试"时**停止全部运行中的会话**（现状是在进入页签时停）。保留"调试与测试互斥"的现有语义，理由有两条：同网卡上的调试流量会干扰带宽测量；不必再做"链路测试端点与各会话端点"的冲突判断。
- 切回通信调试页签时停止在途测试（现状不变）。

## 3. 界面设计

### 3.1 通信调试页签

布局零改动：仍是 320px 左栏加右侧日志/发送加底部状态栏。

```
┌ TCP 调试 ……………………………………………………… ● 已连接  服务端 0.0.0.0:9000 ┐
├─ 320px ───────────────────────┬────────────────────────────────────────┤
│ 连接                            │ 通信日志       [合并日志] [清空] [保存]  │
│ 会话 [● 服务端 0.0.0.0:9000  已连接 ▾]│ …                                  │
│ ○服务端模式   ●客户端模式        │                                        │
│ 地址 [127.0.0.1]   端口 [9001]  │ 发送                                   │
│ [启动] [停止] [关闭会话]          │ [___________________] [发送] ☐ 全部会话│
│ 选项（HEX、帧间隔、轮询…）        │ RX 12.4K   TX 320   已运行 00:12:31     │
│                                │                运行中 2 / 3 会话        │
└────────────────────────────────┴────────────────────────────────────────┘
```

下拉展开：

```
┌───────────────────────────────────────┐
│ ●  客户端 127.0.0.1:9001    已连接    │  当前项
│ ●  服务端 0.0.0.0:9000     已连接  ② │  ② 为未读徽标
│ ○  服务端 0.0.0.0:9002     未连接     │
│ ＋  新建会话                          │
└───────────────────────────────────────┘
```

- 会话行的标签为"会话"。下拉项与收起框沿用串口的模板（状态点、名称、状态文字、未读徽标）。
- 会话显示名随输入实时更新。TCP 复用现有键 `Tcp.Status_Server` / `Tcp.Status_Client`（"服务端 {0}:{1}"、"客户端 {0}:{1}"）；UDP 为 `{LocalAddress}:{LocalPort}`，摘要行为 `→ {RemoteAddress}:{RemotePort}` 或"组播 {MulticastAddress}:{MulticastPort}"。
- 端点文本框用 `UpdateSourceTrigger=PropertyChanged`。这样显示名随输入更新，也避免"文本还没提交就切换会话"时把内容写进新会话。
- 合并日志的行前缀是 `[显示名]`，与串口一致。

### 3.2 各区域绑定

| 区域 | TCP | UDP |
| --- | --- | --- |
| 会话行 | `Choices` / `SelectedChoice` | 同左 |
| 模式单选 | `CurrentSession.ModeIndex` | 同左 |
| 端点输入 | `CurrentSession.Address` / `Port` | `LocalAddress`/`LocalPort`、`RemoteAddress`/`RemotePort`、`MulticastAddress`/`MulticastPort` |
| 启动 / 停止 | `CurrentSession.StartCommand` / `StopCommand` | 同左 |
| 关闭会话 | 页面 `CloseSessionCommand` | 同左 |
| 选项卡 | HEX 收发、帧间隔、轮询间隔、发送总数 | HEX 收发、轮询间隔、发送总数 |
| 已连接客户端卡 | 可见性绑 `CurrentSession.IsListening`；列表、选中项、踢出按钮都指向当前会话 | 无此卡 |
| 日志卡 | `CurrentSession.Log` 或 `MergedLog` | 同左 |
| 发送卡 | `CurrentSession.SendText`、页面 `SendCommand`、"全部会话" | 同左 |
| 头部胶囊、底部状态栏 | `CurrentSession.*` 加页面 `AggregateText` | 同左 |

命名调整（只影响 XAML 引用）：会话的 `IsRunning` 改名为 `IsListening`（仅服务端监听时为真），新增 `IsActive`（监听中或已连接）；踢出所选远端客户端的命令 `DisconnectClientCommand` 改名 `KickClientCommand`，避免与会话自身的断开混淆。

### 3.3 链路测试页签

端点输入改绑页面级的 `LinkAddress` 等，其余（角色、时长、统计、结果）不变。该页签下连接卡整体隐藏，因此看不到会话选择器，现状如此。

## 4. 关键交互

### 4.1 新建会话

在下拉末尾选"＋ 新建会话"：按 2.1 的默认值创建、自动选中、状态为未连接，不自动启动。**不设会话数上限**，该项始终存在。串口现有的 8 路上限一并取消，见 6.6。

### 4.2 切换会话

与串口相同：参数、日志、计数、发送内容、已连接客户端列表整体切换，未读清零，其它会话的收发不受影响。**切换不停止任何会话的定时发送或手动发送。**定时发送是会话级的：每一路各自一个任务和取消令牌，由该会话自己执行，与当前显示哪一路无关；离开页面再回来同样继续，因为页面是单例（见第 7 节）。"定时发送"和"停止"两个按钮反映的是当前会话的状态。选择变更就是切换，所以用键盘在下拉里逐项移动同样会切换，移到末尾的"新建会话"项即创建。这两点来自串口现有的选择机制，键盘点选的细节尚未在运行时实测。

### 4.3 运行中冻结"绑定身份"

| | TCP | UDP |
| --- | --- | --- |
| 运行中禁用 | 模式、地址、端口 | 模式、本地地址、本地端口、组播地址 |
| 运行中可改 | HEX、帧间隔、轮询、发送内容 | 远程地址/端口、组播端口、HEX、轮询、发送内容 |

- 理由：会话的显示名就是这些文本。若允许运行中修改，下拉和头部胶囊会显示一个与实际 socket 不一致的端点。串口不存在这个问题，因为端口名创建后就不会变。
- UDP 的远程地址/端口和组播端口只是发送目标，现状每次发送都实时读取（`UdpViewModel.cs:516-519`），保持可改；组播地址决定加入哪个组，属于身份，冻结。
- 被禁用的控件加 ToolTip 说明"运行中不可修改，停止会话后可编辑"。
- 否决的替代方案"保持可编辑，提示下次启动生效"：显示名会先于真实状态变化，同样误导。
- 连带清理：运行中不能再切模式之后，`OnModeIndexChanged` 里"切模式即停止"的分支（`TcpServerViewModel.cs:739-753`、`UdpViewModel.cs:497-504`）不可达，删除。

### 4.4 端点冲突

| 启动的会话 | 与谁冲突 | 判定 |
| --- | --- | --- |
| TCP 服务端 | 其它服务端模式、运行中或连接中的会话 | 端口相同，且地址相同或任一方是通配地址（`0.0.0.0`、`::`） |
| TCP 客户端 | 无。多个客户端会话可以同时连同一个服务端 | — |
| UDP 普通模式 | 其它运行中或启动中的会话，不论其模式 | 同上 |
| UDP 组播模式 | 其它运行中或启动中的**非组播**会话 | 同上 |

- 地址解析与服务层一致：TCP 解析失败回退 `Loopback`（`TcpServerService.cs:71`），UDP 回退 `IPAddress.Any`（`UdpService.cs:37`）。这段解析和重叠判定抽成 Core 里的 `BindEndpoint`，两个服务改用它，VM 也用它，避免"判定的地址"和"实际绑定的地址"不是同一个。
- UDP 必须在应用层拦截的原因：`UdpService.StartAsync` 给 socket 设了 `ReuseAddress=true`（`UdpService.cs:43`），两个会话绑定同一端口会成功，不会报错。微软文档对这种情形的描述是"the behavior for all sockets bound to that port is indeterminate"，唯一例外是绑定在同一接口和端口、属于同一组播组的 socket 各收一份。因此普通会话同端口要拒绝；组播会话之间同端口是合法用法，放行。`ReuseAddress` 本身保留，组播接收和同端口快速重启需要它。这条结论来自 Windows 文档，Linux 行为未验证。
- TCP 的最终仲裁仍是系统 bind：按 Windows 默认套接字行为，第二个监听绑定同一端口会被拒绝。预检查只负责给出可读提示。这一点放在 P1 烟测里实测。
- 命中冲突时：会话日志写 Error、状态置 Error；只有它是当前会话才弹提示，与串口相同。
- 不需要另做占用登记表：`Start` 命令在 UI 线程上、第一个 `await` 之前就把状态置为 `Connecting`，而该状态只在绑定成功或失败的回调里离开，这些回调都回到 UI 线程。因此检查与占用之间没有竞态窗口。

### 4.5 关闭会话

与串口相同：先停止、释放、从集合移除、选中相邻会话；只剩一个会话时不可关闭；没有确认弹窗，因为 `INotificationService` 没有确认 API。自环场景下关闭服务端会话，对端的客户端会话会收到 `Lost`，属预期。

### 4.6 发送

- 默认发给当前会话；勾选"全部会话"时按顺序发给所有运行中的会话，逐路独立结果，失败只写该路日志。
- 勾选框文案用"全部会话"，串口现有的"广播"同步修改，见 6.6。UDP 页上有真正的广播地址，"广播"容易被理解成向 `255.255.255.255` 发送。
- TCP 服务端会话内部，手动发送与定时发送共用一条目标规则：选中了某个客户端就只发给它，没选中才发给全部已连接客户端。手动发送现状如此（`TcpServerViewModel.cs:564-572`）。定时发送现状固定传 `null`（`TcpServerViewModel.cs:625`），等于无视所选客户端，改掉。
- 定时发送的目标在点击"定时发送"时确定，之后更换列表选择不影响进行中的定时发送，要换目标就停止后重新开始。不采用"每次发送实时解析"：目标客户端断开后会从列表里消失，选中项不再可靠。若被清空，实时解析会让"只发给它"变成"发给其余全部客户端"，数据发到别的设备上；若残留失效值，则什么都不发且没有提示。UDP 的定时发送本来就是在开始时确定目标（`UdpViewModel.cs:562-565`）。
- 目标客户端在定时发送期间断开或被踢出：定时发送停止，并在该会话日志里写一条提示。会话 VM 已经在 UI 线程上处理带 `Endpoint` 的客户端断开事件（`TcpServerService.cs:503-504`），与开始时记下的目标比对后取消任务。前提是踢出也发这个事件，见 6.4。
- 没选客户端、且服务端还没有任何客户端连入时，定时发送保持等待，客户端连入后开始发送。这是现状行为，保留。

### 4.7 链路测试

见 2.2。

## 5. 服务层

`TcpServerService` 和 `UdpService` 现在已经是"一个实例一份 socket 状态"（`TcpServerService.cs:16-27`、`UdpService.cs:12-16`），接口不用改。串口当时要先把单例服务拆开，这一步 TCP / UDP 不需要。实现上有三处改动：两个服务改用 `BindEndpoint` 解析绑定地址，行为不变；TCP 踢出客户端的事件类型修正，见 6.4。

| 项 | 改动 |
| --- | --- |
| `ISessionHub<T>` / `SessionHub<T>`（Core，新增） | `CreateSession()`、`ReleaseSession(T)`、`Dispose()`。泛型一份实现，TCP / UDP 各注册一个单例。串口的 `SerialPortHub` 带端口枚举，本次不动 |
| DI（`App.axaml.cs:250-251`） | 移除 `ITcpServerService` / `IUdpService` 的单例注册，改由两个 Hub 创建会话 |
| 创建方式 | Hub 用工厂委托创建实例，例如 `ActivatorUtilities.CreateInstance<TcpServerService>(sp)`，不经根容器解析服务接口。否则每创建一个会话，根容器都会把它登记为待释放对象并一直持有到进程退出，关闭会话后对象无法回收 |
| 退出 | `ServiceProvider.Dispose` 触发 Hub.Dispose，逐个 `StopServer()` / `Stop()`，单项失败不影响其它 |
| CLI | `NetworkCommands.cs:139,193` 仍然直接解析服务接口，每条命令用完即止，命令名和参数不变，`CliContext.cs:189-190` 不用改 |

## 6. ViewModel 与状态

### 6.1 是否抽公共基类

串口落地后，三个页面需要的东西大量重合。以定时发送循环为例，今天已经有三份逐行相同的实现（`SerialSessionViewModel.cs:690-736`、`TcpServerViewModel.cs:613-659`、`UdpViewModel.cs:569-615`），每份近 50 行；计数合并、未读、计时、HEX 日志格式化同理。

| 方案 | 说明 | 结论 |
| --- | --- | --- |
| 甲. 抽公共基类，串口一起改为派生 | 新增会话级基类和页面级基类 | **采用**。会改动刚上线的串口代码，单独作为一期并做回归 |
| 乙. 基类只给 TCP / UDP 用，串口以后再迁 | 不碰串口 | 过渡期两套实现并存 |
| 丙. 不抽，TCP / UDP 各写一份完整的 | 对串口零风险 | 会话管理和定时发送这类逻辑在三页各一份，以后修一处要改三处 |

评审结论：采用甲。先做抽取（P0），串口回归通过后再叠加 TCP / UDP。P0 里除了 6.6 的两处行为调整，其余都是不带行为变化的搬移。

### 6.2 公共部分

`TerminalSessionViewModelBase`（会话级）承载与协议无关的部分：

| 分组 | 成员 |
| --- | --- |
| 身份 | `Id`、抽象 `DisplayName` |
| 日志 | `Log`（不自带定时器）、`LogSink`（供页面汇聚合并日志） |
| 计数 | `RxCount`/`TxCount`、待上屏计数、`FlushCounters()`、`OnBytesReceived`/`OnDataSent` 的 HEX 与文本格式化 |
| 未读与计时 | `UnreadCount`、`MarkRead()`、`SetActive()`、`ElapsedText`、`UpdateElapsed()` |
| 状态 | `ConnectionStatus`、`StateText`、`StatusText`、`StatusDetail`、抽象 `IsActive`、`Faulted` 事件 |
| 发送选项 | `SendText`、`IsHexSend`、`IsHexDisplay`、轮询间隔、发送总数、`StartPolling()`/`StopPolling()`。定时发送是会话级的，每一路各自一个任务和取消令牌；基类只提供循环代码，手动发送由抽象 `SendManualAsync()` 提供，定时发送每一拍发什么由抽象 `CreatePollingSender()` 提供，TCP 在这里取目标快照 |

`TerminalPageViewModelBase<TSession>`（页面级）承载：会话集合、`CurrentSession`、下拉项与选中项（含串口里"选到新建项就创建并改选"和"集合变更时回推选中项"的两段处理）、"全部会话"发送（属性 `SendToAll`，串口现有的 `IsBroadcast` 改名）、关闭会话并选邻、合并日志、聚合文案。

`SessionChoiceItem` 取代 `SerialChoiceItem`：`Session` 改为基类型，"可添加端口"改为一个固定的"新建会话"项。选择器的两个 `DataTemplate` 从 `SerialPortView.axaml:46-107` 提到 `CommonStyles.axaml`，三个页面共用。

`TerminalTicker`：一个 16ms 加一个 1s 的 `DispatcherTimer`，三个页面订阅。现状三页合计常驻 4 个 `DispatcherTimer`：串口页面自建 2 个（`SerialPortViewModel.cs:49-55`），TCP / UDP 各自的 `LogBuffer` 各带 1 个，而三个页面 VM 都在工作区初始化时构造，与是否打开过该页面无关（`MainWindowViewModel.cs:52`）。如果 TCP / UDP 照抄串口的做法会变成 6 个，共享后是 2 个。

语言切换的刷新也收进基类：订阅 `CultureChanged`，通知会话重发 `StateText`、`DisplayName` 等通知。串口页面目前没有订阅，切换语言后下拉里的状态文字要等到该会话下次属性变化才会刷新。

### 6.3 协议专有部分

| 类 | 专有内容 |
| --- | --- |
| `TcpSessionViewModel` | 模式、地址、端口、帧间隔开关与间隔（直接下发到服务）、`IsListening`/`IsConnected`/`IsActive`、`ConnectedClients`/`SelectedClient`、`Start`/`Stop`/`Send`/`KickClient`、服务事件的分支处理（与现状 `OnConnectionStateChanged` 相同，`Disconnected` 与 `ClientDisconnected` 的区分见 6.4） |
| `UdpSessionViewModel` | 模式、六个地址端口字段、`IsRunning`、`Start`/`Stop`/`Send`、发送目标计算 |
| `TcpServerViewModel` / `UdpViewModel` | 基类成员之外只保留：`FunctionIndex`、链路测试参数与命令、冲突判定（遍历会话调用 `BindEndpoint`）、创建会话 |

冲突判定以回调形式注入会话，同串口的 `PortConflictCheck`，会话在启动前调用。

### 6.4 迁移时不再带走的现状问题

| 问题 | 位置 | 处理 |
| --- | --- | --- |
| `Modes` 集合没有任何绑定，视图用的是单选按钮加 `lan:I18n` | `TcpServerViewModel.cs:33,251-252,260-261` | 删除 |
| `_cts` 字段从未赋值 | `TcpServerViewModel.cs:24,494-496,522`；`UdpViewModel.cs:23,465-467,491` | 删除 |
| 在 UI 线程上 `task.Wait(200)` 等待定时发送任务 | `TcpServerViewModel.cs:460-461`、`UdpViewModel.cs:429-430` | 与串口一致，只 `Cancel`，不阻塞 |
| UDP 的 `FrameBreakInterval` 被持久化，但服务端是空实现、界面也不展示 | `UdpService.cs:204-206`、`UdpViewState.cs:15` | 字段保留以兼容旧存档，不进会话 VM 的界面 |
| 踢出单个客户端时服务层发的是 `Disconnected`，页面在服务端模式下把它当成"服务端已停止"：`IsRunning` 置为 false，状态胶囊变为未连接，计时停止，"已连接客户端"卡隐藏，而监听其实还在继续 | 事件 `TcpServerService.cs:259-260`；处理 `TcpServerViewModel.cs:357-362`，经 `IsRunning = false` 进入 `:454-463` | 踢出改发带 `Endpoint` 的 `ClientDisconnected`，与自然断开同一事件；`Disconnected` 只表示本会话的链路停止 |
| TCP 定时发送固定发给全部客户端，无视所选客户端 | `TcpServerViewModel.cs:625` | 见 4.6 |

以上均为静态阅读的调用链结论，未在运行时复现。

### 6.5 ViewState v2

```csharp
public class TcpServerViewState
{
    public int SchemaVersion { get; set; } = 2;
    public List<TcpSessionViewState> Sessions { get; set; } = [];
    public string? SelectedSessionId { get; set; }

    // 页面级
    public int FunctionIndex { get; set; }
    public string LinkAddress { get; set; } = "127.0.0.1";
    public string LinkPort { get; set; } = "8080";
    public int LinkRoleIndex { get; set; }
    public int LinkDurationSec { get; set; } = 10;
    public int LinkChunkKB { get; set; } = 64;

    // ---- v1 兼容字段（仅迁移读取，不再写回）----
    public int ModeIndex { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    // …其余旧字段原样保留
}

public class TcpSessionViewState
{
    public string Id { get; set; } = string.Empty;
    public int ModeIndex { get; set; }
    public string Address { get; set; } = "127.0.0.1";
    public string Port { get; set; } = "8080";
    public string SendText { get; set; } = string.Empty;
    public bool IsHexSend { get; set; }
    public bool IsHexDisplay { get; set; }
    public bool EnableFrameBreak { get; set; }
    public int FrameBreakInterval { get; set; } = 20;
    public int PollInterval { get; set; } = 1000;
    public bool EnableSendCount { get; set; }
    public int SendCount { get; set; } = 1;
}
```

UDP 同构。页面级字段：`FunctionIndex`、`LinkLocalAddress`/`LinkLocalPort`、`LinkRemoteAddress`/`LinkRemotePort`、`LinkRoleIndex`、`LinkDurationSec`、`LinkPacketSize`、`LinkRateMbps`。会话级字段：`ModeIndex`、六个地址端口、`SendText`、`IsHexSend`/`IsHexDisplay`、`FrameBreakInterval`（仅兼容）、`PollInterval`、`EnableSendCount`、`SendCount`。

- 恢复会话列表、参数和选中项，不自动启动。
- 迁移：`Sessions` 为空就视为 v1，用旧的扁平字段生成一个会话，并把旧的 `Address`/`Port`（UDP 为 `LocalAddress`/`LocalPort` 与 `RemoteAddress`/`RemotePort`）写入链路测试端点。
- 判定依据用"`Sessions` 为空"而不是 `SchemaVersion`：页面始终至少保留一个会话，而旧文件缺这个字段时读到的也是默认值 2，区分不出来。
- `ViewStatesJsonContext` 增补两个会话状态类型。
- 不持久化"全部会话"勾选和合并日志开关，与串口一致。

### 6.6 串口侧随评审结论调整的两处

1. 取消会话上限。删除 `MaxSessions`、`CanAddSession`、超限提示和恢复存档时的截断（`SerialPortViewModel.cs:20,149,213-217,252,343,412`），不再因达到上限而拒绝创建会话。
2. "广播"改为"全部会话"。`SerialPortView.axaml:238` 的勾选框改用公共键 `Session.Label_SendAllSessions`，行为不变，仍是按顺序发给全部已连接的会话。

这两处是有意的行为变化，与 P0 的纯搬移分开验收。

## 7. 性能、线程与资源

- 日志上限改为与串口相同的分级：当前会话 20000 行，其余会话各 5000 行，合并日志固定 20000 行。TCP / UDP 现状是 100000 行（`LogBuffer.cs:21`），切换后"缓冲区 N / M"的分母会变小。需要保留 100000 的话只需改基类里的常量。取消会话上限后，日志总占用随会话数线性增长，每一路自身有界；没有做内存测量。
- 下拉项同步现状是 O(N²)：`SerialPortViewModel.cs:306` 对每个会话在下拉项里线性查找。它只在增删会话和刷新端口时执行，不在节拍热路径上。取消上限后会话数可能到几十上百，基类实现时改为字典查找。节拍处理本身是 O(N)，每一路只做空检查。
- 事件回调只做 `Log.Enqueue` 和 `Interlocked` 计数，属性更新回 UI 线程，现状不变。
- `StatusChanged` 改为合并上屏。现状是每条状态一次 `Dispatcher.Post`（`TcpServerViewModel.cs:338-344`、`UdpViewModel.cs:335-338`），而服务层在开启帧间隔时每帧触发一次（`TcpServerService.cs:598`），N 路高速流就是 N 倍的 UI 投递。改为只记录最新一条，由 16ms 节拍统一上屏。状态文案本来就是后一条覆盖前一条，语义不变。
- 关闭会话时先 `Shutdown()` 解绑服务事件，再 `ReleaseSession`。接收循环 `finally` 里迟到的 `ConnectedClients.Remove` 由服务自己的 dispatcher 完成，不会再访问已移除的会话 VM。
- 页面是单例，导航离开后会话继续运行，未读徽标持续累计；只有在页面上关闭会话才真正释放端口。

## 8. i18n 新增与删除的键（两侧同步）

| 键 | zh-CN | en-US |
| --- | --- | --- |
| `Session.Label_SendAllSessions` | 全部会话 | All sessions |
| `Tcp.Label_Session` | 会话 | Session |
| `Tcp.Menu_AddSession` | 新建会话 | New session |
| `Tcp.Button_CloseSession` | 关闭会话 | Close session |
| `Tcp.Label_Aggregate` | 运行中 {0} / {1} 会话 | {0} of {1} sessions active |
| `Tcp.Msg_EndpointInUse` | {0} 已被另一个会话占用，请先停止该会话。 | {0} is in use by another session. Stop it first. |
| `Tcp.Msg_PollTargetGone` | 定时发送的目标客户端 {0} 已断开，定时发送已停止。 | Timed send stopped: target client {0} has disconnected. |
| `Tcp.Button_MergedLog` | 合并日志 | Merged log |
| `Tcp.ToolTip_LockedWhileRunning` | 运行中不可修改，停止会话后可编辑。 | Locked while running. Stop the session to edit. |
| `Tcp.Msg_LinkTestStoppedSessions` | 已停止 {0} 个运行中的会话以开始链路测试。 | Stopped {0} running session(s) to start the link test. |

- `Session.*` 是新的公共分区，三页共用，串口的"广播"勾选改用它。
- UDP 在 `Udp.*` 下新增同名八键，不含 `Msg_PollTargetGone`，另加 `Udp.Label_MulticastSummary`（"组播 {0}:{1}" / "Multicast {0}:{1}"）。
- 删除 `Serial.Label_Broadcast`、`Serial.Msg_MaxSessions`。取消上限后不再需要上限提示键。
- 链路测试的端点标签复用现有的 `Tcp.Label_Address` / `Tcp.Label_Port` 等键。

## 9. 改动清单

| 文件 | 改动 | 分期 |
| --- | --- | --- |
| `ViewModels/TerminalSessionViewModelBase.cs`（新增） | 会话级公共部分 | P0 |
| `ViewModels/TerminalPageViewModelBase.cs`（新增） | 页面级公共部分 | P0 |
| `ViewModels/SessionChoiceItem.cs`（新增） | 取代 `SerialChoiceItem` | P0 |
| `ViewModels/TerminalTicker.cs`（新增） | 三页共用的 16ms / 1s 节拍 | P0 |
| `ViewModels/SerialSessionViewModel.cs`、`SerialPortViewModel.cs`、`SerialChoiceItem.cs` | 前两者改为派生，`SerialChoiceItem.cs` 删除；`SerialPortViewModel.cs` 取消会话上限 | P0 |
| `Views/SerialPortView.axaml`、`Views/Themes/CommonStyles.axaml` | 选择器两个 `DataTemplate` 移入公共样式；"广播"改为"全部会话" | P0 |
| `i18n/*.json`（串口部分） | 新增 `Session.Label_SendAllSessions`；删除 `Serial.Label_Broadcast`、`Serial.Msg_MaxSessions` | P0 |
| `FancyTool.Core/Services/ISessionHub.cs`、`SessionHub.cs`（新增） | 泛型会话 Hub | P1 |
| `FancyTool.Core/Services/BindEndpoint.cs`（新增） | 绑定地址解析与端点重叠判定 | P1 |
| `TcpServerService.cs`、`UdpService.cs` | 绑定地址解析改用 `BindEndpoint`，行为不变；踢出客户端改发 `ClientDisconnected` | P1 |
| `App.axaml.cs` | 注册两个 Hub，移除两个服务的单例注册 | P1 |
| `_tools/NetworkSessionSmokeTest/`（新增） | 服务层与规则烟测 | P1 |
| `ViewModels/TcpSessionViewModel.cs`（新增）、`TcpServerViewModel.cs` | 会话拆分，页面 VM 只留链路测试与页面级成员 | P2 |
| `Models/ViewStates/TcpServerViewState.cs`、`ViewStatesRoot.cs` | v2、迁移、源生成注册 | P2 |
| `Views/TcpServerView.axaml`、`i18n/*.json`（TCP 部分） | 会话行、绑定改指向会话、链路测试端点、新键 | P2 |
| UDP 对应四项 | 同 TCP | P3 |
| `docs/changelogs.md` | 各期追加 | 各期 |

## 10. 分期

- **P0 公共基类抽取（只有串口，布局不变）**。动手前先用当前实现跑一遍 GUI 留基线，串口实现时尚未做 GUI 实测（见 `changelogs.md` 2026-11-26 条目）。P0 里只有 6.6 的两处是有意的行为变化，其余是不带行为变化的搬移。验收：同一组 GUI 步骤前后结果一致，差异只允许出现在这两处；`fancy-tool serial list / send / listen` 通过。
- **P1 服务层（界面不变）**。Hub、`BindEndpoint`、DI、烟测，以及踢出客户端的事件类型修正。验收：烟测场景全过；CLI `tcp server / tcp client / udp bind / udp send` 行为不变；TCP 踢出一个客户端后，服务端仍为运行中，其余客户端仍在列表里。
- **P2 TCP 多会话**。会话 VM、页面 VM、ViewState v2、View、i18n。
- **P3 UDP 多会话**。同 P2。
- **P4 可选**。日志标注来源端点：TCP 目前不区分是哪个客户端发来的，UDP 丢弃了 `result.RemoteEndPoint`（`UdpService.cs:217-232`）。全部启动 / 停止。一键新建连到当前服务端的客户端会话。

实施后补一条 ADR（0024），把串口与 TCP / UDP 多会话的行为决策一并记录。

P1 烟测走真实的服务与 Hub，建议覆盖：两个服务端会话同端口，规则拒绝第二个，绕过规则直接启动时系统 bind 失败；同一 Hub 内服务端会话与客户端会话环回互发；两个 UDP 普通会话同端口，规则拒绝，并验证绕过规则时确实能绑定成功；两个组播会话同端口，规则放行且都能启动；`BindEndpoint` 的通配、相同地址、不同具体地址三类组合；踢出一个客户端，收到的是带 `Endpoint` 的 `ClientDisconnected`；`ReleaseSession` 后端口立即可被外部使用，`Dispose` 幂等。页面 VM 依赖 `DispatcherTimer`，无法在控制台工程里构造，冲突判定和定时发送的 GUI 部分用 lvt 驱动实测。

## 11. 风险与评审结论

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| P0 改动的是刚上线且未做 GUI 实测的串口代码 | 回归难以归因 | P0 独立成期并先留基线；基线对比不通过就停在 P0，不叠加后续改动 |
| 泛型页面基类遇到 MVVM 源生成或编译绑定的问题 | `[RelayCommand]`、`x:DataType` 解析失败 | P0 第一步先做最小验证，不通则改为非泛型基类 |
| 取消会话上限 | 会话多时下拉定位靠滚动；日志总占用随会话数线性增长 | 每一路日志有界（非当前会话 5000 行）；不做人为限制，定位困难时再考虑别名或搜索 |
| 两个空闲会话端点相同 | 下拉项显示名相同，难以区分 | 新建默认端口 +1；启动时的冲突检查给出提示；串口同样没有重命名入口 |
| 长地址在 320px 列内被截断 | 看不全端点 | 名称列 `CharacterEllipsis`，ToolTip 显示完整名称 |
| 组播会话之间同端口被放行 | 两路加入同一组会各收一份；单播数据报到达该端口时归属不确定 | 属文档明确的例外与既有限制，在提示文案里说明 |
| 键盘在下拉里移到"新建会话"项即创建 | 多出一个空闲会话 | 与串口"添加端口"项是同一机制；会话可随时关闭 |
| 链路测试开始时停止全部会话 | 用户要逐个重启 | 与现状的互斥语义一致，且只在点击"开始测试"时触发 |
| 日志上限由 100000 降为 20000 | 回看范围变小 | 与串口一致；基类常量可调 |
| 切换到别的会话后，看不出原来的会话是否还在定时发送 | 下拉项目前只有状态点和未读徽标 | 建议在共享的下拉项模板里加一个"定时发送中"的小标识。尚未确认，未列入分期 |

评审结论（2026-11-26）：

| 事项 | 结论 |
| --- | --- |
| 1. 是否先抽公共基类并改串口 | 同意，按甲方案，P0 单独成期 |
| 2. 链路测试使用独立端点，开始测试时才停止全部会话 | 同意 |
| 3. 运行中冻结绑定身份，替代"切换即停止" | 同意 |
| 4. TCP 定时发送的目标 | 不能忽略所选客户端。与手动发送共用目标规则，开始时确定目标，目标断开即停止，见 4.6 |
| 5. 发送勾选的文案 | "全部会话"，串口同步修改 |
| 6. 会话上限 | 全部取消，包括串口现有的 8 路上限 |

## 12. 验收要点

- 新建会话后显示名与端点输入一致；启动、停止只影响当前会话。
- 连续新建超过 8 个会话，不出现任何限制提示，重启后全部恢复；串口页同样。
- 同一页面内服务端会话与客户端会话环回互发，日志和计数互不串线。
- 两个服务端会话监听同一端口，第二个被拒并给出可读提示；不同具体地址的同端口可以同时监听。
- 两个 UDP 普通会话绑定同一本地端口，第二个被拒；组播会话之间同端口可以同时启动。
- 运行中的会话不能修改模式与绑定端点，停止后可以；UDP 运行中可以修改远程地址/端口并即时生效。
- TCP 服务端选中某个客户端后启动定时发送，只有该客户端收到数据；它断开或被踢出后定时发送停止并有日志，其余客户端没有收到过数据。
- 踢出一个客户端后，服务端仍显示为运行中，其余客户端仍在列表里。
- 切换会话时参数、日志、计数、发送内容、客户端列表整体切换，未读徽标切换后清零。
- 会话 A 定时发送期间切到会话 B，A 继续发送，从 B 的日志或计数能看出来；切回 A 时仍在定时发送。离开页面再回来同样继续。
- 重启后恢复会话列表与选中项，不自动启动；旧版 `view-states.json` 平滑迁移，链路测试端点不丢。
- 进入链路测试页签不停会话；开始测试停止全部会话；切回调试页签停止在途测试。
- 关闭会话后端口可以被外部程序立即使用；退出后没有残留的定时器、线程和未观察的任务。
- 中英文键齐全，运行时切换语言后下拉里的状态文字即时更新；串口页发送区的勾选框显示"全部会话"。

## 13. P0 实现记录（2026-11-27）

P0 已按第 9、10 节的清单落地，串口页布局不变。实际与设计的差异、验证结论如下；逐项细节与证据位置见 `changelogs.md` 2026-11-27 条目。

| 项 | 实现情况 |
| --- | --- |
| `TerminalSessionViewModelBase` | 按 6.2 落地。收发入口命名为 `OnReceived` / `OnSent`，解码由抽象 `DecodeBytes` 提供；`CreatePollingSender()` 在开始定时发送时于 UI 线程取发送目标快照 |
| `TerminalPageViewModelBase<TSession>` | 按 6.2 落地。泛型基类上的 `[RelayCommand]` 与编译绑定可用，第 11 节第二条风险没有出现；下拉项同步为字典查找（第 7 节） |
| `SessionChoiceItem` | 取代 `SerialChoiceItem`。"新建"项带可选载荷：串口仍是一个端口一个"添加端口 COMx"项，TCP / UDP 用固定的"新建会话"项 |
| `TerminalTicker` | 单例，16ms / 1s 两个节拍；串口页原来的两个 `DispatcherTimer` 迁入 |
| 6.6 两处调整 | 均已实施：串口不再有会话上限；勾选框显示"全部会话"（`Session.Label_SendAllSessions`） |
| `StatusChanged` 合并上屏（第 7 节） | **未在 P0 做**。基类保留 `PostStatusText`（每条状态一次 `Dispatcher.Post`，与串口现状一致），留到 P2 / P3 随 TCP / UDP 迁移一起改 |
| TCP / UDP 的日志与定时器（第 7 节） | 未动。两页仍各自使用自带定时器的 `LogBuffer` 与 100000 行上限，P2 / P3 迁移时切换 |

随搬移产生的四处非搬移变化，设计里没有逐条列出，记在此处：

1. 语言切换刷新按 6.2 收进基类（`CultureChanged` 后重发聚合文案、下拉项、会话状态文字的通知）。旧串口页面没有这一段。
2. TX 文本解码与 RX 共用带回退的 `DecodeBytes`；旧实现 TX 直接 `GetString`，没有异常回退。
3. 定时发送任务不再把取消令牌传给 `Task.Run`，并删除从未被读取的 `_pollTask` 字段。
4. 删除没有绑定的 `RxTxSummary` / `ShortCount`（会话）和 `Summary` / `RxTxSummary`（下拉项）。

**同日按用户要求修复的四处既有问题**（细节见 `changelogs.md`）：

| 问题 | 修法 |
| --- | --- |
| 合并日志保存提示显示裸键（原前缀 `SerialMerged` 没有 i18n 分区） | 新增公共分区 `MergedLog`，前缀改为页面基类里的常量，不再由各页面提供 |
| 恢复存档后总是选中第一路（存档的 `SelectedSessionId` 对不上新建会话的 `Id`） | 会话基类新增 `RestoreId`，`ApplyState` 用存档里的 `Id` 覆盖。**P2 / P3 的会话 VM 迁移时，`ApplyState` 里同样要调用它** |
| 会话出错回调可能在非 UI 线程发布通知 | 页面基类不在 UI 线程时先切回，再刷新聚合状态和弹提示 |
| `ParameterSummary` 没有绑定 | 删除 |

验证环境与逐项结果见 `changelogs.md` 2026-11-27 条目，原始快照在 `.qa/tcp-udp-multi-session-2026-11-26/`（不入库）。要点：GUI 与 CLI 构建均 0 警告 0 错误；同一组 9 个 GUI 状态在改动前后逐元素比对，差异只有"广播"→"全部会话"；同一份 10 路存档旧构建恢复 8 路、新构建恢复 10 路；COM1↔COM2 实测 A 定时发送期间切到 B，A 继续发送；英文启动无裸键；`fancy-tool serial list / send / listen` 通过。

未执行：运行时切换语言后的界面刷新（第 12 节最后一条。后台最小化窗口下 UIA 驱动不了侧栏项，进不了设置页）；前台鼠标键盘点击；定时发送在途时关闭窗口；多路会话的内存占用。P1–P4 未开始。第 10 节末尾的 ADR 0024 待 P1–P3 落地后一并补。
