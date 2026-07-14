namespace LittleFancyToolAva.Models.ViewStates;

public class AsymmetricCipherViewState
{
    public string InputText { get; set; } = string.Empty;
}

public class RsaViewState : AsymmetricCipherViewState
{
    public int PaddingIndex { get; set; }
    public int KeyLengthIndex { get; set; }
    public int KeyFormatIndex { get; set; }
}

public class Sm2ViewState : AsymmetricCipherViewState
{
    public int ModeIndex { get; set; }
}
