using CircuitRF.Design.Cells;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Workspace;

// ── .cwsuser — one person's session, beside the workspace it describes ────────
//
// RC-1 (brief-revision-control-1-workspace-file-split.md, docs/design/revision-control.md §3.1/§3.1a).
// The `.cws` used to carry the panel arrangement, so it changed on EVERY session close for reasons
// that have nothing to do with the design. Measured on a real workspace's `.cws`, 97.8% of it was
// one person's monitor, tabs, expanded tree categories and colour scheme. That is two documents that
// were only ever one by accident, and this file is the other one.
//
// Three properties hold the whole design up, and each is load-bearing:
//   - ABSENT IS NORMAL, not an edge case. Every clone, every archive and every workspace handed to a
//     colleague arrives without one. A versioned document that cannot be opened without an
//     unversioned one is not split, it is broken.
//   - ABSENCE IS NEVER REPORTED. No warning, no repair prompt, no "recovering your layout". The
//     workspace opens on defaults exactly as a freshly-created one does; anything else trains users
//     to think something is wrong when nothing is.
//   - MALFORMED IS TREATED AS ABSENT — the same posture `CwsFile.DockLayout` already takes with a
//     structurally malformed block, and for the same reason: per-user convenience state must never
//     be able to prevent a design from opening.
//
// The consequence worth documenting is that DELETING IT IS A SUPPORTED REPAIR: a designer whose
// panels have ended up somewhere unusable closes the workspace, deletes one file and reopens. That
// comes free, and holds only while NOTHING on the versioned side depends on this file. Nothing may.
// A future field that must survive that deletion belongs in the `.cws`.

/// <summary>
/// The History panel's filter and search, as one person has them set (RC-10 R-rc10-8, §5.10 rule 4).
///
/// <para><b>Every field defaults to what the panel opens on</b>, so a file written before this existed
/// — which is every one of them — restores the default view rather than an empty list. The one field
/// that is false by default is the workspace-close entries, which is the whole of §5.10 rule 3.</para>
///
/// <para>The search term is carried too, and deliberately: a designer who was mid-hunt when the
/// workspace closed is the one person who wants it back. It is emptied by the magnifier, which is the
/// gesture that means "I have finished looking".</para>
/// </summary>
public sealed class CwsHistoryFilter
{
    public bool Versions   { get; set; } = true;
    public bool SavePoints { get; set; } = true;
    public bool AiBatches  { get; set; } = true;
    public bool Automatic  { get; set; }
    public bool TidiedAway { get; set; } = true;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Search { get; set; }
}

/// <summary>
/// brief-em3d-28 R-em3d28-5 — where one 3D view's camera was, so reopening the view puts it back. It is
/// view state, not design: it lives with the dock layout in the <c>.cwsuser</c>, never in the
/// <c>.cem</c>. Scene-local metres and radians, as <c>Camera3D</c> holds them.
/// </summary>
public sealed class CwsCamera3D
{
    public double TargetX { get; set; }
    public double TargetY { get; set; }
    public double TargetZ { get; set; }
    public double Yaw { get; set; }
    public double Pitch { get; set; }
    public double Distance { get; set; }
    public bool Orthographic { get; set; }
}

/// <summary>
/// The per-user half of a workspace — everything that is a property of one person's session rather
/// than of the project. Written beside the <c>.cws</c> as <c>.cwsuser</c>, the same no-stem
/// convention the <c>.cws</c> already uses.
///
/// <para><b>The name states its role, which is the whole reason for it</b> (§3.1a): a
/// <c>.gitignore</c> line reading <c>.cwsuser</c> next to a tracked <c>.cws</c> is self-explanatory
/// in a directory listing, in a diff, and to whoever inherits the workspace. Two four-letter
/// extensions differing by one character would be transcribed wrongly exactly once and then quietly
/// do the wrong thing for a year.</para>
///
/// <para><b>Two designers on a network share have one of these between them, and that is accepted
/// rather than overlooked</b> (§3.1a). The second to close overwrites the first one's panel
/// arrangement. That is the same shape as the identity mistake §4.4 corrects — per-user state in a
/// shared location — but the costs are not comparable: §4.4's is a durable falsehood in a record,
/// this one's is a panel that moved, in a file whose absence is already normal and whose deletion is
/// already a supported repair. The failure is visible, immediate and self-correcting. Splitting it
/// per user would need a path outside the workspace folder, which loses the one property that makes
/// this file comprehensible at all — that it sits beside the thing it describes.</para>
/// </summary>
public sealed class CwsUserFile
{
    /// <summary>
    /// Written for a human reader and for a future writer. <b>It is never rejected on</b>, and that
    /// is deliberate rather than an omission: <see cref="WorkspacePersistence.Deserialize"/> refuses
    /// a <c>.cws</c> whose version it does not know because the alternative is loading a design
    /// wrongly, whereas the worst a misread <c>.cwsuser</c> can do is restore the wrong panel. A
    /// version check here could only ever turn a readable file into a silently-discarded one.
    /// </summary>
    public int FormatVersion { get; set; } = 1;

