using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace FancyToolAva.Utils;

/// <summary>
/// 支持 AddRange 批量添加的 ObservableCollection：整个批次只通知一次 Reset，
/// 避免上千文件逐条 Add 触发上千次 CollectionChanged/布局（UI 卡死的元凶之一）。
/// </summary>
public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        CheckReentrancy();
        foreach (T item in list)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
