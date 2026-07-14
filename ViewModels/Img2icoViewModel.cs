using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels;

public partial class Img2icoViewModel : ViewModelBase
{
    private readonly IIconConversionService _iconConversionService;
    private readonly IImageConversionService _imageConversionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private byte[]? _icoBytes;

    [ObservableProperty]
    private string _imagePath = string.Empty;

    [ObservableProperty]
    private Bitmap? _imagePreview;

    [ObservableProperty]
    private int _selectedSizeIndex = 2;

    [ObservableProperty]
    private Bitmap? _icoPreview;

    public List<int> AvailableSizes { get; } = [16, 32, 48, 64, 128, 256];

    public Img2icoViewModel(
        IIconConversionService iconConversionService,
        IImageConversionService imageConversionService,
        IFileDialogService fileDialogService,
        INotificationService notificationService)
    {
        _iconConversionService = iconConversionService;
        _imageConversionService = imageConversionService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
    }

    [RelayCommand]
    private async Task UploadImage()
    {
        IReadOnlyList<FilePickerFileType> filters = [new("Image Files") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp", "*.tiff"] }];
        string? path = await _fileDialogService.PickOpenFileAsync("选择图片", filters);
        if (path == null) return;

        ImagePath = path;
        ImagePreview = await _imageConversionService.LoadImageAsync(path);
        _icoBytes = null;
        IcoPreview = null;
    }

    [RelayCommand]
    private async Task Convert()
    {
        if (string.IsNullOrEmpty(ImagePath))
        {
            _notificationService.ShowWarn("请先上传图片");
            return;
        }

        int size = AvailableSizes[SelectedSizeIndex];

        try
        {
            _icoBytes = await _iconConversionService.CreateIcoBytesAsync(ImagePath, size);
            using MemoryStream ms = new(_icoBytes);
            IcoPreview = new Bitmap(ms);
            _notificationService.ShowSuccess("转换完成，可在右侧预览");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"转换失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SaveIco()
    {
        if (_icoBytes == null)
        {
            _notificationService.ShowWarn("请先转换图片");
            return;
        }

        string? savePath = await _fileDialogService.PickSaveFileAsync("保存 ICO", "icon.ico");
        if (savePath == null) return;

        try
        {
            await File.WriteAllBytesAsync(savePath, _icoBytes);
            _notificationService.ShowSuccess($"ICO 已保存到: {savePath}");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存失败: {ex.Message}");
        }
    }
}
