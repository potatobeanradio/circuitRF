using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Elaboration;

/// <summary>
/// A single flattened, resolved primitive component in the elaborated netlist.
/// InstancePath is the full dot-separated path from the top ("X1.R1").
/// Parameters are fully resolved to kinded Real/Complex values; units applied.
/// </summary>
public sealed class ElaboratedComponent(
    string componentType,
    string instancePath,
    int[] nodes,
    IReadOnlyDictionary<string, Value> parameters,
    ComponentModel model)
{
    public string     ComponentType { get; } = componentType;
    public string     InstancePath  { get; } = instancePath;
    public int[]      Nodes         { get; } = nodes;
    public int        ReferenceNode { get; init; } = 0;

    /// <summary>Fully resolved parameter values; each is Real or Complex, units applied.</summary>
    public IReadOnlyDictionary<string, Value> Parameters { get; } = parameters;

    public ComponentModel Model       { get; } = model;
    public bool           IsNonlinear => Model.Kind == ModelKind.Nonlinear;
}
