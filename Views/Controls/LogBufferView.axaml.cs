using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

        private ScrollViewer? _scrollViewer;
        private bool _scrollPending;
        private bool _mouseInside;

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
            AttachedToVisualTree += OnAttachedToVisualTree;
            DetachedFromVisualTree += OnDetachedFromVisualTree;
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

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            Dispatcher.UIThread.Post(HookScrollViewer, DispatcherPriority.Loaded);
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            UnhookScrollViewer();
        }

        private void HookScrollViewer()
        {
            if (_scrollViewer != null) return;
            _scrollViewer = LogList?.FindDescendantOfType<ScrollViewer>();
            if (_scrollViewer is null) return;

            _scrollViewer.AddHandler(PointerEnteredEvent, OnPointerEntered, handledEventsToo: true);
            _scrollViewer.AddHandler(PointerExitedEvent, OnPointerExited, handledEventsToo: true);
        }

        private void UnhookScrollViewer()
        {
            if (_scrollViewer is null) return;
            _scrollViewer.RemoveHandler(PointerEnteredEvent, OnPointerEntered);
            _scrollViewer.RemoveHandler(PointerExitedEvent, OnPointerExited);
            _scrollViewer = null;
            _mouseInside = false;
        }

        private void OnPointerEntered(object? sender, PointerEventArgs e)
        {
            _mouseInside = true;
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            _mouseInside = false;
        }

        private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!AutoScrollToEnd) return;
            if (_mouseInside) return;
            if (_scrollPending) return;
            _scrollPending = true;

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                _scrollPending = false;
                if (_mouseInside) return;
                var sv = _scrollViewer ?? LogList?.FindDescendantOfType<ScrollViewer>();
                if (sv is null) return;
                sv.ScrollToEnd();
            }, DispatcherPriority.Render);
        }
    }
}
