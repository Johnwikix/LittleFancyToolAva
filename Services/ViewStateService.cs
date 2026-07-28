using Microsoft.Extensions.Logging;
using System.Text.Json;
using LittleFancyToolAva.Models.ViewStates;

namespace LittleFancyToolAva.Services;

public class ViewStateService : IViewStateService
{
    private readonly string _filePath;
    private readonly ILogger<ViewStateService> _logger;
    private readonly List<IViewState> _activeViews = [];
    private ViewStatesRoot? _loadedRoot;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        TypeInfoResolver = ViewStatesJsonContext.Default,
        MaxDepth = 64
    };

    public ViewStateService(ILogger<ViewStateService> logger)
    {
        _logger = logger;
            _filePath = Path.Combine(AppContext.BaseDirectory, "view-states.json");
    }

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
        if (!File.Exists(_filePath))
        {
            return;
        }
        try
        {
            var fi = new FileInfo(_filePath);
            if (fi.Length > 5 * 1024 * 1024)
            {
                _logger.LogWarning("View states file too large ({Size} bytes), using defaults", fi.Length);
                return;
            }
            TryLoadFrom(_filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load view states");
        }
    }

    private void TryLoadFrom(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            _loadedRoot = JsonSerializer.Deserialize<ViewStatesRoot>(json, _jsonOptions);
            foreach (var view in _activeViews)
            {
                TryRestore(view);
            }
            _logger.LogInformation("View states loaded from {Path}", path);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "View states JSON corrupt in {Path}", path);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "IO error reading {Path}", path);
        }
    }

    public void SaveAll()
    {
        try
        {
            var root = BuildRoot();
            string json = JsonSerializer.Serialize(root, _jsonOptions);
            AtomicWrite(_filePath, json);
            _logger.LogInformation("View states saved ({Count} views)", _activeViews.Count);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "View states save IO error");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "View states save access denied");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error saving view states");
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
            case "tcpServerView": root.TcpServerView = (TcpServerViewState?)state; break;
            case "udpView": root.UdpView = (UdpViewState?)state; break;
            case "fileEncryptionView": root.FileEncryptionView = (FileEncryptionViewState?)state; break;
            case "folderCompareView": root.FolderCompareView = (FolderCompareViewState?)state; break;
            case "img2icoView": root.Img2icoView = (Img2icoViewState?)state; break;
            case "imgConvertView": root.ImgConvertView = (ImgConvertViewState?)state; break;
        }
    }

    private static void AtomicWrite(string path, string content)
    {
        string tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, content);
        File.Move(tmpPath, path, overwrite: true);
    }
}
