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
    /// places it by. A schematic-only sub-cell, or a model-library part (<c>vendormodel://</c>, never a folder), would be skipped by
    /// the planner and so be missing from the EM result; it has no layout.</remarks>
    /// <param name="schematicDir">The directory of the schematic <paramref name="c"/> is placed in: what its <c>CellRef</c> is
    /// relative to.</param>
    public static EmPartKind Of(EditableComponent c, string? schematicDir)
    {
        ArgumentNullException.ThrowIfNull(c);
        if (c.Footprint is { Length: > 0 }) return EmPartKind.Part;
        if (SchematicLayoutPlanner.HasPCellGenerator(c.Symbol, c.PortCount, out _)) return EmPartKind.Copper;
        if (c.CellRef is { Length: > 0 } cellRef)
        {
            if (cellRef.StartsWith("pdk://", StringComparison.OrdinalIgnoreCase)) return EmPartKind.Part;
            return HasLayoutView(cellRef, schematicDir) ? EmPartKind.Copper : EmPartKind.NoLayout;
        }
        return EmPartKind.NoLayout;
    }

    private static bool HasLayoutView(string cellRef, string? schematicDir)
    {
        if (CircuitRF.Design.VendorLibraries.ModelLibrary.VendorModelRefs.IsRef(cellRef) || schematicDir is not { Length: > 0 }) return false;
        try
        {
            if (CircuitRF.Design.Workspace.ExternalCellRef.ResolveCellDir(cellRef, schematicDir) is not { } dir || !Directory.Exists(dir))
                return false;
            var primary = CircuitRF.Design.Cells.CellFolder.ResolvePrimary(dir, CircuitRF.Design.Cells.ViewType.Layout);
            return primary.State is CircuitRF.Design.Cells.PrimaryState.SoleFile or CircuitRF.Design.Cells.PrimaryState.NamedPresent;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
