namespace LittleFancyToolAva.Models.ViewStates;

public class HashViewState
{
    public string InputText { get; set; } = string.Empty;
    public string OutputText { get; set; } = string.Empty;
    public int CaseIndex { get; set; }
    public string? SelectedMode { get; set; }
    public int? SelectedOutputLength { get; set; }
}

public class HashEncryptionViewState
{
    public int AlgorithmIndex { get; set; }
    public Dictionary<string, HashViewState> AlgoStates { get; set; } = [];
}