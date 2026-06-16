namespace CircuitRF.Core.Design;

/// <summary>
/// The single thing you simulate: its own instance list (cell instances AND bare primitives,
/// including Port/Term), global variables, analyses, and measurements.
///
/// A TestBench IS the top-level container — it holds instances directly, exactly like a
/// top-level schematic. It does NOT point at a single TopCell; that artificial wrapper is gone.
/// Nothing ever instantiates a TestBench from above, so it has no Ports list.
/// Top-level port-ness comes from Port/Term primitives in the Instances list.
///
/// Analyses and measurements attach HERE, never to a Cell (data-model §2.1 invariant).
/// </summary>
public sealed class TestBench(string name)
{
    public string Name { get; } = name;

    /// <summary>Top-level contents: cell instances and bare primitives (incl. Port/Term).</summary>
    public List<Instance>     Instances       { get; } = [];
    public List<Variable>     GlobalVariables { get; } = [];
    public List<Analysis>     Analyses        { get; } = [];
    public List<Measurement>  Measurements    { get; } = [];

    /// <summary>
    /// Verbatim analysis/measure lines from .cnl that the reader cannot yet interpret.
    /// Preserved for round-trip fidelity. Replaced by typed entries once the directive
    /// grammar is settled in Phase 2.
    /// Kind = "analysis" or "measure"; RawLine = verbatim remainder after the keyword.
    /// </summary>
    public List<RawDirective> RawDirectives { get; } = [];

    /// <summary>
    /// Net names that came from a user-placed net label in the schematic (provenance set).
    /// Populated by NetExtractor; empty for hand-written netlists. Propagated to
    /// NodeMap.LabeledNames by the Elaborator and persisted in the __LabeledNodes DataCube.
    /// </summary>
    public HashSet<string> LabeledNets { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Opaque round-trippable record for a .cnl directive whose grammar is deferred.
/// </summary>
public sealed class RawDirective(string kind, string rawLine)
{
    public string Kind    { get; } = kind;    // "analysis" or "measure"
    public string RawLine { get; } = rawLine; // verbatim remainder of the line
}
