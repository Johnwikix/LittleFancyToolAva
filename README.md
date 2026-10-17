[**English**](README.en.md) | **中文**

<div align="center">
  <img src="FancyTool/Assets/storeIcon.ico" alt="Logo" width="96">


  <h1>妙妙工具</h1>

  <h3>FancyTool</h3>

  <h4>
    基于 Avalonia 12 + FluentAvalonia 构建的轻量级桌面工具集<br>
    面向 Windows 与 Linux 平台，集成常用加密、通讯与图片处理工具
  </h4>
  <div>
    <a href="https://github.com/Johnwikix/LittleFancyToolAva/releases/latest"><img src="https://img.shields.io/github/downloads/Johnwikix/LittleFancyToolAva/total?label=%E4%B8%8B%E8%BD%BD%E9%87%8F" alt="下载量"></a>
    <img src="https://img.shields.io/badge/语言-C%23-purple" alt="C#">
    <img src="https://img.shields.io/badge/UI-Avalonia%2012-blue" alt="Avalonia">
    <img src="https://img.shields.io/badge/主题-FluentAvalonia-blue" alt="FluentAvalonia">
    <img src="https://img.shields.io/badge/.NET-10.0-purple" alt=".NET 10">
    <img src="https://img.shields.io/badge/平台-Windows%20%7C%20Linux-blue" alt="Platform">
    <img src="https://img.shields.io/badge/许可证-GPL--3.0-blue" alt="License">
  </div>

  <div>
    <a href="https://github.com/Johnwikix/LittleFancyToolAva"><img src="https://img.shields.io/badge/GitHub-Johnwikix%2FLittleFancyToolAva-181717?logo=github&logoColor=white" alt="GitHub Repo"></a>
    <img src="https://img.shields.io/github/stars/Johnwikix/LittleFancyToolAva?style=social" alt="Stars">
    <img src="https://img.shields.io/github/forks/Johnwikix/LittleFancyToolAva?style=social" alt="Forks">
    <img src="https://img.shields.io/github/watchers/Johnwikix/LittleFancyToolAva?style=social" alt="Watchers">
  </div>


  <br>

</div>

<br>

## 📥 下载与安装

<div align="center">

| Microsoft Store（推荐） |
| :---: |
| <a href="https://apps.microsoft.com/detail/9P543GHQQKVK?referrer=appbadge&mode=direct"><img src="https://get.microsoft.com/images/en-us%20dark.svg" width="200"/></a><br>通过 Microsoft Store 获取最佳安装与更新体验 |

</div>

<br>

## 🌟 核心功能

### 🛰️ 通信调试

- 🛰️ **串口调试**：基于 `System.IO.Ports` 的串口收发与调试
- 🌐 **TCP 服务器**：TCP 服务端调试工具，支持连接管理
- 📡 **UDP 通信**：UDP 收发调试，支持十六进制 / ASCII 切换

### 🔐 加解密与编码

- 🔒 **对称加密**：DES、AES、SM4（国密）字符串加解密
- 🔑 **非对称加密**：RSA、SM2（国密）密钥对生成与加解密
- #️⃣ **哈希计算**：MD5、SHA 系列、SM3（国密）摘要
- 🅱️ **Base64 编解码**：文本 Base64 编码 / 解码

### 🗂️ 文件与图片工具

- 📁 **文件夹比较**：对比两个目录的内容与文件差异（按相对路径、SHA-256 哈希、音乐标题匹配）
- 🔐 **文件加解密**：批量的文件级加密 / 解密操作，支持进度跟踪
- 🖼️ **图片转 Base64**：将图片转换为 Base64 编码字符串
- 🖼️ **图片转换**：图片格式批量转换（jpg / png / bmp / webp / tiff / dds / jxl / heic / ico，基于 SkiaSharp）

### 视频转码

