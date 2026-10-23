# 依赖与模型商业收费许可核查

> 实施更新（2026-10-23）：用户确定采用开源收费方式后，UltraSharp / LiveAction 已从生产代码、自动下载及页面选择中移除，旧设置会迁移至 ClearReality，详见 [ADR 0015](adr/0015-remove-noncommercial-image-models.md)。下文保留移除前的核查基线和许可证据；其余依赖授权及 GPL 分发待办仍然有效，本次移除不等于完成整包商业合规审核。

日期：2026-10-23。范围：当前工作区（包含已有未提交修改）的主工程、歌词子工程、现有 NuGet 还原图、随应用分发的模型/原生库及按需下载模型。没有修改生产代码、替换依赖或重新构建发布包。

## 结论

**大多数依赖和模型允许商业使用，但当前完整功能版本尚不能判定为可原样收费发布。**

明确需要处理的项目是 `4x-UltraSharp` 和 `2xLiveActionV1_SPAN`：两者均采用 **CC BY-NC-SA 4.0 非商业许可**。收费产品集成、收费推理服务等商业使用，不能仅凭当前公开许可获得授权。需要移除/替换相关功能，或取得覆盖实际商业场景的额外授权。改成按需下载、标注“仅供学习”或在界面提示许可，均不能代替商业授权。

本项目自身的 [GPL-3.0 许可](../../LICENSE) **允许收费销售**。但目前进程内调用并随包分发 GPL FFmpeg，不能把整个现有程序当作普通闭源专有软件出售。合规发行时需要向接收者按 GPL 提供完整对应源代码，并保留其修改及再分发权利。收费不等于可以限制接收者再次分享。模型和专有运行时还须分别满足自身条款。

另外，`CHTCHSConv` 的可核验授权链、Windows ML/厂商加速组件与 GPL 的兼容性、完整第三方声明及对应源码交付均有待解决。这里区分“明确非商业限制”“允许商业使用但有条件”和“授权尚未核清”，不把后两者混为禁止商用。

## 模型清单

清单依据：[图片模型下载清单](../Services/ModelManifest.cs)、[视频模型说明](../Libraries/VideoSr/NOTICE.md)、[插帧模型说明](../Libraries/InterpolationModels/NOTICE.md)、`Resources/LyricsModels/*.json`。FP16/INT4 或 ONNX 转换不会自动解除原权重的许可义务。

| 功能/模型 | 核查到的许可 | 商业收费结论与义务 |
| --- | --- | --- |
| RealESRGAN x4plus、x4plus anime 6B、realesr-general-x4v3 | 上游 BSD-3-Clause | 上游许可允许商业使用；保留版权、条款和免责声明。当前 ONNX 来自第三方 universonic 镜像，需要补齐原模型到转换件的来源和许可记录，不能只引用推理代码的许可证。 |
| 4x-UltraSharp | CC BY-NC-SA 4.0 | **公开许可不允许商业用途；收费版需移除/替换或取得商业授权。** |
| 4x-ClearRealityV1（SPAN） | Apache-2.0 | 允许商业使用；保留许可、适用的 NOTICE 和来源/修改说明。 |
| 2xLiveActionV1_SPAN | CC BY-NC-SA 4.0 | **公开许可不允许商业用途；收费版需移除/替换或取得商业授权。** |
| Anime4K Upscale CNN S/M，FP32/FP16 | MIT | 允许商业使用；随分发保留版权与许可文本。 |
| FSRCNNX s/m，FP32/FP16 | 按权重上游 GPL-3.0 管理 | 允许商业使用；权重、转换修改及对应源码按 GPL 履行义务。导出工具的 MIT 不能覆盖权重许可。当前仓库已附 GPL 文本和转换脚本。 |
| RAVU Lite r2/r3，FP32 | LGPL-3.0 | 允许商业使用；保留 LGPL/GPL 文本，提供修改后的模型对应源码及适用的替换/重建方式。导出工具的 MIT 不能覆盖权重许可。 |
| Practical-RIFE 4.25 Lite、4.25（rife_v2 ONNX） | MIT | 允许商业使用；作者明确把所链接训练模型也置于 MIT 下。保留作者和许可，记录转换及镜像来源。 |
| Qwen3-ASR-1.7B，FP32/INT4 ONNX | 原模型及所用转换仓库均声明 Apache-2.0 | 允许商业使用；保留原模型及转换件的许可、适用 NOTICE 和修改说明。 |
| Qwen3-ForcedAligner-0.6B，INT4 ONNX | 原模型及所用转换仓库均声明 Apache-2.0 | 允许商业使用；同上。当前下载清单包括 aligner 的 LICENSE。 |

