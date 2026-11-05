namespace CircuitRF.Design.Revision;

/// <summary>
/// The two files circuitRF generates in a workspace so that a repository, whenever one appears, keeps
/// the right things (<c>revision-control.md</c> §8.1, §8.1a, §6.1, §4.5; R-rc3-9 … R-rc3-12a).
///
/// <para><b>The rule is not "on creation" but <i>a repository circuitRF created has circuitRF's policy
/// files</i></b> (R-rc3-11a, §8.1a). Putting them only in <c>WorkspaceCreate</c> is right and is not
/// enough: <b>every workspace that exists today was created before that function wrote one</b>, so
/// under creation-only they would never receive a <c>.gitignore</c> at all and would track results
/// forever. They are precisely the workspaces that matter — the ones with years of accumulated output
/// already in them. Three paths reach here: New Workspace, the first arming of an existing workspace,
/// and RC-6's ADOPTION of a user's repository (adoption because that is circuitRF taking
/// responsibility for what is kept, and it is the moment the user said yes).</para>
///
/// <para><b>Once written, the file belongs to the workspace, not to circuitRF</b> (R-rc3-11b). A
/// designer may edit it and RC-5's large-file guard appends to it. <b>circuitRF adds lines and never
/// rewrites the file</b>, and never removes a line it did not add in that same operation. A policy
/// file that is silently regenerated is a policy file whose user edits vanish — and the user finds out
/// when something they had excluded turns up in an archive.</para>
/// </summary>
public static class WorkspacePolicyFiles
{
    public const string GitIgnoreName    = ".gitignore";
    public const string GitAttributesName = ".gitattributes";

    /// <summary>
    /// The marker circuitRF's own block opens with. Its only job is to let <see cref="Ensure"/> tell
    /// "this file already has circuitRF's lines" from "this is the user's own file" without diffing
    /// prose — which is what makes appending idempotent instead of duplicating.
    /// </summary>
    public const string BlockMarker = "# circuitRF — what is kept, and what is not";

    /// <summary>
    /// The result-file patterns (R-rc3-9).
    ///
    /// <para><b>The justification is reproducibility, not size.</b> A result is a function of the
    /// design and the engine, so the design is the thing worth versioning. That they are binary and
    /// float-valued and would delta terribly is a second reason, not the first (§3.2's rule:
    /// <i>compressed or binary content does not delta</i>).</para>
    /// </summary>
    public static readonly string[] ResultPatterns = ["*.npy", "*.spl", "*.lpcwave", "*.mat", "*.ds"];

    /// <summary>
    /// The 3D solvers' run folders — <c>Em3dRunService.RunDirectory</c>'s <c>&lt;key&gt;.palace</c>,
    /// its static/eigenmode <c>&lt;key&gt;.palace_es|_ms|_eig</c>, <c>&lt;key&gt;.openems</c>, and
    /// <c>ThermalRunService.RunDirectory</c>'s <c>&lt;key&gt;.thermal</c>.
    ///
    /// <para><b>Excluded as folders, and that is the whole difference from <see cref="ResultPatterns"/>.</b>
    /// A run folder holds the mesh, the solver's own config and logs, and the field files — a Palace
    /// solve of the 3D Connector example writes 150 ParaView pieces of 10.7 MB each, 695 MB together,
    /// every one under <see cref="LargeFileGuard.ThresholdBytes"/>. Kept, they cost ~40 s of
    /// <c>git add</c> and <c>git gc</c> on the UI thread at the next close and ~500 MB of history per
    /// re-solve. The trailing slash matches only a DIRECTORY, so the Touchstone and the <c>.cdd</c>
    /// written BESIDE the folder under the same stem stay kept. <c>.staging-*</c> is what a killed EM run left of its result
    /// (EmResultStaging), removed by the next run of that setup.</para>
    /// </summary>
    public static readonly string[] RunFolderPatterns = ["*.palace/", "*.palace_*/", "*.openems/", "*.thermal/", ".staging-*/"];

