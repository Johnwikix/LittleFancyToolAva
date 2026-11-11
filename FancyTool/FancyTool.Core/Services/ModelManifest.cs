namespace FancyToolAva.Services;

/// <summary>Describes a pinned downloadable model and its native output scale.</summary>
public sealed record ModelManifestEntry(string FileName, string SourceUrl, string MirrorUrl, string Sha256, int NativeScale = 4,
    string? BundledRelativePath = null);

public static class ModelManifest
{
    public static IReadOnlyList<ModelManifestEntry> RealEsrgan { get; } = new[]
    {
        new ModelManifestEntry(
            "RealESRGAN_x4plus.onnx",
            "https://huggingface.co/universonic/RealESRGAN/resolve/main/RealESRGAN_x4plus_fp16.onnx",
            "https://hf-mirror.com/universonic/RealESRGAN/resolve/main/RealESRGAN_x4plus_fp16.onnx",
            "30F8DCE72DD67F2F5C492CDEC6FFE1E684833D9F82E3CB1284184710831CD960"),
        new ModelManifestEntry(
            "RealESRGAN_x4plus_anime.onnx",
            "https://huggingface.co/universonic/RealESRGAN/resolve/main/RealESRGAN_x4plus_anime_6B_fp16.onnx",
            "https://hf-mirror.com/universonic/RealESRGAN/resolve/main/RealESRGAN_x4plus_anime_6B_fp16.onnx",
            "38AB81F8F9B5C8B9E03EEAB8BE2F690FE2EE448AC5603174B6DD9B49B6205A24"),
        new ModelManifestEntry(
            "realesr-general-x4v3_fp16.onnx",
            "https://huggingface.co/universonic/RealESRGAN/resolve/main/realesr-general-x4v3_fp16.onnx",
            "https://hf-mirror.com/universonic/RealESRGAN/resolve/main/realesr-general-x4v3_fp16.onnx",
            "CE89B494B6ADAD237792C31D1012D28604BB22D6CD06B8B5903713D4ED636117"),
        new ModelManifestEntry(
            "4x-ClearRealityV1_fp16.onnx",
            "https://huggingface.co/Kim2091/ClearRealityV1/resolve/main/ONNX/fp16/4x-ClearRealityV1-fp16-opset17.onnx",
            "https://hf-mirror.com/Kim2091/ClearRealityV1/resolve/main/ONNX/fp16/4x-ClearRealityV1-fp16-opset17.onnx",
            "5D710126F970A66166553C9D69A6E04ECAA5AF60A343B9D7B1736B407E84961A"),
    };

    /// <summary>
    /// VOSR-1.4B one-step split graphs, in execution order for one 512×512 tile. The set is a
    /// nine-graph chain instead of a single tile-in/tile-out network, so it is pinned here as a
    /// list and orchestrated by <see cref="VosrImagePipeline"/>.
    /// </summary>
    public static IReadOnlyList<ModelManifestEntry> Vosr { get; } = new[]
    {
        Graph("vosr_dinov2l_layer17.onnx", "69A7A3FC7299AEF23098F267E2655F071BF3EE92D2198ECAEBA07F00010FBDAE"),
        Graph("vosr_qwen_vae_encoder.onnx", "7FCF07F071235FDEC61D0D400D44B380B06BC8BA38B5B925CCECB372923CD62D"),
        Graph("vosr_dit_prepare.onnx", "931BD7435E96E2F8D94085C80BDCE51284CC7A3A20ECBC99176ED162C840627C"),
        Graph("vosr_dit_blocks_00_12.onnx", "48375AF3D4AEC844ED20C4525C2232DF2C5202858207D1E1A7DEE1BFBC89F4BC"),
        Graph("vosr_dit_blocks_12_18.onnx", "4B5C098956F1200C053AC2096ED4B0476A055EC8A88A6365CEBE9764CB3891F7"),
        Graph("vosr_dit_blocks_18_24.onnx", "74D7D7786A018E1625224D79A6B142D4AA470608B5406C29FE7721D386A0E2DE"),
        Graph("vosr_dit_blocks_24_36.onnx", "C16F23184B6B42B6921CF2A06D87B5361157487815FF58662C73C3B1376A81A7"),
        Graph("vosr_dit_final.onnx", "A9F9AFA0F6E7D0BA4EDE1CC3ED906B2FEFFC43D942AD5D4BA6D490FD0C9E01F7"),
        Graph("vosr_qwen_vae_decoder.onnx", "50B98B02C4D77FA317010D7A1B15C5CC9F9E7B14BC4FBF69E26C3253242116D8"),
    };

    // 模型编号不再连续，不能将持久化编号直接用作下载清单下标。
    public static ModelManifestEntry GetImageModel(SuperResolutionModel model) => model switch
    {
        SuperResolutionModel.RealEsrganX4Plus => RealEsrgan[0],
        SuperResolutionModel.RealEsrganX4PlusAnime => RealEsrgan[1],
        SuperResolutionModel.RealEsrganGeneralX4V3 => RealEsrgan[2],
        SuperResolutionModel.SpanClearRealityV1 => RealEsrgan[3],
        SuperResolutionModel.Vosr1_4B => throw new NotSupportedException(
            "VOSR is a nine-graph chain; use ModelManifest.Vosr instead of a single entry."),
        _ => throw new ArgumentOutOfRangeException(nameof(model))
    };

    private static ModelManifestEntry Graph(string fileName, string sha256) => new(
        fileName,
        "https://huggingface.co/AllGPTORG/VOSR_1.4B_Mobile/resolve/main/onnx/" + fileName,
        "https://hf-mirror.com/AllGPTORG/VOSR_1.4B_Mobile/resolve/main/onnx/" + fileName,
        sha256,
        NativeScale: 1);
}
