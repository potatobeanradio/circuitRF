namespace CircuitRF.Core.Elaboration;

/// <summary>
/// Bidirectional mapping between net names (qualified by instance path) and integer indices.
/// Ground is always node 0 (net name "0").
/// </summary>
public sealed class NodeMap
{
    private readonly Dictionary<string, int> _nameToIndex = new(StringComparer.Ordinal);
    private readonly List<string>            _indexToName = [];
    private readonly List<(string Name, int Node)> _aliases = [];

    public NodeMap()
    {
        // ground = 0
        _nameToIndex["0"] = 0;
        _indexToName.Add("0");
    }

    /// <summary>brief-paa-8b — a copy: every name at the same index, the aliases and the labelled names.</summary>
    internal NodeMap Copy()
    {
        var copy = new NodeMap();
        for (int i = 1; i < _indexToName.Count; i++) copy.GetOrAssign(_indexToName[i]);
        foreach (var (name, node) in _aliases) copy.AddAlias(name, node);
        copy.LabeledNames.UnionWith(LabeledNames);
        return copy;
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

    /// <summary>
    /// A SECOND name for a node that already has one — what a placed <c>VProbe</c> leaves behind.
    ///
    /// <para>An alias never becomes a node. It carries no matrix row, so adding one cannot change a
    /// solution; what it changes is the result, where the aliased node appears a second time under
    /// the alias's own name. That is the whole mechanism behind "a voltage probe reports and does
    /// not intrude": the net keeps whatever name the user's own label gave it, and the probe's name
    /// is added beside it rather than over it.</para>
    ///
    /// <para>Uniqueness is the CALLER's to enforce, before it gets here — the elaborator refuses a
    /// run where an alias collides with a net name or with another alias, because either would make
    /// one name mean two things in the results. <see cref="AddAlias"/> asserts what it can (a name
    /// that is already a NODE) so the invariant cannot be broken silently by a future caller.</para>
    /// </summary>
    public IReadOnlyList<(string Name, int Node)> Aliases => _aliases;

    /// <summary>Records <paramref name="alias"/> as a second name for node <paramref name="node"/>.</summary>
    public void AddAlias(string alias, int node)
    {
        if (_nameToIndex.ContainsKey(alias))
            throw new InvalidOperationException(
                $"'{alias}' is already a net name, so it cannot also be an alias.");
        if (node <= 0 || node >= _indexToName.Count)
            throw new ArgumentOutOfRangeException(nameof(node),
                $"Alias '{alias}' names node {node}, which is not a non-ground node of this netlist.");

        _nameToIndex[alias] = node;   // so V("<alias>") and any name→index lookup resolve
        _aliases.Add((alias, node));
    }

    /// <summary>
    /// The rows a result's node axis carries, in order: every non-ground node under its own name,
    /// then every alias under its own name pointing back at the node it names.
    ///
    /// <para>One function rather than the same loop in each packer, because the ALIAS half is easy
    /// to add to one engine and forget in another — and a probe that reports under DC but not under
    /// HB is worse than one that does not exist.</para>
    /// </summary>
    /// <param name="excludeInternal">Drop <c>__</c>-prefixed nodes the elaborator minted (the HB
    /// engines' own rule). DC keeps them, as it always has.</param>
    public (int[] Nodes, string[] Names) ResultRows(bool excludeInternal)
    {
        var nodes = new List<int>(_indexToName.Count + _aliases.Count);
        var names = new List<string>(nodes.Capacity);

        for (int i = 1; i < _indexToName.Count; i++)
        {
            if (excludeInternal && _indexToName[i].StartsWith("__", StringComparison.Ordinal)) continue;
            nodes.Add(i);
            names.Add(_indexToName[i]);
        }
        foreach (var (name, node) in _aliases)
        {
            nodes.Add(node);
            names.Add(name);
        }
        return ([.. nodes], [.. names]);
    }
}
