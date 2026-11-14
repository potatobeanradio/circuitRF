// brief-icp-1 R-icp1-1 — what a schematic part is to an EM result, asked by two callers: an EM cell deciding which of its
// schematic's parts the re-planned layout's solve can stand for (brief-sdl-6 R-sdl6-2, EmModelSwitch), and the component-port
// projection deciding which placed instances "*" takes (EmCellInstances). It was EmModelSwitch's private KindOf; it moved here
// unchanged so the two read one rule, and a part that an EM cell calls copper is a part "*" leaves in the problem.

using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Em;

/// <summary>What a placed part is to an EM result.</summary>
public enum EmPartKind
{
    /// <summary>Its layout is metal the solver can solve: generated geometry (a line, a tee, a bend, a spiral) or a sub-cell's
    /// drawn layout.</summary>
    Copper,
    /// <summary>A footprint or a kit's artwork stands for it: its pads are copper, the part itself is not.</summary>
    Part,
    /// <summary>It has no layout at all.</summary>
    NoLayout,
}

public static class EmPartKinds
{
    /// <summary>
    /// What <paramref name="c"/> is to an EM result: copper when its layout is generated geometry (a line, a tee, a bend — the
    /// microstrip family) or a sub-cell's drawn layout; a part when a footprint or a kit's artwork stands for it (its pads are
    /// copper, the part itself is not); nothing when it has no layout at all.
    /// </summary>
    /// <remarks>A sub-cell is copper only when its reference resolves to a cell folder with a layout view — the test the planner
    /// places it by — and its own schematic places no part without a layout (else it is a device: brief-icp-9). A schematic-only sub-cell, or a model-library part (<c>vendormodel://</c>, never a folder), would be skipped by
    /// the planner and so be missing from the EM result; it has no layout.</remarks>
    /// <param name="schematicDir">The directory of the schematic <paramref name="c"/> is placed in: what its <c>CellRef</c> is
    /// relative to.</param>
    public static EmPartKind Of(EditableComponent c, string? schematicDir)
    {
        var kind = Shallow(c, schematicDir, out string? cellDir);
        // brief-icp-9 (ICP-0 D3: a cell "whose model is a device ... anything the EM cannot represent as metal") — a sub-cell whose
        // own schematic places a part with no layout (a transistor, a controlled source, an ideal R or C) is a model with artwork
        // drawn for it, not metal the solver can stand for: its layout is pads around something the EM cannot see. A sub-cell
        // made of lines, PCells, footprint parts or further sub-cells stays copper, and "*" walks down through it as before.
        return kind == EmPartKind.Copper && cellDir is not null && IsModelCell(cellDir) ? EmPartKind.Part : kind;
    }

    /// <summary>What <paramref name="c"/> is by its own reference alone, its sub-cell's schematic unread.</summary>
    private static EmPartKind Shallow(EditableComponent c, string? schematicDir, out string? cellDir)
    {
        ArgumentNullException.ThrowIfNull(c);
        cellDir = null;
        if (c.Footprint is { Length: > 0 }) return EmPartKind.Part;
        if (SchematicLayoutPlanner.HasPCellGenerator(c.Symbol, c.PortCount, out _)) return EmPartKind.Copper;
        if (c.CellRef is { Length: > 0 } cellRef)
        {
            if (cellRef.StartsWith("pdk://", StringComparison.OrdinalIgnoreCase)) return EmPartKind.Part;
            cellDir = LayoutCellDir(cellRef, schematicDir);
            return cellDir is not null ? EmPartKind.Copper : EmPartKind.NoLayout;
        }
        return EmPartKind.NoLayout;
    }

    /// <summary>Whether <paramref name="cellDir"/>'s primary schematic places a physical part that has no layout of its own: a
    /// device model the cell's layout is drawn for. Memoised on the schematic's path and write time; false when it has none or
    /// cannot be read.</summary>
    private static bool IsModelCell(string cellDir)
    {
        try
        {
            var primary = CircuitRF.Design.Cells.CellFolder.ResolvePrimary(cellDir, CircuitRF.Design.Cells.ViewType.Schematic);
            if (primary.ResolvedName is not { Length: > 0 } name) return false;
            string sch = Path.Combine(CircuitRF.Design.Cells.CellFolder.SubFolderPath(cellDir, CircuitRF.Design.Cells.ViewType.Schematic), name);
            var info = new FileInfo(sch);
            if (!info.Exists) return false;
            // brief-icp-10 — the cell's own layout says which of those parts it draws: a part placed there (an instance carrying its
            // SchematicId, as Update Layout from Schematic writes) HAS layout, whatever its schematic reference resolves to. A kit cell
            // has no layout view of its own — its layout is a PCell the kit links to the part — so without this a sub-block whose
            // schematic places a kit transistor read as a model, "*" stopped at the sub-block, and the transistor inside was never cut.
            var placed = LayoutPlacedIds(cellDir, out string layoutStamp);
            string stamp = info.FullName + "|" + info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + layoutStamp;
            lock (_gate) if (_models.TryGetValue(stamp, out bool known)) return known;
            var model = SchematicPersistence.LoadFromFile(sch).model;
            string dir = Path.GetDirectoryName(sch)!;
            bool device = model.Components.Any(p => SchematicLayoutPlanner.IsPhysical(p) && !PortPlacement.IsPort(p) && p.InstanceName.Length > 0
                                                    && !placed.Contains(p.InstanceName)
                                                    && Shallow(p, dir, out _) == EmPartKind.NoLayout);
            lock (_gate) _models[stamp] = device;
            return device;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>brief-icp-10 — the SchematicIds the cell's primary layout places, and a stamp of that file; empty when it has none or cannot
    /// be read.</summary>
    private static HashSet<string> LayoutPlacedIds(string cellDir, out string stamp)
    {
        stamp = "";
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var primary = CircuitRF.Design.Cells.CellFolder.ResolvePrimary(cellDir, CircuitRF.Design.Cells.ViewType.Layout);
            if (primary.ResolvedName is not { Length: > 0 } name) return ids;
            string clay = Path.Combine(CircuitRF.Design.Cells.CellFolder.SubFolderPath(cellDir, CircuitRF.Design.Cells.ViewType.Layout), name);
            var info = new FileInfo(clay);
            if (!info.Exists) return ids;
            stamp = info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var inst in LayoutPersistence.LoadFromFile(clay).Instances)
                if (inst.SchematicId is { Length: > 0 } sid) ids.Add(sid);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException)
        {
        }
        return ids;
    }

    private static readonly Dictionary<string, bool> _models = new(StringComparer.Ordinal);
    private static readonly Lock _gate = new();

    /// <summary>The cell folder <paramref name="cellRef"/> names when it has a layout view, else null.</summary>
    private static string? LayoutCellDir(string cellRef, string? schematicDir)
    {
        if (CircuitRF.Design.VendorLibraries.ModelLibrary.VendorModelRefs.IsRef(cellRef) || schematicDir is not { Length: > 0 }) return null;
        try
        {
            if (CircuitRF.Design.Workspace.ExternalCellRef.ResolveCellDir(cellRef, schematicDir) is not { } dir || !Directory.Exists(dir))
                return null;
            var primary = CircuitRF.Design.Cells.CellFolder.ResolvePrimary(dir, CircuitRF.Design.Cells.ViewType.Layout);
            return primary.State is CircuitRF.Design.Cells.PrimaryState.SoleFile or CircuitRF.Design.Cells.PrimaryState.NamedPresent ? dir : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
