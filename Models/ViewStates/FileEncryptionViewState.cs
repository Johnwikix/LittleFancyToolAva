namespace LittleFancyToolAva.Models.ViewStates;

public class FileEncryptionViewState
{
    public int KeyLengthIndex { get; set; }
    public int KeyIvTypeIndex { get; set; }
    public string? OutputDirectory { get; set; }
}