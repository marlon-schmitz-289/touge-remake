namespace Penelope;

/// <summary>
///     Hashable cache key for <see cref="BindGroupLayoutDesc"/>. The desc itself isn't usable
///     as a Dictionary key because its <c>Entries</c> array uses reference equality. This
///     captures a defensive copy of the entries (small — typical layouts have 1–4 entries)
///     and ignores <c>DebugName</c>, so two callers asking for the same shape with different
///     debug labels share the underlying GPU layout.
/// </summary>
internal readonly struct BindGroupLayoutKey : IEquatable<BindGroupLayoutKey>
{
    private readonly BindGroupLayoutEntry[] _entries;
    private readonly int _hash;

    public BindGroupLayoutKey(in BindGroupLayoutDesc desc)
    {
        _entries = (BindGroupLayoutEntry[])desc.Entries.Clone();
        var hc = new HashCode();
        foreach (var e in _entries) hc.Add(e);
        _hash = hc.ToHashCode();
    }

    public bool Equals(BindGroupLayoutKey other)
    {
        if (_entries.Length != other._entries.Length) return false;
        for (var i = 0; i < _entries.Length; i++)
            if (!_entries[i].Equals(other._entries[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is BindGroupLayoutKey k && Equals(k);
    public override int GetHashCode() => _hash;
}
