# 已购许可误锁与设置页授权状态验证

日期：2026-10-31。决策见 [ADR 0017](adr/0017-store-package-identity.md)。

## 实际问题与修复

Windows 11 x64（10.0.26100 系列）、.NET SDK 10.0.402、VS Community 2026 的 MSBuild 位于 `I:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`。实际场景为 Visual Studio MSIX Release 开发部署，不是直接启动不带包身份的 exe。

旧部署 PFN 为 `FancyTool.FancyTool_z79ft30j24epr`，日志持续 `LicenseInactive`。通过 Microsoft Store 公开商品接口确认，`9P543GHQQKVK` 的 PFN 为 `SennpaiStudio.---modbus_z79ft30j24epr`。新增比对回归在修复前失败，排除了「两份本地身份文件一致即正确」这一错误假设。

修正清单、商店关联与运行期身份校验后，实际 Release MSIX 开发部署返回：

```text
Store 包身份 SennpaiStudio.---modbus
PFN SennpaiStudio.---modbus_z79ft30j24epr
SignatureKind=None, IsDevelopmentMode=true
IsActive=true, IsTrial=false, SkuStoreId=9P543GHQQKVK/0010
Checking → FullLicense
```

随后实际构造工具依赖、完成模型文件就绪检查和 FFmpeg 探测。lvt 经 UIA 导航设置页，确认「已购买完整版 · 全部功能已解锁」及购买入口隐藏；截图已目视检查新增区域，无资源键直出或文字截断。整个流程没有点击购买、授权覆盖或非商店签名放行。

## 可复现命令

从仓库根目录执行：

```powershell
# 公开目录，仅用于身份核对，不作为授权放行来源
Invoke-RestMethod 'https://storeedgefd.dsx.mp.microsoft.com/v9.0/products/9P543GHQQKVK?market=US&locale=en-US&deviceFamily=Windows.Desktop'

dotnet run --project FancyTool/_tools/LicenseSmokeTest -c Debug -- .qa/license-owned-2026-10-31/non-msix
dotnet run --project FancyTool/_tools/LicenseSmokeTest -c Release -p:FancyToolMsixBuild=true -- .qa/license-owned-2026-10-31/msix --expect-msix
dotnet build FancyTool/FancyToolAva.csproj -c Release -r linux-x64 -p:SelfContained=true -p:PublishAot=false

& 'I:/Program Files/Microsoft Visual Studio/18/Community/MSBuild/Current/Bin/MSBuild.exe' `
  FancyToolAva.Msix/FancyToolAva.Msix.wapproj -nologo -m:1 `
  -p:Configuration=Release -p:Platform=x64 -p:BuildingInsideVisualStudio=true `
  -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never
```

普通 / MSIX 配置各 23/23 通过，Linux 交叉编译通过，真实 DesktopBridge Release MSIX 生成成功。新增覆盖发布身份、错误身份拒绝且不显示未购提示、设置页试用 / 完整版 / 独立版状态和绑定通知；其余严格门控回归继续通过。

实际测试从生成的 MSIX 解包独立布局，并以 `Add-AppxPackage -Register <布局>/AppxManifest.xml` 注册开发包，然后通过 `IApplicationActivationManager.ActivateApplication` 启动正确 AUMID `SennpaiStudio.---modbus_z79ft30j24epr!App`。注册前确认本机没有相同正确身份的已安装包，没有覆盖已有 Store 安装，也没有卸载旧错误身份包。

测试结束后正常关闭测试窗口，仅撤销本轮新增的独立布局注册，使用 `Remove-AppxPackage -PreserveApplicationData` 保留应用数据。既有错误身份开发包未删除；后续请由 VS 重新部署正确身份的项目，避免继续启动旧入口。

VS 的 Build / 生成 MSIX 与 Deploy / 重新注册是不同步骤。此机只 Build 后，已有 `AppX` 注册目录仍可能指向旧部署程序集；必须重新部署并核对进程包身份。安装包归档中的清单、DLL 与构建输出已分别核对。

## 原始证据与边界

证据位于仓库根目录 [.qa/license-owned-2026-10-31/](../../.qa/license-owned-2026-10-31/)：

- `before-app.log`、`reproduced-app.log`：旧身份的真实失败记录。
- `public-store-product.json`：公开 Store 商品身份响应。
- `regression-before.log`、`before-fix/results.json`：修复前身份回归失败。
- `non-msix/results.json`、`msix/results.json`：最终回归结果。
- `msix-fixed-package.log`、`corrected-package.json`：真实打包日志、清单身份与包 / DLL SHA-256。
- `corrected-registration.json`、`corrected-app.log`：实际开发部署身份及真实正式许可结果。
- `settings-full-license-ui.json`、`settings-full-license.png`：真实设置页 UIA 与截图。
- `static-check.json`、`linux-build.log`：双语言、CRLF、包身份静态检查与跨平台构建。

本轮已验证真实已购正式许可；未新购商品、未发放试用、未实测真实试用到期或退款撤权。试用状态展示与购买完成通知由生产代码加可控边界回归验证，不能替代另一真实账户的试用全流程。本轮没有运行 GPU 推理或转码任务，不给出性能结论。
