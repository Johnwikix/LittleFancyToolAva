# 串口调试多端口 UI 重设计方案（v2）

- 日期：2026-11-26
- 状态：待评审（未实现）
- 修订：v1 采用"左侧会话轨"，v2 按评审意见改为**保留现有布局，串口 ComboBox 升级为会话选择器并加连接状态标识**
- 范围：`Views/SerialPortView.axaml`、`ViewModels/SerialPortViewModel.cs`、`FancyTool.Core/Services/SerialPortService.cs`、`Models/ViewStates/SerialPortViewState.cs`、`i18n/*.json`，以及 CLI `serial` 命令的兼容处理

## 1. 目标与硬约束

### 1.1 目标

在**不改变现有页面骨架**（左 320px 设置列 + 右"通信日志 / 发送"上下分栏 + 底部状态栏）的前提下支持同时调试多个串口：

1. 多个串口并发连接、并发收发，各自独立参数、独立日志缓冲；
2. 通过一个 ComboBox 完成"会话选择 + 端口选择"，每项带连接状态标识，下拉展开即可看到各路在线情况；
3. 保留现有能力：HEX 收发、定时发送、帧间隔、RTS/DTR、保存日志、连接时长、缓冲行数提示。

### 1.2 现状与硬约束

| 约束 | 位置 | 影响 |
| --- | --- | --- |
| 服务实例内只有一份 `SerialPort`、接收缓冲、帧定时器 | `SerialPortService.cs:16-24` | 必须先拆"每端口一个会话实例"，UI 才可能多路 |
| 服务与 ViewModel 均 `AddSingleton` | `App.axaml.cs:248,261` | 多实例化 VM 前必须先改注册方式 |
| 现 ComboBox 直接绑 `PortNames`（纯字符串） | `SerialPortView.axaml:43-45` | 需改为绑会话项模型，并加 ItemTemplate |
| 状态色/文案转换器已存在 | `Helpers/ConnectionStatusToBrushConverter.cs`、`ConnectionStatusToTextConverter.cs` | 状态标识直接复用，不新造配色 |
| 日志缓冲自带 16ms 定时器 | `LogBuffer.cs:25-30` | 多路需收敛为共享 ticker |
| ViewState 为扁平单端口 | `SerialPortViewState.cs` | 升级为会话数组 + 选中项 |
| CLI 复用 `ISerialPortService` | `CliContext.cs:187`、`NetworkCommands.cs:30,81` | 拆分后 `serial list/send/listen` 必须仍可用 |

### 1.3 设计原则

- **一个 ComboBox 承担两件事**：已添加会话的选择 + 未添加端口的添加入口。下拉即"会话清单 + 端口清单"，不额外造侧栏。
- **标识在控件上，不在别处**：连接状态点直接画在 ComboBox 的选中项和每个下拉项里，避免用户为了确认"哪一路还连着"去别的地方找。
- **并发不等于同屏**：一次只显示一路的日志（保持现有布局），并发能力由"多会话保持连接 + 下拉可见状态 + 合并日志（可选）"承担。

## 2. 方案修订与选型

| 方案 | 保持现有布局 | 同屏观察 | 状态可见性 | 改动面 | 结论 |
| --- | --- | --- | --- | --- | --- |
| v1：左侧会话轨 + 工作区多布局 | 否（左栏由设置改为会话列表） | 好 | 好 | 大：新增 rail/pane 控件、布局状态 | 撤回 |
| **v2：ComboBox 会话选择器 + 状态标识** | 是 | 单路（可选双分屏） | 好（下拉内每项带点 + 未读 + 收发量） | 小：只改 ComboBox 及其绑定、VM 拆会话 | **采用** |
| v2b：ComboBox + 顶部 Tab 条 | 部分 | 中 | 好 | 中 | 备选（若以后超过 8 路再考虑） |

选择 v2 的理由：串口调试的实际并发量多在 2–6 路，ComboBox 下拉一次能完整展示，超过时需要滚动但仍是线性成本；而侧栏常驻会永久占掉 168px 日志宽度，与"日志优先"矛盾。

## 3. 现有结构映射

