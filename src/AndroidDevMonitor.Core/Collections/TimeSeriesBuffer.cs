namespace AndroidDevMonitor.Core.Collections;

public sealed class TimeSeriesBuffer<T>(TimeSpan window, Func<T, DateTimeOffset> timestamp)
{
    private readonly object _gate = new();
    private readonly Queue<T> _items = new();

    public void Add(T value)
    {
        lock (_gate)
        {
            _items.Enqueue(value);
            var cutoff = timestamp(value) - window;
            while (_items.TryPeek(out var first) && timestamp(first) < cutoff) _items.Dequeue();
        }
    }

    public IReadOnlyList<T> Snapshot()
    {
        lock (_gate) return _items.ToArray();
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }
}
