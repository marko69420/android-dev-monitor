using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace AndroidDevMonitor.Presentation.Collections;

/// <summary>
/// A live, filtered and optionally sorted view over an <see cref="ObservableCollection{T}"/> that any UI toolkit
/// can bind to. Appends, inserts at the front and removals are forwarded as single-item changes so large lists
/// (logcat) stay cheap; anything else, and <see cref="Refresh"/>, rebuilds the view and raises Reset.
/// </summary>
public sealed class FilteredCollection<T> : IList<T>, IList, IReadOnlyList<T>, INotifyCollectionChanged, INotifyPropertyChanged
    where T : class
{
    private readonly ObservableCollection<T> _source;
    private readonly List<T> _items = [];
    private Func<T, bool>? _filter;
    private IComparer<T>? _sort;

    public FilteredCollection(ObservableCollection<T> source, Func<T, bool>? filter = null)
    {
        _source = source;
        _filter = filter;
        _source.CollectionChanged += OnSourceChanged;
        Rebuild();
    }

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Func<T, bool>? Filter
    {
        get => _filter;
        set { _filter = value; Refresh(); }
    }

    public IComparer<T>? Sort
    {
        get => _sort;
        set { _sort = value; Refresh(); }
    }

    public int Count => _items.Count;

    public T this[int index] => _items[index];

    /// <summary>Re-applies the filter and sort to every source item.</summary>
    public void Refresh()
    {
        Rebuild();
        RaiseReset();
    }

    private bool Accepts(T item) => _filter?.Invoke(item) ?? true;

    private void Rebuild()
    {
        _items.Clear();
        IEnumerable<T> visible = _source.Where(Accepts);
        // OrderBy is stable, so equal keys keep their source order.
        _items.AddRange(_sort is null ? visible : visible.OrderBy(item => item, _sort));
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && IsAppendOrPrepend(e):
                bool prepend = e.NewStartingIndex == 0 && _source.Count > e.NewItems.Count;
                int offset = 0;
                foreach (T item in e.NewItems)
                {
                    if (!Accepts(item)) continue;
                    int index = _sort is not null ? UpperBound(item) : prepend ? offset++ : _items.Count;
                    _items.Insert(index, item);
                    RaiseChange(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, item, index));
                }
                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (T item in e.OldItems)
                {
                    int index = _items.IndexOf(item);
                    if (index < 0) continue;
                    _items.RemoveAt(index);
                    RaiseChange(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, item, index));
                }
                break;
            default:
                Refresh();
                break;
        }
    }

    private bool IsAppendOrPrepend(NotifyCollectionChangedEventArgs e) =>
        e.NewStartingIndex == 0 || e.NewStartingIndex + e.NewItems!.Count == _source.Count || _sort is not null;

    /// <summary>The insert position after every item that sorts equal to or before <paramref name="item"/>.</summary>
    private int UpperBound(T item)
    {
        int low = 0, high = _items.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (_sort!.Compare(_items[middle], item) <= 0) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private void RaiseReset() => RaiseChange(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    private void RaiseChange(NotifyCollectionChangedEventArgs args)
    {
        CollectionChanged?.Invoke(this, args);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    public int IndexOf(T item) => _items.IndexOf(item);

    public bool Contains(T item) => _items.Contains(item);

    public void CopyTo(T[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);

    // The view is read-only: change the source collection instead.
    public bool IsReadOnly => true;

    T IList<T>.this[int index]
    {
        get => _items[index];
        set => throw ReadOnly();
    }

    void IList<T>.Insert(int index, T item) => throw ReadOnly();

    void IList<T>.RemoveAt(int index) => throw ReadOnly();

    void ICollection<T>.Add(T item) => throw ReadOnly();

    void ICollection<T>.Clear() => throw ReadOnly();

    bool ICollection<T>.Remove(T item) => throw ReadOnly();

    object? IList.this[int index]
    {
        get => _items[index];
        set => throw ReadOnly();
    }

    bool IList.IsFixedSize => false;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => ((ICollection)_items).SyncRoot;

    int IList.Add(object? value) => throw ReadOnly();

    void IList.Clear() => throw ReadOnly();

    bool IList.Contains(object? value) => value is T item && Contains(item);

    int IList.IndexOf(object? value) => value is T item ? IndexOf(item) : -1;

    void IList.Insert(int index, object? value) => throw ReadOnly();

    void IList.Remove(object? value) => throw ReadOnly();

    void IList.RemoveAt(int index) => throw ReadOnly();

    void ICollection.CopyTo(Array array, int index) => ((ICollection)_items).CopyTo(array, index);

    private static NotSupportedException ReadOnly() => new("The filtered view is read-only; change the source collection.");
}
