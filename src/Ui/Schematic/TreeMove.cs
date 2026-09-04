namespace CircuitRF.Ui.Schematic;

/// <summary>Why a move inside the Project Tree is not allowed. Each has its own sentence, and each
/// is answered in <c>DragOver</c> so the cursor already says no — a refusal that only appears on
/// release has already cost the user the gesture (R-tm1-10).</summary>
public enum MoveRefusal
{
    /// <summary>It is allowed.</summary>
    None,

    /// <summary>Nothing to do — the destination IS the folder it already sits in. Not an error and
    /// not worth a message; the drag simply offers no effect.</summary>
    AlreadyThere,

    /// <summary>This kind of node does not move (R-tm1-8) — a cell's own views, a synthetic group
    /// node, the workspace root.</summary>
    NotMovable,

    /// <summary>The destination is inside the moved subtree — moving a folder into itself.</summary>
    IntoItself,

    /// <summary>The destination already holds an entry of that name.</summary>
    NameTaken,

    /// <summary>Source or destination is not somewhere circuitRF can write (SL2 R-sl2-1).</summary>
    NotWritable,

    /// <summary>Source or destination belongs to a referenced library or another workspace.</summary>
    NotOwned,
}

/// <summary>The decision, and the sentence that goes with it.</summary>
/// <param name="Refusal"><see cref="MoveRefusal.None"/> when the move may proceed.</param>
/// <param name="Message">Empty when permitted or when there is nothing worth saying.</param>
/// <param name="SourcePath">Absolute path of the thing being moved.</param>
/// <param name="DestFolder">Absolute path of the folder it lands in.</param>
/// <param name="DestPath">Absolute path it will occupy — <c>DestFolder/name</c>.</param>
public readonly record struct TreeMoveIntent(
    MoveRefusal Refusal, string Message, string SourcePath, string DestFolder, string DestPath)
{
    public bool Permitted => Refusal == MoveRefusal.None;

    internal static TreeMoveIntent No(MoveRefusal refusal, string message, string src, string dest) =>
        new(refusal, message, src, dest, "");
}

/// <summary>
/// The rule a Project Tree MOVE follows — the drop MW3 deliberately left inert (a drop inside the
/// tree the drag started in), now the gesture that organises a workspace without leaving the
/// application.
///
/// <para>Kept out of the view for the same reason <see cref="TreeDrop"/> is: <c>DragOver</c> and
/// <c>Drop</c> ask it identically, so the effect the cursor promises and the thing that happens are
/// one decision and cannot drift. It is also what the gate asserts against, rather than driving a
/// TreeView and inferring the answer from a cursor.</para>
/// </summary>
public static class TreeMove
{
    /// <summary>
    /// The node kinds a drag may START from (R-tm1-7) — read from here, never respelled at a call
    /// site.
    ///
    /// <para><b><see cref="NodeKind.CellViewFolder"/> and <see cref="NodeKind.ViewFile"/> are absent
    /// on purpose, and it is structural as well as the owner's rule.</b> <c>CellFolder</c> resolves
    /// <c>schematic/</c>, <c>symbol/</c> and <c>layout/</c> BY NAME, and the <c>.ccell</c> names
    /// primaries within them: a cell whose views have been rearranged is not a cell with a different
    /// shape, it is a cell that no longer resolves. The workspace root, every synthetic group node
    /// and <see cref="NodeKind.NotReadYet"/> are absent for the ordinary reason that they are not
    /// things on disk that a user can put somewhere else.</para>
    /// </summary>
    public static bool IsMovable(NodeKind kind) => kind is
        NodeKind.Cell or NodeKind.UserFolder
     or NodeKind.OtherFile or NodeKind.DataDisplayFile or NodeKind.TechFile
     or NodeKind.ColorThemeFile or NodeKind.EmSetupFile or NodeKind.WBondFile
     or NodeKind.HarmonicaFile;

    /// <summary>
    /// What a path on disk moves AS. The drop carries only an absolute path — the payload is a
    /// string on the platform pasteboard — so the receiving side re-derives the kind from the
    /// filesystem rather than trusting the wire, which is also what makes the rule assertable
    /// without a TreeView.
    /// </summary>
    public static NodeKind ClassifyForMove(string path)
    {
        if (Directory.Exists(path))
            return File.Exists(Path.Combine(path, CellFolder.CcellFileName))
                ? NodeKind.Cell
                : NodeKind.UserFolder;

        return File.Exists(path) ? WorkspaceScanner.ClassifyFile(path) : NodeKind.NotReadYet;
    }

