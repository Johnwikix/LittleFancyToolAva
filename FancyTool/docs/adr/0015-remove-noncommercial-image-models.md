# 移除非商业图片超分模型

日期：2026-10-23。

## 背景与约束

产品目标是 GPL 开源收费。[许可核查报告](../commercial-license-audit-2026-10-23.md)确认 UltraSharp 和 LiveAction SPAN 的公开许可为 CC BY-NC-SA 4.0，不能据此授权商业集成。本决策替代 [ADR 0004](0004-windows-ml-and-efficient-sr.md) 中保留这两款图片模型的部分；历史测速与来源记录继续保存。

旧版本把图片下拉框索引作为模型编号保存，原编号为 0、1、2、3（UltraSharp）、4（ClearReality）、5（LiveAction）。删项后若直接压缩枚举或继续把编号当作清单下标，会改变旧设置含义，或者在 ClearReality 的 NPU/普通推理路径访问越界。

## 决策

- 图片超分仅保留 RealESRGAN x4plus、x4plus anime、general x4v3 和 ClearReality V1；四款均支持 2× / 4×，默认仍是 ClearReality。
- 从枚举、下载清单、服务模型列表、文件映射、页面选项、双语言标签和非商业提示中移除 UltraSharp / LiveAction；候选验证工具与视频测速脚本的默认清单也不再加入 LiveAction。历史数据分析脚本仍可读取旧结果中的模型列名，不代表产品继续提供这些模型。
- 保留现存模型编号 0、1、2、4，不复用退役编号 3、5。通过 `ModelManifest.GetImageModel` 显式映射模型与文件，所有普通/NPU 路径不再按枚举编号索引下载清单。
- `ImageConvertViewState.SelectedSrModelIndex` 沿用历史 JSON 字段名，保存稳定模型编号。恢复时转换成当前下拉框索引；退役/无效编号回退至 ClearReality，现存模型和有效倍率保持不变。
- 即使旧缓存仍存在，服务也拒绝退役编号。不自动删除用户模型目录中的旧文件；它们不再进入下载、选择或推理路径。仓库未捆绑这两个模型的权重，无需删除用户私有数据。
- 继续采用 GPL 开源收费。FFmpeg GPL 及 FSRCNNX/RAVU 的 GPL/LGPL 义务继续履行；CHTCHSConv、Windows ML/厂商 EP 授权及整包声明交付等原核查待办仍单独处理。

## 替代方案

- 保留功能并增加非商业提示：提示不能提供商业授权，不采用。
- 压缩模型枚举为 0–3：旧编号 3/4 的含义会改变，不能直接兼容既有状态；保留稳定编号更简单。
- 自动清空所有超分设置或删除旧缓存：会丢失合法模型与倍率偏好，删除用户文件也不是解除产品集成所必需，不采用。

## 验证与已知限制

新增 `FfmpegSmokeTest --image-models-only`，覆盖真实下载服务、真实 ViewModel/源生成状态持久化以及真实 Avalonia 控件绑定：

```powershell
# 沙箱内将 NuGet 临时锁放到工作区；不更改 SDK/global.json。
$env:NUGET_SCRATCH = Join-Path (Get-Location) '.qa/model-removal-2026-10-23/nuget-scratch'
$env:NUGET_HTTP_CACHE_PATH = Join-Path (Get-Location) '.qa/model-removal-2026-10-23/http-cache'
dotnet build FancyTool/_tools/FfmpegSmokeTest/FfmpegSmokeTest.csproj -c Debug --no-restore -m:1 -p:BuildInParallel=false
dotnet run --project FancyTool/_tools/FfmpegSmokeTest -c Debug --no-build -- .qa/model-removal-2026-10-23/run --image-models-only
python .qa/model-removal-2026-10-23/verify.py
# 加 --inspect 可保留测试窗口，使用 lvt 检查下拉框，关闭窗口后完成烟测。
```

下载边界使用离线 404 响应记录请求，实际执行生产服务的清单遍历、重试和失败收尾，没有联网下载模型。状态检查覆盖原编号 0–5 与两个无效值，随后按真实 `ViewStateService.SaveAll`/源生成 JSON 回读并再次恢复。退役编号检查还保留模拟旧缓存，确认不会进入 ONNX 会话加载。

原始构建/运行日志、请求记录和浅色/深色/系统主题截图保存在仓库根目录已忽略的 `.qa/model-removal-2026-10-23/`。

执行结果（Windows，本机 .NET SDK 10.0.402，沿用 global.json 的版本与 rollForward 策略）：Debug 构建成功，0 错误、3 个既有截图 API 过时警告；`--image-models-only` 退出 0，上述下载、退役编号守卫、八种旧状态及真实页面绑定检查全部通过。使用 lvt 的 UIA 模式实际展开 `ImageSrModel`，枚举到且仅枚举到四个保留模型；窗口检查结束后已正常关闭，退出 0。`inspect/expanded.json` 保留控件枚举，`inspect/models-expanded.png` 保留窗口截图；后者不保证捕获独立弹出层，四项结论以 UIA 枚举和真实控件断言为准。

静态检查确认本次涉及的 `ImageConvert` 双语键结构一致、四款保留模型均有标签、生产入口不再引用退役模型、改动文件为 CRLF。完整语言文件中既有的 AppFatal/App 键路径差异与改动前相同，原始差异记录在 `static-checks.json`，本次未扩大修复范围。

本次不更改 FFmpeg/模型权重、推理后端或图像处理算法；未执行 GPU/NPU 数值和画质回归，也不以清单/页面烟测宣称整包商业合规。
