# 0017：以已发布 Store 商品的真实身份进行许可查询

日期：2026-10-31。

## 背景与约束

用户在已有正式许可的电脑上运行 VS MSIX Release，仍看到购买提示。真实日志反复为 `Checking → LicenseInactive`。检查发现，项目清单与商店关联文件虽然彼此一致，但包名均为 `FancyTool.FancyTool`；Microsoft Store 商品 `9P543GHQQKVK` 实际发布的 PFN 为 `SennpaiStudio.---modbus_z79ft30j24epr`。

包身份是许可边界，不能由项目名、应用显示名或两份本地 XML 一致性推断。ADR 0009 对 `FancyTool.FancyTool` 的历史判断由本决策更正；ADR 0016 的严格授权策略继续有效。

## 决策

- 清单 `Identity/Name` 和 `ProductReservedInfo/MainPackageIdentityName` 改为 `SennpaiStudio.---modbus`；Publisher 保持 `CN=CF259542-70C0-4852-9523-066A80212473`，与商店 PFN 的 publisher ID 一致。显示名称与用户可见品牌不变。
- `StoreAppIdentity` 集中记录已发布的商品 ID、包名与 PFN。MSIX 后端先核对实际 PFN，错误身份返回独立的 `PackageIdentityMismatch`，不向另一商品查询或获取权限。
- 身份错误、验证异常、确实非活跃的许可分开提示。前两者不宣称用户未购买；已购用户无需再次付款。商品购买仅使用当前预期商品，不根据错误包的查询结果购买其他商品。
- 保留结构化日志：实际包名 / PFN、签名类型、开发部署标记、Store 原始 `IsActive` / `IsTrial` / SKU / 到期时间。非活跃时仅首次补查商品关联诊断，不用该诊断结果授予权限。
- 设置页复用同一个 `LicenseViewModel`，显示正式许可、试用及到期时间、独立发行版完全解锁状态；未取得完整授权时显示购买提醒与入口。购买确认、刷新和语言切换均通知绑定更新。

## 替代方案

- VS / 非商店签名自动放行：无法验证真实许可，也违反严格门控要求。
- 仅比较清单与本地 StoreAssociation：两者可以同时写错，无法防止本次问题。
- 用用户是否收藏 / 获取商品代替有效应用许可：与授权有效性不等价，不采用。
- 让已购用户重新付款：身份关联错误不能通过重复购买修复。

## 后果与验证

修正后在同一电脑开发部署实际 Release 包，真实 Store 返回 `IsActive=true`、`IsTrial=false`、`SkuStoreId=9P543GHQQKVK/0010`，应用进入 `FullLicense` 并正常打开全部工具；没有授权覆盖或例外放行。

回归先用公开商店身份检查复现失败，再修复并在普通 / MSIX 两种编译渠道各通过 23/23 项。真实设置页显示「已购买完整版 · 全部功能已解锁」且不显示购买按钮。证据及复现命令见 [验证报告](../license-owned-validation-2026-10-31.md)。

身份修复需要重新部署 MSIX，不能只编译后继续启动旧身份的已注册应用。错误身份与正确身份属于不同包家庭，Windows 可能同时保留两个开发部署入口；不能将旧入口能否启动当作新包验证。本次没有自动迁移或删除两个包家庭的用户数据。

## 来源

- 本应用公开 [Microsoft Store 商品](https://apps.microsoft.com/detail/9P543GHQQKVK)，查询原始响应保存在验证目录。
- Microsoft [包身份说明](https://learn.microsoft.com/windows/apps/desktop/modernize/package-identity-overview)。
- Microsoft [Store 试用与内购测试流程](https://learn.microsoft.com/windows/uwp/monetize/in-app-purchases-and-trials#test-your-in-app-purchase-or-trial-implementation)：正确关联项目并在开发电脑取得本商品许可后，可以调试后续代码，无须重复购买。