| 现状控件 | v2 角色 | 变化 |
| --- | --- | --- |
| 端口设置卡 · 串口 ComboBox | **会话选择器**（已添加会话 + 未添加端口） | 绑 `Sessions`/`PortChoices`，加 ItemTemplate 状态点 |
| 端口设置卡 · 波特率/校验/数据/停止/编码 | 当前选中会话的参数 | 绑定改指向 `Session.*`（转发属性），语义不变 |
| 端口设置卡 · 刷新端口 / 连接 / 断开 | 全局刷新 + 当前会话连接控制 | 命令改由页面 VM 转发到当前会话；新增"关闭会话"小按钮 |
| 选项卡（HEX、RTS、DTR、帧间隔、轮询、发送总数） | 当前选中会话的选项 | 同上，按会话隔离 |
| 通信日志卡（清空 / 保存 / 缓冲行数） | 当前选中会话的日志 | 绑定 `Session.Log`；多会话各自独立 |
| 发送卡（输入框 / 发送） | 当前选中会话的发送区 | 增加 `[广播]` 勾选（多发） |
| 底部 `TerminalStatusBar` | 当前会话计数 + 全局聚合 | 右侧 `RightDetail` 显示"已连接 2 / 3 会话" |
| 头部 `StatusPill` | 当前会话状态 | 绑定 `Session.ConnectionStatus/StatusDetail` |

结论：**页面骨架零改动**，差异集中在 ComboBox 的项模板、若干绑定的层级（`Session.X`）和底部聚合文案。

## 4. 界面设计

### 4.1 主布局（沿用现状）

```
┌ 串口调试  串行端口数据收发与轮询                       ● 已连接 COM3 · 115200 8N1 ┐
├─ 320px ───────────────────────┬──────────────────────────────────────────────────┤
│ 端口设置                      │ 通信日志                        [清空] [保存]      │
│ 串口  [● COM3  115200 8N1  ▾]│ 09:12:03.214  RX  01 03 00 00 00 02 C4 0B        │
│       [关闭]                  │ 09:12:03.231  TX  01 03 00 00 00 02 C4 0B        │
│ 波特率      [115200      ▾]   │ 09:12:03.512  RX  01 03 04 00 64 00 C8 3A 5E     │
│ 校验位      [None        ▾]   │ 09:12:04.006  RX  01 03 04 00 6A 00 C9 7B 2F     │
│ 数据位      [8           ▾]   │ 09:12:04.214  SYS 帧间隔 20 ms                   │
│ 停止位      [1           ▾]   │                                    [有新数据 ↓]   │
│ 编码        [UTF8        ▾]   │                                                  │
│ [刷新端口] [连接] [断开]       │ 缓冲区 1,284 / 20,000                            │
│                               │ ┌ 发送 ──────────────────────────────────────────┐│
│ 选项                          │ │ [01 03 00 00 00 02 C4 0B________] [广播] [发送] ││
│ [ ] HEX 发送   [ ] HEX 显示   │ └────────────────────────────────────────────────┘│
│ [ ] RTS        [ ] DTR        │                                                  │
│ 帧间隔 (ms)  [20        ▲▼]   │ RX 12.4K   TX 320   已连接 · 00:12:31             │
│ 轮询间隔(ms) [1000      ▲▼]   │                         已连接 2 / 3 会话  [合并日志]│
│ [ ] 启用发送总数  总数 [1 ▲▼] │                                                  │
│ [定时发送] [停止]             │                                                  │
└───────────────────────────────┴──────────────────────────────────────────────────┘
```

保留要点：

- 左右宽度、卡片顺序、按钮语义与现状一致；`[关闭]` 是唯一新增控件（24px 高的小按钮，紧贴 ComboBox 右侧），用于移除当前会话。
- `[合并日志]` 放在日志卡标题栏右侧（`清空`、`保存` 之后），是可选视图，不改变默认布局。

### 4.2 ComboBox 规格（本次核心）

**折叠态（未展开）**：显示"状态点 + 端口名 + 参数摘要"，即用户不看下拉也能确认当前这路是否连着。

**展开态（下拉）**：分三段，顺序固定。

```
┌─────────────────────────────────┐
│ ●  COM3  115200 8N1   已连接    │  当前项（高亮）
│ ○  COM4   9600  8N1   已断开  ② │  ← ② 未读帧数
│ !  COM7   ——          端口占用  │  ← 错误态
│ ───────────────────────────────  │
│ ＋ 添加端口  COM5  9600 8N1      │  ← 扫描到但未添加，点击即添加
│ ＋ 添加端口  COM6  ——            │
└─────────────────────────────────┘
```