    /// <summary>
    /// Saved dock arrangement — see <see cref="CwsFile.DockLayout"/>, whose notes on why this is our
    /// own schema and why it is a raw <see cref="JsonNode"/> apply here unchanged. The field simply
    /// moved; nothing about it did.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? DockLayout { get; set; }

    /// <summary>Project Tree filter category flags + ordering — see <see cref="CwsFile.TreeViewState"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CwsTreeViewState? TreeViewState { get; set; }

    /// <summary>Documents open when the workspace was last saved — see <see cref="CwsFile.OpenDocuments"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CwsOpenDocument>? OpenDocuments { get; set; }

    /// <summary>The active document when the workspace was last saved — see <see cref="CwsFile.ActiveDocumentPath"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveDocumentPath { get; set; }

    /// <summary>
    /// Name of the colour scheme (<c>.ccolor</c>) to activate when this workspace is opened.
    ///
    /// <para><b>Per-user, by the owner's decision of 2026-09-06</b> (§3.1's table left this the one
    /// field whose side was assigned rather than measured, and §12 recorded it as the last small
    /// thing open before RC-1 landed). The argument ran both ways — a house style is a real thing —
    /// and it was settled the other way: a theme is one person's preference, and a shared workspace
    /// imposing its author's theme on everyone who opens it is the more common annoyance of the two.
    /// The cost is that a workspace deliberately shipping a house theme no longer carries it to a
    /// colleague, and that deleting this file resets the theme along with the panels.</para>
    ///
    /// <para>Null means "use the application-level preference", exactly as it did in the <c>.cws</c>.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorSchemeName { get; set; }

    /// <summary>
    /// RC-10 R-rc10-8. <b>What the History panel's filter and search are set to.</b>
    ///
    /// <para><b>Here rather than on the Settings tab, and the distinction is not a filing
    /// preference.</b> Every row of §10A's tab changes what is KEPT; a filter a designer flips while
    /// hunting for something is not a preference about what exists, and putting it there is the
    /// category error that tab is most exposed to. It is view state, like the tree's own category
    /// flags two fields up, and it belongs in the file whose deletion is a supported repair.</para>
    ///
    /// <para>Null means the default view — every entry somebody stated an intent for, with the
    /// workspace-close entries hidden — which is also what a workspace opened for the first time
    /// shows.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CwsHistoryFilter? HistoryFilter { get; set; }

    /// <summary>brief-em3d-28 R-em3d28-5 — each 3D view's camera, by its <c>.cem</c>'s path relative to
    /// the workspace root (forward slashes). Null when no 3D view has been opened.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, CwsCamera3D>? Viewer3DCameras { get; set; }

    /// <summary>True when this carries nothing worth a file — the state in which no sidecar is written.</summary>
    internal bool IsEmpty =>
        DockLayout is null && TreeViewState is null && OpenDocuments is null &&
        ActiveDocumentPath is null && ColorSchemeName is null && HistoryFilter is null &&
        Viewer3DCameras is null;
}

/// <summary>
/// Reads and writes the <c>.cwsuser</c> sidecar. Framework-free, beside
/// <see cref="WorkspacePersistence"/> and driven only by it.
///
/// <para><b>Nothing outside <see cref="WorkspacePersistence"/> may write one.</b> The <c>.cws</c>
/// write became a single choke point in SL2 precisely because a rule enforced by eighteen callers
/// agreeing is a rule that is true in seventeen places and found by a user in the eighteenth; the
/// split inherits that, so a nineteenth call site gets the sidecar without knowing it exists. A
/// source scan gates it.</para>
/// </summary>
public static class WorkspaceUserPersistence
{
    /// <summary>The sidecar's file name — literally <c>.cwsuser</c>, a dotfile with no stem.</summary>
    public const string FileName = ".cwsuser";

    /// <summary>
    /// The <c>.cws</c> property names that moved here, as they are spelled in the JSON.
    ///
    /// <para><b>The <c>.cws</c> is stripped at the JSON level, not by copying a typed object.</b>
    /// A round trip through a hand-written field list drops any field the list forgot — silently,
    /// and only for the workspaces of whoever hits it. Naming the ones that LEAVE means the next
    /// field added to <see cref="CwsFile"/> keeps being written with no thought required, which is
    /// the direction the mistake should fall. Same reasoning as
    /// <c>WorkspaceArchiveWriter.RewriteCws</c>, which edits the parsed tree for the same reason.</para>
    /// </summary>
    internal static readonly string[] MovedFieldNames =
    [
        // Anchored on CwsFile, because these are the keys being removed from the `.cws`. Anchoring
        // them on CwsUserFile would let a rename on one side and not the other stop the strip
        // silently, which is the only way this list can go wrong.
        nameof(CwsFile.DockLayout),
        nameof(CwsFile.TreeViewState),
        nameof(CwsFile.HistoryFilter),
        nameof(CwsFile.OpenDocuments),
        nameof(CwsFile.ActiveDocumentPath),
        nameof(CwsFile.ColorSchemeName),
        nameof(CwsFile.Viewer3DCameras),
    ];

