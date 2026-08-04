**English** | [**中文**](README.md)

<div align="center">
  <img src="Assets/storeIcon.ico" alt="Logo" width="96">

  <h1>LittleFancyTool</h1>

  <h3>妙妙小工具</h3>

  <h4>
    A lightweight desktop toolkit built with Avalonia 12 + FluentAvalonia<br>
    Targeting Windows, bundling common encryption, communication, and image utilities
  </h4>

  <div>
    <img src="https://img.shields.io/badge/Language-C%23-purple" alt="C#">
    <img src="https://img.shields.io/badge/UI-Avalonia%2012-blue" alt="Avalonia">
    <img src="https://img.shields.io/badge/Theme-FluentAvalonia-blue" alt="FluentAvalonia">
    <img src="https://img.shields.io/badge/.NET-10.0-purple" alt=".NET 10">
    <img src="https://img.shields.io/badge/Platform-Windows-blue" alt="Windows">
    <img src="https://img.shields.io/badge/License-MIT-blue" alt="License">
    <a href="#"><img src="https://img.shields.io/badge/⭐-Stars-TODO-lightgrey" alt="Stars"></a>
    <a href="#"><img src="https://img.shields.io/badge/⬇-Downloads-TODO-lightgrey" alt="Downloads"></a>
  </div>

  <br>

</div>

<br>

<div align="center">

