using System.Diagnostics;
using System.Text.Json;
using LittleFancyToolAva.Models.ViewStates;

namespace LittleFancyToolAva.Services;

public class ViewStateService : IViewStateService
{
    private readonly string _filePath = Path.Combine(AppContext.BaseDirectory, "view-states.json");
    private readonly List<IViewState> _activeViews = [];
    private ViewStatesRoot? _loadedRoot;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = ViewStatesJsonContext.Default
    };

    public void Register(IViewState view)
    {
        _activeViews.Add(view);
        if (_loadedRoot != null)
        {
            TryRestore(view);
        }
    }

    public void Unregister(IViewState view)
    {
        _activeViews.Remove(view);
    }

    public void LoadAll()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            string json = File.ReadAllText(_filePath);
            _loadedRoot = JsonSerializer.Deserialize<ViewStatesRoot>(json, _jsonOptions);
            foreach (var view in _activeViews)
            {
                TryRestore(view);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ViewStateService] Load failed: {ex.Message}");
        }
    }

    public void SaveAll()
    {
        try
        {
            var root = BuildRoot();
            string json = JsonSerializer.Serialize(root, _jsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ViewStateService] Save failed: {ex.Message}");
        }
    }

    private ViewStatesRoot BuildRoot()
    {
        var root = new ViewStatesRoot();
        foreach (var view in _activeViews)
        {
            var state = view.CaptureState();
            Apply(root, view.ViewName, state);
        }
        return root;
    }

    private void TryRestore(IViewState view)
    {
        if (_loadedRoot == null) return;
        var state = Extract(_loadedRoot, view.ViewName);
        if (state != null)
        {
            view.RestoreState(state);
        }
    }

    private static object? Extract(ViewStatesRoot root, string viewName) => viewName switch
    {
        "desView" => root.DesView,
        "sm4View" => root.Sm4View,
        "aesView" => root.AesView,
        "rsaView" => root.RsaView,
        "sm2View" => root.Sm2View,
        "sm3View" => root.Sm3View,
        "md5View" => root.Md5View,
        "shaView" => root.ShaView,
        "base64View" => root.Base64View,
        "serialPortView" => root.SerialPortView,
        "modbusPollView" => root.ModbusPollView,
        "modbusSlaveView" => root.ModbusSlaveView,
        "tcpServerView" => root.TcpServerView,
        "udpView" => root.UdpView,
        "fileEncryptionView" => root.FileEncryptionView,
        "folderCompareView" => root.FolderCompareView,
        "img2icoView" => root.Img2icoView,
        "imgConvertView" => root.ImgConvertView,
        _ => null
    };

    private static void Apply(ViewStatesRoot root, string viewName, object state)
    {
        switch (viewName)
        {
            case "desView": root.DesView = (SymmetricCipherViewState?)state; break;
            case "sm4View": root.Sm4View = (SymmetricCipherViewState?)state; break;
            case "aesView": root.AesView = (AesViewState?)state; break;
            case "rsaView": root.RsaView = (AsymmetricCipherViewState?)state; break;
            case "sm2View": root.Sm2View = (Sm2ViewState?)state; break;
            case "sm3View": root.Sm3View = (HashViewState?)state; break;
            case "md5View": root.Md5View = (Md5ViewState?)state; break;
            case "shaView": root.ShaView = (ShaViewState?)state; break;
            case "base64View": root.Base64View = (Base64ViewState?)state; break;
            case "serialPortView": root.SerialPortView = (SerialPortViewState?)state; break;
            case "modbusPollView": root.ModbusPollView = (ModbusPollViewState?)state; break;
            case "modbusSlaveView": root.ModbusSlaveView = (ModbusSlaveViewState?)state; break;
            case "tcpServerView": root.TcpServerView = (TcpServerViewState?)state; break;
            case "udpView": root.UdpView = (UdpViewState?)state; break;
            case "fileEncryptionView": root.FileEncryptionView = (FileEncryptionViewState?)state; break;
            case "folderCompareView": root.FolderCompareView = (FolderCompareViewState?)state; break;
            case "img2icoView": root.Img2icoView = (Img2icoViewState?)state; break;
            case "imgConvertView": root.ImgConvertView = (ImgConvertViewState?)state; break;
        }
    }
}
