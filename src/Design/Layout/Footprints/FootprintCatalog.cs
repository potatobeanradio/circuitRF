// ONE picker over built-ins, the workspace's own cells and Custom —
// brief-footprint-4-picker-and-import.md R-fp4-1.
//
// IT LIVES BELOW THE FIREWALL ON PURPOSE (R-fp4-1a). `circuitrf explain --footprints` has to be
// able to list what a design could have chosen and `circuitrf check` has to be able to say that a
// stored footprint no longer resolves; both are cheap once this is here and impossible once it is
// in a view model. Nothing in this file draws, docks or observes a canvas — it walks a folder and
// reads `.clay` pin counts.
//
// THE MIDDLE SECTION IS THE WHOLE ANSWER TO "how does this meet Component Import?" (§0). It does
// not meet it: they are not two things. ComponentImport already writes a cell folder holding a
// symbol plus one or more land patterns as sibling .clay views, and the series' governing rule is
//
//      A footprint IS a layout view of a cell. There is no second artifact kind.
//
// so an imported part appears here because it IS a cell with a layout view, with no extra step, no
// second import, no registration and no index. Nothing in this file writes anything at all.
//
// WHAT A ROW COSTS. The walk reads one directory listing per folder and parses one .clay per layout
// view, which is why it is BOUNDED and CACHED (R-fp4-1c) — a workspace holding a large imported
// library must not make the parameter editor pause every time somebody selects a component.

using CircuitRF.Design.Cells;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Layout.Footprints;

/// <summary>Which of the catalog's four sections a row belongs to. The order of the enum IS the
/// order of the list.</summary>
public enum FootprintSection
{
    /// <summary>No artwork. Always the first row.</summary>
    None,

    /// <summary>One of the case sizes <see cref="SmtCaseTable"/> generates.</summary>
    BuiltIn,

    /// <summary>A layout view of a cell already in this workspace — including every part
    /// <c>ComponentImport</c> has ever written here.</summary>
    Workspace,

    /// <summary>The gesture that opens a file picker over <c>.clay</c>. Always the last row.</summary>
    Custom,
}

/// <summary>
/// One row of the picker.
/// </summary>
/// <param name="Reference">What goes in the component's <c>Footprint</c> parameter when this row is
/// chosen — <c>null</c> for <see cref="FootprintSection.None"/> (which REMOVES the parameter) and
/// for <see cref="FootprintSection.Custom"/> (which is a gesture, not a value).</param>
/// <param name="Display">The row's text. Every built-in reads its metric twin and its millimetres,
/// which is the series overview's §1e spelling and is not decoration: <c>0201</c> imperial and
/// <c>0201</c> metric are two real case sizes differing by 2.4x.</param>
/// <param name="PadCount">How many pads the artwork has, or <c>-1</c> where there is no artwork to
/// count (None and Custom).</param>
/// <param name="NotPlaceable">
/// Non-null when choosing this row stores a value that <c>Update Layout</c> will refuse, WITH the
/// reason. The only row that carries one is a layout view that is not its cell's primary — see
/// <see cref="WorkspaceViews"/> for why those are listed anyway rather than hidden.
/// </param>
public sealed record FootprintChoice(
    FootprintSection Section,
    string? Reference,
    string Display,
    int PadCount,
    SmtCase? Case = null,
    DensityLevel Density = DensityLevel.Nominal,
    string? CellName = null,
    string? CellDir = null,
    string? ViewFile = null,
    string? Variant = null,
    bool IsPrimaryView = true,
    string? NotPlaceable = null);

/// <summary>One layout view of one cell in the workspace — the raw result of the walk, before it is
/// filtered by pad count and turned into rows.</summary>
public sealed record FootprintCellView(
    string CellName,
    string CellDir,
    string ViewFile,
    string Variant,
    bool IsPrimary,
    int PadCount);

/// <summary>
/// The rows, and whether the walk that produced them saw everything.
/// </summary>
/// <param name="StoppedShort"><c>true</c> when the depth bound was reached with folders still below
/// it. <b>Said, never swallowed</b> — <c>circuitrf find</c>'s own rule: a listing that quietly gave
/// up is a listing a caller reads as complete.</param>
public sealed record FootprintCatalogResult(
    IReadOnlyList<FootprintChoice> Choices,
    bool StoppedShort,
    string? Note);

