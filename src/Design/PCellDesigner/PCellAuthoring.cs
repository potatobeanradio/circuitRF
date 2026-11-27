// Owner: PCD-11 (brief-pcd-11). Framework-free.
//
// Authoring on the designer's canvas as schematic edits: a part placed by an offset from the nearest pin, drawn metal
// from clicked corners, the free pins a port can go on. Every coordinate is written as a literal in the cell's length
// unit, relative to a pin (S1): an expression the user can later rewrite in parameters.

using System.Globalization;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.PCellDesigner;

/// <summary>brief-pcd-11: the canvas gestures that make new parts, as pure functions of the schematic.</summary>
public static class PCellAuthoring
{
    /// <summary>A length of <paramref name="dbu"/> database units as a literal in <paramref name="unit"/>, rounded to the
    /// database unit and written in six significant figures (D3).</summary>
    public static string Literal(double dbu, string unit, int dbuPerMicron)
    {
        double metres = Math.Round(dbu) / dbuPerMicron * 1e-6;
        double scale = Units.Scale(UnitNormalizer.ToEngineUnit(unit)) ?? 1.0;
        double v = Math.Round(metres / scale, 9);
        return (v == 0 ? 0.0 : v).ToString("G6", CultureInfo.InvariantCulture);
    }

    /// <summary>(along, across) of <paramref name="x"/>, <paramref name="y"/> from <paramref name="anchor"/> in its frame:
    /// along its outward direction, and 90° counter-clockwise from it (PCD-0 §4.2).</summary>
    public static (double Along, double Across) InFrame(PinPose anchor, double x, double y)
    {
        double rad = anchor.OutwardDeg * Math.PI / 180, c = Math.Cos(rad), s = Math.Sin(rad);
        double dx = x - anchor.X, dy = y - anchor.Y;
        return (dx * c + dy * s, -dx * s + dy * c);
    }

    /// <summary>
    /// R-pcd11-2: <paramref name="part"/> added with a new designator, placed by an offset from
    /// <paramref name="anchorInstance"/>.<paramref name="anchorPin"/> so its pin 1 lands at (<paramref name="x"/>,
    /// <paramref name="y"/>): <c>Dx</c>, <c>Dy</c> literals in <paramref name="unit"/>, <c>Angle</c> 0. Never mutates
    /// <paramref name="current"/>.
    /// </summary>
    public static (SchematicEditModel Schematic, string InstanceName) OffsetPlaced(
        SchematicEditModel current, EditableComponent part, string anchorInstance, int anchorPin, PinPose anchor,
        long x, long y, int dbuPerMicron, string unit)
    {
        var (along, across) = InFrame(anchor, x, y);
        var next = CellNetlistMerge.Copy(current);
        var added = part.Clone();
        added.InstanceName = SchematicEditModel.NextAvailableName(next.Components, ComponentTypeRegistry.InstancePrefix(part.Symbol));
        double right = next.Components.Select(c => c.X).DefaultIfEmpty(0).Max();
        (added.X, added.Y) = (Math.Ceiling((right + 2000) / NetlistSchematic.Grid) * NetlistSchematic.Grid, 0);
        added.Offset = new OffsetPlacement
        {
            Anchor = $"{anchorInstance}.{anchorPin}", Pin = 1, Unit = unit, Angle = "0",
            Dx = Literal(along, unit, dbuPerMicron), Dy = Literal(across, unit, dbuPerMicron),
        };
        next.Components.Add(added);
        return (next, added.InstanceName);
    }

    /// <summary>
    /// R-pcd11-4: an <c>MSHAPE</c> from clicked <paramref name="corners"/> (world DBU), its pin 1 at
    /// <paramref name="pin1"/> facing back along <paramref name="frameDeg"/> (the anchor pin's outward direction), the
    /// corners written relative to pin 1 in that frame in <paramref name="unit"/>. Pin 1's width is
    /// <paramref name="pinWidth"/> (the anchor part's <c>W</c>) when given, else the shape's extent across the frame.
    /// <c>Model</c> is <c>"open"</c>.
    /// </summary>
    public static EditableComponent Shape(IReadOnlyList<(long X, long Y)> corners, (long X, long Y) pin1, double frameDeg,
                                          int dbuPerMicron, string unit, EditableParameter? pinWidth = null)
    {
        var frame = new PinPose(pin1.X, pin1.Y, frameDeg);
        var local = corners.Select(c => InFrame(frame, c.X, c.Y)).ToList();
        var shape = new EditableComponent { Symbol = SymbolKind.MShape };
        foreach (var d in ComponentTypeRegistry.DefaultParameters(SymbolKind.MShape, 1))
            if (!d.Name.StartsWith("X[", StringComparison.Ordinal) && !d.Name.StartsWith("Y[", StringComparison.Ordinal))
                shape.Parameters.Add(new EditableParameter { Name = d.Name, Expression = d.Expression, Unit = d.Unit,
                                                             ShowOnSchematic = d.ShowOnSchematic, Dimension = d.Dimension });
        void Set(string name, string expression, string rowUnit)
        {
            var row = shape.Parameters.FirstOrDefault(p => p.Name == name);
            if (row is null) shape.Parameters.Add(row = new EditableParameter { Name = name, Dimension = UnitDimension.Length });
            (row.Expression, row.Unit) = (expression, rowUnit);
        }
        for (int i = 0; i < local.Count; i++)
        {
            Set(DrawnMetal.X(i + 1), Literal(local[i].Along, unit, dbuPerMicron), unit);
            Set(DrawnMetal.Y(i + 1), Literal(local[i].Across, unit, dbuPerMicron), unit);
        }
        Set(DrawnMetal.PinX(1), "0", unit);
        Set(DrawnMetal.PinY(1), "0", unit);
        if (pinWidth is not null) Set(DrawnMetal.PinW(1), pinWidth.Expression, pinWidth.Unit);
        else Set(DrawnMetal.PinW(1), Literal(local.Max(p => p.Across) - local.Min(p => p.Across), unit, dbuPerMicron), unit);
        shape.Parameters.First(p => p.Name == DrawnMetal.Model).Expression = $"\"{DrawnMetal.ModelOpen}\"";
        return shape;
    }

    /// <summary>The pins a port can go on: a part's pin whose net no other pin is on (ports, grounds and references
    /// left out), in netlist order. With <paramref name="ports"/>, the ports' own free pins instead: where the first
    /// part of an empty cell goes (R-pcd11-1).</summary>
    public static IReadOnlyList<(string Instance, int Pin)> FreePins(SchematicEditModel schematic, bool ports = false)
    {
        var tb = SchematicCircuit.Extract(CellNetlistMerge.Copy(schematic), "cell").TestBench;
        var pins = tb.Instances.SelectMany(i => i.NetBindings.Select((n, k) => (i, Pin: k + 1, Net: n))).ToList();
        var portNames = schematic.Components.Where(PortPlacement.IsPort).Select(c => c.InstanceName).ToHashSet(StringComparer.Ordinal);
        return [.. pins.Where(p => p.Net != "0" && portNames.Contains(p.i.InstanceName) == ports && pins.Count(q => q.Net == p.Net) == 1)
                       .Select(p => (p.i.InstanceName, p.Pin))];
    }
}
