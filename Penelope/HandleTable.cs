namespace Penelope;

/// <summary>
///     A registry mapping opaque Penelope handle ids to a backend's resource wrapper. Replaces the
///     hand-rolled <c>Dictionary&lt;ulong, T&gt;</c> + bare-indexer pattern that each backend
///     repeated per resource kind. The read indexer throws a descriptive, resource-labelled error
///     on a missing id (use-after-destroy / never-created) instead of the context-free
///     <see cref="KeyNotFoundException"/> a raw dictionary lookup gives.
///
///     <para>The surface intentionally mirrors the subset of <see cref="Dictionary{TKey,TValue}"/>
///     these backends use (indexer get/set, <see cref="TryGetValue"/>, <see cref="ContainsKey"/>,
///     <see cref="Remove(ulong)"/>, <see cref="Values"/>, <see cref="Count"/>) so a backend adopts
///     it by changing only the field declaration.</para>
///
///     <para>Id generation stays on the device (a single per-device counter shared across all
///     tables), so this type owns storage and lookup, not allocation.</para>
/// </summary>
internal sealed class HandleTable<T>
{
    private readonly Dictionary<ulong, T> _map = new();
    private readonly string _label;

    public HandleTable(string label) => _label = label;

    public T this[ulong id]
    {
        get => _map.TryGetValue(id, out var v)
            ? v
            : throw new InvalidOperationException(
                $"{_label} handle {id} is not registered — it was never created or has been destroyed (use-after-free).");
        set => _map[id] = value;
    }

    public bool TryGetValue(ulong id, out T value) => _map.TryGetValue(id, out value!);
    public bool ContainsKey(ulong id) => _map.ContainsKey(id);
    public bool Remove(ulong id) => _map.Remove(id);
    public bool Remove(ulong id, out T value) => _map.Remove(id, out value!);

    public int Count => _map.Count;
    public Dictionary<ulong, T>.ValueCollection Values => _map.Values;
}
