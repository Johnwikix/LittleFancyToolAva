using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.ViewModels;

public partial class Img2Base64ViewModel : ViewModelBase
{
    private const int MaxTextBoxChars = 50000;
    private readonly IImageConversionService _imageConversionService;
    private readonly IFileDialogService _fileDialogService;
    private readonly INotificationService _notificationService;
    private CancellationTokenSource? _cts;

    public string ImagePath
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public Bitmap? ImagePreview
    {
        get;
        set => SetProperty(ref field, value);
    }

    public string Base64Input
    {
        get;
        set => SetProperty(ref field, value);
    } = string.Empty;

    public string Base64Output
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(Base64Preview));
                OnPropertyChanged(nameof(Base64Length));
            }
        }
    } = string.Empty;

    public string Base64Preview => TruncateBase64(Base64Output);

    public string Base64Length => string.IsNullOrEmpty(Base64Output)
        ? string.Empty
        : $"共 {Base64Output.Length:N0} 字符";

    public bool IsBusy
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value))
            {
                UploadImageCommand.NotifyCanExecuteChanged();
                EncodeCommand.NotifyCanExecuteChanged();
                DecodeCommand.NotifyCanExecuteChanged();
                DecodeFromClipboardCommand.NotifyCanExecuteChanged();
                DecodeFromFileCommand.NotifyCanExecuteChanged();
                CopyBase64Command.NotifyCanExecuteChanged();
                SaveDecodedImageCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public Img2Base64ViewModel(
        IImageConversionService imageConversionService,
        IFileDialogService fileDialogService,
        INotificationService notificationService)
    {
        _imageConversionService = imageConversionService;
        _fileDialogService = fileDialogService;
        _notificationService = notificationService;
    }

    [RelayCommand(CanExecute = nameof(CanUpload))]
    private async Task UploadImage()
    {
        IReadOnlyList<FilePickerFileType> filters = [new("Image Files") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp", "*.tiff"] }];
        string? path = await _fileDialogService.PickOpenFileAsync("选择图片", filters);
        if (path == null) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            ImagePath = path;
            SetImagePreview(await _imageConversionService.LoadImageAsync(path, _cts.Token));
            Base64Input = string.Empty;
            Base64Output = string.Empty;
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanEncode))]
    private async Task Encode()
    {
        if (string.IsNullOrEmpty(ImagePath))
        {
            _notificationService.ShowWarn("请先上传图片");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            string? base64 = await _imageConversionService.ImageToBase64Async(ImagePath, _cts.Token);
            if (base64 != null)
            {
                Base64Output = base64;
                await SetClipboardAsync(base64);
                _notificationService.ShowSuccess($"编码完成，已复制到剪贴板（共 {base64.Length:N0} 字符）");
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDecode))]
    private async Task Decode()
    {
        if (string.IsNullOrEmpty(Base64Input))
        {
            _notificationService.ShowWarn("请先输入 Base64 字符串");
            return;
        }

        if (Base64Input.Length > MaxTextBoxChars)
        {
            _notificationService.ShowWarn(
                $"Base64 字符串过长（{Base64Input.Length:N0} 字符），建议使用「从剪贴板解码」或「从文件导入」以避免界面卡顿");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            SetImagePreview(await _imageConversionService.Base64ToBitmapAsync(Base64Input, _cts.Token));
            _notificationService.ShowSuccess("解码完成");
        }
        catch (OperationCanceledException) { }
        catch
        {
            _notificationService.ShowError("Base64 解码失败，请检查输入是否正确");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanDecodeFromClipboard))]
    private async Task DecodeFromClipboard()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow?.Clipboard is not { } clipboard)
        {
            _notificationService.ShowError("无法访问剪贴板");
            return;
        }

        IAsyncDataTransfer? data = await clipboard.TryGetDataAsync();
        if (data is null)
        {
            _notificationService.ShowWarn("剪贴板中没有数据");
            return;
        }

        using (data)
        {
            IAsyncDataTransferItem? textItem = data.Items
                .FirstOrDefault(i => i.Formats.Contains(DataFormat.Text));

            if (textItem is null)
            {
                _notificationService.ShowWarn("剪贴板中没有文本数据");
                return;
            }

            object? raw = await textItem.TryGetRawAsync(DataFormat.Text);
            if (raw is not string text || string.IsNullOrWhiteSpace(text))
            {
                _notificationService.ShowWarn("剪贴板文本为空");
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            IsBusy = true;
            try
            {
                SetImagePreview(await _imageConversionService.Base64ToBitmapAsync(text, _cts.Token));
                _notificationService.ShowSuccess("解码完成");
            }
            catch (OperationCanceledException) { }
            catch
            {
                _notificationService.ShowError("Base64 解码失败，请检查剪贴板内容是否为有效的 Base64 编码");
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanDecodeFromFile))]
    private async Task DecodeFromFile()
    {
        IReadOnlyList<FilePickerFileType> filters = [new("Base64 Files") { Patterns = ["*.txt", "*.b64", "*.base64", "*.b64txt"] }];
        string? path = await _fileDialogService.PickOpenFileAsync("选择 Base64 文件", filters);
        if (path == null) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            string text = await File.ReadAllTextAsync(path, _cts.Token);
            if (string.IsNullOrWhiteSpace(text))
            {
                _notificationService.ShowWarn("文件内容为空");
                return;
            }

            SetImagePreview(await _imageConversionService.Base64ToBitmapAsync(text.Trim(), _cts.Token));
            _notificationService.ShowSuccess("解码完成");
        }
        catch (OperationCanceledException) { }
        catch
        {
            _notificationService.ShowError("Base64 解码失败，请检查文件内容是否为有效的 Base64 编码");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCopyBase64))]
    private async Task CopyBase64()
    {
        if (string.IsNullOrEmpty(Base64Output))
        {
            _notificationService.ShowWarn("没有可复制的 Base64 字符串");
            return;
        }

        await SetClipboardAsync(Base64Output);
        _notificationService.ShowSuccess("已复制到剪贴板");
    }

    [RelayCommand(CanExecute = nameof(CanSaveDecodedImage))]
    private async Task SaveDecodedImage()
    {
        if (ImagePreview == null)
        {
            _notificationService.ShowWarn("没有可保存的图片");
            return;
        }

        IReadOnlyList<FilePickerFileType> filters = [new("PNG Image") { Patterns = ["*.png"] }];
        string? path = await _fileDialogService.PickSaveFileAsync("保存图片", "decoded.png", filters);
        if (path == null) return;

        try
        {
            ImagePreview.Save(path);
            _notificationService.ShowSuccess($"图片已保存到: {path}");
        }
        catch (Exception ex)
        {
            _notificationService.ShowError($"保存失败: {ex.Message}");
        }
    }

    private static async Task SetClipboardAsync(string text)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow?.Clipboard is { } clipboard)
        {
            var dataTransfer = new DataTransfer();
            dataTransfer.Add(DataTransferItem.CreateText(text));
            await clipboard.SetDataAsync(dataTransfer);
        }
    }

    private static string TruncateBase64(string base64)
    {
        const int edgeChars = 80;
        if (string.IsNullOrEmpty(base64) || base64.Length <= edgeChars * 2 + 3)
            return base64 ?? string.Empty;

        return base64[..edgeChars] + "\n...\n" + base64[^edgeChars..];
    }

    private bool CanUpload() => !IsBusy;
    private bool CanEncode() => !IsBusy && !string.IsNullOrEmpty(ImagePath);
    private bool CanDecode() => !IsBusy && !string.IsNullOrEmpty(Base64Input);
    private bool CanDecodeFromClipboard() => !IsBusy;
    private bool CanDecodeFromFile() => !IsBusy;
    private bool CanCopyBase64() => !IsBusy && !string.IsNullOrEmpty(Base64Output);
    private bool CanSaveDecodedImage() => !IsBusy && ImagePreview != null;

    private void SetImagePreview(Bitmap? newPreview)
    {
        var old = ImagePreview;
        ImagePreview = null;
        old?.Dispose();
        ImagePreview = newPreview;
    }
}