| 元素 | 内容 | 绑定/来源 |
| --- | --- | --- |
| 状态点 | 8px 圆点，颜色 = 绿/黄/红/灰 | `ConnectionStatus` + `ConnectionStatusToBrushConverter`（现有） |
| 端口名 | `COM3`，加粗 12px | `Session.PortName` |
| 别名 | 可选，10px 次文本（默认隐藏，重命名后显示） | `Session.Alias` |
| 参数摘要 | `115200 8N1` | 由索引派生 |
| 状态文本 | 已连接 / 连接中 / 已断开 / 端口占用 | `ConnectionStatusToTextConverter`（现有） |
| 未读徽标 | `②`，仅非当前会话、非零时显示 | `Session.UnreadCount` |
| 收发量 | 右对齐 10px：`RX 12.4K · TX 320` | `Session.RxCount/TxCount` 缩写 |
| 添加入口 | `＋ 添加端口 COM5 9600 8N1`，平淡色，仅在未添加端口存在时出现 | 页面 VM 的 `PortChoices` 派生集合 |

**Avalonia 落地细节**：

- `ComboBox.ItemsSource` 绑 `ObservableCollection<SerialChoiceItem>`，两类项用一个模型 + `IsAvailablePort` 标记区分，靠 `ItemTemplate` 内的样式触发器切换前缀（`●`/`＋`）。
- `ComboBox.ItemTemplate` 用 `DataTemplate x:DataType="vm:SerialChoiceItem"`；`SelectionBoxItemTemplate` 用更紧凑的同款模板（去掉未读徽标与收发量），保证 320px 列宽下不截断。
- 选中未添加端口项时（`IsAvailablePort == true`）：`SelectedItem` 的 setter 拦截 → 创建会话 → 立即改选该新会话（避免选中态停留在"添加项"上）。
- 状态点用 `Ellipse Width=8 Height=8 Fill={Binding ConnectionStatus, Converter={StaticResource ConnectionStatusBrush}}`；`CommonStyles.axaml` 里补一个 `ConnectionStatusBrush` 静态资源引用，避免页面重复声明。

### 4.3 参数区行为

- 参数控件绑定当前选中会话，切换会话即切换整组参数（不需要重新加载页面）。
- 连接态下修改参数不立即生效：字段保持可编辑，改动旁出现 11px 提示"参数将在下次连接时生效"（避免为防误改而禁用控件，导致用户必须先断开才能看参数）。
- `[连接]` 使用当前会话的 `CancellationTokenSource`，超时沿用 `AppPreferences.ConnectionTimeoutSec`；一路失败不影响其他路。

### 4.4 日志与发送（按会话隔离）

- 日志卡绑定当前会话的 `LogBuffer`；`清空/保存` 只作用于当前会话，保存默认文件名带端口：`COM3_20261126_0912.log`。
- 非当前会话仍持续接收并写入各自缓冲（上限降级，见 7.2），下拉项里以未读徽标体现"有数据"，切过去即看到完整内容。
- 发送区：默认发给当前会话；勾选 `[广播]` 时按会话顺序依次发给**全部已连接**会话，每路独立结果（成功计各自 TX，失败只写该路日志）。
- 定时发送：按会话独立任务与周期；广播模式下各会话各自起定时器（多路写入为毫秒级间隔，不能当作严格同步触发，设计说明中明确此限制）。

### 4.5 合并日志（可选视图）

`[合并日志]` 切换后，日志卡内容替换为跨端口时间线：每行前缀端口色标 + 端口名，顶部有端口筛选 chips。仅在激活时写入合并环形缓冲（容量 20,000 行），退出即停。这样"多路同时看"有一个低成本通道，而不必改布局。

### 4.6 双分屏（P4，可选，不进入首版）

若后续确实需要左右对照：工作区 RowDefinitions 的日志区横向再分两列，每列一个同款 `ComboBox`（`SelectionBoxItemTemplate` 复用），各自日志与发送区。此模式与 v2 的 ComboBox 语义不冲突，作为独立增量，不进 P0–P3。

## 5. 关键交互

### 5.1 新增会话