模型的一手许可依据：

- [UltraSharp 作者模型卡](https://huggingface.co/Kim2091/UltraSharp) 和 [LiveAction 固定提交的作者说明](https://github.com/jcj83429/upscaling/tree/9332e7d5b07747ff347e5abdc43f8144364de9f7/2xLiveActionV1_SPAN) 均明确标注 CC BY-NC-SA 4.0；[Creative Commons 条款](https://creativecommons.org/licenses/by-nc-sa/4.0/) 明确限制商业用途。
- [Real-ESRGAN 上游许可](https://github.com/xinntao/Real-ESRGAN/blob/master/LICENSE)、[ClearReality 作者模型卡](https://huggingface.co/Kim2091/ClearRealityV1)、[Anime4K 固定源提交的许可](https://github.com/bloc97/Anime4K/blob/7684e9586f8dcc738af08a1cdceb024cc184f426/LICENSE)。本次未能通过在线页面读取 universonic 镜像模型卡，因此没有把这一转换件授权链标为完全核清。
- [FSRCNNX 权重上游 GPL](https://github.com/igv/FSRCNN-TensorFlow/blob/master/LICENSE)、[RAVU 上游 LGPL](https://github.com/bjin/mpv-prescalers/blob/master/LICENSE)。具体权重来源还应保留本仓库 `Libraries/VideoSr/Licenses/*-provenance.txt` 与固定的导出脚本，不能只看中间托管仓库的 MIT。
- [Practical-RIFE 作者说明](https://github.com/hzwer/Practical-RIFE#trained-model) 明确说明模型链接内容与项目采用相同 MIT 许可。
- [Qwen3-ASR 原模型](https://huggingface.co/Qwen/Qwen3-ASR-1.7B)、[所用 ASR 转换仓库](https://huggingface.co/andrewleech/qwen3-asr-1.7b-onnx)、[ForcedAligner 原模型](https://huggingface.co/Qwen/Qwen3-ForcedAligner-0.6B)、[所用 aligner 转换仓库](https://huggingface.co/jinhwan000/qwen3-aligner-0.6b-onnx)。

NanoVSR 及旧的独立 OpenVINO 分发目录已被后续 ADR/实现移除，不作为当前随包依赖；历史实验文件仍可能存在于 `.qa/`，不能据此算作正在发行的功能。

## 托管依赖、原生库与字体

本机 `obj/project.assets.json` 中有 **82 个 package 项**，其中包括构建工具、分析器和非当前平台资产；这不是“82 个实际随包分发的库”。逐包名称、版本、NuSpec 声明、文件式许可正文和源文件快照见本地证据 `../../.qa/license-audit-2026-10-23/inventory.json`。原始 NuSpec 声明不能替代包内第三方组件条款。

| 依赖/组件 | 许可或核查结果 | 商业收费结论 |
| --- | --- | --- |
| Avalonia/desktop/platform/Skia 12.1.1；FluentAvaloniaUI 3.0.2；CommunityToolkit.Mvvm 8.4.2；Lang.Avalonia/Json/Analysis 12.1.1.2 | NuSpec 为 MIT | 允许商业使用，保留版权和许可。没有引用收费的 Avalonia XPF 产品。 |
| Microsoft.Extensions.*、System.IO.Ports 10.0.11、System.Numerics.Tensors 9.0.0、MicroCom、Tmds.DBus、Roslyn/ILLink 等 | NuSpec 为 MIT | 允许商业使用；区分构建期工具与实际随包资产。 |
| .NET/Satori 10.0.13-satori.38707290103.1 | SDK、Runtime/AOT 包 NuSpec 为 MIT；Runtime 包附 LICENSE.TXT 和 THIRD-PARTY-NOTICES.TXT | 允许商业使用；自包含发行也应保留运行时和第三方声明。 |
| BouncyCastle.Cryptography 2.7.0、z440.atl.core 7.16.0、SharpZipLib 1.4.2 | NuSpec 为 MIT | 允许商业使用，保留版权和许可。SharpZipLib 此版本是 MIT，不能沿用旧版 GPL 印象。 |
| Serilog 4.4.0、Serilog.Extensions.Logging 10.0.0、Serilog.Sinks.File 7.0.0 | Apache-2.0 | 允许商业使用，保留许可及适用 NOTICE。 |
| FFmpeg.AutoGen 9.0.1.1 | 包内 LICENSE.txt 为 MIT | 绑定层允许商业使用；不能用它的 MIT 覆盖实际 FFmpeg 二进制的 GPL。 |
| FFmpeg N-127579-g5cb9fcd211，BtbN GPL shared Windows/Linux 六库 | GPL-3.0；来源/哈希见 `Libraries/FFmpeg/BUILD_INFO.md` | 允许收费，但当前进程内链接及分发需要 GPL 合规。必须提供与二进制对应的 FFmpeg 及所需组件源代码、修改/构建材料；只放 GPL 文本或上游首页不等于已完成源码交付。 |
| Lyricify.Lyrics.Helper 本地修改版 | Apache-2.0 | 外层库允许商业使用；子依赖另算。保留本地修改及来源说明。 |
| CHTCHSConv 1.0.0 / ChineseConverter.dll | NuGet 包未声明许可，也没有附 EULA；原组件为微软 Visual Studio International Pack | **授权链未核清；不宜直接批准当前跨平台/GPL 分发方式。** 详见后文。 |
| Ude.NetStandard 1.2.0（ATL 的间接依赖） | 上游可选 MPL-1.1 / GPL-2.0-or-later / LGPL-2.1-or-later；包内 COPYING 仅写“See license directory”，实际未附该目录 | 允许商业使用；应明确采用可兼容的许可路径并补齐正文/版权与对应源码获取方式，不能列成 MIT。 |
| SkiaSharp 3.119.4、HarfBuzzSharp 8.3.1.3 及 native assets | 包声明 MIT，包含上游原生组件 | 允许商业使用；还需保留其原生组件第三方声明，不能只收集 .NET 包最外层 MIT。 |
| ANGLE native 2.1.27548.20260419 | 包内 LICENSE 为 BSD 风格三条款 | 允许商业使用，保留版权、条款和免责声明。 |
| Avalonia.Fonts.Inter / Inter 字体 | .NET 包声明 MIT；字体本体为 SIL OFL-1.1 | 可随收费软件捆绑，需保留字体版权和 OFL；字体不能脱离软件单独售卖。README 只标 MIT 不能完整表达字体许可。 |
| Microsoft.ML.OnnxRuntime 1.22.1（非 Windows 条件引用） | 上游 MIT | 允许商业使用；本次现有主工程还原图为 Windows，没有重新还原 Linux 图或验证其最终发布文件。 |
| Packaging.Targets 0.1.232；Inno Setup 安装器 | 前者 MIT；后者自有宽松许可明确允许商业应用 | 允许商业使用；保留适用声明。构建工具许可与产品代码许可分别判断。 |

相关一手来源：[Ude 的多重许可说明](https://github.com/yinyue200/ude#license)、[Inter OFL](https://github.com/rsms/inter/blob/master/LICENSE.txt)、[Inno Setup 许可](https://jrsoftware.org/files/is/license.txt)、[ONNX Runtime 许可](https://github.com/microsoft/onnxruntime/blob/v1.22.1/LICENSE)、[FFmpeg 官方法律说明](https://ffmpeg.org/legal.html)。其余逐版本结论来自当前本机已还原包的 NuSpec/许可文件，原文与路径记录于 inventory.json。

## 需要解决的发布问题

### 1. 两个非商业模型仍进入正常功能路径

`Services/ModelManifest.cs` 第 27–40 行配置了 UltraSharp 和 LiveAction 的下载地址；`SuperResolutionService.StartWarmupAsync` 对完整 `ModelManifest.RealEsrgan` 清单调用下载流程，二者也出现在图片超分模型列表中。

`ViewModels/ImageConvertViewModel.cs:151` 的 `IsSrNonCommercialModel` 只判断 LiveAction；`i18n` 的对应提示也只描述 LiveAction。**UltraSharp 的非商业限制没有同样提示。** 补提示只能改善告知，不能使商业集成取得授权。发布前应从收费产品的模型列表、下载路径和实际调用入口一起排除，或先取得有效授权；不能只改一个界面选项。

### 2. CHTCHSConv 不是外层 Apache-2.0 许可的一部分

真实引用路径为：主工程 → `External/Lyricify.Lyrics.Helper` → `CHTCHSConv 1.0.0`。`Helpers/General/ChineseHelper.cs:19` 和 `:32` 直接调用微软 ChineseConverter；该包声明 .NET Standard 2.0 并不能证明微软授权允许跨平台分发。

本次从[微软官方下载页](https://www.microsoft.com/en-us/download/details.aspx?id=15251)取得 `vsintlpack1.zip`，仅以只读数据库方式提取其中 `CHTCHSConv.msi` 的许可文本，没有安装组件。原始 EULA 包含：

- 再分发代码运行平台限定为 Windows；
- 再分发需遵守 REDIST.TXT 清单、增加主要功能及向下游传递保护条款等条件；
- “Excluded License” 限制，需与当前 GPL 组合单独核对；
- 对商业软件托管服务的限制；安装/使用权条款还提及 Visual Studio 2005/2008。

官方旧组件 EULA 不能自动证明第三方重新打包的 .NET Standard DLL 已得到完整授权；NuGet 包本身又没有提供新的授权文件。本次没有核实 NuGet DLL 与原始 MSI DLL 的二进制对应关系或取得其他授权。因此结论是“当前授权证据不足且存在明确上游平台限制”，不是笼统宣布所有版本的中文转换组件均禁止商用。优先替换成授权清楚、与产品平台及许可证兼容的实现。

原始包、MSI、RTF 和纯文本保存在 `../../.qa/license-audit-2026-10-23/`，纯文本为 `CHTCHSConv-official-EULA.txt`。

### 3. Windows ML 与厂商提供程序不能按普通 MIT 库处理

Windows 使用的准确包是 `Microsoft.Windows.AI.MachineLearning 2.4.89`。[该版本的 NuGet 许可正文](https://www.nuget.org/packages/Microsoft.Windows.AI.MachineLearning/2.4.89/License)是微软专有条款；GitHub WindowsML 仓库上的 MIT 不能覆盖此二进制包的全部内容。

该条款允许符合条件的随应用再分发，同时包含 Windows 用途、下游保护条款及第 3(c)(ii) 的反 copyleft 限制。结合本项目进程内使用 GPL FFmpeg，**需要核实整体组合是否可合法分发，不能仅因微软允许商业应用就判定当前 GPL 组合也合规**。GPL 系统库例外及各组件的独立性需要结合实际部署判断，本次没有取得适用于本程序的例外/额外许可，也不把潜在冲突直接当成已经裁定的侵权。

`WindowsMlAccelerator` 还通过目录安装/注册 OpenVINO、NvTensorRtRtx、MIGraphX、VitisAI、QNN。微软[第三方许可说明](https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/third-party-notices)明确：它们有各自的许可，并不随 Windows ML 的许可统一授权。

- [TensorRT for RTX 专有条款](https://docs.nvidia.com/deeplearning/tensorrt-rtx/latest/reference/sla.html)含 NVIDIA GPU 用途及限制使其受开源许可约束等条件；不能据此为所有 GPL 组合放行。需以实际下载版本附带条款为准。
- 微软文档中 OpenVINO 链接的显示名称为商业用途许可，但本次实际返回的是 [Limited Distribution License Agreement v2025.02.12](https://cdrdv2.intel.com/v1/dl/getContent/849090?explicitVersion=true)，包含生产用途限制和 CNDA 条件。**文档标题与返回文件不一致，本次未据此认定所有 OpenVINO 都禁止商用，也未批准当前下载件商用。** 应核对实际 EP 包附带许可或向供应商澄清。
- AMD/Qualcomm 同样需要按实际安装包、版本及适用硬件核实；本次没有完成所有厂商下载组件的逐二进制许可核查。

按需安装并不免除使用时的许可义务。建议把这项作为 Windows 收费发布前的独立合规事项。

### 4. 第三方声明和对应源码交付仍不完整

工程已经复制 FFmpeg、Windows ML、Lyricify、歌词和 RIFE 许可，并在 `Libraries/VideoSr/Licenses` 保留视频模型许可。这是有效的部分工作。

但当前工程/发布脚本未见覆盖全部依赖的统一声明生成机制。抽查本机现存 Release 与 MSIX publish 的 `Licenses` 均只有七份：`FFmpeg.txt`、`Lyricify-Lyrics-Helper.txt`、`Lyrics-NOTICE.txt`、`RIFE.txt`、`RIFE-NOTICE.md`、`WindowsML.txt`、`WindowsML-ThirdPartyNotices.txt`。视频模型许可位于另一目录，不能因此判成丢失。

发布前仍应逐项补验 Avalonia/FluentAvalonia、Serilog、BouncyCastle、ATL/Ude、Inter 字体、Skia/HarfBuzz/ANGLE、Satori 运行时及图片模型的版权/许可。个别声明可能嵌在二进制中，本次没有完整解析所有二进制资源，故不把“Licenses 目录没有文本”直接等同于“所有组件都已违反许可”。

GPL 完整对应源码应与实际收费版本匹配，涵盖应用及所需 GPL 组件的修改、生成/构建脚本；FFmpeg 的版本与哈希清单只是来源证据，不是源码交付本身。FSRCNNX/RAVU 的转换源码在仓库中已有，发行时还需保证接收者可取得与发行件对应的材料。GPL 允许收费的依据可核对[GPL 正文](../../LICENSE)第 4、6、10 节和 [GNU FAQ](https://www.gnu.org/licenses/gpl-faq.en.html)。

## 发布前处理顺序

1. 移除/替换 UltraSharp、LiveAction 的商业集成，或取得覆盖产品分发、商业推理等具体场景的授权。
2. 清理 CHTCHSConv 授权链和跨平台限制；在取得明确证据前不把它作为已批准商用依赖。
3. 核实 GPL FFmpeg 与 Windows ML/实际厂商 EP 的组合授权，确定可执行的发行方案。
4. 保留各组件独立许可，补齐第三方版权声明、模型来源及修改说明；不要把整个包都简单标为同一种许可。
5. 对最终交付件逐文件复核，并准备与发行版本一致的 GPL/LGPL 对应源码及交付方式。

**若目标是保持 GPL 开源并收费，处理以上事项后可以继续推进。若目标是闭源收费，当前 GPL FFmpeg 集成和项目许可还需要另行设计，不能只移除两个 NC 模型就宣布合规。**

## 证据、复现和边界

仓库基准 HEAD：`4e562c10191965b06bcd980ac29a244715821fd9`；核查对象包括当时工作区已有修改，不能把 HEAD 当作全部输入的唯一标识。逐文件摘要保存在 inventory.json。

在仓库根目录复现本机清单采集（脚本和缓存原始证据均不入库）：

```powershell
pwsh -File .qa/license-audit-2026-10-23/collect.ps1
pwsh -File .qa/license-audit-2026-10-23/extract-eula.ps1
Get-Content .qa/license-audit-2026-10-23/inventory.json -Raw
Get-Content .qa/license-audit-2026-10-23/CHTCHSConv-official-EULA.txt
```

官方组件来源：

```powershell
Invoke-WebRequest -Uri 'https://download.microsoft.com/download/5/7/3/57345088-acf8-4e9b-a9a7-eba35452def2/vsintlpack1.zip' -OutFile .qa/license-audit-2026-10-23/vsintlpack1.zip
```

MSI 的 `Control` 表中 `EulaForm/LicenseText` 为 RTF 许可；可通过 Windows Installer COM 的 `OpenDatabase(path, 0)` 只读读取，随后将 RTF 转为文本，不需要执行 MSI。

本次执行了配置/源码/包缓存静态核查、官方许可在线核对、已有发布目录声明抽查及微软原始 EULA 提取。没有运行应用、GPU 推理或编译，也未重新产出/审计最终签名安装包；这些运行测试不能代替许可审核，反之本报告也不是功能烟测。

本报告是工程层面的许可证筛查，并非法律意见。未核清的项目已明确列出；正式收费发行应针对上述具体兼容性问题确认授权。编解码器专利、第三方歌词/音乐服务的商业条款，以及用户素材、训练数据、肖像/商标等其他权利不因软件/模型开源而自动获得，本次未对这些权利作全面清权。
