// ONE designator pool across a cell's two primary views — brief-footprint-6 §5, R-fp6-4.
//
// THIS IS A CORRECTNESS FIX, NOT A NICETY. LayoutInstance.DisplayRefDes prefers SchematicId over
// RefDes, which is the design stating that R1 in the layout IS R1 in the schematic. Two independent
// name pools contradict that, and both collisions are reachable:
//
//   - the schematic holds R1, not yet pushed; a hand placement in the layout sees no R1 among the
//     layout's own instances, takes R1, and Update Layout later places the real R1 beside it;
//   - the layout holds a hand-placed R1; a schematic placement scans only schematic.Components,
//     takes R1, and Update Layout puts a SECOND R1 on the board.
//
// NEITHER CHOOSER GROWS A COPY OF THE OTHER'S SCAN (R-fp6-4b). SchematicEditModel.NextAvailableName
// and FootprintLabel.SeedDesignator each take the taken-set as a parameter; this is the one function
// that assembles it, and the only thing either of them knows about the other document.
//
// IT RENAMES NOTHING (R-fp6-4d). R-fp4b-8d's rule stands — a duplicate that already exists is
// REPORTED, never renumbered. Choosing a free name for the part being placed right now is the
// cheaper half of the same rule, not a reconciliation pass.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>
/// Every reference designator already spoken for in one cell — across its <b>primary</b> schematic
/// view and its <b>primary</b> layout view (R-fp6-4a).
/// </summary>
public static class DesignatorPool
{
    /// <summary>Every designator <paramref name="view"/>'s own placements draw — which already
    /// covers linked and hand-placed instances both, because <see cref="LayoutInstance.DisplayRefDes"/>
    /// is the one accessor and resolves either source.</summary>
    public static IEnumerable<string> NamesIn(LayoutView? view)
    {
        if (view is null) yield break;
        foreach (var inst in view.Instances)
            if (inst.DisplayRefDes is { Length: > 0 } d) yield return d;
    }

    /// <summary>Every instance name <paramref name="schematic"/>'s components carry.</summary>
    public static IEnumerable<string> NamesIn(SchematicEditModel? schematic)
    {
        if (schematic is null) yield break;
        foreach (var comp in schematic.Components)
            if (comp.InstanceName is { Length: > 0 } n) yield return n;
    }

    /// <summary>
    /// The absolute path of <paramref name="cellDir"/>'s primary view of <paramref name="type"/>, or
    /// null when there is none.
    ///
    /// <para><b>PRIMARY only</b> (R-fp6-4e): a non-primary layout view is a variant land pattern and
    /// is not on the board, and an instance draws its cell's primary view regardless. A cell with
    /// several views and none named primary contributes nothing, which is the same answer every other
    /// consumer of <see cref="CellFolder.ResolvePrimary"/> gives that state.</para>
    /// </summary>
    public static string? PrimaryViewPath(string? cellDir, ViewType type)
    {
        if (cellDir is not { Length: > 0 }) return null;
        try
        {
            var primary = CellFolder.ResolvePrimary(cellDir, type);
            if (primary.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent)) return null;
            return Path.Combine(CellFolder.SubFolderPath(cellDir, type), primary.ResolvedName!);
        }
        catch { return null; }   // an unreadable cell folder simply contributes no names
    }

    /// <summary>
    /// The designators taken in <paramref name="cellDir"/>'s primary view of <paramref name="type"/>,
    /// read from DISK. Empty when there is no such view, when it cannot be read, or when
    /// <paramref name="cellDir"/> is null — <b>a cell with only one of the two views loses nothing</b>
    /// (R-fp6-4f): the absent side contributes an empty set and both choosers behave exactly as they
    /// did before this existed.
    /// </summary>
    public static IReadOnlyList<string> ReadPrimary(string? cellDir, ViewType type)
    {
        if (PrimaryViewPath(cellDir, type) is not { Length: > 0 } path) return [];
        try
        {
            return type switch
            {
                ViewType.Layout    => [.. NamesIn(LayoutPersistence.LoadFromFile(path))],
                ViewType.Schematic => [.. NamesIn(SchematicPersistence.LoadFromFile(path).model)],
                _ => [],
            };
        }
        catch { return []; }   // a corrupt sibling is not a failed placement
    }
}

/// <summary>
/// <see cref="DesignatorPool.ReadPrimary"/>, read at most once per (path, write time) — R-fp6-4c.
///
/// <para>Placing twenty parts must not pay for the sibling document twenty times, so the parse is
/// cached. It is keyed on the file's own last-write time rather than invalidated by an event, which
/// makes "invalidated on save and on external change" a property of the key instead of a
/// subscription that can be forgotten: a save the user makes in the other window, and a file another
/// process rewrites, both move the stamp and both are picked up. The stat is one call; the parse is
/// what this exists to avoid.</para>
///
/// <para>Not thread-safe, and not required to be: it is reached from placement gestures on the UI
/// thread only.</para>
/// </summary>
public sealed class SiblingDesignatorCache
{
    private string? _path;
    private DateTime _stamp;
    private IReadOnlyList<string> _names = [];

    /// <summary>How many times the sibling has actually been READ AND PARSED. The gate asserts a
    /// counter rather than a clock, per the standing rule against timing tests.</summary>
    public int Reads { get; private set; }

    /// <summary>The designators taken in <paramref name="cellDir"/>'s primary <paramref name="type"/>
    /// view.</summary>
    public IReadOnlyList<string> Get(string? cellDir, ViewType type)
    {
        string? path = DesignatorPool.PrimaryViewPath(cellDir, type);
        if (path is not { Length: > 0 }) { _path = null; _names = []; return _names; }

        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); }
        catch { stamp = default; }

        if (string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) && _stamp == stamp)
            return _names;

        _names = DesignatorPool.ReadPrimary(cellDir, type);
        _path  = path;
        _stamp = stamp;
        Reads++;
        return _names;
    }
}