    /// <summary>
    /// Opens the run-folder section. <b>A marker of its own</b>, because the section arrived after
    /// <see cref="BlockMarker"/>'s block had already been written into existing workspaces —
    /// <see cref="Ensure"/> appends it to a file holding the older block, once.
    /// </summary>
    public const string RunFoldersMarker = "# circuitRF — 3D solver run folders";

    /// <summary>
    /// The document types marked unmergeable AND pinned byte-for-byte (R-rc3-12, R-rc3-12a, §6.1).
    /// <c>.c3d</c> joined them with brief-em3d-41 for <c>.clay</c>'s reason: a three-way merge of a
    /// polyhedron's faces can leave it open while the JSON stays well-formed. The block is appended
    /// once and never rewritten, so a workspace whose block predates it does not gain the line.
    /// </summary>
    public static readonly string[] UnmergeableExtensions = [".clay", ".csch", ".csym", ".cws", ".ctech", ".c3d"];

    /// <summary>
    /// Writes both files into <paramref name="workspaceDir"/> if circuitRF's block is not already in
    /// them, and returns the paths it touched. Appends; never rewrites.
    /// </summary>
    public static IReadOnlyList<string> Ensure(string workspaceDir)
    {
        List<string> written = [];
        string ignore = Path.Combine(workspaceDir, GitIgnoreName);

        // A file holding the older block gains only the run-folder section; a file holding neither
        // gains the whole block, which already carries that section.
        if (HasBlock(ignore)
                ? Append(ignore, RunFoldersBlock(), RunFoldersMarker)
                : Append(ignore, GitIgnoreBlock(), BlockMarker))
            written.Add(GitIgnoreName);
        if (Append(Path.Combine(workspaceDir, GitAttributesName), GitAttributesBlock(), BlockMarker))
            written.Add(GitAttributesName);
        return written;
    }

    /// <summary>True when circuitRF's block is already in the file at <paramref name="path"/>.</summary>
    public static bool HasBlock(string path) => HasMarker(path, BlockMarker);

