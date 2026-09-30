using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AndroidDevMonitor.Presentation.Collections;

namespace AndroidDevMonitor.Presentation.Tests;

public sealed class FilteredCollectionTests
{
    private sealed record Item(string Name, int Size);

    private static (ObservableCollection<Item> Source, FilteredCollection<Item> View, List<NotifyCollectionChangedEventArgs> Events) Create(
        Func<Item, bool>? filter = null, params Item[] items)
    {
        ObservableCollection<Item> source = new(items);
        FilteredCollection<Item> view = new(source, filter);
        List<NotifyCollectionChangedEventArgs> events = [];
        view.CollectionChanged += (_, e) => events.Add(e);
        return (source, view, events);
    }

    [Fact]
    public void Initial_view_applies_the_filter_in_source_order()
    {
        var (_, view, _) = Create(item => item.Size > 1, new Item("a", 1), new Item("b", 2), new Item("c", 3));
        Assert.Equal(["b", "c"], view.Select(item => item.Name));
    }

    [Fact]
    public void Appends_are_forwarded_as_single_adds_at_the_end()
    {
        var (source, view, events) = Create(item => item.Size > 1, new Item("a", 2));
        source.Add(new Item("skip", 0));
        source.Add(new Item("b", 5));
        Assert.Equal(["a", "b"], view.Select(item => item.Name));
        NotifyCollectionChangedEventArgs added = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Add, added.Action);
        Assert.Equal(1, added.NewStartingIndex);
    }

    [Fact]
    public void Prepends_go_to_the_front()
    {
        var (source, view, events) = Create(null, new Item("a", 1), new Item("b", 2));
        source.Insert(0, new Item("new", 3));
        Assert.Equal(["new", "a", "b"], view.Select(item => item.Name));
        Assert.Equal(0, Assert.Single(events).NewStartingIndex);
    }

    [Fact]
    public void Inserts_in_the_middle_rebuild_the_view()
    {
        var (source, view, events) = Create(null, new Item("a", 1), new Item("c", 3));
        source.Insert(1, new Item("b", 2));
        Assert.Equal(["a", "b", "c"], view.Select(item => item.Name));
        Assert.Equal(NotifyCollectionChangedAction.Reset, Assert.Single(events).Action);
    }

    [Fact]
    public void Removing_from_the_source_removes_visible_items_with_their_index()
    {
        Item hidden = new Item("hidden", 0);
        Item first = new Item("first", 5);
        Item second = new Item("second", 6);
        var (source, view, events) = Create(item => item.Size > 1, hidden, first, second);
        source.Remove(hidden);
        Assert.Empty(events);
        source.RemoveAt(0);
        NotifyCollectionChangedEventArgs removed = Assert.Single(events);
        Assert.Equal(NotifyCollectionChangedAction.Remove, removed.Action);
        Assert.Equal(0, removed.OldStartingIndex);
        Assert.Same(second, Assert.Single(view));
    }

    [Fact]
    public void Clear_and_refresh_raise_reset()
    {
        var (source, view, events) = Create(null, new Item("a", 1));
        source.Clear();
        Assert.Empty(view);
        view.Refresh();
        Assert.All(events, e => Assert.Equal(NotifyCollectionChangedAction.Reset, e.Action));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public void Changing_the_filter_re_evaluates_every_item()
    {
        var (_, view, _) = Create(null, new Item("a", 1), new Item("b", 2));
        view.Filter = item => item.Name == "b";
        Assert.Equal(["b"], view.Select(item => item.Name));
        view.Filter = null;
        Assert.Equal(2, view.Count);
    }

    [Fact]
    public void Sorted_view_keeps_new_items_in_order_and_equal_keys_stable()
    {
        var (source, view, _) = Create(null, new Item("small", 1), new Item("large", 9), new Item("mid", 5));
        view.Sort = Comparer<Item>.Create((a, b) => b.Size.CompareTo(a.Size));
        Assert.Equal(["large", "mid", "small"], view.Select(item => item.Name));
        source.Add(new Item("mid2", 5));
        source.Insert(0, new Item("huge", 100));
        Assert.Equal(["huge", "large", "mid", "mid2", "small"], view.Select(item => item.Name));
    }

    [Fact]
    public void View_is_read_only_through_list_interfaces()
    {
        var (_, view, _) = Create(null, new Item("a", 1));
        Assert.True(((System.Collections.IList)view).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ((IList<Item>)view).Add(new Item("b", 2)));
        Assert.Equal(0, ((System.Collections.IList)view).IndexOf(view[0]));
    }
}
