## 目标

把 Super-Resolution 用的三个 ONNX 模型从「随包发布」改为「应用启动时后台下载到用户可写目录」，并对国内网络做了 hf-mirror.com 自动加速与源站回退。

## 决策摘要（已与你确认）

| 决策项 | 选择 |
|---|---|
| 模型目录 | 改用 `AppPaths.DataDirectory/Models`（MSIX VFS 友好）；顺手修 DataDirectory 在 MSIX 下的写入问题 |
| 下载时机 | 应用启动时后台预下载所有缺失模型 |
| 镜像策略 | 智能探测：优先 `https://hf-mirror.com`，失败回退 `https://huggingface.co` |
| csproj 打包 | 完全移除 `Assets/Models/*.onnx` 的 `<Content>` 与 `VerifySuperResolutionModels` 警告目标，程序包瘦身 |
| 复用现有 | SHA256 表与 PowerShell 脚本共用，模型清单收敛到 C# 一处单一来源（PS1 脚本后续可改为读取该清单） |

## 文件改动清单

### 1. 新增 `FancyTool/Services/ModelManifest.cs`
模型清单的单一来源。三个模型的 URL、SHA256、文件名、友好名集中在此处，供 `SuperResolutionService` 和（未来）PS1 脚本共享。
```csharp
public sealed record ModelManifestEntry(string FileName, string SourceUrl, string MirrorUrl, string Sha256);
public static class ModelManifest
{
    public static IReadOnlyList<ModelManifestEntry> RealEsrgan { get; } = new[]
    {
        new("RealESRGAN_x4plus.onnx",
            "https://huggingface.co/universonic/RealESRGAN/resolve/main/RealESRGAN_x4plus_fp16.onnx",
            "https://hf-mirror.com/universonic/RealESRGAN/resolve/main/RealESRGAN_x4plus_fp16.onnx",
            "30F8DCE72DD67F2F5C492CDEC6FFE1E684833D9F82E3CB1284184710831CD960"),
        new("RealESRGAN_x4plus_anime.onnx",
            "https://huggingface.co/universonic/RealESRGAN/resolve/main/RealESRGAN_x4plus_anime_6B_fp16.onnx",
            "https://hf-mirror.com/universonic/RealESRGAN/resolve/main/RealESRGAN_x4plus_anime_6B_fp16.onnx",
            "38AB81F8F9B5C8B9E03EEAB8BE2F690FE2EE448AC5603174B6DD9B49B6205A24"),
        new("realesr-general-x4v3_fp16.onnx",
            "https://huggingface.co/universonic/RealESRGAN/resolve/main/realesr-general-x4v3_fp16.onnx",
            "https://hf-mirror.com/universonic/RealESRGAN/resolve/main/realesr-general-x4v3_fp16.onnx",
            "CE89B494B6ADAD237792C31D1012D28604BB22D6CD06B8B5903713D4ED636117"),
    };
}
```

### 2. 新增 `FancyTool/Services/ModelDownloadService.cs`
负责实际下载与校验。
- `EnsureModelAsync(entry, ct, progress)`：
  1. 若文件已存在且 SHA256 匹配 → 直接返回 `true`
  2. 否则按顺序尝试 `MirrorUrl` → `SourceUrl`，任一成功即写入并校验
  3. 使用 `HttpClient`（默认注入到 DI），单次超时 15 min（覆盖 33 MB 在国内带宽下的尾段）
  4. 流式写入 `.partial` 临时文件，SHA256 校验通过后原子 `File.Move` 覆盖（避免下载中断残留坏文件）
- 智能探测策略：先 mirror，HTTP 失败（连接错误/超时/5xx）或 SHA256 不匹配都视为失败，fallback 到源站；源站也失败则抛 `ModelDownloadException`
- 通过 `IProgress<ModelDownloadProgress>` 上报：当前文件名、字节进度、阶段（Connecting/Downloading/Verifying/Done）
- 尊重 `HF_ENDPOINT` 环境变量（huggingface_hub 社区惯例），便于海外用户/高级用户覆盖
- AOT 友好：只使用 `HttpClient` + `SHA256`，无反射；DI 中显式注册 `HttpClient` 避免静态分析报警

### 3. 新增 `FancyTool/Models/ModelDownloadProgress.cs`（轻量 POCO）
```csharp
public sealed record ModelDownloadProgress(
    string FileName,
    ModelDownloadStage Stage,
    long BytesDownloaded,
    long? TotalBytes);
public enum ModelDownloadStage { Connecting, Downloading, Verifying, Done, Failed }
```

### 4. 修订 `FancyTool/Services/AppPaths.cs`
让 DataDirectory 在 MSIX 下也能持久写入：
```csharp
public static string DataDirectory { get; } = OperatingSystem.IsWindows()
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FancyTool")
    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "fancy-tool");

public static string ModelsDirectory => Path.Combine(DataDirectory, "Models");
```
说明：
- `LocalApplicationData` 在 MSIX VFS 下被透明重定向到 `LocalState\Local`（包内 `Packages\<pkg>\LocalCache\Local`），对应用透明且永久持久
- 非 MSIX（普通 .exe）下退回到 `%LocalAppData%\FancyTool`，避开 `Program Files` 写入失败
- Linux 路径不变
- 现有 `preferences.json` 写入路径会从 EXE 旁迁到 LocalAppData 下——若不希望老用户丢偏好，可加一段「搬迁老位置 → 新位置」的迁移代码（建议加，3 行即可）

