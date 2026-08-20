namespace LittleFancyToolAva.Models.ViewStates;

public class AsymmetricCipherViewState
{
    public string InputText { get; set; } = string.Empty;
    public string OutputText { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public string? SelectedPadding { get; set; }
    public int? SelectedKeyLength { get; set; }
    public string? SelectedKeyFormat { get; set; }
    public string? SelectedMode { get; set; }
}

public class AsymmetricEncryptionViewState
{
    public int AlgorithmIndex { get; set; }
    public Dictionary<string, AsymmetricCipherViewState> AlgoStates { get; set; } = [];
}