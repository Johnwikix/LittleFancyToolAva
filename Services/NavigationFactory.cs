using Avalonia.Controls;
using FluentAvalonia.UI.Controls;
using LittleFancyToolAva.ViewModels;
using LittleFancyToolAva.Views;
using System;
using System.Collections.Generic;

namespace LittleFancyToolAva.Services
{
    public class NavigationFactory : IFANavigationPageFactory
    {
        private readonly Dictionary<Type, Func<Control>> _map = new();

        public void Register<TViewModel, TView>()
            where TViewModel : class
            where TView : Control, new()
        {
            _map[typeof(TViewModel)] = () => new TView();
        }

        public Control GetPage(Type srcType)
        {
            return null!;
        }

        public Control GetPageFromObject(object target)
        {
            if (_map.TryGetValue(target.GetType(), out var factory))
            {
                var page = factory();
                page.DataContext = target;
                return page;
            }
            return null!;
        }
    }
}
