namespace CircuitRF.Core.Elaboration;

/// <summary>
/// Bidirectional mapping between net names (qualified by instance path) and integer indices.
/// Ground is always node 0 (net name "0").
/// </summary>
public sealed class NodeMap
{
    private readonly Dictionary<string, int> _nameToIndex = new(StringComparer.Ordinal);
    private readonly List<string>            _indexToName = [];

    public NodeMap()
    {
        // ground = 0
        _nameToIndex["0"] = 0;
        _indexToName.Add("0");
    }

    /// <summary>Gets or assigns an index for the given net name.</summary>
    public int GetOrAssign(string netName)
    {
        if (_nameToIndex.TryGetValue(netName, out var idx)) return idx;
        idx = _indexToName.Count;
        _nameToIndex[netName] = idx;
        _indexToName.Add(netName);
        return idx;
    }

    public int     Count                         => _indexToName.Count;
    public string  NameOf(int index)             => _indexToName[index];
    public bool    TryGetIndex(string name, out int index) => _nameToIndex.TryGetValue(name, out index);
    public int     IndexOf(string name)          => _nameToIndex[name];
    public IReadOnlyList<string> AllNames        => _indexToName;

    /// <summary>
    /// Net names that originated from a user-placed schematic net label (propagated from
    /// TestBench.LabeledNets by the Elaborator). Empty for hand-written netlists.
    /// </summary>
    public HashSet<string> LabeledNames { get; } = new(StringComparer.Ordinal);
}
