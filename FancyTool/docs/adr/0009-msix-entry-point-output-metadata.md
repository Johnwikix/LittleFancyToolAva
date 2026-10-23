# Build the isolated MSIX application and its dependencies

On 2026-10-16 an actual Debug/x64 build with
`BuildingInsideVisualStudio=true` reproduced the missing
`EntryPointExe FancyToolAva.exe` error at DesktopBridge's package file collection.
Visual Studio builds the solution's ordinary app evaluation, while the MSIX
reference uses `FancyToolMsixBuild=true` and separate `bin/msix` / `obj/msix`
directories. DesktopBridge normally skips building its dependent projects inside
the IDE. Consequently the isolated apphost may never have been generated.
Metadata-only path checks cannot prove the required executable exists.

The packaging project now sets `WapAlwaysBuildDependentProjects=true`. Its app
reference additionally sets `BuildingInsideVisualStudio=false` and
`BuildProjectReferences=true`: the isolated publish must build its own complete
dependency graph, including Lyricify, instead of relying on the IDE's ordinary
dependency outputs. Merely forcing the app's publish without its dependencies
produced CS0006 for the isolated Lyricify reference assembly. Both flags are
scoped to the MSIX reference. The existing managed apphost configuration remains
in place. No custom output-metadata rewriting target is needed.

Actual Release package generation also exposed two independent packaging
errors. Package `Properties/DisplayName` must match a reserved name in the
repository's StoreAssociation file; it now uses the first reserved name. The
Start-menu `VisualElements/DisplayName` is unchanged. Temporary store certificate
generation now follows `AppxPackageSigningEnabled`, avoiding a cleanup task with
an empty certificate thumbprint for the configured unsigned build.

Validation used VS 18's full-framework MSBuild, the stable .NET 10 SDK, and the
real Build/Publish/package targets:

```powershell
& 'C:/Program Files/Microsoft Visual Studio/18/Professional/MSBuild/Current/Bin/MSBuild.exe' `
  FancyToolAva.Msix/FancyToolAva.Msix.wapproj -nologo -m:1 `
  -p:Configuration=Debug -p:Platform=x64 -p:BuildingInsideVisualStudio=true

& 'C:/Program Files/Microsoft Visual Studio/18/Professional/MSBuild/Current/Bin/MSBuild.exe' `
  FancyToolAva.Msix/FancyToolAva.Msix.wapproj -restore -nologo -m:1 `
  -p:Configuration=Release -p:Platform=x64
```

Both builds exited 0. The Release build generated unsigned MSIX, MSIXBundle, and
store-upload artifacts. Archive inspection resolved the manifest executable to
`FancyToolAva/FancyToolAva.exe`, checked its PE signature, and matched the SHA-256
of both the packaged executable and main DLL against the actual isolated build.
The six FFmpeg runtime DLLs and runtimeconfig are present. The bundle's embedded
MSIX hash matches the inspected standalone MSIX. No package was installed.

已入库的验证摘要已迁至本地 [verification.json](../../../.qa/git-history-cleanup-2026-10-17/evidence/msix-build-2026-10-16/verification.json)。
历史 IDE 失败、构建成功和 Release 打包日志及原始 binlog 保留在原验证机器的 `.qa/msix-fix-2026-10-16/`；这些日志从未入库，本机没有副本。

## 2026-10-22 补充：签名打包会启用商店保留名校验

前文「`Properties/DisplayName` 取第一条保留名」不是可有可无的样式选择，而是签名打包的硬性约束。把 `AppxPackageSigningEnabled` 打开、`PackageCertificateThumbprint` 指向真实证书后，`Microsoft.Build.AppxPackage` 会拿 `Package.StoreAssociation.xml` 里 `ProductReservedInfo/ReservedNames` 的名字校验清单，`Properties/DisplayName` 不在其中就报「Properties 元素的 DisplayName 元素必须具有应用程序名称值」并列出全部候选名。未签名打包不触发该校验，所以此前用 `FancyTool` 也能出包。

据此恢复三条约束：`Properties/DisplayName` 取第一条保留名（`妙妙小工具---串口、modbus、网络调试、图像转换、文本文件加密解密工具`，与 1.1.0.0 成功打包时一致）；清单 `Identity/Publisher` 与 `Package.StoreAssociation.xml` 的 `Publisher` 统一为 `CN=CF259542-70C0-4852-9523-066A80212473`（这些保留名正是在该 DN 名下预留的，占位 DN 会让商店侧认不出）；`Identity/Name` 与 `MainPackageIdentityName` 同为 `FancyTool.FancyTool`。

开始菜单与任务栏显示的是 `uap:VisualElements/DisplayName`（`Fancy Tool`），不受此约束影响；`Properties/DisplayName` 只出现在商店列表和「设置 - 应用」里，因此可以保持为较长的商店保留名。`PublisherDisplayName` 未列入校验范围，与 `Package.StoreAssociation.xml` 同为 `FancyTool`。

本次只做了静态校验（本机无 Visual Studio 与 DesktopBridge，无法真实打包）：清单与商店关联文件的 Identity 名、Publisher、保留名包含关系均已用脚本逐项比对通过，XML 可解析、UTF-8 BOM 与 CRLF 保持不变；真实签名打包仍需在装有 VS 的机器上跑一次确认。