“滤镜”页支持 RIFE AI 插帧和无需模型的 CPU 运动补偿插帧，可选择 2×、4× 或 6× 源帧率，保持原时长和音画同步，并默认启用场景切换保护。AI 插帧提供 RIFE 4.25 Lite（默认）与 4.25，沿用 Windows ML 的 GPU/NPU 加速和 DirectML GPU 回退；模型按需下载至统一模型目录的 `Interpolation` 子目录，支持校验和目录迁移。开启插帧后，GIF 同样使用所选倍率；HDR 输入须显式选择“强制 SDR”后插帧。与超分同时启用时自动选择处理顺序，也可手动选择先插帧或先超分；未开启超分的降分辨率输出会先缩小画面，以降低插帧计算量。NPU 首次编译大画面可能较慢，速度优先推荐 GPU。

“画质”页集中显示 AI 视频超分和 HDR 输出选项。HDR 输出可选“与源一致”“强制 HDR”“强制 SDR”：强制 HDR 将 SDR 经 BT.2446 逆映射转换为 1000 尼特目标的 10 位 PQ / BT.2020；强制 SDR 对 HDR 做亮度映射并输出 BT.709。与源一致保留 SDR、PQ 或 HLG 类型，GIF 始终输出 SDR。不支持 10 位 HDR 的编码器或档次会提示调整设置。

视频超分提供 Anime4K CNN S/M、FSRCNNX s/m 和 RAVU Lite r2/r3 六档，模型随应用分发，可离线安装与校验，存放在模型管理目录的 `VideoSr` 子目录。仅使用 GPU，按模型自动选择已验证的后端，没有 CPU 回退。Anime4K / FSRCNNX 可选 FP16，使用厂商 GPU 加速（Intel 为 OpenVINO）；RAVU r2 使用 DirectML FP32，r3 使用厂商 GPU FP32。缺少兼容组件时可点击安装 GPU 加速。批量视频独占租用并复用已编译会话。2×运行一次，4×运行两次，耗时和内存更高。旧视频模型选择会迁移到 Anime4K S，图片超分仍保留原有模型。模型来源及许可见 [模型说明](FancyTool/Libraries/VideoSr/NOTICE.md)。

超分和插帧使用有界流水线，让解码、推理、编码与封装处理不同的帧。Windows 的 NVIDIA 编码任务优先使用 NVDEC；标准 8 位 4:2:0 SDR 输入可经 CUDA 像素转换、超分、RIFE 直接交给 NVENC，整帧保留在显存。场景检测仅回读标志，预览先在 GPU 缩小；CPU 负责音频、调度和封装。特殊滤镜、色彩格式及 DirectML 等路径保留混合处理，硬件解码协商失败会完整重试软件解码。GPU 色彩转换与原 zscale 路径存在舍入和采样差异，不能宣称逐像素相同。设计及验证见 [显存管线设计](FancyTool/docs/adr/0011-gpu-resident-video-frames.md) 和 [RTX 5070 Ti 实测](FancyTool/docs/video-cuda-pipeline-2026-10-17.md)。

现有超分模型处理 SDR 图像；HDR 素材启用超分时先映射为 SDR，再按输出选择转回 HDR。逆映射可扩展高光，但不能保证恢复 SDR 中已经丢失的细节。算法与模型目录说明见 [设计记录](FancyTool/docs/adr/0005-model-storage-and-hdr-output.md)。

### 歌词与字幕提取

“音视频处理 → 歌词与字幕提取”支持文件夹递归扫描、拖放、批量队列、暂停、取消、失败重试及队列恢复。使用 Qwen3-ASR 1.7B 和独立的 Qwen3-ForcedAligner 0.6B 时间对齐器，复用已有 ONNX Runtime 与进程内 FFmpeg，无需 Python。可识别中文、英语、日语、韩语、法语、西班牙语、俄语、德语、葡萄牙语等语言；逐词对齐覆盖上述九种语言以及意大利语、粤语。其他识别语言保留分块时间，逐词格式会提示需要对齐。