/// <summary>
/// What a footprint picker may offer, and what a stored footprint resolves to.
/// </summary>
public static class FootprintCatalog
{
    /// <summary>How deep below the workspace root the walk goes. <c>CellLookup.MaxDepth</c>'s number
    /// and its reason: deep enough for the folder-per-library arrangements circuitRF's own
    /// workspaces use, shallow enough that a workspace sitting above a large unrelated tree does not
    /// turn opening a combobox into a full disk walk.</summary>
    public const int DefaultDepth = 6;

    /// <summary>How stale a cached walk may be. <see cref="CellStat.Freshness"/>'s bound and its
    /// reason — shorter than the fastest way a person can observe a change they just made.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(2);

    private sealed record WalkKey(string Root, int Depth);
    private sealed record WalkEntry(DateTime At, IReadOnlyList<FootprintCellView> Views, bool StoppedShort);

    private static readonly Dictionary<WalkKey, WalkEntry> _cache = new();

    /// <summary>
    /// Pad count per <c>.clay</c>, keyed by path and LAST-WRITE TIME.
    /// </summary>
    /// <remarks>
    /// <b>This, not the walk memo, is what makes R-fp4-1c true rather than true-for-two-seconds.</b>
    /// The freshness window above bounds how stale an ANSWER may be; it does nothing about the cost
    /// of the walk that produces the next one, and that cost is one <c>.clay</c> PARSE per layout
    /// view — which on a workspace with a large imported library is the pause the requirement is
    /// about. Keyed by mtime rather than invalidated, so an edited view is re-read and an untouched
    /// one never is.
    /// </remarks>
    private static readonly Dictionary<(string Path, DateTime Mtime), int> _pads = new();

    private static readonly object _lock = new();

    /// <summary>Drops the memoised walks. Called when a cell is created, imported or deleted — the
    /// same lifecycle every other per-workspace memo in <c>WorkspaceRootFinder.InvalidateCache</c>
    /// has, and for the same reason: a memo with a lifecycle of its own is the one that goes
    /// stale.</summary>
    public static void Invalidate()
    {
        lock (_lock) { _cache.Clear(); _pads.Clear(); }
    }

    // ── the list ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every row a picker may offer a component of <paramref name="portCount"/> ports.
    /// </summary>
    /// <param name="workspaceRoot">The workspace whose cells fill the middle section. Null lists the
    /// built-ins and Custom only, which is the honest answer for a document belonging to no
    /// workspace.</param>
    /// <param name="referenceDir">What a stored reference is resolved RELATIVE TO — the schematic's
    /// own directory, because that is what <c>SchematicToLayoutGenerator.ResolveFootprintPath</c>
    /// hands <c>ExternalCellRef.ResolveCellDir</c>. A row spelled against anything else produces a
    /// reference that does not resolve.</param>
    /// <param name="portCount">
    /// R-fp4-1b. A row is offered only when its pad count EQUALS this. A picker that offers a
    /// four-pad cell to a two-terminal capacitor offers a refusal — the pad-count contract
    /// (R-fp3-5) would then report it and place nothing, which is a worse way to learn it.
    ///
    /// <para>Zero or less means "do not filter", for a caller listing what exists rather than what
    /// one component may have.</para>
    /// </param>
    public static FootprintCatalogResult Build(
        string? workspaceRoot, string? referenceDir, int portCount, int maxDepth = DefaultDepth)
    {
        var rows = new List<FootprintChoice>();
        rows.Add(new FootprintChoice(FootprintSection.None, null, NoneRow, -1));

        // Built-ins: one row per case, at the density the caller's own density control carries. Two
        // pads on every one of them — SmtCaseTable holds two-terminal chips and moulded tantalums
        // only, which is brief 1 §8's scope.
        if (portCount <= 0 || portCount == BuiltInPadCount)
            foreach (var c in SmtCaseTable.All)
                rows.Add(new FootprintChoice(
                    FootprintSection.BuiltIn, FootprintRef.For(c).ToString(), c.Display,
                    BuiltInPadCount, Case: c));

        bool stoppedShort = false;
        string? note = null;

        if (workspaceRoot is { Length: > 0 })
        {
            var (views, truncated) = WorkspaceViews(workspaceRoot, maxDepth);
            stoppedShort = truncated;

            foreach (var v in views)
            {
                if (portCount > 0 && v.PadCount != portCount) continue;
                if (StoredReferenceFor(referenceDir, v) is not { } reference) continue;

                rows.Add(new FootprintChoice(
                    FootprintSection.Workspace, reference, DisplayOf(v), v.PadCount,
                    CellName: v.CellName, CellDir: v.CellDir, ViewFile: v.ViewFile,
                    Variant: v.Variant, IsPrimaryView: v.IsPrimary,
                    NotPlaceable: v.IsPrimary ? null : NotPrimarySentence(v)));
            }

            if (truncated)
                note = $"the walk stopped at depth {maxDepth}; there are cell folders below it that " +
                       "are not listed here.";
        }

        rows.Add(new FootprintChoice(FootprintSection.Custom, null, CustomRow, -1));
        return new FootprintCatalogResult(rows, stoppedShort, note);
    }

