using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using FancyToolAva.Models.ViewStates;
using FancyToolAva.ViewModels;
using FancyToolAva.Views;
using Microsoft.Extensions.Logging;
using Serilog;

namespace FancyToolAva.Services
{
    public class NavigationFactory : IFANavigationPageFactory
    {
        private readonly Dictionary<Type, Func<Control>> _map = new();
        private object? _previousDataContext;

        public void Register<TViewModel, TView>()
            where TViewModel : class
            where TView : Control, new()
        {
            _map[typeof(TViewModel)] = () => new TView();
        }

        public void Unregister<TViewModel>()
        {
            _map.Remove(typeof(TViewModel));
        }

        public Control GetPage(Type srcType)
        {
            throw new NotSupportedException($"GetPage(Type) is not supported. Use GetPageFromObject instead. Type={srcType?.Name}");
        }

        public Control GetPageFromObject(object target)
        {
            if (_previousDataContext is IViewLifecycle lifecycle && _previousDataContext != target)
            {
                lifecycle.OnNavigatedFrom();
                if (_previousDataContext is IDisposable d)
                {
                    try { d.Dispose(); } catch (Exception ex) { Log.Warning(ex, "Failed to dispose ViewModel {Type}", _previousDataContext.GetType().Name); }
                }
            }

            if (_previousDataContext != target && target is IViewLifecycle lifecycle2)
            {
                lifecycle2.OnNavigatedTo();
            }
            _previousDataContext = target;

            if (_map.TryGetValue(target.GetType(), out var factory))
            {
                var page = factory();
                page.DataContext = target;
                return page;
            }
            throw new InvalidOperationException($"No view registered for ViewModel type '{target.GetType().Name}'. Register via NavigationFactory.Register<TViewModel, TView>()");
        }
    }
}
