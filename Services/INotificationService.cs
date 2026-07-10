namespace LittleFancyToolAva.Services
{
    public interface INotificationService
    {
        void ShowError(string message);
        void ShowSuccess(string message);
        void ShowInfo(string message);
        void ShowWarn(string message);
    }
}
