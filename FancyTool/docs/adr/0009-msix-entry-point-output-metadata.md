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

Evidence is in [verification.json](../data/msix-build-2026-10-16/verification.json),
[reproduced IDE failure](../data/msix-build-2026-10-16/ide-debug-before.log),
[successful IDE build](../data/msix-build-2026-10-16/ide-debug-after.log), and
[successful Release packaging](../data/msix-build-2026-10-16/package-release-final.log).
Raw binlogs and intermediate failure logs remain in `.qa/msix-fix-2026-10-16`.
