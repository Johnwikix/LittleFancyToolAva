using Avalonia.Platform.Storage;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LittleFancyToolAva.Services
{
    public interface IFileDialogService
    {
        Task<string?> PickOpenFileAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null);
        Task<IReadOnlyList<string>?> PickOpenFilesAsync(string title, IReadOnlyList<FilePickerFileType>? filters = null);
        Task<string?> PickSaveFileAsync(string title, string? defaultFileName = null, IReadOnlyList<FilePickerFileType>? filters = null);
        Task<string?> PickFolderAsync(string title);
        void OpenInExplorer(string folderPath);
    }
}