在下拉底部选 `＋ 添加端口 COM5 …` → 以默认参数（继承上一次连接的参数）创建会话并选中 → 状态为"已断开"，不自动连接（避免抢占端口或误发数据）。达到上限（8）时该段显示"已达上限，先关闭一个会话"并禁用。

### 5.2 切换会话

选项即切换：日志、参数、选项、计数、头部 StatusPill、发送区内容一起切换；未读清零。切换不打断其他会话的收发。

### 5.3 关闭会话

`[关闭]` → 若连接中先断开（一次确认，文案 `Serial.Msg_CloseConnectedSession`）→ 释放端口句柄 → 从集合移除 → 自动选中相邻会话。端口立刻可被外部程序打开。

### 5.4 连接 / 断开 / 异常

- 连接前检查同一 COM 是否已被本进程其他会话占用；占用则该端口从"添加端口"段移除，并提示"COMx 已被会话占用"。
- 连接失败把错误写入该会话日志 + 下拉项状态转红（`端口占用` / `连接失败`），仅当该会话是当前项时补一条 toast，避免 N 路同时失败时弹 N 个提示。
- 异常掉线（`ConnectionEventType.Lost`）：状态转红、日志 Error、底部聚合文案变"1 个会话异常"。

### 5.5 未读

非当前会话收到数据按帧累加未读（单帧 +1，上限 999，显示 `99+`），切换即清零；徽标只在会话项出现，不改页面其它区域。

## 6. 服务层拆分

```
ISerialPortSession（每端口一个实例）
  bool IsOpen; 事件同现状（BytesReceived/DataSent/StatusChanged/ConnectionStateChanged）
  OpenAsync / Close / SendAsync / SetFrameBreakInterval / SetHandshake(rts,dtr)

ISerialPortHub（Singleton）
  string[] GetAvailablePorts();
  bool IsPortInUse(string portName);
  ISerialPortSession CreateSession(string portName);
  void ReleaseSession(ISerialPortSession session);
  Task DisconnectAllAsync();
  void Dispose();
```

- `SerialPortService` → `SerialPortSession`（内部结构与事件语义保持，便于回归）；`RTS/DTR` 补为真实写 `SerialPort.RtsEnable/DtrEnable`（现状仅弹提示）。
- 端口枚举移到 `SerialPortCatalog.GetPortNames()`，会话与 Hub 共用。
- 占用登记 `ConcurrentDictionary<string, ISerialPortSession>`，`CreateSession` 原子判定，杜绝同口双开。
- CLI：`CliContext.cs:187` 改为 `AddTransient<ISerialPortSession>` + `AddSingleton<ISerialPortHub>`；`serial send/listen` 走"建临时会话 → Open → 收发 → Release"，命令名与参数不变。

## 7. ViewModel 与状态

### 7.1 ViewModel

`SerialSessionViewModel`（每会话一个）：

| 分组 | 成员 |
| --- | --- |
| 身份 | `Id`、`PortName`、`Alias`、`ColorIndex` |
| 参数 | `BaudRateIndex`、`ParityIndex`、`DataBitsIndex`、`StopBitsIndex`、`EncodingIndex`、`FrameBreakInterval`、`PollInterval`、`EnableSendCount`、`SendCount` |
| 选项 | `IsHexSend`、`IsHexDisplay`、`IsRtsEnabled`、`IsDtrEnabled` |
| 状态 | `IsConnected`、`ConnectionStatus`、`StatusDetail`、`ElapsedText`、`IsPolling` |
| 数据 | `Log`、`RxCount`、`TxCount`、`ErrorCount`、`UnreadCount`、`SendText` |
| 命令 | `Connect/Disconnect/Send/Clear/SaveData/StartPolling/StopPolling/Rename` |

`SerialChoiceItem`（ComboBox 项模型）：`SerialSessionViewModel? Session`、`string PortName`、`bool IsAvailablePort`、显示派生属性（`StateBrush`、`StateText`、`ParameterSummary`、`RxTxSummary`、`UnreadText`）。未添加端口项由页面 VM 从 `AvailablePorts - Sessions.PortName` 派生重建（刷新端口、增删会话后重算）。

`SerialPortViewModel`（页面，Singleton）：

