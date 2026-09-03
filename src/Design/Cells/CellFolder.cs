namespace CircuitRF.Design.Cells;

// ──────────────────────────────────────────────────────────────────────────────
//  CellFolder — cell folder layout + primacy resolution.
//  Framework-free (no Avalonia / Skia).  The filesystem is truth; this helper
//  reads structure and resolves primacy per workspace-and-project-tree.md §2.
//  This is the single source of primacy truth for the project tree (step 3) and
//  the cell-reference model (step 5).
// ──────────────────────────────────────────────────────────────────────────────

// ── View-type enum + resolution result ───────────────────────────────────────

/// <summary>The three view types that can live inside a cell folder.</summary>
public enum ViewType
{
    Schematic,
    Symbol,
    Layout,
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
    public const string CcellFileName      = ".ccell";

    // ── View helpers ──────────────────────────────────────────────────────────

    /// <summary>Returns the sub-folder name for the given view type.</summary>
    public static string SubFolderName(ViewType type) => type switch
    {
        ViewType.Schematic => SchematicSubFolder,
        ViewType.Symbol    => SymbolSubFolder,
        ViewType.Layout    => LayoutSubFolder,
        _                  => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Returns the file extension for the given view type.</summary>
    public static string ViewExtension(ViewType type) => type switch
    {
        ViewType.Schematic => ".csch",
        ViewType.Symbol    => ".csym",
        ViewType.Layout    => ".clay",
        _                  => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Returns the full path to a view sub-folder inside the given cell folder.</summary>
    public static string SubFolderPath(string cellFolder, ViewType type)
        => Path.Combine(cellFolder, SubFolderName(type));

    // ── Create ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the cell folder structure under <paramref name="parentDir"/>:
    ///   cellName/ + schematic/ + symbol/ + layout/ + initial .ccell.
    /// Validates <paramref name="cellName"/> via NameValidator first.
    /// </summary>
    /// <returns>The absolute path of the new cell folder.</returns>
    /// <exception cref="ArgumentException">If cellName fails NameValidator.</exception>
    public static string CreateCellFolder(string parentDir, string cellName)
    {
        var reason = NameValidator.Validate(cellName);
        if (reason is not null)
            throw new ArgumentException($"Invalid cell name '{cellName}': {reason}", nameof(cellName));

        string cellDir = Path.Combine(parentDir, cellName);
        Directory.CreateDirectory(cellDir);
        Directory.CreateDirectory(Path.Combine(cellDir, SchematicSubFolder));
        Directory.CreateDirectory(Path.Combine(cellDir, SymbolSubFolder));
        Directory.CreateDirectory(Path.Combine(cellDir, LayoutSubFolder));

        CellPersistence.SaveToFile(Path.Combine(cellDir, CcellFileName), new CcellFile());

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
