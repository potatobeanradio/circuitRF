namespace CircuitRF.Design.Cells;

// ──────────────────────────────────────────────────────────────────────────────
//  CellFolder — cell folder layout + primacy resolution.
//  Framework-free (no Avalonia / Skia).  The filesystem is truth; this helper
//  reads structure and resolves primacy per workspace-and-project-tree.md §2.
//  This is the single source of primacy truth for the project tree (step 3) and
//  the cell-reference model (step 5).
// ──────────────────────────────────────────────────────────────────────────────

// ── View-type enum + resolution result ───────────────────────────────────────

/// <summary>The four view types that can live inside a cell folder.</summary>
/// <remarks>
/// <b>A fifth member is a change to every <c>switch</c> on this type</b>, and most of them throw on a
/// value they do not know (brief-em3d-41 R-em3d41-1c). <c>ThreeDViewTypeTests</c> enumerates this
/// type through the functions that must accept every member, so a new one fails there first.
/// </remarks>
public enum ViewType
{
    Schematic,
    Symbol,
    Layout,

    /// <summary>A cell's 3D view — a <c>.c3d</c> in the cell's <c>3d/</c> sub-folder
    /// (brief-em3d-41). Named <c>ThreeD</c> because an identifier cannot start with a digit; the user
    /// sees "3D".</summary>
    ThreeD,
}

/// <summary>
/// The result of resolving the primary view file for one view type in a cell folder.
/// See workspace-and-project-tree.md §2 for the five-branch rule.
/// </summary>
public sealed class PrimaryResolution
{
    public PrimaryState State        { get; init; }
    /// <summary>The resolved primary filename (SoleFile or NamedPresent branches).</summary>
    public string?      ResolvedName { get; init; }
    /// <summary>The filename named in .ccell that is absent (MissingNamedPrimary branch only).</summary>
    public string?      MissingName  { get; init; }
}

/// <summary>
/// Primacy resolution outcome per view type.  Keep the MissingNamedPrimary state
/// distinct — it drives the System.Warning surfacing in the Project Tree.
/// </summary>
public enum PrimaryState
{
    /// <summary>Exactly one file in the sub-folder — that file is implicitly primary.</summary>
    SoleFile,
    /// <summary>.ccell names a primary and the file exists; it is primary.</summary>
    NamedPresent,
    /// <summary>
    /// .ccell names a primary that does not exist — a blatant contradiction.
    /// Surfaced as System.Warning in the Project Tree.  Do NOT collapse into NoPrimary.
    /// </summary>
    MissingNamedPrimary,
    /// <summary>Multiple files; .ccell names none.  No primary chosen yet (not an error).</summary>
    NoPrimary,
    /// <summary>The sub-folder is empty (or absent) — no view of this type exists (not an error).</summary>
    NoView,
}

// ── CellFolder ────────────────────────────────────────────────────────────────

/// <summary>
/// Layout constants and helpers for the cell folder structure, and the authoritative
/// five-branch primacy resolution used by the Project Tree and cell-reference model.
/// </summary>
public static class CellFolder
{
    // ── Sub-folder names ──────────────────────────────────────────────────────

    public const string SchematicSubFolder = "schematic";
    public const string SymbolSubFolder    = "symbol";
    public const string LayoutSubFolder    = "layout";

    /// <summary>
    /// The 3D view's sub-folder (brief-em3d-41, owner decision D10). <b>Created when the first 3D
    /// view is made, never by <see cref="CreateCellFolder"/></b> — every cell made before it existed
    /// lacks it, so every reader treats an absent <c>3d/</c> as <see cref="PrimaryState.NoView"/>,
    /// and nothing may create it as a side effect of reading.
    /// </summary>
    public const string ThreeDSubFolder    = "3d";
    public const string CcellFileName      = ".ccell";

    // ── View helpers ──────────────────────────────────────────────────────────