| 成员 | 说明 |
| --- | --- |
| `ObservableCollection<SerialSessionViewModel> Sessions` | 会话集合（顺序即下拉顺序） |
| `ObservableCollection<SerialChoiceItem> PortChoices` | 下拉项（已添加会话 + 未添加端口） |
| `SerialChoiceItem? SelectedChoice` | 选中项；setter 处理"选到添加项 → 建会话 → 改选" |
| `SerialSessionViewModel? SelectedSession` | 当前会话；参数/日志/状态/命令对其转发 |
| 转发属性 | `SelectedPort`→`Session.PortName`、`BaudRateIndex`→`Session.BaudRateIndex` 等（保持 XAML 现有绑定路径可用） |
| 聚合 | `ConnectedCount`、`TotalSessions`、`FaultedCount`、`AggregateText` |
| 命令 | `RefreshPortsCommand`、`ConnectCommand`、`DisconnectCommand`、`CloseSessionCommand`、`BroadcastSendCommand`、`ToggleMergedLogCommand` |

页面 VM 另承担：统一 16ms 刷新调度（7.3）、未读累加、合并日志推送、选择变更时的绑定解绑。

### 7.2 ViewState v2

```csharp
public class SerialPortViewState
{
    public int SchemaVersion { get; set; } = 2;
    public List<SerialSessionViewState> Sessions { get; set; } = [];
    public string? SelectedSessionId { get; set; }
    public bool IsMergedLogVisible { get; set; }

    // 旧字段：读到即迁移为单会话，随后不再回写
    public string? SelectedPort { get; set; }
    public int? BaudRateIndex { get; set; }
    // ...
}

public class SerialSessionViewState
{
    public string Id { get; set; } = string.Empty;
    public string Alias { get; set; } = string.Empty;
    public string PortName { get; set; } = string.Empty;
    public int BaudRateIndex { get; set; }
    public int ParityIndex { get; set; }
    public int DataBitsIndex { get; set; } = 3;
    public int StopBitsIndex { get; set; }
    public int EncodingIndex { get; set; }
    public int FrameBreakInterval { get; set; } = 20;
    public bool IsHexSend { get; set; }
    public bool IsHexDisplay { get; set; }
    public bool IsRtsEnabled { get; set; }
    public bool IsDtrEnabled { get; set; }
    public int PollInterval { get; set; } = 1000;
    public bool EnableSendCount { get; set; }
    public int SendCount { get; set; } = 1;
    public string SendText { get; set; } = string.Empty;
}
```

- 恢复：会话列表、参数、别名、选中项、合并日志开关；**不自动连接**。
- 迁移：`Sessions` 为空且旧 `SelectedPort` 非空时，构造单会话（Id 取固定值或新 Guid），保证升级后参数不丢。
- `ViewStatesJsonContext` 增补 `[JsonSerializable(typeof(SerialSessionViewState))]`，保持 NativeAOT 无反射序列化。

## 8. 性能、线程与资源

- **调度收敛**：`LogBuffer` 增加共享 `LogBufferTicker`（单 `DispatcherTimer`，注册/注销）；默认构造保持自建定时器以兼容 TCP/UDP 页面。1s 时钟改为页面级单定时器统一更新 `ElapsedText`。
- **缓冲降级**：当前会话 20,000 行；非当前会话 5,000 行，切回时恢复。理由：现状 100,000 行 × N 路在 8 路时是 80 万条，且裁剪 `RemoveRange` 为 O(n)。
- **事件与 UI 线程**：事件线程只做 `Log.Enqueue` + `Interlocked` 计数；属性更新回 `Dispatcher.UIThread`。
- **定时发送收尾**：把现状 `task.Wait(200)`（`SerialPortViewModel.cs:390-392`）改为 `Cancel` + await 有界等待，避免 UI 线程阻塞。
- **释放**：页面 Singleton 不随导航释放，`OnNavigatedFrom` 只暂停刷新；退出时 `SerialPortHub.Dispose` 统一关闭，再保存 ViewState。
- **上限 8 路**：Windows 每路独占句柄与读线程，8 路可接受；超过时提示先关闭会话。

## 9. i18n 新增键（两侧同步）

