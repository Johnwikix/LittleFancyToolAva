namespace LittleFancyToolAva.Models.ViewStates;

public class SymmetricCipherViewState
{
    public string InputText { get; set; } = string.Empty;
    public string OutputText { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string Iv { get; set; } = string.Empty;
    public int PaddingIndex { get; set; }
    public int EncryptModeIndex { get; set; }
    public int OutputTypeIndex { get; set; }
    public int KeyIvTypeIndex { get; set; }
    public int KeyLengthIndex { get; set; }
}

public class SymmetricEncryptionViewState
{
    public int AlgorithmIndex { get; set; }
    public Dictionary<string, SymmetricCipherViewState> AlgoStates { get; set; } = [];
}