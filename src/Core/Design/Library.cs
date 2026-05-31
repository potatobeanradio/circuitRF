namespace CircuitRF.Core.Design;

/// <summary>A named collection of Cell definitions.</summary>
public sealed class Library(string name)
{
    public string Name { get; } = name;
    public List<Cell> Cells { get; } = [];

    public Cell? Find(string cellName)
        => Cells.FirstOrDefault(c => c.Name == cellName);
}
