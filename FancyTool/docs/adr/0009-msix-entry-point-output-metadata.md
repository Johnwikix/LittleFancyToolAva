# MSIX entry point output metadata

On 2026-10-16, the packaging project reported that `FancyToolAva.exe` was missing
from project outputs. Metadata-only evaluation reproduced a concrete mismatch:
DesktopBridge's `AddProjectMetadata` reported `bin/x64/Release/...` and `obj/`,
while its reference's `FancyToolMsixBuild=true` evaluated the app with
`bin/msix/x64/Release/...` and `obj/msix/`. `SetPublishProperties` then calculated
`PublishDir` from the ordinary output path, mixing the two evaluations.

The app now exposes `GetFancyToolMsixMetadata`, which returns its actual evaluated
output and intermediate paths without compiling or publishing. The packaging
project calls this query with the original reference properties after
`SetProjectReferenceProperties`, updates both reference metadata collections,
and replaces the generated publish directory with the isolated app's
`TargetDir/msixpublish/`. Reference consumers now agree on the same output root.

For MSIX only, the app sets `PublishAot=false`, `UseAppHost=true`,
`PublishSingleFile=false`, `PublishTrimmed=false`, and `SelfContained=true`.
This supplies a managed executable for the DesktopBridge output groups during
ordinary IDE Build/deploy, which cannot rely on the native AOT executable that
is produced only by Publish. Other publishing modes retain their existing AOT
configuration. The manifest entry point name is unchanged.

The before/after check used VS 18 MSBuild with `DesignTimeBuild=true` and only
`AssignProjectConfiguration;SetProjectReferenceProperties`, followed by
`-getItem:ProjectReferenceWithExtraMetadata,ProjectReferenceWithPublishProps`.
After the change, output, target, publish, and NuGet extension paths all resolve
under the isolated roots. A separate `dotnet msbuild -getProperty:...` evaluation
confirmed the MSIX apphost settings. No Restore, Build, Publish, or package
generation was run for this fix, following the user's no-build requirement.

This confirms the metadata mismatch is corrected; a full MSIX build remains
necessary to confirm the final package generation succeeds.
