namespace CircuitRF.Core.Design;

/// <summary>
/// A reusable circuit definition: ports, parameter interface, cell-scoped variables,
/// and a list of instances (sub-cells or primitives). Cells never contain analyses.
/// </summary>
public sealed class Cell(string name)
{
    public string Name { get; } = name;

    public List<string>               Ports      { get; } = [];
    public List<ParameterDeclaration> Parameters { get; } = [];
    public List<Variable>             Variables  { get; } = [];
    public List<Instance>             Instances  { get; } = [];
}