    private static bool HasMarker(string path, string marker)
    {
        try
        {
            return File.Exists(path)
                && File.ReadAllText(path).Contains(marker, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool Append(string path, string block, string marker)
    {
        try
        {
            if (HasMarker(path, marker)) return false;

            string existing = File.Exists(path) ? File.ReadAllText(path) : "";
            string prefix   = existing.Length == 0 || existing.EndsWith('\n') ? "" : "\n";

            // Appended, so a user's own lines above are untouched and stay untouched — R-rc3-11b. The
            // whole file is written back rather than opened for append so a workspace on a share sees
            // one write rather than two.
            File.WriteAllText(path, existing + prefix + block);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A read-only workspace takes no policy files and needs none: nothing will commit there
            // either. WorkspaceWritability answers the same question for the .cws.
            return false;
        }
    }

    /// <summary>
    /// circuitRF's block for <c>.gitignore</c>.
    ///
    /// <para><b>The <c>results/</c> DIRECTORY is deliberately not excluded, and the patterns are.</b>
    /// §8.1 says "results, and simulation output directories" — but that folder is not purely output:
    /// a Data Display (<c>.cdd</c>) is a design document a designer authored, and an EM run's
    /// Touchstone lands there too under a predictable name precisely so a schematic's SnP reference
    /// survives a re-run (<c>EmRunService.ResolveSnpPath</c>). Excluding the folder would drop both
    /// from every restore and every clone, silently. Excluding by EXTENSION keeps the reproducible
    /// output out and the design in. <c>.sNp</c> is never excluded for the same reason in reverse: a
    /// vendor-supplied Touchstone is design INPUT and shares the extension.</para>
    /// </summary>
    public static string GitIgnoreBlock() => string.Join("\n",
    [
        BlockMarker,
        "#",
        "# circuitRF ADDS lines to this file and never rewrites it. Anything you put here stays.",
        "#",
        "# Simulation results are not kept in history, and they are NOT recoverable from it —",
        "# re-run the analysis. The reason is reproducibility rather than size: a result is a",
        "# function of the design and the engine, so the design is the thing worth keeping.",
        .. ResultPatterns,
        "",
        "# Per-user session state: panel layout, open tabs, tree expansion, colour theme. It belongs",
        "# to whoever is sitting here, not to the design, and nothing is lost by leaving it out.",
        CircuitRF.Design.Workspace.WorkspaceUserPersistence.FileName,
        "",
        "# Rebuildable caches, and circuitRF's own per-session bookkeeping dropped in the workspace",
        "# root — the advisory 'someone has this open' notice, the write probe, and a file being",
        "# written atomically. These matter MORE here than in an archive: they are live while the",
        "# workspace is open, which is exactly when a restore point is taken, and a restore point",
        "# that captured the open-notice would record one machine's session in a design's history.",
        GeneratedCellsFolder + "/",
        ".crf-*",
        "*.crf-tmp-*",
        "",
    ]) + "\n" + RunFoldersBlock();

    /// <summary>The run-folder section — part of <see cref="GitIgnoreBlock"/>, and appended on its own
    /// to a file whose block predates it.</summary>
    public static string RunFoldersBlock() => string.Join("\n",
    [
        RunFoldersMarker,
        "#",
        "# A 3D solve's working folder: the mesh, the solver's own files and the field data, often",
        "# hundreds of megabytes. Re-run the setup to rebuild it; the S-parameters written beside it",
        "# are kept.",
        .. RunFolderPatterns,
        "",
    ]) + "\n";

    /// <summary>
    /// circuitRF's block for <c>.gitattributes</c>. Two things, in one file for one reason: <b>a design
    /// document's bytes are the design.</b>
    ///
    /// <para><b><c>-merge</c></b> (R-rc3-12, §6.1). A three-way text merge of a polygon's vertex list
    /// can produce geometry that is invalid, or valid and wrong, while remaining well-formed JSON that
    /// opens without complaint; the same is true of a schematic's connectivity. <b>A merged design that
    /// is silently wrong is worse than a conflict</b>, because the conflict is at least visible.
    /// Conflict resolution is whole-file, pick a side — RC-7 builds that, and this makes it the only
    /// thing git will offer.</para>
    ///
    /// <para><b><c>-text</c></b> (R-rc3-12a) is not housekeeping. §2's entire measurement rests on
    /// <c>.clay</c> being byte-stable, line-oriented text with <c>long</c> coordinates. Git's
    /// end-of-line conversion makes the bytes on disk PLATFORM-DEPENDENT — a whole-file diff on every
    /// cross-platform exchange, and a <c>.clay</c> that is not the file <c>LayoutPersistence</c> wrote.
    /// Belt and braces with <c>core.autocrlf=false</c> in the repository
    /// (<see cref="GitRepositoryConfig"/>): the two answer to different scopes, and a user's global
    /// config can reintroduce the problem the attribute alone would have fixed.</para>
    /// </summary>
    public static string GitAttributesBlock() => string.Join("\n",
    [
        BlockMarker,
        "#",
        "# circuitRF ADDS lines to this file and never rewrites it.",
        "#",
        "# -merge : these are never merged line by line. A three-way merge of a vertex list can",
        "#          produce geometry that is valid and WRONG while staying well-formed JSON. A",
        "#          conflict is worse to look at and better to have; resolution is whole-file.",
        "# -text  : the bytes on disk are the bytes circuitRF wrote, on every platform. Line-ending",
        "#          conversion would make a design document differ from itself across machines.",
        .. UnmergeableExtensions.Select(e => $"*{e} -merge -text"),
        "",
    ]) + "\n";

    /// <summary>
    /// <c>GeneratedCellStore.ReservedFolderName</c>'s value, repeated here because that constant lives
    /// in <c>src/Ui</c> and this project is below the firewall. <b>Kept in step with
    /// <c>WorkspaceArchiveScanner.IsSkipped</c> by a test</b> (R-rc3-10): a file the archive considers
    /// rebuildable is a file the repository considers rebuildable too.
    /// </summary>
    public const string GeneratedCellsFolder = ".generated-cells";
}
