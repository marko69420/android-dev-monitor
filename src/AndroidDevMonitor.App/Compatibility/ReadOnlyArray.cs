using System.Collections;

internal sealed class _003C_003Ez__ReadOnlyArray<T>(T[] items) : IReadOnlyList<T>
{
    public int Count => items.Length;

    public T this[int index] => items[index];

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
}
