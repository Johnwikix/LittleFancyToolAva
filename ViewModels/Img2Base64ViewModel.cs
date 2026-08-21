using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FancyToolAva.Services;
using FancyToolAva.Utils;

namespace FancyToolAva.ViewModels;

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
        : LocalizationRegistry.Get("Img2Base64.Label_CharCount", Base64Output.Length);

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
        IReadOnlyList<FilePickerFileType> filters = [new(LocalizationRegistry.Get("Img2Base64.Picker_SelectImage")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp", "*.tiff"] }];
        string? path = await _fileDialogService.PickOpenFileAsync(LocalizationRegistry.Get("Img2Base64.Picker_SelectImage"), filters);
        if (path == null) return;

        await LoadImageFromPathAsync(path);
    }

    public async Task LoadImageFromPathAsync(string path)
    {
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
            _notificationService.ShowWarn(LocalizationRegistry.Get("Img2Base64.Msg_UploadFirst"));
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
                await ClipboardHelper.SetTextAsync(base64);
                _notificationService.ShowSuccess(LocalizationRegistry.Get("Img2Base64.Msg_CharCountCopied", base64.Length));
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
            _notificationService.ShowWarn(LocalizationRegistry.Get("Img2Base64.Msg_NeedInput"));
            return;
        }

        if (Base64Input.Length > MaxTextBoxChars)
        {
            _notificationService.ShowWarn(LocalizationRegistry.Get("Img2Base64.Msg_TooLongTip", Base64Input.Length));
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            SetImagePreview(await _imageConversionService.Base64ToBitmapAsync(Base64Input, _cts.Token));
            _notificationService.ShowSuccess(LocalizationRegistry.Get("Img2Base64.Msg_Decoded"));
        }
        catch (OperationCanceledException) { }
        catch
        {
            _notificationService.ShowError(LocalizationRegistry.Get("Img2Base64.Msg_DecodeFailInput"));
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
            _notificationService.ShowError(LocalizationRegistry.Get("Img2Base64.Msg_ClipboardFail"));
            return;
        }

        IAsyncDataTransfer? data = await clipboard.TryGetDataAsync();
        if (data is null)
        {
            _notificationService.ShowWarn(LocalizationRegistry.Get("Img2Base64.Msg_ClipboardNoData"));
            return;
        }

        using (data)
        {
            IAsyncDataTransferItem? textItem = data.Items
                .FirstOrDefault(i => i.Formats.Contains(DataFormat.Text));

            if (textItem is null)
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Img2Base64.Msg_ClipboardNoText"));
                return;
            }

            object? raw = await textItem.TryGetRawAsync(DataFormat.Text);
            if (raw is not string text || string.IsNullOrWhiteSpace(text))
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Img2Base64.Msg_ClipboardEmpty"));
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            IsBusy = true;
            try
            {
                SetImagePreview(await _imageConversionService.Base64ToBitmapAsync(text, _cts.Token));
                _notificationService.ShowSuccess(LocalizationRegistry.Get("Img2Base64.Msg_Decoded"));
            }
            catch (OperationCanceledException) { }
            catch
            {
                _notificationService.ShowError(LocalizationRegistry.Get("Img2Base64.Msg_DecodeFailClipboard"));
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
        string? path = await _fileDialogService.PickOpenFileAsync(LocalizationRegistry.Get("Img2Base64.Picker_SelectBase64File"), filters);
        if (path == null) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        try
        {
            string text = await File.ReadAllTextAsync(path, _cts.Token);
            if (string.IsNullOrWhiteSpace(text))
            {
                _notificationService.ShowWarn(LocalizationRegistry.Get("Img2Base64.Msg_ClipboardEmpty"));
                return;
            }

            SetImagePreview(await _imageConversionService.Base64ToBitmapAsync(text.Trim(), _cts.Token));
            _notificationService.ShowSuccess(LocalizationRegistry.Get("Img2Base64.Msg_Decoded"));
        }
        catch (OperationCanceledException) { }
        catch
        {
            _notificationService.ShowError(LocalizationRegistry.Get("Img2Base64.Msg_DecodeFailFile"));
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
            _notificationService.ShowWarn(LocalizationRegistry.Get("Img2Base64.Msg_NoCopyData"));
            return;
        }

        await ClipboardHelper.SetTextAsync(Base64Output);
        _notificationService.ShowSuccess(LocalizationRegistry.Get("Img2Base64.Msg_Copied"));
    }

    [RelayCommand(CanExecute = nameof(CanSaveDecodedImage))]
    private async Task SaveDecodedImage()
    {
        if (ImagePreview == null)
        {
            _notificationService.ShowWarn(LocalizationRegistry.Get("Img2Base64.Msg_NoImageToSave"));
            return;
        }

        IReadOnlyList<FilePickerFileType> filters = [new("PNG Image") { Patterns = ["*.png"] }];
        string? path = await _fileDialogService.PickSaveFileAsync(LocalizationRegistry.Get("Img2Base64.Picker_SaveImage"), "decoded.png", filters);
        if (path == null) return;

        try
        {
            ImagePreview.Save(path);
            _notificationService.ShowSuccess(LocalizationRegistry.Get("Img2Base64.Msg_ImageSaved", path));
        }
        catch (Exception ex)
        {
            _notificationService.ShowError(LocalizationRegistry.Get("Img2Base64.Msg_SaveFail", ex.Message));
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
