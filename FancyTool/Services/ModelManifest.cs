namespace FancyToolAva.Services;

/// <summary>Describes a pinned downloadable model and its native output scale.</summary>
public sealed record ModelManifestEntry(string FileName, string SourceUrl, string MirrorUrl, string Sha256, int NativeScale = 4);

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
            "4x-UltraSharp_fp16.onnx",
            "https://huggingface.co/Kim2091/UltraSharp/resolve/main/ONNX/4x-UltraSharp-fp16-opset17.onnx",
            "https://hf-mirror.com/Kim2091/UltraSharp/resolve/main/ONNX/4x-UltraSharp-fp16-opset17.onnx",
            "7295B39B71F1D5882FEC1AE02F55227F7CA6516F92EAE6920AB2A28A39CADE73"),
        new ModelManifestEntry(
            "4x-ClearRealityV1_fp16.onnx",
            "https://huggingface.co/Kim2091/ClearRealityV1/resolve/main/ONNX/fp16/4x-ClearRealityV1-fp16-opset17.onnx",
            "https://hf-mirror.com/Kim2091/ClearRealityV1/resolve/main/ONNX/fp16/4x-ClearRealityV1-fp16-opset17.onnx",
            "5D710126F970A66166553C9D69A6E04ECAA5AF60A343B9D7B1736B407E84961A"),
        new ModelManifestEntry(
            "2xLiveActionV1_SPAN.onnx",
            "https://raw.githubusercontent.com/jcj83429/upscaling/9332e7d5b07747ff347e5abdc43f8144364de9f7/2xLiveActionV1_SPAN/2xLiveActionV1_SPAN_490000.onnx",
            "https://raw.githubusercontent.com/jcj83429/upscaling/9332e7d5b07747ff347e5abdc43f8144364de9f7/2xLiveActionV1_SPAN/2xLiveActionV1_SPAN_490000.onnx",
            "BFA72F3C6347076AED140D0836CEE30C27EA434C047BEEAF9466469483836ECC", 2),
    };
    /// <summary>Gets the complete, SHA-256 pinned AudioSR basic FP32 ONNX pack, including external weights.</summary>
    public static IReadOnlyList<ModelManifestEntry> AudioSr { get; } = new[]
    {
        new ModelManifestEntry("alphas_cumprod.npy",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/alphas_cumprod.npy",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/alphas_cumprod.npy",
            "94067713977EED99200702404E66AE664A3ABAEEE5664E902EC74700871EEB2E", 1),
        new ModelManifestEntry("ddpm.onnx",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/ddpm.onnx",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/ddpm.onnx",
            "E628B9AF14425806EF3B5899DDA910BBE414A46D12E46989A290ACDFDE60A81A", 1),
        new ModelManifestEntry("ddpm.onnx.data",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/ddpm.onnx.data",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/ddpm.onnx.data",
            "290CEE7BC4123C0C59244DC2D2D437E034D0F5DD085A26E2B2739B739FA90971", 1),
        new ModelManifestEntry("manifest.json",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/manifest.json",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/manifest.json",
            "21452B97F89BB653207F294C88C95526A202B3AFCDE559D9B02CACAA31559898", 1),
        new ModelManifestEntry("mel_basis.npy",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/mel_basis.npy",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/mel_basis.npy",
            "AAB24278E3879DC523839E64A03C63538A01EC6A585C4461CD3AE7D06F21525B", 1),
        new ModelManifestEntry("vae_decoder.onnx",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/vae_decoder.onnx",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/vae_decoder.onnx",
            "466099B7CBBC888F82658BE165865C4F7406CDCFF7D88677979CD9FCBE43FCF1", 1),
        new ModelManifestEntry("vae_feature_extract.onnx",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/vae_feature_extract.onnx",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/vae_feature_extract.onnx",
            "F84279CFA0ED31A025B3A3BF9E0FB725D87A91A88D12C30D3B2E05B6EC604949", 1),
        new ModelManifestEntry("vocoder.onnx",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/vocoder.onnx",
            "https://github.com/santiquiroz/port-audiosr-onnx/releases/download/models-v1.0/vocoder.onnx",
            "85B65BCF83F85BA05361100EBA0FCC5A557654A7CB93D4317C23CFEC0F4BC327", 1),
    };
}
