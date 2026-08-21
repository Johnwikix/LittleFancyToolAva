using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace FancyToolAva.Services
{
    public class FileDialogService : IFileDialogService
    {
        private static TopLevel? GetTopLevel()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                return desktop.MainWindow as TopLevel;
            }
            return null;
        }

        public async Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null)
        {
            var top = GetTopLevel();
            if (top == null) return null;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                FileTypeFilter = filters,
                AllowMultiple = false,
            });
            return files?.FirstOrDefault()?.TryGetLocalPath();
        }

        public async Task<IReadOnlyList<string>?> PickOpenFilesAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null)
        {
            var top = GetTopLevel();
            if (top == null) return null;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                FileTypeFilter = filters,
                AllowMultiple = true,
            });
            return files?.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList()!;
        }

        public async Task<string?> PickSaveFileAsync(string title, string? defaultFileName = null, IReadOnlyList<FilePickerFileType>? filters = null)
        {
            var top = GetTopLevel();
            if (top == null) return null;

            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = defaultFileName,
                FileTypeChoices = filters,
            });
            return file?.TryGetLocalPath();
        }

        public async Task<string?> PickFolderAsync(string title)
        {
            var top = GetTopLevel();
            if (top == null) return null;

            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
            return folders?.FirstOrDefault()?.TryGetLocalPath();
        }

        public void OpenInExplorer(string folderPath)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    Process.Start("explorer.exe", folderPath);
                }
                else if (OperatingSystem.IsLinux())
                {
                    Process.Start("xdg-open", folderPath);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    Process.Start("open", folderPath);
                }
            }
            catch
            {
            }
        }
    }
}