默认 INT4，识别与对齐都优先使用 Windows DirectML GPU 加速。所有档位默认开启“优先使用 GPU 加速”，GPU 加载或推理失败时自动改用 CPU 重试相应步骤，结果会显示实际后端、回退提示和底层诊断原因；也可关闭该选项强制使用 CPU。DirectML 对 INT4 解码器的 MatMulNBits 可能只做部分卸载，想让解码器完整使用 GPU 请选高精度 FP32。非 Windows 平台使用 CPU。自动语言会先参考音频标签和文件名中的文字脚本；没有脚本提示时，复用同一个 Qwen3-ASR 会话，对中文、英语、日语、韩语、法语、西班牙语、俄语、德语、葡萄牙语做强制语言评分和文字特征投票，最多采样开头与结尾两个 8 秒窗口，锁定歌曲主语言，避免日文歌曲被翻译为 English，同时保留实际英语片段；混合语言仍建议复核。节省资源档减少线程与分块长度，高精度档使用同一识别模型的 FP32 权重。当前 INT4 导出的识别编码器仍为 FP32，模型合计下载约 5 GB；此前 CPU 测试的进程峰值约 5.3 GB，建议至少 12–16 GB 系统内存。GPU 另需显存或共享内存，具体用量随驱动和分块长度变化。FP32 额外下载约 10 GB，建议至少 24 GB 内存。若某个 GPU 或驱动的识别结果异常，可关闭 GPU 选项后重新识别。

导出 LRC、ELRC、KRC、QRC、YRC、SRT、VTT、ASS、TXT 与 JSON，并保留 `.fancylyrics.json` 校正项目。所有生成格式均为可直接打开的 UTF-8 文本：KRC 保留逐词时间标签，不加入厂商压缩/加密外层；QRC 输出明文 XML。优先读取有效的内嵌同步歌词与同名歌词文件；未同步标签回退到识别。输出目录留空时，歌词/字幕写在各自源音频旁边。支持预览、文本校正、重新对齐和整体时间偏移。歌曲长音、伴奏与合唱仍可能造成识别或对齐偏差，零时长词会显示复核提示。

默认模型保存在模型管理目录的 `Qwen3Lyrics` 子目录。设置页的“模型管理”可选择图像、视频超分与默认歌词模型的存储根目录，重启后生效，并提供打开目录和恢复默认；已有模型不自动搬迁，可手动复制到新目录继续使用。歌词高级设置中的独立目录仍然保留。下载采用固定版本及 SHA-256 校验，下载完成后可离线处理。新提取结果遇到同名文件自动加编号，不覆盖原歌词；校正后再次导出更新对应结果。恢复队列时，进行中的文件重新排队，完成的结果可继续预览；当前文件从头重试。

歌词库使用本地音乐播放器中的 System.Text.Json 修改版，保留源生成序列化。许可证和分词来源见 `FancyTool/External/NOTICE-Lyrics.txt`。主项目原有的 FFmpeg 动态绑定仍不支持 NativeAOT，发布方式保持自包含托管应用。

验证工程位于 `FancyTool/_tools/LyricsSmokeTest`。例如运行格式回读检查：

```powershell
dotnet run --project FancyTool/_tools/LyricsSmokeTest -- formats .qa/lyrics/formats
```

测试可用 `FANCYTOOL_DATA_DIRECTORY` 隔离应用数据，`FANCYTOOL_LYRICS_MODELS` 指定已有模型根目录，`FANCYTOOL_LYRICS_DATA` 隔离歌词队列。测试模型、音频缓存和输出放在已忽略的 `.qa/lyrics`，不随源码分发。网络歌词源只用于测试对照，不会覆盖 Qwen3-ASR 识别结果；可运行 `network .qa/lyrics/network D:\music\MP3\歌曲.mp3` 对比 Lyricify Helper 的网易云和 QQ 音乐结果，网络策略、源站限制或缺失元数据可能导致没有结果。

