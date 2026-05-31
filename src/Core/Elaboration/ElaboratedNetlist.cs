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
}
