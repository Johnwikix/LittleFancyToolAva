namespace LittleFancyToolAva.Models.ViewStates;

public class AsymmetricCipherViewState
{
    public string InputText { get; set; } = string.Empty;
    public string OutputText { get; set; } = string.Empty;
    public string PublicKey { get; set; } = string.Empty;
    public string PrivateKey { get; set; } = string.Empty;
    public int PaddingIndex { get; set; }
    public int KeyLengthIndex { get; set; }
    public int KeyFormatIndex { get; set; }
    public int ModeIndex { get; set; }
}

public class AsymmetricEncryptionViewState
{
    public int AlgorithmIndex { get; set; }
    public Dictionary<string, AsymmetricCipherViewState> AlgoStates { get; set; } = [];
}