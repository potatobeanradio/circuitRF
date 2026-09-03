namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Edit-time cycle rejection for SCHEMATIC cell instances — the counterpart of
/// <see cref="CircuitRF.Design.Layout.CellHierarchy.WouldCreateCycle"/>, which has guarded the layout
/// view since R-L3a-2 and which a schematic never had.
///
/// <para><b>What was missing, and what it cost.</b> A layout placement that would close a reference
/// cycle is refused at the gesture, naming the path. The same gesture in a schematic did nothing at
/// all: the instance was placed, the file was saved, and the loop was reported only at extraction —
/// <c>NetExtractor</c>'s <c>CellScope</c> guard — as a conflict, long after the edit, phrased as a
/// property of the netlist rather than of the thing the user just did. That is pre-existing and
/// applies inside one workspace; MW2's external references only made it easier to reach, since
/// <c>A/Amp → ws://B/Buf → ws://A/Amp</c> spans two projects and no single file shows the loop.</para>
///
/// <para><b>Reading disk, not sessions, is deliberate and it is the layout rule.</b> The alternative
/// — walking through <see cref="ICellResolver"/>, which <c>WorkspaceViewModel</c> implements over its
/// session registry — would see unsaved edits, and it would also call <c>GetOrCreateSession</c> for
/// every cell reachable from the candidate: registering sessions the user never opened and emitting
/// their unknown-component warnings into Messages, on a gesture as ordinary as dropping a part. So
/// the answer here is the one on disk, exactly as <c>CellHierarchy</c> gives for a layout, and the
/// session-aware backstop stays where it already is. The consequence is stated rather than hidden:
/// <b>a cycle closed entirely through UNSAVED edits is not refused here</b>; it is caught at
/// extraction, which is the same limitation the layout view has always had.</para>
///
/// <para>The three-layer shape is <c>CellHierarchy</c>'s own, and this file is the first of the
/// three for schematics: edit time (here), resolve time (<see cref="CanReach"/>'s visiting set, which
/// makes a malformed EXISTING sub-graph unable to hang this check), and extraction time
/// (<c>NetExtractor</c>). <see cref="MaxDepth"/> is shared with the layout walk rather than
/// re-chosen — one hierarchy depth for one design.</para>
/// </summary>
public static class SchematicHierarchy
{
    /// <summary>The layout walk's cap, deliberately the same number: a cell's schematic and its
    /// layout describe one hierarchy and should not disagree about how deep it may be.</summary>
    public const int MaxDepth = CircuitRF.Design.Layout.CellHierarchy.MaxDepth;

    /// <summary>
    /// True if placing <paramref name="candidateCellRef"/> (resolved from
    /// <paramref name="candidateBaseDir"/>, the directory holding the <c>.csch</c> being edited) into
    /// the cell at <paramref name="currentCellAbsDir"/> would close a cycle — i.e. the candidate can
    /// already reach that cell through its own instances.
    ///
    /// <para>A scratch document (no directory yet) cannot participate in a cycle, because nothing can
    /// reference back to a path that does not exist; that returns false, as the layout check does for
    /// the same reason.</para>
    /// </summary>
    public static bool WouldCreateCycle(
        string? currentCellAbsDir, string? candidateCellRef, string? candidateBaseDir)
    {
        if (string.IsNullOrEmpty(currentCellAbsDir) || string.IsNullOrEmpty(candidateCellRef))
            return false;

        // Normalize once: every directory this method compares against comes back from
        // ResolveCellDirOf already Path.GetFullPath-clean, and the caller's own may not be.
        string target;
        try { target = Path.GetFullPath(currentCellAbsDir); }
        catch { return false; }

        if (ResolveCellDirOf(candidateCellRef, candidateBaseDir) is not { } candidateDir)
            return false;   // virtual, unresolvable or not a folder — nothing real to cycle through

        if (SameDir(candidateDir, target)) return true;   // directly self-referential

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { candidateDir };
        return CanReach(candidateDir, target, visiting, 1);
    }

    /// <summary>
    /// The chain from <paramref name="cellDir"/> down to <paramref name="targetAbsDir"/>, as a list of
    /// cell folder names, or null when there is none. Used to say WHICH loop was refused — a bare
    /// "that would create a cycle" leaves the user hunting for an edge they cannot see, and with
    /// external references the loop can run through a workspace that is not even on screen.
    /// </summary>
    public static IReadOnlyList<string>? DescribeCycle(
        string? currentCellAbsDir, string? candidateCellRef, string? candidateBaseDir)
    {
        if (!WouldCreateCycle(currentCellAbsDir, candidateCellRef, candidateBaseDir)) return null;

        string target = Path.GetFullPath(currentCellAbsDir!);
        string candidateDir = ResolveCellDirOf(candidateCellRef, candidateBaseDir)!;

        var path = new List<string> { LeafOf(target), LeafOf(candidateDir) };
        if (SameDir(candidateDir, target)) return path;

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { candidateDir };
        AppendRoute(candidateDir, target, visiting, 1, path);
        path.Add(LeafOf(target));
        return path;
    }

