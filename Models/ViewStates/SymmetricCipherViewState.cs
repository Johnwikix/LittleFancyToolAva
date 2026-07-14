namespace LittleFancyToolAva.Models.ViewStates;

public class SymmetricCipherViewState
{
    public string InputText { get; set; } = string.Empty;
    public int PaddingIndex { get; set; }
    public int EncryptModeIndex { get; set; }
    public int OutputTypeIndex { get; set; }
    public int KeyIvTypeIndex { get; set; }
}

public class AesViewState : SymmetricCipherViewState
{
    public int KeyLengthIndex { get; set; }
}
