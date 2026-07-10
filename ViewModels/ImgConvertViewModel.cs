using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels;

public partial class ImgConvertViewModel : ViewModelBase
{
    private readonly IImageConversionService _imageConversionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;

    [ObservableProperty]
    private string _imagePath = string.Empty;

    [ObservableProperty]
    private Bitmap? _imagePreview;

    [ObservableProperty]
    private int _formatIndex;

    [ObservableProperty]
    private string _convertedPath = string.Empty;

    public List<string> AvailableFormats { get; } = ["jpg", "png", "gif", "bmp", "webp", "tiff"];

    public ImgConvertViewModel(
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
        ConvertedPath = string.Empty;
    }

    [RelayCommand]
    private async Task ConvertAndSave()
    {
        if (string.IsNullOrEmpty(ImagePath))
        {
            _notificationService.ShowWarn("请先上传图片");
            return;
        }

        string format = AvailableFormats[FormatIndex];
        string outputPath = Path.ChangeExtension(ImagePath, "." + format);

        string? savePath = await _fileDialogService.PickSaveFileAsync("保存转换后的图片", Path.GetFileName(outputPath));
        if (savePath == null) return;

        try
        {
            string? result = await _imageConversionService.ConvertImageFormatAsync(ImagePath, savePath, format);
            if (result != null)
            {
                ConvertedPath = result;
                ImagePreview = await _imageConversionService.LoadImageAsync(result);
                _notificationService.ShowSuccess($"转换完成: {result}");
            }
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"转换失败: {ex.Message}");
        }
    }
}
