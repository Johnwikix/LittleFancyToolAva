# Satori 运行时的包选择必须与发布 RID 一致

2026-10-22。补充 ADR 0009 的 MSIX 打包说明，记录 Satori GC SDK 引入后 MSIX 打包报错的根因与修复。

`PublishWithSatoriGC` SDK（`10.0.13-satori.38707290103.1`）在 `Sdk/Sdk.targets` 里按 `PublishAot` 决定引用哪一组包：`PublishAot != true` 时引用自包含托管发布用的 `PublishWithSatoriGC.Runtime.$(RuntimeIdentifier)`，`PublishAot == true` 时引用 AOT 用的 `PublishWithSatoriGC.AotSdk.$(RuntimeIdentifier)`，另外按宿主 RID 引用 `PublishWithSatoriGC.AotTools.$(SatoriHostRuntimeIdentifier)`。发布阶段 `Sdk/PublishWithSatoriGC.targets` 的 `_ResolveSatoriPaths` 从 NuGet 资产导入的 `SatoriRuntimePack` / `SatoriAotSdkPack` 项里按 `$(RuntimeIdentifier)` 取运行时路径，取不到就报 `Restore the Satori Runtime pack with the same RID used for publish.`。

MSIX 打包与普通发布有两处不一致。其一，MSIX 打包要求托管 apphost，`FancyToolMsixBuild=true` 会把 `PublishAot` 置为 `false`；但该开关只经 `ProjectReference` 的 `AdditionalProperties` 传给入口工程，DesktopBridge 自行还原入口工程时并不带这个属性，还原仍按 `PublishAot=true` 进行，资产里只有 AotSdk 包。随后发布阶段按 `PublishAot=false` 去找 Runtime 包，必然取不到。其二，入口工程同时声明了 `RuntimeIdentifiers`（`win-x64;linux-x64`）与 `RuntimeIdentifier`（`win-x64`），SDK 用 `"$(RuntimeIdentifier);$(RuntimeIdentifiers)"` 组合 RID 列表，而其 `KeepDuplicates="false"` 对展开的项列表不生效，同一个 `PackageReference` 被写入两次，每次还原都报 NU1504。

修复放在 `FancyTool/FancyToolAva.csproj`：

- 去掉 `RuntimeIdentifiers`，只保留单个 `RuntimeIdentifier`（Windows 默认 `win-x64`，其余平台留空、发布时由脚本传入），使 SDK 的 RID 列表不再重叠，NU1504 消失。Linux 交叉发布仍用 `dotnet restore -p:RuntimeIdentifier=linux-x64` 拉取对应 RID 的包。
- 在 `PublishAot=true` 时显式补一条 `PublishWithSatoriGC.Runtime.$(RuntimeIdentifier)` 包引用，版本取 SDK 的 `$(SatoriPackageVersion)`。包引用写在工程文件里，因此无论还原由谁触发、用什么属性触发，资产里都带得上与发布 RID 一致的 Satori 运行时；`PublishAot=false` 时该引用不参与，由 SDK 自己添加，不会重复声明。

未采用的做法：一是在 MSIX 打包里关闭 `UseSatoriGC`，让 MSIX 载荷改用原版 CoreCLR——这会让 MSIX 包与安装版运行时不一致，偏离项目「自包含托管发布 + Satori GC」的形态；二是让入口工程的 `PublishAot` 默认取 `false`、由发布脚本再打开 AOT——这会改变安装版与 IDE 构建的既有行为，超出本次打包修复的范围；三是把 DesktopBridge 内部使用的中间目录（`obj/wappublish/<rid>`）写进工程文件硬编码还原路径——那是对打包工具内部实现的猜测，工具换版本即失效。

已验证（Windows 11，.NET SDK 10.0.402，VS 18 的 DesktopBridge 本机不可用）：修复前用「按 `PublishAot=true` 还原 + 按 `PublishAot=false` 发布」复现出完全相同的 `Restore the Satori Runtime pack with the same RID used for publish.` 与 NU1504 重复项告警；修复后同一场景还原与发布均退出 0，托管发布产物中的 `coreclr.dll` 与 Satori Runtime 包内的文件哈希一致（`5ad003ea72a8022be856a4c7…`），即 MSIX 载荷携带的是 Satori 运行时。AOT 发布（`PublishAot=true`）产物不变：仍是原生 `FancyToolAva.exe`、无托管 DLL、未混入 CoreCLR/Satori 运行时文件。Debug 构建、`-r win-x64` 与 `-r linux-x64` 还原均无 NU1504。

已知限制：本机没有 Visual Studio 与 DesktopBridge（`Microsoft.DesktopBridge.props` 不存在），MSIX 打包的收尾步骤（生成并签名 .msix/.msixbundle）无法在此复现验证，上述结论覆盖到「入口工程按 MSIX 形态发布」为止，最后一步仍需在装有 VS 的机器上跑一次真实打包确认。