| 键 | zh-CN | en-US |
| --- | --- | --- |
| `Serial.Label_Session` | 串口（会话） | Port (session) |
| `Serial.Button_CloseSession` | 关闭 | Close |
| `Serial.SessionState_Connected` | 已连接 | Connected |
| `Serial.SessionState_Connecting` | 连接中 | Connecting |
| `Serial.SessionState_Idle` | 已断开 | Disconnected |
| `Serial.SessionState_Error` | 端口占用 | Port in use |
| `Serial.SessionItem_RxTx` | RX {0} · TX {1} | RX {0} · TX {1} |
| `Serial.SessionItem_Unread` | {0} 未读 | {0} unread |
| `Serial.Menu_AddPort` | 添加端口 {0} | Add port {0} |
| `Serial.Menu_AddPortWithParams` | 添加端口 {0} ({1}) | Add port {0} ({1}) |
| `Serial.Msg_MaxSessions` | 最多 {0} 路，先关闭一个会话。 | Up to {0} sessions. Close one first. |
| `Serial.Msg_PortInUseBySession` | {0} 已被会话占用 | {0} is already in use by a session |
| `Serial.Msg_CloseConnectedSession` | 该会话仍在线，关闭将断开端口，是否继续？ | This session is still connected. Closing will disconnect it. Continue? |
| `Serial.Msg_ParamsApplyOnConnect` | 参数将在下次连接时生效 | Settings apply on next connect |
| `Serial.Label_Broadcast` | 广播 | Broadcast |
| `Serial.Label_Aggregate` | 已连接 {0} / {1} 会话 | {0} of {1} sessions connected |
| `Serial.Msg_SessionFaulted` | {0} 个会话异常 | {0} session(s) faulted |
| `Serial.Button_MergedLog` | 合并日志 | Merged log |
| `Serial.Group_MergedLog` | 合并日志（全部端口） | Merged log (all ports) |
| `Serial.Msg_NoAvailablePort` | 未发现新端口 | No new ports found |

## 10. 改动清单

| 文件 | 改动 |
| --- | --- |
| `FancyTool.Core/Services/ISerialPortService.cs` | 会话级接口，新增 `SetHandshake` |
| `FancyTool.Core/Services/SerialPortService.cs` | 重命名 `SerialPortSession.cs`，补 RTS/DTR 实际写入 |
| `FancyTool.Core/Services/SerialPortCatalog.cs` | 新增，端口枚举 |
| `FancyTool.Core/Services/SerialPortHub.cs` + 接口 | 新增，占用登记/编队/全部断开/释放 |
| `App.axaml.cs` | `ISerialPortService`→`AddTransient`；新增 `AddSingleton<ISerialPortHub, SerialPortHub>` |
| `FancyTool.Cli/CliContext.cs`、`Commands/NetworkCommands.cs` | 会话化改造，命令面不变 |
| `ViewModels/SerialSessionViewModel.cs` | 新增，承载原 VM 的会话级属性与命令 |
| `ViewModels/SerialChoiceItem.cs` | 新增，ComboBox 项模型（会话项 / 添加端口项） |
| `ViewModels/SerialPortViewModel.cs` | 页面级：Sessions、PortChoices、SelectedChoice、聚合、调度、合并日志 |
| `Models/ViewStates/SerialPortViewState.cs`、`ViewStatesRoot.cs` | v2 结构 + 旧字段迁移 + 源生成注册 |
| `Views/SerialPortView.axaml` | 仅改 ComboBox 绑定与模板、加"关闭"按钮、底部聚合文案；其余布局保留 |
| `Views/Themes/CommonStyles.axaml` | 补 `ConnectionStatusBrush` 资源、下拉项模板所需的小样式 |
| `Utils/LogBuffer.cs` | 共享 ticker + `MaxLines` 动态调整 |
| `i18n/zh-CN.json`、`i18n/en-US.json` | 新增 `Serial.*` 键（见 9） |
| `docs/changelogs.md` | 实现阶段追加记录 |

## 11. 分期实施