    /// <summary>Every built-in land pattern has two pads: <see cref="SmtCaseTable"/> is two-terminal
    /// chips and moulded tantalums, and <see cref="ChipLandPatternGenerator"/> emits pin "1" and pin
    /// "2". Named rather than written as <c>2</c> in three places.</summary>
    public const int BuiltInPadCount = 2;

    /// <summary>The <b>None</b> row's text — index 0, always.</summary>
    public const string NoneRow = "None";

    /// <summary>The <b>Custom…</b> row's text — always last, so the workspace section can grow
    /// without moving it.</summary>
    public const string CustomRow = "Custom…";

    /// <summary>The workspace marker the walk stops at. Spelled here because <c>src/Design</c>
    /// declares no shared constant for it and <c>DocumentKinds</c> is <c>src/Cli</c>'s.</summary>
    private const string CwsFileName = ".cws";

    // ── the walk ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every layout view of every cell under <paramref name="root"/>, with its pad count.
    /// </summary>
    /// <remarks>
    /// <b>ONE ROW PER VIEW, not one per cell</b> (R-fp4-1d). A part imported with three density
    /// variants is one cell holding three sibling <c>.clay</c> views, and they are three different
    /// pieces of artwork — the primary is not automatically the one wanted, so collapsing them to
    /// the primary would hide two of the three things the import produced.
    ///
    /// <para><b>A non-primary view is listed and marked, not hidden and not silently substituted.</b>
    /// A <see cref="LayoutInstance"/> names a cell FOLDER and draws that folder's primary view —
    /// there is no per-view reference in the <c>.clay</c> format — so brief 3's
    /// <c>ResolveFootprintPath</c> refuses a <c>.clay</c> that is not its cell's primary and names
    /// the one that is. Listing the variant with that sentence attached is the only rendering that
    /// is both complete and honest: hiding it makes an import look like it produced one pattern, and
    /// placing the primary instead would put different artwork on the board than the row said.</para>
    ///
    /// <para><b>Bounded, cached, and a directory symlink is never followed</b> (R-fp4-1c) — the
    /// three rules <c>circuitrf find</c>'s own walk states, for the same three reasons: a bound that
    /// is not reported reads as "nothing here", a walk on every selection is a pause the user feels,
    /// and one link is a path outside the root, or a cycle.</para>
    /// </remarks>
    public static (IReadOnlyList<FootprintCellView> Views, bool StoppedShort) WorkspaceViews(
        string root, int maxDepth = DefaultDepth)
    {
        string full;
        try { full = Path.GetFullPath(root); }
        catch { return ([], false); }

        var key = new WalkKey(full, maxDepth);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < Freshness)
                return (hit.Views, hit.StoppedShort);
        }

        var views = new List<FootprintCellView>();
        bool truncated = false;
        Walk(full, 0, maxDepth, views, ref truncated);
        views.Sort((a, b) =>
        {
            int byName = string.Compare(a.CellName, b.CellName, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName
                 : string.Compare(a.ViewFile, b.ViewFile, StringComparison.OrdinalIgnoreCase);
        });

        lock (_lock) _cache[key] = new WalkEntry(DateTime.UtcNow, views, truncated);
        return (views, truncated);
    }

    private static void Walk(
        string dir, int level, int limit, List<FootprintCellView> found, ref bool truncated)
    {
        string[] subs;
        try { subs = Directory.GetDirectories(dir); }
        catch { return; }   // an unreadable folder holds nothing anyone can list

        Array.Sort(subs, StringComparer.Ordinal);

        foreach (string sub in subs)
        {
            // A directory symbolic link is where a bounded walk stops being bounded and where a walk
            // confined to a workspace stops being confined: one link is a path outside it, or a
            // cycle. Not followed, and not an error.
            if (IsLink(sub)) continue;

            // A nested workspace is somebody else's tree — two workspaces have different default
            // technologies, and offering the inner one's cells as the outer one's would offer
            // artwork generated against the wrong stackup.
            if (File.Exists(Path.Combine(sub, CwsFileName))) continue;

            if (ReservedFolders.IsReserved(sub)) continue;

            if (LooksLikeCellFolder(sub)) { Collect(sub, found); continue; }

            if (level >= limit)
            {
                // Only a directory that could still have held something counts as a truncation.
                try { if (Directory.EnumerateDirectories(sub).Any()) truncated = true; }
                catch { /* unreadable: nothing was missed that could have been listed */ }
                continue;
            }

            Walk(sub, level + 1, limit, found, ref truncated);
        }
    }

    private static bool IsLink(string dir)
    {
        try { return new DirectoryInfo(dir).LinkTarget is not null; }
        catch { return true; }   // cannot tell: do not walk it
    }

    /// <summary>By SHAPE, not by a manifest — <c>CellLookup</c>'s own test. A cell folder is legal
    /// with no <c>.ccell</c> at all, since primacy is implicit when a view sub-folder holds exactly
    /// one file.</summary>
    private static bool LooksLikeCellFolder(string dir)
        => File.Exists(Path.Combine(dir, CellFolder.CcellFileName))
        || Directory.Exists(CellFolder.SubFolderPath(dir, ViewType.Schematic))
        || Directory.Exists(CellFolder.SubFolderPath(dir, ViewType.Symbol))
        || Directory.Exists(CellFolder.SubFolderPath(dir, ViewType.Layout));

    private static void Collect(string cellDir, List<FootprintCellView> found)
    {
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        string[] files;
        try
        {
            if (!Directory.Exists(layoutDir)) return;
            files = Directory.GetFiles(layoutDir, "*" + CellFolder.ViewExtension(ViewType.Layout));
        }
        catch { return; }
        if (files.Length == 0) return;

        string cellName = Path.GetFileName(Path.TrimEndingDirectorySeparator(cellDir));
        string? primary;
        try { primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout).ResolvedName; }
        catch { primary = null; }

        foreach (string file in files)
        {
            string name = Path.GetFileName(file);

            int pads = PadsOfCached(file);
            if (pads < 0) continue;   // an unreadable view is `check`'s finding, not a picker's

            // The view's own density suffix, read through the one vocabulary (R-fp4-2c) so an
            // imported `PART_M` and a generated `PART-M` are one row and not two.
            string stem = Path.GetFileNameWithoutExtension(name);
            string variant = DensityVariant.Nominal;
            foreach (string suffix in DensityVariant.Suffixes)
                if (stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && stem.Length > suffix.Length)
                { variant = DensityVariant.Canonical(suffix); break; }

            found.Add(new FootprintCellView(
                cellName, cellDir, name, variant,
                string.Equals(primary, name, StringComparison.OrdinalIgnoreCase), pads));
        }
    }

    // ── spelling a row as a stored reference ────────────────────────────────────────────────────

    /// <summary>
    /// What a chosen row is written into the <c>Footprint</c> parameter as.
    /// </summary>
    /// <remarks>
    /// <b>Relative to the REFERENCING DOCUMENT, through <see cref="ExternalCellRef.MakeCellRef"/>.</b>
    /// That is the inverse of the resolution brief 3 performs — <c>ResolveCellDir(footprint,
    /// schematicDir)</c> — and it is also what gives a cell in a REFERENCED workspace its
    /// <c>ws://alias/…</c> spelling for free. A reference spelled against the workspace root instead
    /// silently fails to resolve for every schematic that is not at the root, which is every
    /// schematic.
    /// </remarks>
    public static string? StoredReferenceFor(string? referenceDir, FootprintCellView view)
    {
        if (referenceDir is not { Length: > 0 }) return null;

        string cellRef;
        try { cellRef = ExternalCellRef.MakeCellRef(referenceDir, view.CellDir).Replace('\\', '/'); }
        catch { return null; }
        if (cellRef.Length == 0) return null;

        // The primary view IS the cell: an instance draws a cell folder's primary, so naming the
        // folder is both shorter and stable across a later re-primary.
        return view.IsPrimary
            ? cellRef
            : $"{cellRef.TrimEnd('/')}/{CellFolder.SubFolderName(ViewType.Layout)}/{view.ViewFile}";
    }

    /// <summary>The same, for an absolute path a file picker returned — the <b>Custom…</b> row's
    /// answer. A <c>.clay</c> is spelled through its owning cell folder for the reason above; a path
    /// that is not inside a cell folder is spelled relative to the document and left to brief 3's
    /// own refusal to explain.</summary>
    public static string StoredReferenceForPath(string? referenceDir, string absolutePath)
    {
        if (referenceDir is not { Length: > 0 }) return absolutePath;

        try
        {
            string target = Path.GetFullPath(absolutePath);
            bool isClay = target.EndsWith(CellFolder.ViewExtension(ViewType.Layout),
                                          StringComparison.OrdinalIgnoreCase);
            string anchor = isClay
                ? Path.GetDirectoryName(Path.GetDirectoryName(target) ?? "") ?? ""
                : target;

            if (anchor.Length == 0) return absolutePath;

            string cellRef = ExternalCellRef.MakeCellRef(referenceDir, anchor).Replace('\\', '/');
            if (cellRef.Length == 0) return absolutePath;
            if (!isClay) return cellRef;

            return $"{cellRef.TrimEnd('/')}/{CellFolder.SubFolderName(ViewType.Layout)}/" +
                   Path.GetFileName(target);
        }
        catch { return absolutePath; }
    }

    // ── row text ────────────────────────────────────────────────────────────────────────────────

    private static string DisplayOf(FootprintCellView v)
    {
        string pads = $"{v.PadCount} pad{(v.PadCount == 1 ? "" : "s")}";
        string density = v.Variant.Length == 0
            ? ""
            : $" — {DensityVariant.Label(DensityVariant.LevelOf(v.Variant))}";
        string marker = v.IsPrimary ? "" : "   (not the cell's primary view)";
        return $"{v.CellName}{density}   {pads}{marker}";
    }

    private static string NotPrimarySentence(FootprintCellView v)
        => $"'{v.ViewFile}' is not {v.CellName}'s primary layout view, and an instance draws the " +
           "primary. Make it primary, or point the footprint at a cell of its own.";

    // ── what a STORED value resolves to (check / explain) ───────────────────────────────────────

    /// <summary>What a stored <c>Footprint</c> turned out to be.</summary>
    public enum FootprintState
    {
        /// <summary>The component states none. Not a finding.</summary>
        None,

        /// <summary>A built-in case size circuitRF generates.</summary>
        BuiltIn,

        /// <summary>A cell, or one of its layout views.</summary>
        Cell,

        /// <summary>It claims to be a built-in and is not one, or it names a path that is not
        /// there.</summary>
        Unresolved,
    }

    /// <summary>
    /// What one stored value resolves to, with the WALK reported as well as the answer — which is
    /// what <c>explain</c> exists to say and what <c>check</c> warns about (R-fp4-4).
    /// </summary>
    /// <param name="PadCount">-1 when nothing resolved, so a caller cannot read a missing answer as
    /// zero pads.</param>
    public sealed record FootprintResolution(
        string Stored,
        FootprintState State,
        string Walk,
        int PadCount,
        SmtCase? Case = null,
        DensityLevel Density = DensityLevel.Nominal,
        string? ResolvedPath = null,
        string? Refusal = null);

    /// <summary>
    /// Resolves one stored <c>Footprint</c> against <paramref name="referenceDir"/> — the same walk
    /// <c>SchematicToLayoutGenerator</c> performs, so <c>check</c> cannot say a design is fine and
    /// Update Layout then refuse it.
    /// </summary>
    public static FootprintResolution Resolve(string? stored, string? referenceDir)
    {
        string s = (stored ?? "").Trim();
        if (s.Length == 0)
            return new FootprintResolution("", FootprintState.None, "no Footprint parameter", -1);

        if (FootprintRef.IsBuiltInReference(s))
        {
            if (!FootprintRef.TryParse(s, out var parsed, out string? refusal))
                return new FootprintResolution(
                    s, FootprintState.Unresolved,
                    "the built-in grammar, because the value starts with 'smt:'", -1, Refusal: refusal);

            return new FootprintResolution(
                s, FootprintState.BuiltIn,
                "the built-in case table, because the value starts with 'smt:' — generated on demand, " +
                "not read from a file",
                BuiltInPadCount, Case: parsed!.Case, Density: parsed.Density);
        }

        string walk = referenceDir is { Length: > 0 }
            ? $"a path, resolved against the document's own folder ({referenceDir})"
            : "a path, with no document folder to resolve it against";

        if (referenceDir is not { Length: > 0 })
            return new FootprintResolution(
                s, FootprintState.Unresolved, walk, -1,
                Refusal: $"footprint '{s}' is a path and there is no document folder to resolve it against.");

        if (ExternalCellRef.ResolveCellDir(s, referenceDir) is not { } resolved)
            return new FootprintResolution(
                s, FootprintState.Unresolved, walk, -1,
                Refusal: $"footprint '{s}' could not be resolved.");

        bool isClay = s.EndsWith(CellFolder.ViewExtension(ViewType.Layout), StringComparison.OrdinalIgnoreCase);

        if (isClay)
        {
            if (!File.Exists(resolved))
                return new FootprintResolution(
                    s, FootprintState.Unresolved, walk, -1, ResolvedPath: resolved,
                    Refusal: $"footprint '{s}' not found.");

            int pads = PadsOf(resolved);
            string owningCell = Path.GetDirectoryName(Path.GetDirectoryName(resolved) ?? "") ?? "";
            string? primary = null;
            try { primary = CellFolder.ResolvePrimary(owningCell, ViewType.Layout).ResolvedName; }
            catch { /* reported below as "not the primary" */ }

            string? notPrimary = string.Equals(primary, Path.GetFileName(resolved), StringComparison.OrdinalIgnoreCase)
                ? null
                : $"footprint '{s}' is not its cell's primary layout view — '{primary}' is, and an " +
                  "instance draws the primary.";

            return new FootprintResolution(
                s, FootprintState.Cell, walk, pads, ResolvedPath: resolved, Refusal: notPrimary);
        }

        if (!Directory.Exists(resolved))
            return new FootprintResolution(
                s, FootprintState.Unresolved, walk, -1, ResolvedPath: resolved,
                Refusal: $"footprint '{s}' not found.");

        PrimaryResolution res;
        try { res = CellFolder.ResolvePrimary(resolved, ViewType.Layout); }
        catch { res = new PrimaryResolution { State = PrimaryState.NoView }; }

        if (res.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent))
            return new FootprintResolution(
                s, FootprintState.Unresolved, walk, -1, ResolvedPath: resolved,
                Refusal: $"footprint '{s}' has no layout view.");

        string viewPath = Path.Combine(CellFolder.SubFolderPath(resolved, ViewType.Layout), res.ResolvedName!);
        return new FootprintResolution(
            s, FootprintState.Cell, walk, PadsOf(viewPath), ResolvedPath: viewPath);
    }

    private static int PadsOf(string clayPath)
    {
        try { return LayoutPersistence.LoadFromFile(clayPath).Pins.Count; }
        catch { return -1; }
    }

    /// <summary>The same, memoised by path and mtime — the walk's own reader. <c>-1</c> is not
    /// cached: an unreadable file is asked again immediately, for <c>CellStat</c>'s reason (a share
    /// that blinks must not fill a picker with absences that persist after it recovers).</summary>
    private static int PadsOfCached(string clayPath)
    {
        DateTime mtime;
        try { mtime = File.GetLastWriteTimeUtc(clayPath); }
        catch { return -1; }

        var key = (clayPath, mtime);
        lock (_lock) if (_pads.TryGetValue(key, out int hit)) return hit;

        int pads = PadsOf(clayPath);
        if (pads >= 0) lock (_lock) _pads[key] = pads;
        return pads;
    }

    // ── the import's own completion sentence (R-fp4-2b) ─────────────────────────────────────────

    /// <summary>
    /// The one sentence <c>ComponentImport</c>'s completion report adds: <b>this part is now
    /// available as a footprint</b>, with its pad count.
    /// </summary>
    /// <remarks>
    /// It lives here and not there because R-fp4-2a is gated by a source scan: this series adds no
    /// code to <c>ComponentImport.cs</c> beyond a CALL SITE. It is also the right place on its own
    /// terms — the claim the sentence makes is a claim about this catalog, and a sentence written
    /// beside the writer could go on being printed after the picker stopped offering the thing.
    /// </remarks>
    public static string ImportAvailabilitySentence(int padCount, int variantCount)
    {
        string pads = $"{padCount:N0} pad{(padCount == 1 ? "" : "s")}";
        string where = variantCount > 1
            ? $"Its {variantCount:N0} land patterns are offered as separate rows, each labelled with its density"
            : "It is offered as a row of its own";

        return $"This part is now available as a footprint: {where}, to any component in this " +
               $"workspace with {pads}.";
    }
}
