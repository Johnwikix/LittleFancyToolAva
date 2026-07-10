using Avalonia.Controls;
using LittleFancyToolAva.ViewModels.Dialogs;

namespace LittleFancyToolAva.Views.Dialogs
{
    public partial class SystemSetDialog : UserControl
    {
        public SystemSetDialog()
        {
            InitializeComponent();
        }

        protected override void OnDataContextChanged(System.EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (DataContext is SystemSetDialogViewModel vm)
            {
                AnimationSwitch.IsChecked = vm.IsAnimationOn;
                ShadowSwitch.IsChecked = vm.IsShadowOn;
                ScrollBarSwitch.IsChecked = vm.IsScrollBarHidden;
                MessageInWindowSwitch.IsChecked = vm.IsMessageInWindow;
                OffsetInput.Value = vm.NoticeWindowOffsetXY;
            }
        }
    }
}
