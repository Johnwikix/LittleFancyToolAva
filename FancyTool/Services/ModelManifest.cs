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

    // 模型编号不再连续，不能将持久化编号直接用作下载清单下标。
    public static ModelManifestEntry GetImageModel(SuperResolutionModel model) => model switch
    {
        SuperResolutionModel.RealEsrganX4Plus => RealEsrgan[0],
        SuperResolutionModel.RealEsrganX4PlusAnime => RealEsrgan[1],
        SuperResolutionModel.RealEsrganGeneralX4V3 => RealEsrgan[2],
        SuperResolutionModel.SpanClearRealityV1 => RealEsrgan[3],
        _ => throw new ArgumentOutOfRangeException(nameof(model))
    };
}
