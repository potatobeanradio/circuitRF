using CircuitRF.Core.Devices;
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

    /// <summary>
    /// Names of global variables declared with an explicit unit in the design layer.
    /// Used by <c>FreqUnit.ResolveHz</c> for the var-unit-wins rule: a referenced variable in
    /// this set already carries its unit in its resolved Hz value, so the field unit is ignored.
    /// </summary>
    public IReadOnlyCollection<string> GlobalsWithExplicitUnit => _globalsWithExplicitUnit;
    private readonly HashSet<string> _globalsWithExplicitUnit = new(StringComparer.Ordinal);
    internal void MarkGlobalHasUnit(string name) => _globalsWithExplicitUnit.Add(name);

    public void AddComponent(ElaboratedComponent c)
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

    /// <summary>
    /// Elaboration and engine run-time warnings (buried Terms, duplicate Num, regularization, HB
    /// convergence issues, etc.). Also written to Console.Error for headless runs.
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;
    private readonly List<string> _warnings = [];
    private readonly HashSet<string> _seenWarningKeys = new(StringComparer.Ordinal);

    public void AddWarning(string message)
    {
        _warnings.Add(message);
        Console.Error.WriteLine($"[circuitRF] {message}");
    }

    /// <summary>
    /// Adds a warning only if <paramref name="key"/> has not been seen in this run.
    /// Prevents repeated identical warnings (e.g. per-frequency regularization messages)
    /// from flooding the list — only the first occurrence is recorded.
    /// </summary>
    public void AddWarningOnce(string key, string message)
    {
        if (_seenWarningKeys.Add(key))
            AddWarning(message);
    }

    /// <summary>
    /// R-mk-7/R-mk-8 (brief-mklopf-performance-and-messages.md): after stamping a component, drains
    /// and records any warnings it accumulated during <c>Stamp</c> (e.g. a microstrip validity-range
    /// violation) — the ONLY route from deep inside a per-frequency <c>Stamp()</c> call, which has no
    /// netlist reference of its own, into <see cref="Warnings"/> and therefore into the Messages UI.
    /// A no-op for any model that does not implement <see cref="IReportsWarnings"/>.
    /// </summary>
    public void DrainModelWarnings(ComponentModel model)
    {
        if (model is IReportsWarnings rw)
            foreach (var (key, message) in rw.DrainWarnings())
                AddWarningOnce(key, message);
    }
}