### 5. 修订 `FancyTool/Services/SuperResolutionService.cs`
- 构造函数用 `_modelsDirectory = AppPaths.ModelsDirectory;`（替换原 `AppContext.BaseDirectory`）
- 启动时（构造函数中）调用 `EnsureModelsAsync()`：遍历 `ModelManifest.RealEsrgan`，若任一缺失则触发下载；但**不阻塞**：把 `Task` 暴露为 `WarmupTask` 属性供 UI 显示「正在下载模型…」状态
- `IsModelAvailable` 与 `GetOrCreateSession` 的逻辑不变，仍按本地文件存在性走；下载未完成时 UI 提示「模型下载中」而非「缺失」
- 错误文案改为中文：「模型未找到，正在尝试下载… 请检查网络或稍后重试」

### 6. 修订 `FancyTool/App.axaml.cs`
- DI 注册新增：
  - `services.AddHttpClient<ModelDownloadService>();`（`Microsoft.Extensions.Http`，与 `Microsoft.Extensions.Logging` 共存）
  - `services.AddSingleton<ModelDownloadService>();`
  - `services.AddSingleton<ISuperResolutionService>(sp => new SuperResolutionService(...));` 改由服务自身在 ctor 内 fire-and-forget 触发下载
- `OnFrameworkInitializationCompleted` 中在主窗口创建**之后**调用 `Task.Run(() => _serviceProvider!.GetRequiredService<ISuperResolutionService>().WarmupTask)`，确保 UI 先呈现
- `desktop.Exit` 中增加 `WarmupTask` 的优雅取消

### 7. 修订 `FancyTool/ViewModels/ImageConvertViewModel.cs`
- `IsSuperResolutionReady` 改为同时检查：模型本地存在 **或** 正在下载
- 新增 `IsModelDownloading` / `ModelDownloadProgressText` 两个属性，绑定到 UI 提示
- 用户点击「开始转换」时若 `WarmupTask` 尚未完成 → 弹窗「模型仍在下载，请稍候」，避免 race

### 8. 修订 `FancyTool/Views/ImageConvertView.axaml`（或对应 View）
在超分模型下拉框下方加一个 `ProgressBar` + `TextBlock`，显示下载进度。样式与现有 UI 保持一致。

### 9. 修订 `FancyTool/FancyToolAva.csproj`
**删除**第 65-69 行：
```xml
<ItemGroup>
  <Content Include="Assets\Models\*.onnx" CopyToOutputDirectory="PreserveNewest" Condition="Exists('$(MSBuildThisFileDirectory)Assets\Models\')">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </Content>
</ItemGroup>
```
**删除**第 71-80 行的 `VerifySuperResolutionModels` Target（依赖已删除的源文件路径）
**删除**或**保留可选**：`tools/download-models.ps1` 保留作为开发者辅助脚本，但改注释提示「已废弃：程序会自动下载，仅在需要离线预热时手动运行」
**新增 NuGet**：`Microsoft.Extensions.Http`（如果尚未引入；查看 csproj 确认）

### 10. 修订 README（`README.md`、`README.en.md`）
添加一段「首次启动会自动下载超分模型（约 47 MB）」的说明，国内用户走 hf-mirror 加速，海外用户可通过 `HF_ENDPOINT` 环境变量切回 huggingface.co。

## 错误处理与边界

| 场景 | 行为 |
|---|---|
| 启动时无网络 | `WarmupTask` 保持未完成状态；UI 显示「下载失败：模型未就绪」；用户重新打开程序或点重试按钮再触发 |
| 下载中用户退出 | `CancellationToken` 联动到 `desktop.Exit`，取消正在进行的 HTTP 请求；下次启动重新检测 |
| SHA256 不匹配（被劫持/中间人） | 删除 `.partial` 文件，尝试下一镜像；两个都失败则抛异常 |
| MSIX VFS 兼容 | 已通过改用 `LocalApplicationData` 自动规避 |
| AOT 兼容 | 只用 `HttpClient` + `SHA256`，显式 DI 注册避免 trim 警告 |
| 用户已有旧版「随包模型」在 EXE 旁 | 旧目录不再被读取；用户首次启动时新位置会重新下载（浪费 47 MB 一次）。可加迁移：检测旧路径若有文件则 copy 到新位置 |

## 验证步骤（实现阶段完成后）

1. **清理构建**：删 `FancyTool/Assets/Models/*.onnx`，`dotnet publish -c Release -r win-x64`，确认输出不再包含任何 .onnx 文件
2. **冷启动**：删 `%LocalAppData%\FancyTool\Models`，启动程序 → 观察下载进度 → 完成后正常转换
3. **国内加速**：在国内网络下确认 hf-mirror.com 路径生效，下载时间 < 2 min（33 MB 模型）
4. **回退**：通过 hosts 或防火墙阻断 hf-mirror，确认自动回退到 huggingface.co 并成功
5. **MSIX**：构建 MSIX 包，安装后首次启动验证下载到 `LocalCache\Local\FancyTool\Models`，二次启动秒过
6. **重试**：模拟启动时断网 → 启动后联网 → UI 触发「重试下载」后成功
7. **资源占用**：确认不存在时 `IsSuperResolutionReady` 返回 false 但 UI 文案是「下载中」而非「缺失」

## 不在本次范围内

- 把 `tools/download-models.ps1` 完全重写为从 `ModelManifest.cs` 生成（可以后续清理）
- 添加下载限速/并发控制（单文件依次下载足够，国内带宽一般也只跑一个）
- 镜像站测速与自动择优（先实现「mirror-first + source-fallback」就够用）

## 风险与权衡

- **首次启动体验下降**：用户必须联网；可在 README 与「关于」页明显说明
- **镜像站稳定性依赖**：hf-mirror.com 是社区站，长期可用性不如官方；为此保留了源站 fallback 与 SHA256 校验
- **磁盘位置变化**：从 EXE 旁改到 `%LocalAppData%`，老用户的 `preferences.json` 需要迁移代码（已在第 4 步覆盖）