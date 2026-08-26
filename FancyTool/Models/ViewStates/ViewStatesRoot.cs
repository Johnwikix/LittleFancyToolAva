using System.Text.Json.Serialization;

namespace FancyToolAva.Models.ViewStates;

public class ViewStatesRoot
{
    public SymmetricEncryptionViewState? SymmetricView { get; set; }
    public AsymmetricEncryptionViewState? AsymmetricView { get; set; }
    public HashEncryptionViewState? HashView { get; set; }
    public Base64ViewState? Base64View { get; set; }
    public SerialPortViewState? SerialPortView { get; set; }
    public TcpServerViewState? TcpServerView { get; set; }
    public UdpViewState? UdpView { get; set; }
    public FileEncryptionViewState? FileEncryptionView { get; set; }
    public FolderCompareViewState? FolderCompareView { get; set; }
    public ImageConvertViewState? ImageConvertView { get; set; }
    public VideoTranscodeViewState? VideoTranscodeView { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, MaxDepth = 64)]
[JsonSerializable(typeof(ViewStatesRoot))]
[JsonSerializable(typeof(SymmetricCipherViewState))]
[JsonSerializable(typeof(SymmetricEncryptionViewState))]
[JsonSerializable(typeof(AsymmetricCipherViewState))]
[JsonSerializable(typeof(AsymmetricEncryptionViewState))]
[JsonSerializable(typeof(HashViewState))]
[JsonSerializable(typeof(HashEncryptionViewState))]
[JsonSerializable(typeof(Base64ViewState))]
[JsonSerializable(typeof(SerialPortViewState))]
[JsonSerializable(typeof(TcpServerViewState))]
[JsonSerializable(typeof(UdpViewState))]
[JsonSerializable(typeof(FileEncryptionViewState))]
[JsonSerializable(typeof(KeyIvDto))]
[JsonSerializable(typeof(FolderCompareViewState))]
[JsonSerializable(typeof(ImageConvertViewState))]
[JsonSerializable(typeof(VideoTranscodeViewState))]
internal partial class ViewStatesJsonContext : JsonSerializerContext
{
}