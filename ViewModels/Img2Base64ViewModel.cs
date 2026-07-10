using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels;

public partial class Img2Base64ViewModel : ViewModelBase
{
    private readonly IImageConversionService _imageConversionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    private string _imagePath = string.Empty;

    [ObservableProperty]
    private Bitmap? _imagePreview;

    [ObservableProperty]
    private string _base64Output = string.Empty;

    [ObservableProperty]
    private string _decodedImagePath = string.Empty;

    [ObservableProperty]
    private Bitmap? _decodedImagePreview;

    public Img2Base64ViewModel(
        IImageConversionService imageConversionService,
        IFileDialogService fileDialogService,
        INotificationService notificationService)
    {
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
        Base64Output = string.Empty;
    }

    [RelayCommand]
    private async Task Encode()
    {
        if (string.IsNullOrEmpty(ImagePath))
        {
            _notificationService.ShowWarn("请先上传图片");
            return;
        }

        string? base64 = await _imageConversionService.ImageToBase64Async(ImagePath);
        if (base64 != null)
        {
            Base64Output = base64;
            _notificationService.ShowSuccess("编码完成");
        }
    }

    [RelayCommand]
    private async Task Decode()
    {
        if (string.IsNullOrEmpty(Base64Output))
        {
            _notificationService.ShowWarn("请先输入 Base64 字符串");
            return;
        }

        try
        {
            DecodedImagePreview = await _imageConversionService.Base64ToBitmapAsync(Base64Output);
            DecodedImagePath = string.Empty;
            _notificationService.ShowSuccess("解码完成");
        }
        catch
        {
            _notificationService.ShowError("Base64 解码失败，请检查输入是否正确");
        }
    }

    [RelayCommand]
    private async Task SaveDecodedImage()
    {
        if (DecodedImagePreview == null)
        {
            _notificationService.ShowWarn("没有可保存的图片");
            return;
        }

        IReadOnlyList<FilePickerFileType> filters = [new("PNG Image") { Patterns = ["*.png"] }];
        string? path = await _fileDialogService.PickSaveFileAsync("保存图片", "decoded.png", filters);
        if (path == null) return;

        try
        {
            DecodedImagePreview.Save(path);
            DecodedImagePath = path;
            _notificationService.ShowSuccess($"图片已保存到: {path}");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存失败: {ex.Message}");
        }
    }
}