[TODO: Product Page / User Guide](#)

</div>

<br>

## 📥 Download & Install

> No prebuilt installer is published yet — please build from source.

- **Prerequisites**: .NET 10 SDK
- **OS**: Windows 10 19041 or later (ships as `win-x64` runtime)

```bash
git clone <TODO: repo URL>
cd little-fancy-tool-ava
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

Produce a distributable folder via `Properties/PublishProfiles/FolderProfile.pubxml`:

```bash
dotnet publish -c Release -p:PublishProfile=FolderProfile
```

Output is placed at `bin/Release/net10.0/win-x64/publish/win-x64/`.

## 🌟 Features

### 🛰️ Communication Debug

- 🛰️ **Serial Port Debug**: send / receive over COM ports via `System.IO.Ports`
- 🌐 **TCP Server**: TCP server-side debug tool with connection management
- 📡 **UDP Communication**: UDP send / receive debug with HEX / ASCII switching

### 🔐 Encryption & Encoding

- 🔒 **Symmetric Ciphers**: DES, AES, SM4 (national cipher)
- 🔑 **Asymmetric Ciphers**: RSA, SM2 (national cipher) key pair generation and encryption
- #️⃣ **Hashing**: MD5, SHA family, SM3 (national cipher) digests
- 🅱️ **Base64 Codec**: Base64 encode / decode for text

### 🗂️ File & Image Tools

- 📁 **Folder Compare**: compare two folders and list differences by relative path, SHA-256 hash, and music title
- 🔐 **File Encryption**: batch file-level encryption / decryption with progress tracking
- 🖼️ **Image → Base64**: convert an image into a Base64 string
- 🖼️ **Image → ICO**: convert a bitmap into an ICO icon
- 🖼️ **Image Format Conversion**: batch image format conversion powered by SkiaSharp

## 📊 Feature Matrix

| Category | Tool | Description | Algorithm / Implementation | Main Dependency |
| :--- | :--- | :--- | :--- | :--- |
| Comms | Serial Port Debug | COM port send/receive | `System.IO.Ports` | System.IO.Ports |
| Comms | TCP Server | TCP server debug | `TcpListener` / `Socket` | .NET BCL |
| Comms | UDP Communication | UDP send/receive | `UdpClient` | .NET BCL |
| Symmetric | DES | DES cipher | DES | BouncyCastle.Cryptography |
| Symmetric | AES | AES cipher | AES | BouncyCastle.Cryptography |
| Symmetric | SM4 | SM4 national cipher | SM4 | BouncyCastle.Cryptography |
| Asymmetric | RSA | RSA cipher | RSA | BouncyCastle.Cryptography |
| Asymmetric | SM2 | SM2 national cipher | SM2 | BouncyCastle.Cryptography |
| Hash | MD5 | MD5 digest | MD5 | BouncyCastle.Cryptography |
| Hash | SHA | SHA-1/256/384/512 | SHA family | BouncyCastle.Cryptography |
| Hash | SM3 | SM3 national digest | SM3 | BouncyCastle.Cryptography |
| Encoding | Base64 | Text encode/decode | Base64 | .NET BCL |
| File | Folder Compare | Diff two directories | SHA-256 hash / music title | .NET BCL / ATL |
| File | File Encryption | Batch file encrypt / decrypt | AES / SM4 (extensible) | BouncyCastle.Cryptography |
| Image | Image → Base64 | Image → Base64 | Stream encoding | .NET BCL |
| Image | Image → ICO | Bitmap → ICO | ICO encoding | SkiaSharp |
| Image | Image Format Conversion | Batch format conversion | Image re-encoding | SkiaSharp |

## 🧱 Architecture

- **UI**: Avalonia 12 desktop + FluentAvalonia 3.0 (FANavigationView / Frame / ContentDialog family); supports System / Light / Dark themes
- **MVVM**: `CommunityToolkit.Mvvm` with `[ObservableProperty]` / `[RelayCommand]` source generation
- **Dependency Injection**: `Microsoft.Extensions.DependencyInjection` container wires up services, ViewModels and algorithms (`AddSingleton` / `AddTransient` / `AddKeyedSingleton`) in `App.axaml.cs`
- **Navigation**: `NavigationFactory` implements `IFANavigationPageFactory`; `FAFrame.NavigateFromObject` jumps between pages; `IViewLifecycle` hooks fire on entry / exit
- **State Persistence**: `IViewStateService` serializes per-tool page state; `AppPreferences` persists theme, animations, shadows, notification placement, etc.; `ApplicationHostService` calls `LoadState` / `LoadViewStates` on launch and `SaveState` on exit
- **Logging**: `Serilog` 4 + `Serilog.Extensions.Logging` bridging `Microsoft.Extensions.Logging`; daily-rolling output to `{AppBaseDirectory}\logs\tool-.log`, capped at 50 MB per file, 30 days retained
- **Stability**: `Program.cs` registers `AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`; startup failures surface through `MessageBoxW`
- **Publishing**: Native AOT for non-Debug builds (`PublishAot`); `FolderProfile.pubxml` provides `SelfContained` + `PublishSingleFile` folder publishing

## ✍️ Contributing & Building

Issues and Pull Requests are welcome.

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- Windows 10 19041 or later
- Visual Studio 2022 17.x / Rider / VS Code recommended

### Build from Source

1. Clone the repository:

   ```bash
   git clone <TODO: repo URL>
   ```
2. Restore and build:

   ```bash
   dotnet restore
   dotnet build -c Release
   ```
3. Launch the app:

   ```bash
   dotnet run -c Debug
   ```

## 💖 Dependencies & Credits

### Third-Party Libraries

| Library | Purpose | License |
| :--- | :--- | :--- |
| [Avalonia](https://avaloniaui.net/) | Cross-platform UI framework | MIT |
| [Avalonia.Desktop](https://avaloniaui.net/) | Desktop runtime | MIT |
| [Avalonia.Fonts.Inter](https://avaloniaui.net/) | Inter font | MIT |
| [FluentAvaloniaUI](https://github.com/amwx/FluentAvalonia) | Fluent Design control library | MIT |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM source generator | MIT |
| [Microsoft.Extensions.DependencyInjection](https://learn.microsoft.com/dotnet/core/extensions/dependency-injection) | DI container | MIT |
| [Microsoft.Extensions.Logging](https://learn.microsoft.com/dotnet/core/extensions/logging) | Logging abstractions | MIT |
| [Serilog](https://serilog.net/) | Structured logging | Apache-2.0 |
| [Serilog.Extensions.Logging](https://github.com/serilog/serilog-extensions-logging) | Serilog ↔ MEL bridge | Apache-2.0 |
| [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) | File log sink | Apache-2.0 |
| [BouncyCastle.Cryptography](https://www.bouncycastle.org/csharp/) | Ciphers (AES / DES / RSA / SM2 / SM3 / SM4 / SHA / MD5) | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) | Image processing & format conversion (Avalonia built-in renderer) | MIT |
| [z440.atl.core](https://github.com/Zeugma440/atldotnet) | Audio metadata (music title extraction) | MIT |
| [System.IO.Ports](https://learn.microsoft.com/dotnet/api/system.io.ports) | Serial port communication | MIT |
| [System.Windows.Extensions](https://learn.microsoft.com/dotnet/api/) | Windows extensions | MIT |

## 📄 License

This project is licensed under the [MIT License](LICENSE).

## 📬 Contact

- Author: Sennpei Studio
- Email: dannypan9709@foxmail.com

## 🗂️ Data Storage

Application data is stored at:

- **Log directory**: `{AppBaseDirectory}\logs\`, daily rolling, ≤ 50 MB per file, 30 days retained
- **Per-tool page state**: serialized by `IViewStateService` on exit and restored on launch
- **App preferences** : managed by `AppPreferences`

---

<div align="center">
  <sub>Crafted with ❤ by Sennpei Studio</sub>
</div>
