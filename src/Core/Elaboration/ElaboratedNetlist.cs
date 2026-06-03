using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Elaboration;

/// <summary>
/// The flattened, resolved result of elaborating a TestBench.
/// This is what the engine consumes. Nothing here is symbolic.
/// </summary>
public sealed class ElaboratedNetlist
{
    public List<ElaboratedComponent> Components { get; } = [];
    public NodeMap                   Nodes      { get; } = new();

    /// <summary>Indices into Components whose Model is nonlinear (HB partition seed).</summary>
    public IReadOnlyList<int> NonlinearComponents => _nonlinearComponents;
    private readonly List<int> _nonlinearComponents = [];

    /// <summary>Node indices touched by any nonlinear component.</summary>
    public IReadOnlySet<int> NonlinearNodes => _nonlinearNodes;
    private readonly HashSet<int> _nonlinearNodes = [];

    /// <summary>
    /// Fully resolved global variable values (Real or Complex), populated by the Elaborator.
    /// The HB engine uses these to resolve analysis directive expressions
    /// (e.g. Tone=RFfreq → ResolvedGlobals["RFfreq"]) and to re-evaluate sweep-dependent
    /// expressions at each sweep step.
    /// </summary>
    public IReadOnlyDictionary<string, Value> ResolvedGlobals => _resolvedGlobals;
    private readonly Dictionary<string, Value> _resolvedGlobals = new(StringComparer.Ordinal);

    internal void AddComponent(ElaboratedComponent c)
    {
        int idx = Components.Count;
        Components.Add(c);
        if (c.IsNonlinear)
        {
            _nonlinearComponents.Add(idx);
            foreach (var n in c.Nodes) _nonlinearNodes.Add(n);
        }
    }

    internal void SetResolvedGlobal(string name, Value val) => _resolvedGlobals[name] = val;
}
