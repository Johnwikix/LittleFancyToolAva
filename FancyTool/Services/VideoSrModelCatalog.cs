namespace FancyToolAva.Services;

/// <summary>Identifies GPU-only video enhancement models independently of image models.</summary>
public enum VideoSrModel
{
    /// <summary>Uses the small Anime4K network.</summary>
    Anime4KCnnS,
    /// <summary>Uses the medium Anime4K network.</summary>
    Anime4KCnnM,
    /// <summary>Uses the small FSRCNNX network.</summary>
    FsrcnnxS,
    /// <summary>Uses the medium FSRCNNX network.</summary>
    FsrcnnxM,
    /// <summary>Uses the radius-two RAVU Lite filter.</summary>
    RavuLiteR2,
    /// <summary>Uses the radius-three RAVU Lite filter.</summary>
    RavuLiteR3
}

/// <summary>Describes a packaged video model and its validated precision and provider policy.</summary>
public sealed record VideoSrModelDefinition(VideoSrModel Model, string LabelKey, ModelManifestEntry FullPrecision,
    ModelManifestEntry? HalfPrecision = null, bool RequiresDirectMl = false, bool RequiresVendorGpu = false)
{
    /// <summary>Gets whether this model has a validated FP16 graph.</summary>
    public bool SupportsFp16 => HalfPrecision != null;

    /// <summary>Gets the requested precision, rejecting unsupported combinations.</summary>
    public ModelManifestEntry GetGraph(bool useFp16) => useFp16
        ? HalfPrecision ?? throw new NotSupportedException(LocalizationRegistry.Get("VideoTranscode.Sr_Fp16Unavailable"))
        : FullPrecision;
}

/// <summary>Pins the packaged video graphs, hashes and GPU-only execution restrictions.</summary>
public static class VideoSrModelCatalog
{
    /// <summary>Gets all supported models in stable selector order.</summary>
    public static IReadOnlyList<VideoSrModelDefinition> Models { get; } = Array.AsReadOnly(new[]
    {
        new VideoSrModelDefinition(VideoSrModel.Anime4KCnnS, "VideoTranscode.Sr_Anime4KS",
            Graph("anime4k_upscale_cnn_s_fp32", "8f923b5b71b4205a8c1904ac5928d064e1f6cded12e01a7afad84392b9f05a90"),
            Graph("anime4k_upscale_cnn_s_fp16", "5f55b0e06dc4c679ed4412d2aba9c1fda803be4bd568aef64f1077d7b0cc0507"), RequiresVendorGpu: true),
        new VideoSrModelDefinition(VideoSrModel.Anime4KCnnM, "VideoTranscode.Sr_Anime4KM",
            Graph("anime4k_upscale_cnn_m_fp32", "7bdf434d50fe2af2c0a68dafe79c77cec6275591c108e75b34fedb2471c40062"),
            Graph("anime4k_upscale_cnn_m_fp16", "65872434c5d54a5eb46004f1c6d0ce24a48cc13dbef479cdefbc194dbbdfe62b"), RequiresVendorGpu: true),
        new VideoSrModelDefinition(VideoSrModel.FsrcnnxS, "VideoTranscode.Sr_FsrcnnxS",
            Graph("fsrcnnx_s_fp32", "2c08452a23665a9e072fafe335415d6fbf2b90311d18fcce08add27830b6e379"),
            Graph("fsrcnnx_s_fp16", "420d4e0790ab5a7c527a41957762d434c0493baa1b9a43f823a327e84691943b"), RequiresVendorGpu: true),
        new VideoSrModelDefinition(VideoSrModel.FsrcnnxM, "VideoTranscode.Sr_FsrcnnxM",
            Graph("fsrcnnx_m_fp32", "54fd3900b4a9dca3247764b0ec4c87e6e689ad5297e14afa3ba47e7710a3f361"),
            Graph("fsrcnnx_m_fp16", "1d0d075a145199b631d38d90a558c4e32026bc683be1a83557b168931dda7c43"), RequiresVendorGpu: true),
        new VideoSrModelDefinition(VideoSrModel.RavuLiteR2, "VideoTranscode.Sr_RavuR2",
            Graph("ravu_lite_r2_fp32", "15a6ba5be0ba3703a397837827725809be4f95793084ca483ac32a2a43d1dbf1"), RequiresDirectMl: true),
        new VideoSrModelDefinition(VideoSrModel.RavuLiteR3, "VideoTranscode.Sr_RavuR3",
            Graph("ravu_lite_r3_fp32", "a9c4237ee7c793ef6c5d7636dea71936603a718229f8f2c581bd032a76295c8e"), RequiresVendorGpu: true)
    });

    /// <summary>Gets a supported definition, rejecting unknown persisted numeric values.</summary>
    public static VideoSrModelDefinition Get(VideoSrModel model) => Models.FirstOrDefault(entry => entry.Model == model)
        ?? throw new ArgumentOutOfRangeException(nameof(model));

    private static ModelManifestEntry Graph(string name, string sha256) => new(name + ".onnx", "", "", sha256, 2,
        "Libraries/VideoSr/" + name + ".onnx");
}
