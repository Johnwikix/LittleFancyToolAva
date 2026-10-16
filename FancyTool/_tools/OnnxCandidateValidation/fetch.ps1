param([string]$Work = '.qa/onnx-candidates')
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force "$Work/upstream", "$Work/weights" | Out-Null
$sources = @(
    @('HWEnc-onnx-models', 'rigaya/HWEnc-onnx-models', 'master', '909fc391b5584702892081adb98f93ea2db16e73', $null),
    @('Anime4K', 'bloc97/Anime4K', 'master', '7684e9586f8dcc738af08a1cdceb024cc184f426', 'glsl'),
    @('mpv-prescalers', 'bjin/mpv-prescalers', 'source', '6b9ba842a05a113a75e5a2ac3d0a7f00f21ffbae', 'weights'),
    @('BasicSR', 'XPixelGroup/BasicSR', 'v1.4.2', '651835a1b9d38dbbdaf45750f56906be2364f01a', 'basicsr/archs')
)
foreach ($source in $sources) {
    $target = "$Work/upstream/$($source[0])"
    if (!(Test-Path -LiteralPath $target)) {
        git clone --depth 1 --branch $source[2] --filter=blob:none --sparse "https://github.com/$($source[1])" $target
        if ($LASTEXITCODE -ne 0) { throw "Clone failed: $($source[0])" }
    }
    if ((git -C $target rev-parse HEAD) -ne $source[3]) {
        git -C $target fetch --depth 1 origin $source[3]
        if ($LASTEXITCODE -ne 0) { throw "Fetch failed: $($source[0])" }
        git -C $target checkout --detach $source[3]
        if ($LASTEXITCODE -ne 0) { throw "Checkout failed: $($source[0])" }
    }
    if ($source[4]) { git -C $target sparse-checkout set $source[4] }
}
$downloads = @(
    @('FSRCNNX.p', 'https://raw.githubusercontent.com/TianZerL/Anime4KCPP/fe187e020c8e57c6885511344a8705c292a92612/core/internal/AC/Core/Internal/Model/Param/FSRCNNX.p', '5f720088ade86818acb137e5bc134a910fadce96fb0ef4dccd2c9f7f0e4e2509'),
    @('edvrm_x4.pth', 'https://download.openmmlab.com/mmediting/restorers/edvr/edvrm_x4_8x4_600k_reds_20210625-e29b71b5.pth', 'e29b71b5eae845da91be7fa797cbd2e0a1531c4db9da888869f697f81e0f1c51')
)
foreach ($item in $downloads) {
    $path = "$Work/weights/$($item[0])"
    if (!(Test-Path -LiteralPath $path)) { Invoke-WebRequest -Uri $item[1] -OutFile $path }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $item[2]) { throw "SHA-256 mismatch: $path" }
}
'fe187e020c8e57c6885511344a8705c292a92612' | Set-Content "$Work/weights/anime4kcpp-commit.txt"
