using CommunityToolkit.Mvvm.ComponentModel;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.ViewModels.Dialogs
{
    public partial class SystemSetDialogViewModel : ObservableObject
    {
        [ObservableProperty]
        private bool _isAnimationOn = true;

        [ObservableProperty]
        private bool _isShadowOn = true;

        [ObservableProperty]
        private bool _isScrollBarHidden;

        [ObservableProperty]
        private bool _isMessageInWindow = true;

        [ObservableProperty]
        private int _noticeWindowOffsetXY = 50;

        public SystemSetDialogViewModel(AppPreferences preferences)
        {
            IsAnimationOn = preferences.IsAnimationOn;
            IsShadowOn = preferences.IsShadowOn;
            IsScrollBarHidden = preferences.IsScrollBarHidden;
            IsMessageInWindow = preferences.IsMessageInWindow;
            NoticeWindowOffsetXY = preferences.NoticeWindowOffsetXY;
        }

        public void ApplyTo(AppPreferences preferences)
        {
            preferences.IsAnimationOn = IsAnimationOn;
            preferences.IsShadowOn = IsShadowOn;
            preferences.IsScrollBarHidden = IsScrollBarHidden;
            preferences.IsMessageInWindow = IsMessageInWindow;
            preferences.NoticeWindowOffsetXY = NoticeWindowOffsetXY;
        }
    }
}