    // ── The walk ──────────────────────────────────────────────────────────────

    private static bool CanReach(
        string cellDir, string targetAbsDir, HashSet<string> visiting, int depth)
    {
        if (depth >= MaxDepth) return false;

        foreach (var childDir in ChildCellDirs(cellDir))
        {
            if (SameDir(childDir, targetAbsDir)) return true;
            if (!visiting.Add(childDir)) continue;   // already on this path — not a route to target

            bool found = CanReach(childDir, targetAbsDir, visiting, depth + 1);
            visiting.Remove(childDir);
            if (found) return true;
        }
        return false;
    }

    /// <summary><see cref="CanReach"/> again, recording the route it took. Kept separate rather than
    /// folded in so the hot path — the answer, asked on every placement — carries no list.</summary>
    private static bool AppendRoute(
        string cellDir, string targetAbsDir, HashSet<string> visiting, int depth, List<string> route)
    {
        if (depth >= MaxDepth) return false;

        foreach (var childDir in ChildCellDirs(cellDir))
        {
            if (SameDir(childDir, targetAbsDir)) return true;
            if (!visiting.Add(childDir)) continue;

            route.Add(LeafOf(childDir));
            if (AppendRoute(childDir, targetAbsDir, visiting, depth + 1, route))
            {
                visiting.Remove(childDir);
                return true;
            }
            route.RemoveAt(route.Count - 1);
            visiting.Remove(childDir);
        }
        return false;
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every cell folder the cell at <paramref name="cellDir"/> instances from its PRIMARY schematic.
    ///
    /// <para>The primary only, matching what <c>HierarchyResolver</c> descends into and what the
    /// extractor builds — a non-primary view is not what an instance of this cell resolves to, so a
    /// reference inside one cannot be part of a cycle any instance would traverse.</para>
    /// </summary>
    private static IEnumerable<string> ChildCellDirs(string cellDir)
    {
        string? primary = PrimarySchematicOf(cellDir);
        if (primary is null) yield break;

        SchematicEditModel model;
        try { (model, _, _) = SchematicPersistence.LoadFromFile(primary); }
        catch { yield break; }   // unreadable — no edges we can see, and not this gesture's problem

        string baseDir = model.SchematicDirectory ?? Path.GetDirectoryName(primary)!;

        foreach (var comp in model.Components)
        {
            if (ResolveCellDirOf(comp.CellRef, baseDir) is { } dir)
                yield return dir;
        }
    }

    private static string? PrimarySchematicOf(string cellDir)
    {
        try
        {
            if (!Directory.Exists(cellDir)) return null;
            var pr = CellFolder.ResolvePrimary(cellDir, ViewType.Schematic);
            if (pr.State is not (PrimaryState.SoleFile or PrimaryState.NamedPresent)) return null;
            return Path.Combine(
                CellFolder.SubFolderPath(cellDir, ViewType.Schematic), pr.ResolvedName!);
        }
        catch { return null; }
    }

    /// <summary>
    /// The existing cell FOLDER a reference names, or null.
    ///
    /// <para>A VIRTUAL reference is skipped by asking
    /// <see cref="CellSymbolResolver.NeedsNoBaseDirectory"/> rather than by listing the schemes here
    /// — a <c>pdk://</c> part, a wBond design or an unconfigured SPICE model is not a path, resolves
    /// by its own rule, and can never name a cell folder that reaches back. That is the trap that
    /// file's own note records, and it is the reason this asks instead of re-deriving the list.</para>
    ///
    /// <para>The <c>ws://</c> form needs no case of its own: <see cref="ExternalCellRef.ResolveCellDir"/>
    /// handles both spellings, so a cycle that runs through another workspace is found by the same
    /// walk that finds a local one, keyed on absolute folders that cross the boundary without
    /// needing to know one exists.</para>
    /// </summary>
    private static string? ResolveCellDirOf(string? cellRef, string? baseDir)
    {
        if (string.IsNullOrEmpty(cellRef)) return null;
        if (CellSymbolResolver.NeedsNoBaseDirectory(cellRef!)) return null;

        string? dir = ExternalCellRef.ResolveCellDir(cellRef, baseDir);
        return dir is not null && Directory.Exists(dir) ? dir : null;
    }

    private static bool SameDir(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string LeafOf(string dir) =>
        Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
}
