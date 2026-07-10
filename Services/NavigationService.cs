using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Media.Animation;
using FluentAvalonia.UI.Navigation;

namespace LittleFancyToolAva.Services
{
    public class NavigationService
    {
        public static NavigationService Instance { get; } = new();

        private FAFrame? _frame;

        public void SetFrame(FAFrame frame)
        {
            _frame = frame;
        }

        public void NavigateFromContext(object dataContext, FANavigationTransitionInfo? transition = null)
        {
            if (_frame == null)
                return;

            _frame.NavigateFromObject(dataContext, new FAFrameNavigationOptions
            {
                IsNavigationStackEnabled = false,
                TransitionInfoOverride = transition ?? new FAEntranceNavigationTransitionInfo()
            });
        }
    }
}
