using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace FancyToolAva.Helpers;

/// <summary>
/// ListBox 拖拽排序附加行为（Avalonia 无内置 reorder 支持，等价 WinUI ListView.CanReorderItems 的手写实现）。
/// 按住条目即进入拖放手势（自定义进程内 DataFormat，不与 Explorer 文件拖入的 File 格式冲突）；
/// DragOver 时按命中条目中线上/下计算插入位置并<b>实时重排</b>（live reorder，条目随指针腾位），
/// Drop 时顺序已是目标状态。通过 <c>helpers:ListBoxDragReorder.Enabled</c> 绑定开关控制（如仅拼接模式且非转码中）。
/// </summary>
public static class ListBoxDragReorder
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>(
            "Enabled", typeof(ListBoxDragReorder), false, false, Avalonia.Data.BindingMode.OneWay, null, null);

    /// <summary>进程内私有格式：拖动项仅在本次会话内传递，不进剪贴板/跨进程序列化。</summary>
    private static readonly DataFormat<object> ReorderFormat = DataFormat.CreateInProcessFormat<object>("FancyTool.ListBoxReorder");

    private sealed class ChangedObserver : IObserver<AvaloniaPropertyChangedEventArgs<bool>>
    {
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(AvaloniaPropertyChangedEventArgs<bool> value) => OnEnabledChanged(value);
    }

    static ListBoxDragReorder() => EnabledProperty.Changed.Subscribe(new ChangedObserver());

    public static bool GetEnabled(ListBox element) => element.GetValue(EnabledProperty);
    public static void SetEnabled(ListBox element, bool value) => element.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(AvaloniaPropertyChangedEventArgs<bool> e)
    {
        if (e.Sender is not ListBox listBox) return;
        if (e.NewValue.Value)
        {
            listBox.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
            DragDrop.SetAllowDrop(listBox, true);
            listBox.AddHandler(DragDrop.DragOverEvent, OnDragOver);
            listBox.AddHandler(DragDrop.DropEvent, OnDrop);
        }
        else
        {
            listBox.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
            listBox.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
            listBox.RemoveHandler(DragDrop.DropEvent, OnDrop);
        }
    }

    private static void OnPointerPressed(object? sender, PointerEventArgs e)
    {
        if (sender is not ListBox listBox || e.Source is not Visual source) return;
        if (e is not PointerPressedEventArgs pressed) return;
        if (e.Pointer.Type != PointerType.Mouse) return; // 触摸/笔交给滚动惯性
        var point = e.GetCurrentPoint(listBox);
        if (!point.Properties.IsLeftButtonPressed) return;
        var item = source.GetVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()
            ?? source as ListBoxItem;
        // 行内的按钮、输入与滑杆控件不作为拖拽起点；未命中条目不启动
        if (item?.Content == null || !IsDraggableSource(source, item)) return;
        var transfer = new DataTransfer();
        transfer.Add(DataTransferItem.Create(ReorderFormat, item.Content!));
        _ = RunDragDropAsync(listBox, pressed, transfer);
    }

    private static async Task RunDragDropAsync(ListBox listBox, PointerPressedEventArgs trigger, DataTransfer transfer)
    {
        try
        {
            await DragDrop.DoDragDropAsync(trigger, transfer, DragDropEffects.Move);
        }
        catch
        {
            // 平台拖放被中断（如窗口失焦）；顺序已在 DragOver 实时应用，无需回滚
        }
    }

    /// <summary>从命中视觉向上走到行容器为止，途中遇到交互控件则不发起拖拽。</summary>
    private static bool IsDraggableSource(Visual source, ListBoxItem container)
    {
        for (Visual? v = source; v != null && !ReferenceEquals(v, container); v = v.GetVisualParent() as Visual)
        {
            if (v is Button or RepeatButton or ToggleButton or TextBox) return false;
            switch (v.GetType().Name)
            {
                case "NumericUpDown":
                case "NumberBox":
                case "RangeSlider":
                case "FARangeSlider":
                    return false;
            }
        }
        return true;
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        // 只处理本行为发起的进程内格式；文件拖入等外部负载交给页面自身处理
        if (e.DataTransfer.TryGetValue(ReorderFormat) is not object dragged) return;
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        if (listBox.ItemsSource is not IList items || items.IsReadOnly) return;
        int insert = HitInsertIndex(listBox, e.GetPosition(listBox), items.Count);
        if (insert < 0) return;
        int from = items.IndexOf(dragged);
        if (from < 0) return;
        int to = insert > from ? insert - 1 : insert; // insert 是“移除前”语义，先移除自身再落位
        if (to != from)
        {
            items.RemoveAt(from);
            items.Insert(to, dragged);
        }
    }

    private static void OnDrop(object? sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        if (e.DataTransfer.TryGetValue(ReorderFormat) is not object) return;
        // 实时重排模式下 Drop 时顺序已是目标状态，仅终结手势
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>命中条目的中线上/下决定插入位；列表尾部空白插入到末尾；其余未命中返回 -1。</summary>
    private static int HitInsertIndex(ListBox listBox, Point position, int itemCount)
    {
        ListBoxItem? last = null;
        foreach (var item in listBox.GetVisualDescendants().OfType<ListBoxItem>())
        {
            last = item;
            if (item.TranslatePoint(new Point(), listBox) is not { } topLeft) continue;
            var bounds = new Rect(topLeft, item.Bounds.Size);
            if (!bounds.Contains(position)) continue;
            int index = ((IList)listBox.ItemsSource!).IndexOf(item.Content);
            if (index < 0) break;
            return index + (position.Y - bounds.Y > bounds.Height / 2 ? 1 : 0);
        }
        // 尾部空白：落在最后一个已生成条目之下时移动到末尾
        if (last != null && last.TranslatePoint(new Point(), listBox) is { } lastOrigin
            && position.Y > lastOrigin.Y + last.Bounds.Height)
            return itemCount;
        return -1;
    }
}
