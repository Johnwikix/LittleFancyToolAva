# 0016：MSIX 商店版 AI 超分与 RIFE 许可门控

日期：2026-10-23。

## 背景与约束

用户要求参照 `G:\SoftwareProject\winui\music_player` 的 MSIX 收费实现，将 AI 图片超分、AI 视频超分和 AI 补帧纳入收费范围，普通 Windows 构建与 Linux 不受影响。

参考的是该项目 `Services/LicenseService.cs`、`Services/LicensePolicy.cs`、`ViewModel/AppViewModel.License.cs` 和 `docs/LicensePolicy.md` 的现有应用许可策略；不复制其产品编号或音频功能清单。当前项目已有 `FancyToolMsixBuild=true` 的独立 MSIX 构建形态，仍使用自包含托管应用，不调整 SDK、Windows ML、模型和推理后端。

## 决策

### 渠道与许可状态

`FancyToolMsixBuild=true` 在主项目定义 `FANCYTOOL_MSIX`。真实 Store 后端仅在 `WINDOWS && FANCYTOOL_MSIX` 编译，其他形态的默认服务立即初始化为不受限，无 Store 查询、购买调用、定时器或初始化等待。

| 构建 / 运行渠道 | 行为 |
| --- | --- |
| 普通 Windows 构建 | AI 全部开放，隐藏授权提示 |
| Linux | AI 全部开放，编译中排除 Store 后端；即使误传 MSIX 标记也由 WINDOWS 条件隔离 |
| MSIX 构建，Store 签名 | 使用当前应用的 Store 应用许可证 |
| MSIX 构建，非 Store 签名或无可用商店身份 | 按参考项目旁路；开发签名和旁加载不作为正式收费验收 |

Store 状态也沿用参考规则：

| 状态 | AI | 购买入口 |
| --- | --- | --- |
| 尚未完成初查 | 等待，普通任务不等待 | 显示检查状态 |
| 已购正式许可 | 开放 | 隐藏购买按钮 |
| 有效试用 | 开放，显示剩余天数 | 显示 |
| Store 确认 `IsActive=false` | 限制收费清单 | 显示购买及刷新 |
| 首次查询失败 | 放行 | 按商店不可用处理 |
| 后续查询失败 / 断网 | 保留上次确认状态，已受限不会因此解锁 | 按保留状态显示 |

这保留了参考项目的首次失败放行策略，并非“任何未知状态都锁定”的严格离线授权方案。若产品以后要改为首次失败拒绝，应单独调整策略和相应用例。

### 收费范围

`LicenseFeature.AllAi` 是集中收费清单，包含：

- `ImageSuperResolution`：图片全部四款超分模型及有效 2× / 4× 输出。
- `VideoSuperResolution`：视频全部超分模型、精度与倍率。
- `AiFrameInterpolation`：RIFE 4.25 Lite / 4.25 和全部有效插帧倍率。

普通图片格式转换、缩放、视频转码、帧率转换、CPU 运动补偿、AMD / NVIDIA 无模型光流均不在该清单。歌词识别与音频处理未纳入本次用户指定范围。

### 唯一状态源与购买

- `ILicenseService` 统一提供状态、功能检查、购买、刷新和通知；App 的四个生产服务工厂显式注入同一实例，避免界面状态与实际执行使用不同许可。
- 主窗口 `Opened` 后取得 Avalonia 平台 HWND，先初始化 StoreContext 的窗口所有者，再查询许可。激活窗口、Store 离线许可事件与 30 分钟定时器触发刷新；试用期间一分钟时钟只刷新剩余天数，失效由 Store 查询确认。
- 状态与通知统一在 UI 线程维护，后台工作通过不可变快照读取；刷新单飞，查询中的新请求合并补查，避免购买与旧查询交错。
- 三页面共享单例 `LicenseViewModel` 的购买 / 刷新命令，购买期间不重复发起操作。购买成功、取消或失败后均重新查询，以查询结果决定解锁。
- StoreId 从当前应用查询；失败时使用当前包 PFN 打开商店页面，不硬编码参考项目产品 ID。没有持久化授权偏好，也没有正式包可用的 DEBUG 文件或环境变量逃生门。
- Dispose 停止计时器、解绑事件、释放初始化等待并忽略迟到结果。

