using System.Text.Json.Serialization;

namespace LittleFancyToolAva.Models.ViewStates;

public class ViewStatesRoot
{
    public SymmetricCipherViewState? DesView { get; set; }
    public SymmetricCipherViewState? Sm4View { get; set; }
    public AesViewState? AesView { get; set; }
    public AsymmetricCipherViewState? RsaView { get; set; }
    public Sm2ViewState? Sm2View { get; set; }
    public HashViewState? Sm3View { get; set; }
    public Md5ViewState? Md5View { get; set; }
    public ShaViewState? ShaView { get; set; }
    public Base64ViewState? Base64View { get; set; }
    public SerialPortViewState? SerialPortView { get; set; }
    public TcpServerViewState? TcpServerView { get; set; }
    public UdpViewState? UdpView { get; set; }
    public FileEncryptionViewState? FileEncryptionView { get; set; }
    public FolderCompareViewState? FolderCompareView { get; set; }
    public Img2icoViewState? Img2icoView { get; set; }
    public ImgConvertViewState? ImgConvertView { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ViewStatesRoot))]
[JsonSerializable(typeof(SymmetricCipherViewState))]
[JsonSerializable(typeof(AesViewState))]
[JsonSerializable(typeof(AsymmetricCipherViewState))]
[JsonSerializable(typeof(RsaViewState))]
[JsonSerializable(typeof(Sm2ViewState))]
[JsonSerializable(typeof(HashViewState))]
[JsonSerializable(typeof(Md5ViewState))]
[JsonSerializable(typeof(ShaViewState))]
[JsonSerializable(typeof(Base64ViewState))]
[JsonSerializable(typeof(SerialPortViewState))]
[JsonSerializable(typeof(TcpServerViewState))]
[JsonSerializable(typeof(UdpViewState))]
[JsonSerializable(typeof(FileEncryptionViewState))]
[JsonSerializable(typeof(KeyIvDto))]
[JsonSerializable(typeof(FolderCompareViewState))]
[JsonSerializable(typeof(Img2icoViewState))]
[JsonSerializable(typeof(ImgConvertViewState))]
internal partial class ViewStatesJsonContext : JsonSerializerContext
{
}