## 📊 功能矩阵

| 类别 | 工具 | 说明 | 算法 / 实现 | 主要依赖 |
| :--- | :--- | :--- | :--- | :--- |
| 通信 | 串口调试 | 串口收发与调试 | `System.IO.Ports` | System.IO.Ports |
| 通信 | TCP 服务器 | TCP 服务端调试 | `TcpListener` / `Socket` | .NET BCL |
| 通信 | UDP 通信 | UDP 收发调试 | `UdpClient` | .NET BCL |
| 对称加密 | DES | DES 加解密 | DES | BouncyCastle.Cryptography |
| 对称加密 | AES | AES 加解密 | AES | BouncyCastle.Cryptography |
| 对称加密 | SM4 | SM4 国密加解密 | SM4 | BouncyCastle.Cryptography |
| 非对称加密 | RSA | RSA 加解密 | RSA | BouncyCastle.Cryptography |
| 非对称加密 | SM2 | SM2 国密加解密 | SM2 | BouncyCastle.Cryptography |
| 哈希 | MD5 | MD5 摘要 | MD5 | BouncyCastle.Cryptography |
| 哈希 | SHA | SHA-1/256/384/512 | SHA 系列 | BouncyCastle.Cryptography |
| 哈希 | SM3 | SM3 国密摘要 | SM3 | BouncyCastle.Cryptography |
| 编码 | Base64 | 文本编解码 | Base64 | .NET BCL |
| 文件 | 文件夹比较 | 两个目录差异对比 | SHA-256 哈希 / 音乐标题 | .NET BCL / ATL |
| 文件 | 文件加解密 | 文件级加密 / 解密 | AES / SM4 等（可扩展） | BouncyCastle.Cryptography |
| 图片 | 图片转 Base64 | 图片 → Base64 | 字节流编码 | .NET BCL |
| 图片 | 图片转换 | 图片格式批量转换（jpg / png / bmp / webp / tiff / dds / jxl / heic / ico） | SKBitmap 解码 + 多格式编码 + ICO 多尺寸封装 | SkiaSharp |

## 🖼️ 软件截图

<img src="FancyTool/docs/img/zh/1.png" width="50%"><img src="FancyTool/docs/img/zh/2.png" width="50%">
<img src="FancyTool/docs/img/zh/3.png" width="50%"><img src="FancyTool/docs/img/zh/4.png" width="50%">
<img src="FancyTool/docs/img/zh/5.png" width="50%"><img src="FancyTool/docs/img/zh/6.png" width="50%">
<img src="FancyTool/docs/img/zh/7.png" width="50%"><img src="FancyTool/docs/img/zh/8.png" width="50%">
<img src="FancyTool/docs/img/zh/9.png" width="50%"><img src="FancyTool/docs/img/zh/10.png" width="50%">
<img src="FancyTool/docs/img/zh/11.png" width="50%"><img src="FancyTool/docs/img/zh/12.png" width="50%">

## 🧱 技术架构