    /// <summary>
    /// True when <paramref name="path"/> sits inside a cell folder — a view sub-folder, or anything
    /// under one.
    ///
    /// <para><b>This is the structural half of R-tm1-8 and it is not redundant.</b> A cell's
    /// <c>schematic/</c> folder classifies from disk as an ordinary
    /// <see cref="NodeKind.UserFolder"/>, so the kind check alone would let it be dragged out —
    /// after which the cell resolves nothing, because <c>CellFolder</c> finds those folders BY NAME
    /// and the <c>.ccell</c> names primaries within them. The view files themselves are already
    /// excluded, since <c>.csch</c>/<c>.csym</c>/<c>.clay</c> classify as
    /// <see cref="NodeKind.ViewFile"/>.</para>
    /// </summary>
    public static bool IsInsideACell(string path, string workspaceRoot)
    {
        string root = Norm(workspaceRoot);
        var dir = Path.GetDirectoryName(Norm(path));

        while (dir is not null && !string.Equals(Norm(dir), root, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(Path.Combine(dir, CellFolder.CcellFileName))) return true;
            var next = Path.GetDirectoryName(dir);
            if (next is null || string.Equals(next, dir, StringComparison.Ordinal)) break;
            dir = next;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="sourcePath"/> may be moved into <paramref name="destFolder"/>, and
    /// why not when it may not.
    /// </summary>
    /// <param name="destFolder">The folder the drop resolves to — a folder node itself, a CELL
    /// node's parent, a file's own directory. Null means the workspace root.</param>
    public static TreeMoveIntent For(
        string? sourcePath, NodeKind sourceKind, string? destFolder, string? workspaceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(workspaceRoot))
            return TreeMoveIntent.No(MoveRefusal.NotMovable, "", sourcePath ?? "", destFolder ?? "");

        if (!IsMovable(sourceKind))
            return TreeMoveIntent.No(
                MoveRefusal.NotMovable,
                sourceKind is NodeKind.CellViewFolder or NodeKind.ViewFile
                    ? "A cell's own views cannot be rearranged — a cell resolves its schematic, "
                    + "symbol and layout folders by name."
                    : "That cannot be moved.",
                sourcePath, destFolder ?? "");

        string root = Norm(workspaceRoot);
        string src  = Norm(sourcePath);
        string dest = Norm(string.IsNullOrWhiteSpace(destFolder) ? root : destFolder!);

        string name = Path.GetFileName(src);
        if (name.Length == 0)
            return TreeMoveIntent.No(MoveRefusal.NotMovable, "", src, dest);

        // ── 4. Ownership, asked of BOTH ends ──────────────────────────────────
        // A cell under a referenced library or another workspace is someone else's disk; so is a
        // destination outside this tree. This is OwnedByThisWorkspace's existing question, asked
        // before the gesture rather than after it.
        if (WorkspaceRootFinder.IsOutside(src, root))
            return TreeMoveIntent.No(
                MoveRefusal.NotOwned,
                $"'{name}' belongs to another workspace or a referenced library, so it cannot be "
              + "moved from here. Open that workspace in its own window and do it there.",
                src, dest);

        if (WorkspaceRootFinder.IsOutside(dest, root))
            return TreeMoveIntent.No(
                MoveRefusal.NotOwned,
                "That folder belongs to another workspace or a referenced library, so nothing can "
              + "be moved into it from here.",
                src, dest);

        if (IsInsideACell(src, root))
            return TreeMoveIntent.No(
                MoveRefusal.NotMovable,
                "A cell's own views cannot be rearranged — a cell resolves its schematic, symbol and "
              + "layout folders by name.",
                src, dest);

        // ── Nothing to do ─────────────────────────────────────────────────────
        string? currentParent = Path.GetDirectoryName(src);
        if (currentParent is not null && string.Equals(Norm(currentParent), dest, StringComparison.OrdinalIgnoreCase))
            return TreeMoveIntent.No(MoveRefusal.AlreadyThere, "", src, dest);

        // ── 1. Into itself ────────────────────────────────────────────────────
        if (string.Equals(dest, src, StringComparison.OrdinalIgnoreCase)
         || dest.StartsWith(src + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return TreeMoveIntent.No(
                MoveRefusal.IntoItself,
                $"'{name}' cannot be moved inside itself.", src, dest);

        // ── 2. Name already taken ─────────────────────────────────────────────
        string target = Path.Combine(dest, name);
        if (Directory.Exists(target) || File.Exists(target))
            return TreeMoveIntent.No(
                MoveRefusal.NameTaken,
                $"'{Path.GetFileName(dest)}' already holds something called '{name}'.", src, dest);

        // ── 3. Writability, both ends (SL2 R-sl2-1) ───────────────────────────
        // A move REMOVES from the source's parent and ADDS to the destination, so both have to be
        // writable and one answer for "the workspace" would be wrong the moment the two are
        // different mounts.
        if (currentParent is not null && !WorkspaceWritability.IsWritable(currentParent))
            return TreeMoveIntent.No(
                MoveRefusal.NotWritable,
                $"'{name}' is in a read-only location, so it cannot be moved out of it.", src, dest);

        if (!WorkspaceWritability.IsWritable(dest))
            return TreeMoveIntent.No(
                MoveRefusal.NotWritable,
                $"'{Path.GetFileName(dest)}' is read-only, so nothing can be moved into it.", src, dest);

        return new TreeMoveIntent(MoveRefusal.None, "", src, dest, target);
    }

    private static string Norm(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch { return path; }
    }
}
