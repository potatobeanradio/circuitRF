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

    /// <summary>The cell's named parameter sets — <c>corner</c> lines inside its <c>define</c> (brief vendorlib-13).</summary>
    public List<CellCorner>           Corners    { get; } = [];

    /// <summary>
    /// The library part number this definition was placed from (<c>part &lt;number&gt;</c> inside its <c>define</c>),
    /// or null for a cell that is not a placed library model. What a bench corner's <c>part:</c> axis and the
    /// statistics line's held parts find a part's instances by, in a netlist that no longer names the library.
    /// </summary>
    public string? Part { get; set; }

    /// <summary>A copy sharing every list member, with <paramref name="corner"/>'s values as the defaults of the
    /// parameters it binds (a binding without a unit keeps the declaration's).</summary>
    public Cell CopyWith(CellCorner corner)
    {
        var c = CopyShallow(Name);
        c.Parameters.Clear();
        foreach (var p in Parameters)
        {
            var b = corner.Bindings.FirstOrDefault(x => x.Name == p.Name);
            c.Parameters.Add(b is null ? p : new ParameterDeclaration(p.Name, b.Expression, b.Unit ?? p.Unit, p.Hidden));
        }
        return c;
    }

    /// <summary>A copy under <paramref name="name"/> holding the same members (the lists are new, their items shared).</summary>
    public Cell CopyShallow(string name)
    {
        var c = new Cell(name) { Part = Part };
        c.Ports.AddRange(Ports);
        c.Parameters.AddRange(Parameters);
        c.Variables.AddRange(Variables);
        c.Instances.AddRange(Instances);
        c.Corners.AddRange(Corners);
        return c;
    }
}
