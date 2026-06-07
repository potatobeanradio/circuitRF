namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Uniform-grid spatial index over schematic world space.
/// Enables O(visible cells) viewport queries instead of O(all items).
///
/// Build once from the model; query each frame for visible items.
/// Items that span multiple cells are inserted into all overlapping cells.
/// The viewport query returns a conservative set — callers still do the
/// per-item bounding-box test for exact culling.
/// </summary>
public sealed class SchematicSpatialIndex
{
    private readonly Dictionary<(int Cx, int Cy), List<int>> _compCells = new();
    private readonly Dictionary<(int Cx, int Cy), List<int>> _wireCells = new();
    private readonly double _cellSize;

    public int ComponentCount { get; }
    public int WireCount      { get; }

    public SchematicSpatialIndex(SchematicModel model, double cellSize = 1500.0)
    {
        _cellSize      = cellSize;
        ComponentCount = model.Components.Count;
        WireCount      = model.Wires.Count;

        for (int i = 0; i < model.Components.Count; i++)
        {
            var c = model.Components[i];
            Insert(_compCells, i, c.BbMinX, c.BbMinY, c.BbMaxX, c.BbMaxY);
        }

        for (int i = 0; i < model.Wires.Count; i++)
        {
            var w = model.Wires[i];
            Insert(_wireCells, i, w.BbMinX, w.BbMinY, w.BbMaxX, w.BbMaxY);
        }
    }

    private void Insert(Dictionary<(int, int), List<int>> cells, int idx,
                        double minX, double minY, double maxX, double maxY)
    {
        int cx0 = CellCoord(minX), cy0 = CellCoord(minY);
        int cx1 = CellCoord(maxX), cy1 = CellCoord(maxY);

        for (int cx = cx0; cx <= cx1; cx++)
        for (int cy = cy0; cy <= cy1; cy++)
        {
            var key = (cx, cy);
            if (!cells.TryGetValue(key, out var list))
                cells[key] = list = [];
            list.Add(idx);
        }
    }

    private int CellCoord(double v) => (int)Math.Floor(v / _cellSize);

    /// <summary>
    /// Returns the (possibly non-unique) component and wire indices that overlap the
    /// given world viewport. Callers should still test each item's bounding box.
    /// </summary>
    public void QueryViewport(
        double vpMinX, double vpMinY, double vpMaxX, double vpMaxY,
        HashSet<int> outComponents, HashSet<int> outWires)
    {
        int cx0 = CellCoord(vpMinX), cy0 = CellCoord(vpMinY);
        int cx1 = CellCoord(vpMaxX), cy1 = CellCoord(vpMaxY);

        for (int cx = cx0; cx <= cx1; cx++)
        for (int cy = cy0; cy <= cy1; cy++)
        {
            if (_compCells.TryGetValue((cx, cy), out var comps))
                foreach (int i in comps) outComponents.Add(i);
            if (_wireCells.TryGetValue((cx, cy), out var wires))
                foreach (int i in wires) outWires.Add(i);
        }
    }
}