    /// <summary>The per-user half of <paramref name="ws"/>, lifted out for the sidecar.</summary>
    internal static CwsUserFile Extract(CwsFile ws) => new()
    {
        DockLayout         = ws.DockLayout,
        TreeViewState      = ws.TreeViewState,
        HistoryFilter      = ws.HistoryFilter,
        OpenDocuments      = ws.OpenDocuments,
        ActiveDocumentPath = ws.ActiveDocumentPath,
        ColorSchemeName    = ws.ColorSchemeName,
        Viewer3DCameras    = ws.Viewer3DCameras,
    };

    /// <summary>
    /// Overlays a loaded sidecar onto the <c>.cws</c> half, giving callers the one merged
    /// <see cref="CwsFile"/> shape they already hold.
    ///
    /// <para><b>The sidecar is authoritative for all seven, including the ones it leaves null.</b>
    /// Anything else would resurrect a stale copy left in an older <c>.cws</c> after the user had
    /// closed the tab it names.</para>
    /// </summary>
    internal static void Merge(CwsFile ws, CwsUserFile user)
    {
        ws.DockLayout         = user.DockLayout;
        ws.TreeViewState      = user.TreeViewState;
        ws.HistoryFilter      = user.HistoryFilter;
        ws.OpenDocuments      = user.OpenDocuments;
        ws.ActiveDocumentPath = user.ActiveDocumentPath;
        ws.ColorSchemeName    = user.ColorSchemeName;
        ws.Viewer3DCameras    = user.Viewer3DCameras;
    }

    /// <summary>The sidecar beside a given <c>.cws</c> path.</summary>
    public static string PathFor(string cwsPath)
        => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(cwsPath)) ?? "", FileName);

    /// <summary>
    /// The <c>.cws</c> a given <c>.cwsuser</c> belongs to, or <b>null</b> when there is none beside
    /// it.
    ///
    /// <para>The sidecar resolves to its workspace through the FOLDER that contains it, exactly as
    /// its sibling <c>.cws</c> does — which is what lets double-clicking either half open the whole
    /// workspace (R-rc1-13).</para>
    ///
    /// <para>Null is the answer for the case that is silent if missed: <b>a <c>.cwsuser</c> with no
    /// <c>.cws</c> beside it is not a workspace</b> (R-rc1-8). Someone who copied one file out of a
    /// folder must get a sentence, not an empty window — and the sidecar is precisely the file
    /// someone copies by mistake, being the small one with the unfamiliar name.</para>
    /// </summary>
    public static string? ResolveWorkspace(string cwsUserPath)
    {
        string dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(cwsUserPath)) ?? ""; }
        catch { return null; }

        string cws = Path.Combine(dir, WorkspacePersistence.FileName);
        return File.Exists(cws) ? cws : null;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(CwsUserFile user) => JsonSerializer.Serialize(user, JsonOpts);

    /// <summary>
    /// The sidecar beside <paramref name="cwsPath"/>, or <b>null</b> when there is none to read —
    /// missing, unreadable, truncated, or valid JSON of the wrong shape. Every one of those is the
    /// same answer on purpose (R-rc1-4, R-rc1-6): this file may never be the reason a design does not
    /// open, and a caller that could tell them apart would only be tempted to report one of them.
    /// </summary>
    public static CwsUserFile? TryLoad(string cwsPath)
    {
        try
        {
            string path = PathFor(cwsPath);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<CwsUserFile>(File.ReadAllText(path), JsonOpts);
        }
        catch { return null; }
    }

    /// <summary>
    /// Writes the sidecar beside <paramref name="cwsPath"/>, or deletes it when
    /// <paramref name="user"/> carries nothing.
    ///
    /// <para><b>Delete-on-empty is what keeps the split behaviour-preserving.</b> Before RC-1 a
    /// caller that assembled a <see cref="CwsFile"/> without session state and saved it cleared
    /// those fields out of the <c>.cws</c>; the sidecar has to do the same thing or the two halves
    /// disagree about whether a layout exists.</para>
    ///
    /// <para><b>Failure is swallowed, and there is no cross-file transaction</b> (R-rc1-11). The
    /// <c>.cws</c> write succeeds or leaves the old file intact, as it always did; a sidecar write
    /// that fails after it loses panel positions and nothing else — which is exactly why the split
    /// puts the unimportant half in the second file.</para>
    /// </summary>
    internal static void Save(string cwsPath, CwsUserFile user)
    {
        string path = PathFor(cwsPath);
        try
        {
            if (user.IsEmpty)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            AtomicFile.WriteAllText(path, Serialize(user));
        }
        catch { /* R-rc1-11: panel positions, and nothing else. */ }
    }
}