    /// <summary>Returns the sub-folder name for the given view type.</summary>
    public static string SubFolderName(ViewType type) => type switch
    {
        ViewType.Schematic => SchematicSubFolder,
        ViewType.Symbol    => SymbolSubFolder,
        ViewType.Layout    => LayoutSubFolder,
        ViewType.ThreeD    => ThreeDSubFolder,
        _                  => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Returns the file extension for the given view type.</summary>
    public static string ViewExtension(ViewType type) => type switch
    {
        ViewType.Schematic => ".csch",
        ViewType.Symbol    => ".csym",
        ViewType.Layout    => ".clay",
        ViewType.ThreeD    => ".c3d",
        _                  => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>What a sentence calls the view: "schematic", "symbol", "layout", "3D view".</summary>
    public static string ViewNoun(ViewType type) => type switch
    {
        ViewType.Schematic => "schematic",
        ViewType.Symbol    => "symbol",
        ViewType.Layout    => "layout",
        ViewType.ThreeD    => "3D view",
        _                  => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Returns the full path to a view sub-folder inside the given cell folder.</summary>
    public static string SubFolderPath(string cellFolder, ViewType type)
        => Path.Combine(cellFolder, SubFolderName(type));

    /// <summary>
    /// Where the <paramref name="other"/> view of the view file at <paramref name="viewPath"/> lives:
    /// in a cell folder, that view's own sub-folder; for a document sitting LOOSE — at a workspace's
    /// root, or anywhere that is not its view's sub-folder — the same folder, under the same name.
    /// </summary>
    /// <remarks>
    /// Field report (2026-09-24): Update Layout from Schematic took the schematic's GRANDPARENT as the
    /// cell folder unconditionally. For a schematic saved at a workspace's root that is the folder
    /// ABOVE the workspace, so the layout was written outside it — where no workspace is open, and the
    /// generated footprints then had nowhere to live — and the reverse command made a stray
    /// <c>schematic</c> folder the same way. A loose document's other view is the one beside it.
    /// </remarks>
    /// <param name="own">The view type of <paramref name="viewPath"/> itself.</param>
    public static SiblingViewLocation SiblingView(string viewPath, ViewType own, ViewType other)
    {
        string full = Path.GetFullPath(viewPath);
        string dir  = Path.GetDirectoryName(full)!;
        string name = Path.GetFileNameWithoutExtension(full);

        if (string.Equals(Path.GetFileName(dir), SubFolderName(own), StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(dir) is { Length: > 0 } cellDir)
        {
            string targetDir = SubFolderPath(cellDir, other);
            return new SiblingViewLocation(Path.Combine(targetDir, name + ViewExtension(other)), targetDir, cellDir);
        }

        return new SiblingViewLocation(Path.Combine(dir, name + ViewExtension(other)), dir, null);
    }

    // ── Create ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the cell folder structure under <paramref name="parentDir"/>:
    ///   cellName/ + schematic/ + symbol/ + layout/ + initial .ccell.
    /// Deliberately NOT <c>3d/</c>: that one is made with the cell's first 3D view (D10).
    /// Validates <paramref name="cellName"/> via NameValidator first.
    /// </summary>
    /// <returns>The absolute path of the new cell folder.</returns>
    /// <exception cref="ArgumentException">If cellName fails NameValidator.</exception>
    /// <param name="ccell">The cell's <c>.ccell</c> as it should be written; null writes the empty terminal map below. A caller
    /// that knows the cell's terminals passes them here rather than rewriting the file it just created: replacing a file
    /// seconds after creating it is the write Windows refuses with "Access to the path is denied" while a scanner or the
    /// network share still holds the new file (designer feedback, a workspace on a mapped drive).</param>
    public static string CreateCellFolder(string parentDir, string cellName, CcellFile? ccell = null)
    {
        var reason = NameValidator.Validate(cellName);
        if (reason is not null)
            throw new ArgumentException($"Invalid cell name '{cellName}': {reason}", nameof(cellName));

        string cellDir = Path.Combine(parentDir, cellName);
        Directory.CreateDirectory(cellDir);
        Directory.CreateDirectory(Path.Combine(cellDir, SchematicSubFolder));
        Directory.CreateDirectory(Path.Combine(cellDir, SymbolSubFolder));
        Directory.CreateDirectory(Path.Combine(cellDir, LayoutSubFolder));

        // R-lvs1-5c: an EMPTY terminal map, which is not the same thing as no map at all. It says
        // "circuitRF created this cell and it has no terminals yet" — which a brand-new cell with no
        // views legitimately is. Every path that goes on to author real terminals (a component
        // import, a generated PCell) overwrites it with its own table; a cell someone then DRAWS
        // derives one, because the empty list records what was true at creation and not what is true
        // after a symbol and a layout exist.
        CellPersistence.SaveToFile(Path.Combine(cellDir, CcellFileName), ccell ?? new CcellFile { Terminals = [] });

        return cellDir;
    }

    // ── Primacy resolution ────────────────────────────────────────────────────

    /// <summary>
    /// Resolves the primary view file for <paramref name="viewType"/> in the given cell folder.
    /// Implements the five-branch rule from workspace-and-project-tree.md §2, in order:
    /// <list type="number">
    ///   <item>Empty sub-folder → <see cref="PrimaryState.NoView"/></item>
    ///   <item>Sole file → <see cref="PrimaryState.SoleFile"/> (ignore .ccell)</item>
    ///   <item>Multiple files + .ccell names a present one → <see cref="PrimaryState.NamedPresent"/></item>
    ///   <item>Multiple files + .ccell names an absent one → <see cref="PrimaryState.MissingNamedPrimary"/></item>
    ///   <item>Multiple files + .ccell names none → <see cref="PrimaryState.NoPrimary"/></item>
    /// </list>
    /// </summary>
    /// <param name="useStatCache">
    /// SL4 R-sl4-7. False (the default) reads the filesystem every time, exactly as before SL4 — the
    /// PROJECT TREE's own scan and the tree node view models pass this, because a user who has just
    /// created a symbol and pressed Refresh must see it, and because §3's answer to the tree's cost
    /// is R-sl4-10 (don't walk a referenced sub-tree on focus), not a cheaper stat.
    ///
    /// <para>True is <c>CellSymbolResolver.Resolve</c>, and only it: a positive answer may then be up
    /// to <see cref="CellStat.Freshness"/> old, which is the one guarantee SL4 traded away and is
    /// stated there in full. That is the path that runs once per referenced component per EDIT.</para>
    /// </param>
    public static PrimaryResolution ResolvePrimary(
        string cellFolder, ViewType viewType, bool useStatCache = false)
    {
        string subFolder = SubFolderPath(cellFolder, viewType);
        string ext       = ViewExtension(viewType);

        // SL4 R-sl4-6: every filesystem call on the resolution path goes through CellStat, which is
        // what makes the cost of a reference a COUNT — whether or not this caller caches.
        if (!CellStat.DirectoryExists(subFolder, useStatCache))
            return new PrimaryResolution { State = PrimaryState.NoView };

        var files = CellStat.GetFiles(subFolder, $"*{ext}", useStatCache)
                             .Select(Path.GetFileName)
                             .Where(f => f is not null)
                             .Cast<string>()
                             .ToList();

        // Branch 5: empty sub-folder.
        if (files.Count == 0)
            return new PrimaryResolution { State = PrimaryState.NoView };

        // Branch 1: sole file implies primary (ignore .ccell).
        if (files.Count == 1)
            return new PrimaryResolution { State = PrimaryState.SoleFile, ResolvedName = files[0] };

        // Multiple files — consult .ccell for the named primary.
        string? namedPrimary = ReadNamedPrimary(cellFolder, viewType, useStatCache);

        if (namedPrimary is not null)
        {
            // Branch 2: named primary present.
            if (files.Contains(namedPrimary, StringComparer.OrdinalIgnoreCase))
                return new PrimaryResolution { State = PrimaryState.NamedPresent, ResolvedName = namedPrimary };

            // Branch 3: named primary missing — the contradiction.
            return new PrimaryResolution { State = PrimaryState.MissingNamedPrimary, MissingName = namedPrimary };
        }

        // Branch 4: multiple files, no primary named.
        return new PrimaryResolution { State = PrimaryState.NoPrimary };
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Step 4 of the resolution path — reached only when a view sub-folder holds more than one file.
    /// SL4 moved the read itself into <see cref="CellStat.NamedPrimary"/>, for the same two reasons
    /// the three stat calls above went there: it is counted, and a positive answer is bounded by T.
    /// </summary>
    private static string? ReadNamedPrimary(string cellFolder, ViewType viewType, bool useStatCache)
        => CellStat.NamedPrimary(cellFolder, viewType, useStatCache);
}

/// <summary>Where a view file's other view lives — see <see cref="CellFolder.SiblingView"/>.</summary>
/// <param name="TargetPath">The other view's file.</param>
/// <param name="TargetDir">The folder it is in, which may not exist yet.</param>
/// <param name="CellDir">The cell folder both views belong to, or null for a LOOSE document — one
/// with no cell folder, and therefore no primary view to maintain.</param>
public readonly record struct SiblingViewLocation(string TargetPath, string TargetDir, string? CellDir);
