[**English**](README.en.md) | **中文**

<div align="center">
  <img src="Assets/storeIcon.ico" alt="Logo" width="96">

  <h1>妙妙小工具</h1>

  <h3>LittleFancyTool</h3>

  <h4>
    基于 Avalonia 12 + FluentAvalonia 构建的轻量级桌面工具集<br>
    面向 Windows 平台，集成常用加密、通讯与图片处理工具
  </h4>

  <div>
    <img src="https://img.shields.io/badge/语言-C%23-purple" alt="C#">
    <img src="https://img.shields.io/badge/UI-Avalonia%2012-blue" alt="Avalonia">
    <img src="https://img.shields.io/badge/主题-FluentAvalonia-blue" alt="FluentAvalonia">
    <img src="https://img.shields.io/badge/.NET-10.0-purple" alt=".NET 10">
    <img src="https://img.shields.io/badge/平台-Windows-blue" alt="Windows">
    <img src="https://img.shields.io/badge/许可证-MIT-blue" alt="License">
    <a href="#"><img src="https://img.shields.io/badge/⭐-Star%20%E6%95%B0-TODO-lightgrey" alt="Stars"></a>
    <a href="#"><img src="https://img.shields.io/badge/⬇-Downloads-TODO-lightgrey" alt="Downloads"></a>
  </div>

  <br>

</div>

<br>

<div align="center">

