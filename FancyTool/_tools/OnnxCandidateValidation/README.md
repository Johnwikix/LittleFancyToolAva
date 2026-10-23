# ONNX GPU 候选验证

本目录保存最初的候选验证和后续产品模型打包工具。初筛结果见 [2026-10-16 实测报告](../../docs/onnx-vsr-validation-2026-10-16.md)，当前产品接入见 [ADR 0008](../../docs/adr/0008-lightweight-gpu-video-sr.md)。

`fetch.ps1` 固定上游提交与权重 SHA-256；`prepare.py` 导出九个变体，比较 PyTorch/CPU ONNX，并生成确定性参考张量。C# 验证入口复用应用的 Windows ML、位图分块和视频转码服务，强制禁用 CPU EP 回退。`run.py` 串行启动独立进程，保留失败；`summarize.py` 汇总证据及合格配置。

在仓库根目录运行。需要 Python 3.14、.NET 10 SDK、已安装的 Windows ML OpenVINO GPU 提供程序和 Intel GPU 驱动。GPU 运行必须在可访问真实适配器的用户会话中执行。

```powershell
$work = '.qa/onnx-candidates'
$tools = 'FancyTool/_tools/OnnxCandidateValidation'
& "$tools/fetch.ps1" -Work $work
py -3.14 -m venv "$work/venv"
$python = "$work/venv/Scripts/python.exe"
# 显式 target 覆盖本机全局 pip target；-I 排除全局 PYTHONPATH。
& $python -I -m pip --isolated install --target "$work/venv/Lib/site-packages" --extra-index-url https://download.pytorch.org/whl/cpu -r .qa/git-history-cleanup-2026-10-17/evidence/onnx-vsr-validation-2026-10-16/python-lock.txt
& $python -I "$tools/prepare.py" $work
dotnet build FancyTool/_tools/FfmpegSmokeTest -c Release -p:PublishAot=false
$runner = 'FancyTool/_tools/FfmpegSmokeTest/bin/Release/net10.0-windows10.0.19041.0/win-x64/FfmpegSmokeTest.exe'
& $python -I "$tools/run.py" $work --runner $runner
```

OpenVINO 的默认 GPU 配置使用 f16 精度提示。失败配置必须保留，补充 f32 精度提示单独记录：

```powershell
foreach ($model in @('nnedi3_nns16_win8x4', 'ravu_lite_r2', 'ravu_lite_r3', 'edvrm_x4_128x128_frames5')) {
    & $python -I "$tools/run.py" $work --runner $runner --model $model --gpu-only --fp32 --results-subdir results-precision
}
foreach ($model in @('ravu_lite_r2', 'ravu_lite_r3')) {
    & $python -I "$tools/run.py" $work --runner $runner --model $model --dml-only --benchmark-dml --results-subdir results-dml-benchmark
}
foreach ($model in @('anime4k_upscale_cnn_s', 'fsrcnnx_s', 'edvrm_x4_128x128_frames5')) {
    & $python -I "$tools/run.py" $work --runner $runner --model $model --dml-only --benchmark-dml --results-subdir results-bounded
}
```

可选基线使用本地已有的 `4x-ClearRealityV1_fp16.onnx`，仅测速与转码，不作新的数值资格认证：

```powershell
& $python -I "$tools/baselines.py" $work .qa/video-sr/models
foreach ($model in @('baseline_clearreality')) {
    & $python -I "$tools/run.py" $work --runner $runner --model $model --gpu-only --results-subdir results-precision
}
& $python -I "$tools/summarize.py" $work .qa/git-history-cleanup-2026-10-17/evidence/onnx-vsr-validation-2026-10-16
```

`run.py` 跳过目标目录中已存在的结果；需要重测时选择新的 `--results-subdir`，并在汇总脚本中加入该目录。根目录 `results.json` 仅是最近一次调用的进度，不是总报告。汇总读取四个约定目录中的完整记录；`eligible-configurations.json` 只包含 GPU 设备、三个数值样本全部通过且整个记录通过的候选。相同配置的张量验证与位图验证可能分别存在，需查看 `bitmapPathValidated` 和 `transcodeValidated`。

EDVR 固定输入 `[1,15,128,128]`，五帧输出一帧。本工具使用五张不同的合成邻帧，限制为 320×180 输入；它没有接入产品的时序调度器。早期 640×360 DirectML 测试超时的历史诊断仍在存档中，新版脚本不会重现该大尺寸尝试。

候选模型、参考张量、视频和环境保存在忽略目录 `.qa`。产品选中的六档模型另行打包在 `Libraries/VideoSr`。存档含 SHA-256、固定上游提交、依赖锁定及失败日志。GPU 数值容差为最大绝对误差 0.02 且 MAE 0.001，二者必须同时通过；不要为了使失败模型入选而放宽。

产品模型打包与回归（RGB适配图也参与CPU/GPU逐像素比较）：

```powershell
& $python -I "$tools/package.py" $work
# 打包转换器可插入乱序Cast，脚本会恢复拓扑顺序并运行ONNX checker。
# 模型内容变化时必须同步VideoSrModelCatalog中的固定哈希。
$env:FANCYTOOL_DATA_DIRECTORY = (Join-Path (Get-Location) '.qa/video-sr-integration/app-data')
& $runner .qa/video-sr-integration --sr-only --sr-reference-root $work
# 可按模型/精度/后端单独回归。
& $runner .qa/video-sr-integration/fsrcnnx --sr-only --sr-model FsrcnnxS --sr-fp32 --sr-reference-root $work
```

生产管线在后台执行。验证器保留Avalonia消息循环等待GPU任务完成，避免在STA入口同步阻塞而影响原生运行时调用。生成的JSON和日志保存在指定工作目录中，失败不会通过放宽阈值忽略。

产品后端由档位自动选择：Anime4K、FSRCNNX、RAVU r3只使用厂商GPU，RAVU r2使用DirectML FP32。`--sr-backend`保留为诊断参数，强制未获准的DirectML组合会失败。不要同时启动多个GPU回归进程；原生初始化可能等待较久，手工终止时必须记录未完成状态，不能将其当作通过。正式回归见ADR 0008的数据链接。