- **UI**：Avalonia 12 桌面端 + FluentAvalonia 3.0（FANavigationView / Frame / ContentDialog 体系），跟随系统 / 浅色 / 深色三套主题
- **MVVM**：`CommunityToolkit.Mvvm`（`[ObservableProperty]` / `[RelayCommand]` 源生成）
- **依赖注入**：`Microsoft.Extensions.DependencyInjection` 容器统一管理 Services、ViewModels、Algorithms（`AddSingleton` / `AddTransient` / `AddKeyedSingleton`），在 `App.axaml.cs` 中配置
- **导航**：`NavigationFactory` 实现 `IFANavigationPageFactory`，通过 `FAFrame.NavigateFromObject` 跳转；`IViewLifecycle` 钩子在进入 / 离开页面时调度 ViewModel 生命周期
- **状态持久化**：`IViewStateService` 序列化各工具页面状态；`AppPreferences` 持久化主题、动画、阴影、通知位置等设置；`ApplicationHostService` 在启动时 `LoadState` / `LoadViewStates`，退出时 `SaveState`
- **日志**：`Serilog` 4 + `Serilog.Extensions.Logging` 桥接 `Microsoft.Extensions.Logging`；按天滚动写入 `{AppBaseDirectory}\logs\tool-.log`，单文件上限 50 MB，保留 30 天
- **稳定性**：`Program.cs` 注册 `AppDomain.UnhandledException` 与 `TaskScheduler.UnobservedTaskException`；启动失败时通过 `MessageBoxW` 兜底提示
- **发布**：因 FFmpeg 动态绑定使用自包含托管发布（`PublishAot=false`）；`FolderProfile.pubxml` 提供 `SelfContained` + `PublishSingleFile` 文件夹发布；仓库根目录提供两个 PowerShell 脚本作为入口——`publish-win.ps1`（Windows x64 自包含发布）与 `publish-linux-deb.ps1`（基于 `Packaging.Targets` 的 `.deb` 打包），均封装了 `dotnet` 子进程调用

## 🛠️ 构建

### 前置条件