[TODO: 产品主页 / 使用说明](#) | [**🐞 反馈问题**](#)

</div>

<br>

## 📥 下载与安装

> 当前未发布预编译安装包，请通过源码构建。

- **前置**：.NET 10 SDK
- **系统**：Windows 10 19041 或更高版本（已配置 `win-x64` 运行时）

```bash
git clone <TODO: 仓库 URL>
cd little-fancy-tool-ava
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

发布为可分发目录（应用 `Properties/PublishProfiles/FolderProfile.pubxml`）：

```bash
dotnet publish -c Release -p:PublishProfile=FolderProfile
```

输出位于 `bin/Release/net10.0-windows/win-x64/publish/win-x64/`。

## 🌟 核心功能

### 🛰️ 通讯调试

- 🛰️ **串口调试**：基于 `System.IO.Ports` 的串口收发与调试
- 🌐 **TCP 服务器**：TCP 服务端调试工具，支持连接管理
- 📡 **UDP 通信**：UDP 收发调试，支持十六进制 / ASCII 切换

### 🔐 加解密与编码

- 🔒 **对称加密**：DES、AES、SM4（国密）字符串加解密
- 🔑 **非对称加密**：RSA、SM2（国密）密钥对生成与加解密
- #️⃣ **哈希计算**：MD5、SHA 系列、SM3（国密）摘要
- 🅱️ **Base64 编解码**：文本 Base64 编码 / 解码

### 🗂️ 文件与图片工具

- 📁 **文件夹比较**：对比两个目录的内容与文件差异（按相对路径、大小匹配）
- 🔐 **文件加解密**：批量的文件级加密 / 解密操作，支持进度跟踪
- 🖼️ **图片转 Base64**：将图片转换为 Base64 编码字符串
- 🖼️ **图片转 ICO**：将位图转换为 ICO 图标格式
- 🖼️ **图片格式转换**：基于 Magick.NET 的图片格式批量转换

## 📊 功能矩阵

| 类别 | 工具 | 说明 | 算法 / 实现 | 主要依赖 |
| :--- | :--- | :--- | :--- | :--- |
| 通讯 | 串口调试 | 串口收发与调试 | `System.IO.Ports` | System.IO.Ports |
| 通讯 | TCP 服务器 | TCP 服务端调试 | `TcpListener` / `Socket` | .NET BCL |
| 通讯 | UDP 通信 | UDP 收发调试 | `UdpClient` | .NET BCL |
| 对称加密 | DES | DES 加解密 | DES | BouncyCastle.Cryptography |
| 对称加密 | AES | AES 加解密 | AES | BouncyCastle.Cryptography |
| 对称加密 | SM4 | SM4 国密加解密 | SM4 | BouncyCastle.Cryptography |
| 非对称加密 | RSA | RSA 加解密 | RSA | BouncyCastle.Cryptography |
| 非对称加密 | SM2 | SM2 国密加解密 | SM2 | BouncyCastle.Cryptography |
| 哈希 | MD5 | MD5 摘要 | MD5 | BouncyCastle.Cryptography |
| 哈希 | SHA | SHA-1/256/384/512 | SHA 系列 | BouncyCastle.Cryptography |
| 哈希 | SM3 | SM3 国密摘要 | SM3 | BouncyCastle.Cryptography |
| 编码 | Base64 | 文本编解码 | Base64 | .NET BCL |
| 文件 | 文件夹比较 | 两个目录差异对比 | 路径 / 大小比较 | .NET BCL |
| 文件 | 文件加解密 | 文件级加密 / 解密 | AES / SM4 等（可扩展） | BouncyCastle.Cryptography |
| 图片 | 图片转 Base64 | 图片 → Base64 | 字节流编码 | .NET BCL / TagLibSharp |
| 图片 | 图片转 ICO | 位图 → ICO | ICO 编码 | Magick.NET |
| 图片 | 图片格式转换 | 图片批量格式转换 | 图像重编码 | Magick.NET |

## 🧱 技术架构

- **UI**：Avalonia 12 桌面端 + FluentAvalonia 3.0（FANavigationView / Frame / ContentDialog 体系），跟随系统 / 浅色 / 深色三套主题
- **MVVM**：`CommunityToolkit.Mvvm`（`[ObservableProperty]` / `[RelayCommand]` 源生成）
- **依赖注入**：`Microsoft.Extensions.DependencyInjection` 容器统一管理 Services、ViewModels、Algorithms（`AddSingleton` / `AddTransient` / `AddKeyedSingleton`），在 `App.axaml.cs` 中配置
- **导航**：`NavigationFactory` 实现 `IFANavigationPageFactory`，通过 `FAFrame.NavigateFromObject` 跳转；`IViewLifecycle` 钩子在进入 / 离开页面时调度 ViewModel 生命周期
- **状态持久化**：`IViewStateService` 序列化各工具页面状态；`AppPreferences` 持久化主题、动画、阴影、通知位置等设置；`ApplicationHostService` 在启动时 `LoadState` / `LoadViewStates`，退出时 `SaveState`
- **日志**：`Serilog` 4 + `Serilog.Extensions.Logging` 桥接 `Microsoft.Extensions.Logging`；按天滚动写入 `{AppBaseDirectory}\logs\tool-.log`，单文件上限 50 MB，保留 30 天
- **稳定性**：`Program.cs` 注册 `AppDomain.UnhandledException` 与 `TaskScheduler.UnobservedTaskException`；启动失败时通过 `MessageBoxW` 兜底提示
- **发布**：`PublishTrimmed` + `PublishReadyToRun` + `SelfContained`（`FolderProfile.pubxml`）

## ✍️ 贡献与构建

欢迎提交 Issue 与 Pull Request。

### 前置条件

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- Windows 10 19041 或更高版本
- 推荐使用 Visual Studio 2022 17.x / Rider / VS Code

### 从源码构建

1. 克隆仓库：

   ```bash
   git clone <TODO: 仓库 URL>
   ```
2. 还原 NuGet 包并构建：

   ```bash
   dotnet restore
   dotnet build -c Release
   ```
3. 启动调试：

   ```bash
   dotnet run -c Debug
   ```

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
| [Magick.NET-Q16-AnyCPU](https://github.com/dlemstra/Magick.NET) | 图片处理与格式转换 | Apache-2.0 |
| [TagLibSharp](https://github.com/mono/taglib-sharp) | 媒体元数据（图片） | LGPL-2.1 |
| [RabbitMQ.Client](https://www.rabbitmq.com/client-libraries/dotnet-api-guide) | 消息队列客户端 | Apache-2.0 |
| [System.IO.Ports](https://learn.microsoft.com/dotnet/api/system.io.ports) | 串口通信 | MIT |
| [System.Windows.Extensions](https://learn.microsoft.com/dotnet/api/) | Windows 扩展 | MIT |

## 📄 许可证

本项目基于 [MIT 许可证](LICENSE) 授权。

> 仓库中尚未包含 `LICENSE` 文件，请补入标准 MIT 许可证文本（项目根目录，作者 / 年份占位即可）。

## 📬 联系方式

- 作者：`<TODO: 作者>`
- 邮箱：`<TODO: 邮箱>`
- 反馈渠道：`<TODO: Issue / QQ 群 / 讨论区>`

## 🗂️ 数据存储

应用程序数据存储位置：

- 日志目录：`{AppBaseDirectory}\logs\`，按天滚动，单文件 ≤ 50 MB，保留 30 天
- 工具页面状态：通过 `IViewStateService` 在应用退出时序列化，启动时还原
- 应用偏好（主题 / 动画 / 阴影 / 通知位置）：由 `AppPreferences` 管理

---

<div align="center">
  <sub>由 <TODO: 作者 / 团队> 用 ❤ 制作</sub>
</div>
