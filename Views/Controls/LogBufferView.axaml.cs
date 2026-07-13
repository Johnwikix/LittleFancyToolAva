using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LittleFancyToolAva.Models;

namespace LittleFancyToolAva.Views.Controls
{
    public partial class LogBufferView : UserControl
    {
        public static readonly StyledProperty<IEnumerable> EntriesProperty =
            AvaloniaProperty.Register<LogBufferView, IEnumerable>(nameof(Entries));

        public static readonly StyledProperty<bool> AutoScrollToEndProperty =
            AvaloniaProperty.Register<LogBufferView, bool>(nameof(AutoScrollToEnd), true);

        private const double StickinessThreshold = 24.0;

        public IEnumerable Entries
        {
            get => GetValue(EntriesProperty);
            set => SetValue(EntriesProperty, value);
        }

        public bool AutoScrollToEnd
        {
            get => GetValue(AutoScrollToEndProperty);
            set => SetValue(AutoScrollToEndProperty, value);
        }

        public LogBufferView()
        {
            InitializeComponent();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == EntriesProperty)
            {
                if (change.OldValue is INotifyCollectionChanged oldNcc)
                    oldNcc.CollectionChanged -= OnEntriesChanged;
                if (change.NewValue is INotifyCollectionChanged newNcc)
                    newNcc.CollectionChanged += OnEntriesChanged;
            }
        }

        private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!AutoScrollToEnd) return;
            if (LogList is null) return;

            Dispatcher.UIThread.Post(() =>
            {
                var scrollViewer = LogList.FindDescendantOfType<ScrollViewer>();
                if (scrollViewer is null) return;

                _ = Dispatcher.UIThread.InvokeAsync(() =>
                {
                    bool atBottom = scrollViewer.Offset.Y
                        >= scrollViewer.Extent.Height - scrollViewer.Viewport.Height - StickinessThreshold;

                    if (atBottom)
                    {
                        scrollViewer.ScrollToEnd();
                    }
                }, DispatcherPriority.Render);
            });
        }
    }
}