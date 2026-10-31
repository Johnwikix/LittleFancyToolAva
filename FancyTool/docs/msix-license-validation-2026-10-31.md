# MSIX 全功能授权验证

> 后续实测（同日）：已定位本机 I: 盘 VS Community，完成真正 DesktopBridge Release 打包并验证真实已购许可。初轮遗漏了已发布商品身份比对，导致错误包名；现已修复。见 [已购许可验证](license-owned-validation-2026-10-31.md)。下文保留初轮验证边界，不作为真实授权验收结论。

日期：2026-10-31。策略见 [ADR 0016](adr/0016-msix-application-license.md)。

## 环境与命令

本机 Windows x64、.NET SDK 10.0.402、Avalonia 12.1.1、FluentAvalonia 3.0.2，沿用 global.json 和 Satori SDK。机器 / OS / 运行时信息也记录在各 `results.json` 中。本次不做 GPU 性能或分配收益结论。

从仓库根目录执行：

```powershell
dotnet run --project FancyTool/_tools/LicenseSmokeTest -c Debug -- .qa/msix-license-2026-10-31/non-msix
dotnet run --project FancyTool/_tools/LicenseSmokeTest -c Release -p:FancyToolMsixBuild=true -- .qa/msix-license-2026-10-31/msix --expect-msix
dotnet build FancyTool/FancyToolAva.csproj -c Release -p:FancyToolMsixBuild=true
dotnet build FancyTool/FancyToolAva.csproj -c Release -r linux-x64 -p:SelfContained=true -p:PublishAot=false
python .qa/msix-license-2026-10-31/verify-static-and-ui.py
```

普通 Windows 与实际 MSIX Release 配置各执行 20 项回归，各通过 20/20。Linux 交叉编译通过，仍为 `net10.0`；MSIX 为 `net10.0-windows10.0.19041.0`，两者编译后端不同。第三方 Lyricify 源码的既有分析告警仍存在。本机没有 Visual Studio DesktopBridge，未生成或安装新的 MSIX。

## 已覆盖边界

- 检查实际主程序集后端类型与默认构造，普通渠道立即全解锁；MSIX 缺少包身份不放行。
- 可控 Store 边界覆盖非活跃、有效正式许可、有效 / 到期 / 缺失到期时间试用、首次失败、后续失败、初始化失败恢复、超时及迟到结果、监控失败、Dispose 和后台事件 UI 调度。
- 到期无需 Store 刷新；同一试用在进程中回拨 UTC 并重复刷新不能延长单调时钟额度。
- 覆盖刷新单飞补查、并发购买、购买取消 / 失败、购买后重新验证和购买与旧查询竞态；超时底层调用未完成前不叠加查询。
- 测试宿主读取真实 `App.ConfigureServices` 的依赖图，仅替换 Store / 时钟与模型下载 HTTP 边界，确认初查前不构造工具，通过后完整构造工具，到期先禁用工作区并请求结束旧会话。FFmpeg 探测走真实代码，未启动转码。
- 独立子进程调用生产 `LicenseSessionRestarter`，确认隐藏 / 保存抛异常仍能结束旧进程并启动新进程，旧后台写入停止、TCP 监听及独占文件句柄可重新获取。探针环境变量仅由 `_tools` 宿主读取，主程序没有覆盖许可的入口。

这些回归调用生产实现，不在测试中复制授权规则；Store 数据仍是边界替身，不能据此宣称真实试用发放或交易已验收。

## 真实 Avalonia 窗口

使用 MSIX Release 输出的实际 `FancyToolAva.exe`，不安装包，分别以 `FANCYTOOL_DATA_DIRECTORY` 隔离启动中文浅色、英文深色、中文跟随系统三组数据。lvt 使用测试窗口 HWND 读取 UIA 并通过 InvokePattern 操作。

已确认三组窗口均显示本地化购买提示及授权锁定页，树中无工具导航；中文浅色窗口关闭购买提示、点击「重新验证许可」后仍处于锁定，程序未启动模型下载。三个窗口正常关闭后，预先放入的页面状态标记保持不变。

窗口保持隐藏，没有切换用户前台；lvt 的 WGC 截图对隐藏窗口返回 `0x80070057`，未得到可检查图像。因此仅确认 UIA 文本、控件与交互状态，未确认像素布局、主题对比度或焦点视觉。未触发真实购买。

## 原始证据与剩余范围

仓库根目录 [.qa/msix-license-2026-10-31/](../../.qa/msix-license-2026-10-31/) 中保留：

- `non-msix/results.json`、`msix/results.json`：最终每渠道 20 项结果与运行环境。
- `smoke-non-msix.log`、`smoke-msix.log`、`build-msix.log`、`build-linux.log`：回归 / 编译日志。环境或构建占用导致的早期失败另保留日志，不作为通过证据。
- `process-*` 子目录：旧 / 新进程 PID、监听端口及后台写入探针。
- `zh-light-tree.json`、`en-dark-tree.json`、`zh-system-tree.json`、`zh-light-closed-tree.json`、`zh-light-refreshed-tree.json`：真实窗口树；各 GUI 数据目录保存原始日志与状态文件。
- `static-and-ui.json`：双语言键、CRLF 和 UIA 状态 / 状态文件检查结果；`ui-detect.json` 为界面机械扫描结果。

仍需发布前在真正 Store 分发、与本商品关联的签名安装包内验收：有效试用、试用运行中到期、过期冷启动、正式购买与取消、离线有效许可和 Store 故障、重新进入锁定页、原生转码 / GPU / 串口实际工作中断后的输出处理。Linux 实机运行、NativeAOT 完整发布和 DesktopBridge 打包不在本次已执行范围。