### 界面与服务守卫

图片、视频和设置页使用现有 FluentAvalonia 组件显示授权说明、试用天数、购买和刷新入口。未受门控渠道不显示该区域。新增资源同步进入中英 JSON 和统一字符串表，许可与语言变化即时刷新。

保留已保存的 AI 选择，不因许可失效改写 ViewState。受限时可以取消旧超分勾选、改选非 AI 插帧，继续普通任务；开始和安装命令既检查 CanExecute，也检查实际执行入口。

服务层覆盖：

1. 图片推理、硬件组件安装和首次模型预热。初查 / 失效期间不预热，恢复许可后同一服务自动启动首次预热；同步模型哈希校验在线程池执行。
2. 视频超分与 RIFE 的模型下载、组件安装和会话创建。
3. 转码统一入口：当选项包含超分或 RIFE 时，在源文件探测、FFmpeg 加载、创建输出之前等待许可并检查。
4. 图片首次实际获得推理及模型存储锁后、视频等待独占会话后、读取下载结果并创建 / 复用缓存会话前再次检查，避免排队期间失效后继续启动。

已获得许可并开始的原生作业和下载可以完成；新任务重新检查。不在帧 / tile 热循环中访问 Store，也不突断 GPU 原生调用。许可恢复继续使用原有设置与缓存。

图片 / 视频 ViewModel 虽以 `AddTransient` 注册，但 `MainWindowViewModel.BuildImageCategory` / `BuildVideoCategory` 在启动时解析一次，并将对象保存在 `PageNavigationItem.Content`；后续往返导航复用同一实例。这两个 ViewModel 保留 `IDisposable`，不接入导航离开释放，保证返回原页面后仍可继续使用。许可、下载等事件由应用退出时 DI 容器的 `Dispose` 统一解绑，并向在途任务发出取消；CTS 由实际异步工作的局部 `using` 持有，在工作结束后释放，退出清理不在 UI 线程同步等待 GPU / 转码任务。

## 替代方案

- 只禁用界面：不能覆盖恢复队列、直接命令、缓存和服务调用，因此同时接入执行边界。
- 仅按运行时包身份门控：会改变普通构建的渠道行为，因此先使用构建符号隔离。
- 将所有补帧都收费：超出“AI 相关”的范围，无模型补帧继续开放。
- 首次查询失败即锁定，或增加新的内购 SKU：与参考策略不同，本次未采用。

## 后果、验证与限制

生产服务和界面共用同一状态源，普通渠道仍可调用全部现有 AI 功能。原始证据位于仓库根目录 `.qa/msix-ai-license-2026-10-23/`，复现命令及结果见 [验证报告](../msix-ai-license-validation-2026-10-23.md)。

这实现的是与参考项目相同的**应用收费 / 试用许可**，不代表新增了 AI 独立内购商品。商店后台仍需正确配置当前应用的收费与试用；若产品保持免费基础版并单独出售 AI，需要另行定义增购商品及对应 entitlement，不能把免费应用的正式许可证当作 AI 购买凭据。

真实 Store 交易必须在关联且由 Store 分发的安装包内验收。自动测试替换 Store 边界，不能代替真实账户、定价、试用发放和付款验证。Windows 对到期应用试用可能直接阻止启动，因此本次也不承诺“试用到期后商店版仍可启动为免费基础版”。此边界与参考项目相同。参见 Microsoft [应用内购买与试用说明](https://learn.microsoft.com/windows/uwp/monetize/in-app-purchases-and-trials)、[离线应用许可 API](https://learn.microsoft.com/uwp/api/windows.services.store.storecontext.getapplicenseasync) 和 [购买窗口要求](https://learn.microsoft.com/uwp/api/windows.services.store.storecontext.requestpurchaseasync)。
