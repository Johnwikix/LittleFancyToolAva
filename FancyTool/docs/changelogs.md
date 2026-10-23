# 变更记录

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