- **P0 服务层拆分（无 UI 变化）**：Session + Hub + Catalog + DI + CLI。验收：单端口行为等价；`serial list/send/listen` 通过；同口双开被拒；关闭后端口可立即被外部程序打开。
- **P1 VM 与状态**：`SerialSessionViewModel` + `SerialChoiceItem` + 页面 VM 转发 + ViewState v2 + 迁移 + i18n 键。验收：重启恢复会话与选中项、不自动连接；旧 `view-states.json` 平滑升级。
- **P2 ComboBox 改造（本次核心）**：项模板、折叠态、状态点、未读徽标、添加端口段、关闭按钮。验收：下拉可见全部会话状态；切换会话日志/参数/计数同步；未读计数正确；添加端口后可连接。
- **P3 广播与合并日志**：`[广播]` 发送、合并时间线、聚合文案（`已连接 2 / 3 会话`）、异常定位。
- **P4 可选**：发送历史与 `↑/↓` 调取、自动跟随与"新数据"浮标、双分屏、端口热插拔提示（5s 轮询差集）。

## 12. 风险与待确认

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| ComboBox 下拉项信息过多，320px 列宽拥挤 | 状态文本被截断 | 折叠态/展开态用两套模板；展开态把"收发量"降为 10px 次文本，可考虑只显 RX |
| 下拉即会话列表，关闭操作只靠一个小按钮 | 误关 | 关闭前确认（仅连接中）；若误关，重新添加成本低 |
| `LogBuffer` 共享 ticker 波及 TCP/UDP | 回归面扩大 | 默认构造不变；串口页显式注册；TCP/UDP 各做一次收发回归 |
| 8 路 × 大缓冲内存与裁剪开销 | 内存、滚动 | 当前/非当前分级上限 |
| Linux 枚举不含 `/dev/ttyUSB*` | 跨平台不一致 | 沿用现有枚举，Linux 另开任务处理 |
| 编译绑定/源生成遗漏 | 运行时绑定失败、AOT 报错 | 模板显式 `x:DataType`；新 ViewState 类型进 `JsonSerializerContext` |

待确认：

1. 会话上限仍按 8 路，还是先按 4 路更贴合实际（影响下拉长度与内存预算）？
2. 是否需要跨端口转发（A 收到自动转发到 B）——本设计未包含。
3. 关闭会话是否需要"移除确认"（默认只在连接中确认）？

## 13. 验收要点

- 下拉展开即可区分"已连接 / 已断开 / 端口占用"，且与头部 StatusPill、日志内容一致。
- 切换会话时参数、日志、计数、发送内容同时切换，不串线；未读徽标切换后清零。
- 添加端口后状态为"已断开"，点击连接才占端口；同一 COM 不能出现在两个会话中。
- 关闭会话后端口可被外部程序立即打开；进程退出无残留定时器/线程。
- 广播发送按已连接会话逐个下发，失败只影响该路。
- 中英文键齐全，运行时切换语言即时生效；旧 `view-states.json` 升级不丢参数。

## 14. 实现记录（2026-11-26）

已按本方案落地，实际与设计的差异如下：

| 项 | 实现情况 |
| --- | --- |
| 服务层 | `SerialPortSession`（原 `SerialPortService` 改名）+ `SerialPortHub` + `SerialPortCatalog`；GUI `AddTransient<ISerialPortService>` + `AddSingleton<ISerialPortHub>`；CLI 不改命令，只换注册 |
| ComboBox | 折叠态与下拉共用同一 `ItemTemplate`（未单独做 `SelectionBoxItemTemplate`）：状态点 + 端口名/别名 + 参数摘要 + 状态文字 + 未读徽标 |
| 添加端口 | 下拉末尾追加"添加端口 COMx"项，选中即建会话；上限 8 路，超限提示 |
| 会话关闭 | ComboBox 右侧"关闭"按钮；**无确认弹窗**（`INotificationService` 没有确认 API），直接断开并释放端口 |
| 广播 | 发送区"广播"勾选，页面 `SendCommand` 按已连接会话逐个下发 |
| 聚合 | `TerminalStatusBar` 新增 `MiddleDetail`，显示"已连接 N / M 会话" |
| 日志刷新 | `LogBuffer(autoFlush:false)` + 页面级单 16ms 定时器统一 `Flush`/计数，替换每会话定时器 |
| 合并日志 | **未实现**，日志卡标题栏未加"合并日志"按钮 |
| 双分屏 | 未实现（原 P4 可选） |
| 端口热插拔轮询 | 未实现，仍靠"刷新端口"按钮 |

验证：GUI 与 CLI 均 `dotnet build` 0 错误 0 警告；`fancy-tool serial list` 实跑通过；中英键 808/808 对称。待人工验证：GUI 交互、真实串口多路收发、TCP/UDP 日志刷新回归。