namespace LittleFancyToolAva.Models.ViewStates;

public class HashViewState
{
    public string InputText { get; set; } = string.Empty;
    public int CaseIndex { get; set; }
}

public class Md5ViewState : HashViewState
{
    public int OutputLengthIndex { get; set; }
}

public class ShaViewState : HashViewState
{
    public int ModeIndex { get; set; }
}
