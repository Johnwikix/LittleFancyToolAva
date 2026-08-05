using Lang.Avalonia;
using LittleFancyToolAva.Services;

namespace LittleFancyToolAva.Utils
{
    public static class LogFileHelper
    {
        public static async Task SaveAsync(LogBuffer log, IFileDialogService dialog, INotificationService notification, string keyPrefix)
        {
            if (log.Count == 0)
            {
                notification.ShowWarn(LocalizationRegistry.Get($"{keyPrefix}.Msg_NoDataToSave"));
                return;
            }

            string? path = await dialog.PickSaveFileAsync(
                LocalizationRegistry.Get($"{keyPrefix}.Msg_SaveDialogTitle"), "received.txt");
            if (path != null)
            {
                var lines = log.Entries.Select(e => $"{e.TimestampText} {e.Tag}: {e.Text}");
                await File.WriteAllLinesAsync(path, lines);
                notification.ShowSuccess(LocalizationRegistry.Get($"{keyPrefix}.Msg_SaveSuccess", path));
            }
        }
    }
}
