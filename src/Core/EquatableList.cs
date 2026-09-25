using System.Collections;

namespace DeadlockAdvisor.Core;

/// <summary>
/// An immutable list compared by its contents, so records holding one keep value equality
/// (the Python app compares whole tooltips with ==, e.g. to tell whether a sync changed anything).
/// </summary>
public sealed class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    public static readonly EquatableList<T> Empty = new([]);

    private readonly T[] _items;

    public EquatableList(IEnumerable<T> items)
    {
        _items = items.ToArray();
    }

    public T this[int index] => _items[index];
    public int Count => _items.Length;

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public bool Equals(EquatableList<T>? other) =>
        other is not null && _items.AsSpan().SequenceEqual(other._items, EqualityComparer<T>.Default);

    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
            hash.Add(item);
        return hash.ToHashCode();
    }
}

public static class EquatableList
{
    public static EquatableList<T> ToEquatableList<T>(this IEnumerable<T> items) => new(items);
}