- [.NET 10 SDK](https://dotnet.microsoft.com/)（`global.json` 已固定 SDK 版本与 `rollForward` 策略）
- Windows 10 19041 或更高版本
- Linux x64（如 Ubuntu 22.04+，需安装 `.deb` 运行时依赖）
- 推荐使用 Visual Studio 2026

### 从源码构建

仓库根目录提供 PowerShell 构建脚本（默认 `Release` / `win-x64`，启用 NativeAOT）：

```powershell
# Windows PowerShell 5.1+ 或 PowerShell 7+
pwsh -File .\publish-win.ps1
```

可选参数：

```powershell
# 自定义配置与输出目录
pwsh -File .\publish-win.ps1 -Configuration Debug -Output .\out\debug
```

产物默认位于 `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\win-x64\`，直接运行 `FancyToolAva.exe` 即可。

### 调试运行

脚本外如需快速迭代，可使用 dotnet CLI 直接启动：

```bash
dotnet run -c Debug
```

### 跨平台分发

- Linux x64 `.deb` 包：在仓库根目录执行 `pwsh -File .\publish-linux-deb.ps1`，产物落在 `dist\`
- Windows MSIX 包：在仓库根目录执行 `pwsh -File .\publish-msix.ps1`，脚本会先用 `FancyTool/Assets/icon.png` 重采样生成 7 张必需图标，再调用 MSBuild 生成带自签名证书的 `.msix`；产物位于 `FancyToolAva.Msix\bin\x64\Release\FancyToolAva.Msix_1.0.0.0_x64.msix`，首次安装需将 `FancyToolAva.Msix\FancyToolAva.Msix_TemporaryKey.pfx` 导入本机 `TrustedPeople` 存储
- 任意 RID 自包含发布：`dotnet publish -c Release -r <RID> -p:PublishSelfContained=true`（亦可经由 `Properties\PublishProfiles\FolderProfile.pubxml`：`dotnet publish -c Release -p:PublishProfile=FolderProfile`）

> ⚠️ **注意**：Linux 版本未充分验证，可能存在兼容性问题。

## 💖 依赖与致谢

### 第三方库

| 库 | 用途 | 许可证 |
| :--- | :--- | :--- |
| [Avalonia](https://avaloniaui.net/) | 跨平台 UI 框架 | MIT |
| [Avalonia.Desktop](https://avaloniaui.net/) | 桌面端运行支持 | MIT |
| [Avalonia.Fonts.Inter](https://avaloniaui.net/) | Inter 字体 | MIT |
| [FluentAvaloniaUI](https://github.com/amwx/FluentAvalonia) | Fluent Design 控件库 | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM 源生成框架 | MIT |
| [Microsoft.Extensions.DependencyInjection](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection) | 依赖注入容器 | MIT |
| [Microsoft.Extensions.Logging](https://learn.microsoft.com/dotnet/core/extensions/logging) | 统一日志抽象 | MIT |
| [Serilog](https://serilog.net/) | 结构化日志 | Apache-2.0 |
| [Serilog.Extensions.Logging](https://github.com/serilog/serilog-extensions-logging) | Serilog ↔ MEL 桥接 | Apache-2.0 |
| [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) | 文件日志输出 | Apache-2.0 |
| [BouncyCastle.Cryptography](https://www.bouncycastle.org/csharp/) | 加密算法（AES / DES / RSA / SM2 / SM3 / SM4 / SHA / MD5） | MIT |
| [z440.atl.core](https://github.com/Zeugma440/atldotnet) | 音频元数据（音乐标题提取） | MIT |
| [System.IO.Ports](https://learn.microsoft.com/dotnet/api/system.io.ports) | 串口通信 | MIT |
| [Lang.Avalonia](https://github.com/avaloniaui/avalonia) | 多语言运行时（`I18nManager` / `lan:I18n`） | MIT |
| [Lang.Avalonia.Json](https://github.com/avaloniaui/avalonia) | JSON 多语言插件（`i18n\*.json`） | MIT |
| [Avalonia.Skia](https://github.com/AvaloniaUI/Avalonia) | Skia 渲染层（Avalonia Desktop 传递依赖） | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | 图片处理与格式转换（直接用于 ICO / PNG / JPEG / WebP / HEIF / GIF / BMP 编码） | MIT |
| [HarfBuzzSharp](https://github.com/harfbuzz/harfbuzz-sharp) | 文字整形（Avalonia Desktop 传递依赖） | MIT |
| [FFmpeg](https://ffmpeg.org/) | 视频/音频转码（FFmpeg 9.0.1 GPL shared 库随应用分发，进程内经 FFmpeg.AutoGen 直调，见 `FancyTool/Libraries/FFmpeg/BUILD_INFO.md`） | GPL-3.0 |
| [FFmpeg.AutoGen](https://github.com/Ruslan-B/FFmpeg.AutoGen) | FFmpeg C API 的 .NET 绑定 | MIT |
| [Packaging.Targets](https://github.com/qmfrederik/dotnet-packaging) | Linux `.deb` 包打包（`CreateDeb` MSBuild 目标） | MIT |

## 📄 许可证

本项目基于 [GPL-3.0 许可证](LICENSE) 授权。

视频转码功能捆绑分发 FFmpeg 9.0.1 GPL shared 库（含 libx264/libx265 等 GPL 组件，位于应用根目录，
许可证文本随应用分发至 `Licenses/FFmpeg.txt`），因此本仓库自捆绑之日起由 MIT 变更为 GPL-3.0。

## 🗂️ 数据存储

应用程序数据存储位置：

- 偏好与日志目录：`%LocalAppData%\FancyTool\`（MSIX 下由系统重定向到包内的 LocalCache，Linux 等价于 `~/.local/share/fancy-tool/`）
- 日志子目录：`logs\`，按天滚动，单文件 ≤ 50 MB，保留 30 天
- 工具页面状态：通过 `IViewStateService` 在应用退出时序列化，启动时还原
- 应用偏好：由 `AppPreferences` 管理
- **超分模型**：`Models\` 子目录，首次启动时**自动从 HuggingFace 下载**（国内走 `hf-mirror.com`，失败回退官方源），下载一次后保留供离线运行；不再随安装包分发
  - 海外/高级用户可通过设置系统环境变量 `HF_ENDPOINT` 自定义镜像站
  - 旧版本若把 `preferences.json` 放在 EXE 旁，首次启动会自动迁移到 `%LocalAppData%\FancyTool\`

---
