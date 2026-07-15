using CommunityToolkit.Mvvm.ComponentModel;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.ViewModels.Dialogs
{
    public partial class SystemSetDialogViewModel : ObservableObject
    {
        public bool IsAnimationOn
        {
            get;
            set => SetProperty(ref field, value);
        } = true;

        public bool IsShadowOn
        {
            get;
            set => SetProperty(ref field, value);
        } = true;

        public bool IsScrollBarHidden
        {
            get;
            set => SetProperty(ref field, value);
        }

        public bool IsMessageInWindow
        {
            get;
            set => SetProperty(ref field, value);
        } = true;

        public int NoticeWindowOffsetXY
        {
            get;
            set => SetProperty(ref field, value);
        } = 50;

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
