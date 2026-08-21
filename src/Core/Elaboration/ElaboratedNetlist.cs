using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Elaboration;

/// <summary>
/// The flattened, resolved result of elaborating a TestBench.
/// This is what the engine consumes. Nothing here is symbolic.
/// </summary>
public sealed class ElaboratedNetlist : IDisposable
{
    public List<ElaboratedComponent> Components { get; } = [];
    public NodeMap                   Nodes      { get; } = new();

    /// <summary>
    /// The ambient temperature this netlist was elaborated at, in °C — the design's own
    /// <c>temp</c> global, or <see cref="Devices.Temperature.NominalC"/> when it states none.
    ///
    /// <para>Device models already receive it at construction, so nothing in the numeric layer
    /// needed it until electrothermal devices arrived. Those bring a node whose voltage IS a
    /// temperature, and the ambient is the reference that node is supposed to sit above — which
    /// makes it a property of the circuit rather than of any one device, and therefore something
    /// the engine has to be able to see.</para>
    /// </summary>
    public double AmbientC { get; internal set; } = Devices.Temperature.NominalC;

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
    /// convergence issues, etc.). Routed into the UI Messages pane by
    /// <c>SchematicRunService</c>/<c>WorkspaceViewModel.RunAnalysis</c> — never written to
    /// Console.Error (brief-housekeeping-tearoff-palette-repo.md R-hk-9/R-hk-10: this was the
    /// single shared choke point still echoing every warning to the terminal after the prior
    /// per-model console-writing bugs were fixed).
    /// </summary>
    public IReadOnlyList<string> Warnings => _warnings;
    private readonly List<string> _warnings = [];
    private readonly HashSet<string> _seenWarningKeys = new(StringComparer.Ordinal);

    public void AddWarning(string message)
    {
        _warnings.Add(message);
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
    /// Things the run WORKED OUT and is reporting, rather than things it is unhappy about.
    ///
    /// <para><b>Why a separate list and not a warning.</b> A warning says something may be wrong. A
    /// note says circuitRF established something the design did not state and is telling you what it
    /// established — a resolution, not a complaint. Mixed into the warnings, a run that resolved
    /// everything correctly still reads as a run with problems, and the warnings that DO need
    /// attention are harder to pick out for it.</para>
    ///
    /// <para>Carried to the Messages pane at <c>Info</c> by the same route the warnings take.</para>
    /// </summary>
    public IReadOnlyList<string> Notes => _notes;
    private readonly List<string> _notes = [];

    /// <summary>Adds a note only if <paramref name="key"/> has not been seen in this run.</summary>
    public void AddNoteOnce(string key, string message)
    {
        if (_seenWarningKeys.Add(key)) _notes.Add(message);
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

    /// <summary>
    /// Releases anything a model of this netlist is holding OUTSIDE this process.
    ///
    /// <para><b>Almost every model needs nothing here</b> — a resistor is a few doubles the garbage
    /// collector reclaims on its own, and a netlist that is never disposed is no worse off than it was
    /// before this existed. The one that does need it is a device an external provider supplies: its
    /// instance lives in a WORKER process, which the collector cannot see and which outlives the run.
    /// A sweep re-elaborates once per point by design, so an undisposed netlist there leaks one such
    /// instance per point until the worker refuses to make another.</para>
    ///
    /// <para><b>Disposing does not invalidate a result.</b> What an analysis returns is a
    /// <c>DataSet</c> of numbers; nothing downstream holds a model. So a caller that runs a netlist
    /// and keeps the answer may dispose it the moment the run returns, which is what the sweep
    /// does.</para>
    ///
    /// <para>Idempotent, and one model failing to release does not stop the others — this runs while
    /// something is being thrown away, and a half-completed clean-up is worse than a slow one.</para>
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var component in Components)
            if (component.Model is IDisposable d)
            {
                try { d.Dispose(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { /* see remarks */ }
            }
    }

    private bool _disposed;
}
