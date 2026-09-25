using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DeadlockAdvisor.Core;

/// <summary>An observable collection whose whole contents can be swapped with one Reset notification.</summary>
public class ResettableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        var list = items.ToList();
        if (list.SequenceEqual(Items))
            return;

        Items.Clear();
        foreach (var item in list)
            Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
