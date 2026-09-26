using CircuitRF.Design.Cells;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Workspace;

// ── .cws workspace manifest — rev 2 ──────────────────────────────────────────
// A workspace is the collection of files that make up a project:
//   - member_files: relative paths to .csch / .cdd / .csym / .cnl etc.
//   - library_refs: paths to library manifests (.clib)
//   - dock_layout:  saved panel/tab/floating-window arrangement (OUR schema — see CwsDockLayout)
//
// Rules (mirror .csch):
//   - format_version: reject on mismatch
//   - references, never embedded payloads
//   - relative paths preferred
//
// ── The per-user half lives elsewhere, since RC-1 ────────────────────────────
// `CwsFile` is still the ONE shape callers hold, but five of its properties — DockLayout,
// TreeViewState, HistoryFilter, OpenDocuments, ActiveDocumentPath and ColorSchemeName — are PERSISTED in a sibling
// `.cwsuser` (see WorkspaceUserPersistence). They were ~98% of a real `.cws`, and none of them
// describes the design: the file used to change on every session close for reasons that have nothing
// to do with the project, which is wrong in an archive, wrong on a share, wrong in a read-only
// referenced workspace, and wrong a fourth way under version control.
//
// The split is a PERSISTENCE split, not a model split. Serialize/SaveToFile/SaveToFileAtomic write
// two files; LoadFromFile reads both and hands back one merged `CwsFile`. No caller changed.

/// <summary>
/// One open document entry persisted in .cws — path, kind, and tab order.
/// Restored when the workspace is next opened.
/// </summary>
public sealed class CwsOpenDocument
{
    /// <summary>Relative (preferred) or absolute path to the document file or cell folder.</summary>
    public string Path { get; set; } = "";

    /// <summary>"schematic" (.csch), "symbol" (.csym), or "cell" (cell folder).</summary>
    public string Kind { get; set; } = "schematic";

    /// <summary>Zero-based tab order used to restore the original tab sequence.</summary>
    public int TabOrder { get; set; }
}

/// <summary>
/// Tree view-state persisted in .cws — filter category flags + ordering preference.
/// Ordering is alphabetical-only in v1; the field is reserved for a future ordering UI (§3.1).
/// </summary>
public sealed class CwsTreeViewState
{
    public bool Cells               { get; set; } = true;
    public bool Libraries           { get; set; } = true;
    public bool TestBenches         { get; set; } = true;
    public bool DataDisplays        { get; set; } = true;
    public bool ColorThemes         { get; set; } = true;
    public bool TechFiles           { get; set; } = true;
    public bool KnownFiles          { get; set; } = true;
    public bool WorkspaceFileSystem { get; set; } = true;

    /// <summary>
    /// The root-level rows for cells referenced ONE AT A TIME out of another workspace
    /// (<see cref="CwsFile.ReferencedCells"/>), and the root-level rows for whole referenced
    /// workspaces. Both default to on, like every other category, and an older <c>.cws</c> with no
    /// value for them therefore restores as on rather than hiding rows the user never turned off.
    /// </summary>
    public bool ReferencedCells      { get; set; } = true;
    public bool ReferencedWorkspaces { get; set; } = true;

    /// <summary>Ordering mode; "alphabetical" is the only valid value in v1.</summary>
    [System.Text.Json.Serialization.JsonIgnore(
        Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Ordering { get; set; }
}

public sealed class CwsFile
{
    public int FormatVersion { get; set; } = 2;

    /// <summary>
    /// Relative or absolute paths to external library folders (or legacy .clib manifest files).
    /// Resolved by the scanner relative to the workspace root when relative.
    /// </summary>
    public List<string> LibraryRefs { get; set; } = [];

    /// <summary>
    /// Relative or absolute paths to files bookmarked for convenient access (§5).
    /// Unresolvable paths produce a KnownFile node with a WarningReason.
    /// </summary>
    public List<string> KnownFiles { get; set; } = [];

    /// <summary>
    /// Saved dock arrangement — panels, tabbed groups, floating windows, document tab order.
    ///
    /// <para>This is <b>our</b> schema (<c>CircuitRF.Ui.Docking.CwsDockLayout</c>), never the docking
    /// library's serialized object graph (brief-dock-layout-persistence.md R-dock-3): <c>.cws</c> is a
    /// human-readable, long-lived file, and a third-party library's graph is neither — it is opaque to
    /// a reader, and a library upgrade can invalidate every saved workspace in the field.</para>
    ///
    /// <para>Typed as a raw <see cref="JsonNode"/> on purpose (R-dock-5): a structurally malformed
    /// block must not take the rest of the <c>.cws</c> — the tree state and the open-document list —
    /// down with it. <c>DockLayoutSerialization.TryRead</c> parses it separately behind its own
    /// try/catch, so a layout problem can never prevent a workspace from opening. It also means a
    /// block written by a newer build round-trips verbatim instead of being rewritten to a lossy
    /// subset. Null when no layout has been captured yet.</para>
    ///
    /// <para><b>Persisted in the sibling <c>.cwsuser</c>, not in the <c>.cws</c></b> (RC-1) — see
    /// <see cref="CwsUserFile"/>. Still read and written through this property; only the file changed.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? DockLayout { get; set; }

    /// <summary>
    /// Name of the color scheme (.ccolor) to activate when this workspace is opened.
    /// Resolved via ThemeResolver (workspace dir → user dir → built-in assets).
    /// Null means "use the application-level preference".
    ///
    /// <para><b>Persisted in the sibling <c>.cwsuser</c>, not in the <c>.cws</c></b> (RC-1) — see
    /// <see cref="CwsUserFile"/>. Still read and written through this property; only the file changed.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ColorSchemeName { get; set; }

    /// <summary>
    /// Project Tree filter category flags + ordering, restored on open.
    /// Null means "use defaults" (all categories on, alphabetical ordering).
    ///
    /// <para><b>Persisted in the sibling <c>.cwsuser</c>, not in the <c>.cws</c></b> (RC-1) — see
    /// <see cref="CwsUserFile"/>. Still read and written through this property; only the file changed.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CwsTreeViewState? TreeViewState { get; set; }

    /// <summary>
    /// What the History panel's filter and search are set to, restored on open (RC-10 R-rc10-8).
    /// Null means the default view — every entry somebody stated an intent for, with the
    /// workspace-close entries hidden.
    ///
    /// <para><b>Persisted in the sibling <c>.cwsuser</c>, not in the <c>.cws</c></b> — see
    /// <see cref="CwsUserFile"/>. It is view state, and deliberately not a Settings row: every row on
    /// the Revision Control tab changes what is KEPT, and a filter a designer flips while hunting is
    /// not a preference about what exists.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CwsHistoryFilter? HistoryFilter { get; set; }

    /// <summary>
    /// brief-em3d-28 R-em3d28-5 — each 3D view's camera, keyed by its <c>.cem</c>'s workspace-relative
    /// path. View state, so it is <b>persisted in the sibling <c>.cwsuser</c></b> with the dock layout,
    /// never in the <c>.cem</c> — the camera is not part of the design.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, CwsCamera3D>? Viewer3DCameras { get; set; }

    /// <summary>
    /// Documents open in the main DocumentDock when the workspace was last saved.
    /// Null or empty means no documents to restore (welcome stub is shown).
    /// Scratch documents are never persisted here.
    ///
    /// <para><b>Persisted in the sibling <c>.cwsuser</c>, not in the <c>.cws</c></b> (RC-1) — see
    /// <see cref="CwsUserFile"/>. Still read and written through this property; only the file changed.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CwsOpenDocument>? OpenDocuments { get; set; }

    /// <summary>
    /// Relative (preferred) or absolute path of the active document when the workspace
    /// was last saved.  Null when no named document was active.
    ///
    /// <para><b>Persisted in the sibling <c>.cwsuser</c>, not in the <c>.cws</c></b> (RC-1) — see
    /// <see cref="CwsUserFile"/>. Still read and written through this property; only the file changed.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ActiveDocumentPath { get; set; }

    /// <summary>
    /// Relative path (from the workspace root) to the .ctech that TechnologyResolver falls back to
    /// when a .clay's own TechRef is null (docs/design/layout-view.md §2.4 "one default per
    /// workspace"). Null means "no default" — a valid state that resolves to the fallback palette.
    /// No FormatVersion bump: an absent field on an older .cws loads gracefully as null.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultTechRef { get; set; }

    /// <summary>
    /// Relative path (from the workspace root) to the `.wasm` assembly rule file
    /// <c>WasmResolver</c> falls back to when a `.wBond`'s own <c>AssemblyRef</c> is null
    /// (docs/design/wbond.md §8, WB31).
    ///
    /// <para>Null means "no assembly rules", which is a valid state and NOT an error: a design with
    /// no house stated simply has its die-side rules checked and its wire geometry validated. It sits
    /// beside <see cref="DefaultTechRef"/> rather than inside the `.ctech` because the relation
    /// between assembly houses and process technologies is many-to-many and their lifecycles differ —
    /// that is the whole of WB31.</para>
    ///
    /// <para>No FormatVersion bump: an absent field on an older `.cws` loads gracefully as null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultAssemblyRef { get; set; }

    /// <summary>
    /// The Python interpreter this workspace settled on for PCell generator scripts — the command,
    /// with any prefix arguments after it (e.g. <c>py.exe -3</c>).
    ///
    /// <para><b>An automatically-made decision, recorded so it is visible and one line to
    /// correct.</b> Discovery probes candidates by RUNNING each one, which costs a process launch
    /// apiece; the answer does not change between sessions, so it is replayed rather than
    /// re-derived — the same bargain a kit's own settings already strike, where the measured
    /// difference was 0.5 ms against 199.8 ms. A recorded interpreter that no longer works is
    /// re-derived and re-recorded rather than treated as fatal: an interpreter can be upgraded or
    /// removed between sessions, and the workspace should heal rather than need the user to know
    /// that is what happened.</para>
    ///
    /// <para>Null means "not settled yet". No <c>FormatVersion</c> bump — an absent field on an
    /// older <c>.cws</c> loads as null and discovery simply runs.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PythonInterpreter { get; set; }

    /// <summary>
    /// PDKs this workspace references. An import writes nothing into the workspace — a kit's
    /// translated symbols and parameter interfaces are the vendor's content and are rebuilt in
    /// memory on open (docs/design/pdk-import.md). Null or empty means no kits.
    ///
    /// <para>No FormatVersion bump: an absent field on an older .cws loads as null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CwsPdkRef>? PdkRefs { get; set; }

    /// <summary>
    /// Other WORKSPACES this one may reference cells in — the alias table a <c>ws://</c> cell
    /// reference resolves through (MW2 R-mw2-2).
    ///
    /// <para><b>This is the one place a cross-workspace path is written.</b> Relocating the other
    /// project is one edit here rather than a rewrite of every document that referenced it, which is
    /// the concern <c>workspace-and-project-tree.md</c> §5A R37 names when it defers the choice
    /// between an alias and a raw path in every file. It sits beside <see cref="LibraryRefs"/>
    /// because it is the same kind of entry for a workspace rather than a <c>.clib</c> folder:
    /// configuration, never membership.</para>
    ///
    /// <para>Null or empty means this workspace references no other, which is the overwhelmingly
    /// common case. No <c>FormatVersion</c> bump — an absent field on an older <c>.cws</c> loads as
    /// null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CwsWorkspaceRef>? ReferencedWorkspaces { get; set; }

    /// <summary>
    /// Individual cells in other workspaces this one references — each a <c>ws://alias/rel/path</c>
    /// string resolving through <see cref="ReferencedWorkspaces"/>, and each rendered as ONE row at
    /// the Project Tree's root.
    ///
    /// <para><b>Why a second list rather than more aliases.</b> Referencing a cell and referencing a
    /// workspace are different acts with the same machinery underneath: the alias is how a
    /// cross-workspace path is written down exactly once (R-mw2-4) and it has to exist either way,
    /// but bringing in one cell must not bring in the other project's whole catalogue — which is what
    /// listing only the alias did, and what put dozens of unwanted rows in the tree. So the alias is
    /// still created, marked <see cref="CwsWorkspaceRef.CellsOnly"/>, and the CELL is what the tree
    /// lists.</para>
    ///
    /// <para>Null or empty means this workspace references no individual cell. No
    /// <c>FormatVersion</c> bump — an absent field on an older <c>.cws</c> loads as null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ReferencedCells { get; set; }

    /// <summary>
    /// RC-4 (<c>docs/design/revision-control.md</c> §5.7, §5.7a, R-rc4-12): whether circuitRF keeps a
    /// history for THIS workspace. <b>Null — never recorded — falls back to the per-user preference</b>
    /// (<c>RevisionArming.IsArmed</c>); an explicit value outranks it, for this workspace only.
    ///
    /// <para><b>Why it is here and not in <c>AppPreferences</c>.</b> An installation-wide flag cannot
    /// gate per-workspace state: it is correct for the first workspace and silently wrong for the
    /// second. This repository has already made that mistake once and recorded it
    /// (<c>src/Ui/RESOLVED.md</c>, the wirebond group work, where a per-installation flag failed on the
    /// second workspace as floating panels). The per-user preference beside it is not that flag — it is
    /// the value this one falls back to when it is absent, which is why absent and <c>false</c> are
    /// different states and this property is nullable.</para>
    ///
    /// <para><b>The VERSIONED half, deliberately, not the <c>.cwsuser</c></b> — so turning it off is
    /// itself a recorded change, and the last checkpoint before the history goes quiet is the one that
    /// says why (§5.7). It follows from that placement that the setting TRAVELS: a clone or an archive
    /// of a workspace that was switched off arrives switched off, and the recipient's own preference
    /// does not override it, because the flag says <i>not for this one</i> about the workspace and the
    /// workspace is what travelled.</para>
    ///
    /// <para><b>Off never deletes anything</b> (§5.7). It means circuitRF stops writing; every restore
    /// point already taken stays browsable and restorable, and turning it back on resumes the same
    /// history. Removing a history is deleting one plainly-named folder and is not something circuitRF
    /// offers.</para>
    ///
    /// <para>No <c>FormatVersion</c> bump: an absent field on an older <c>.cws</c> loads as null, which
    /// is exactly the "has never recorded one" state the fallback is written for.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? RevisionControl { get; set; }
}

/// <summary>
/// One referenced workspace: the name cells are addressed by, and where that workspace is.
///
/// <para>The alias is what a <c>ws://</c> reference in a design document carries, so it is not
/// cosmetic — change it and every external instance placed through it stops resolving, exactly as
/// renaming a <see cref="CwsPdkRef.Provider"/> does.</para>
/// </summary>
public sealed class CwsWorkspaceRef
{
    /// <summary>The name <c>ws://&lt;alias&gt;/…</c> uses. Defaults to the other workspace's folder
    /// name at the moment the reference is created; unique within one <c>.cws</c>.</summary>
    public string Alias { get; set; } = "";

    /// <summary>
    /// The other workspace's <c>.cws</c> file — workspace-relative where it can be, absolute across
    /// volumes, which is <c>WorkspaceRefs.ToStoredRef</c>'s rule. Two sibling project folders are
    /// the ordinary case, so relative is the ordinary answer and the pair travels together.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// True when this alias exists ONLY to address the cells listed in
    /// <see cref="CwsFile.ReferencedCells"/> — the alias table entry a per-cell reference needs, and
    /// nothing more. Such an entry is NOT rendered in the Project Tree: referencing one cell brings
    /// in one cell, never the other workspace's whole catalogue.
    ///
    /// <para><b>It changes addressing not at all.</b> A <c>ws://</c> reference resolves through the
    /// same table whatever this flag says — <see cref="ExternalCellRef"/> never reads it — so the
    /// flag is purely about what the tree shows. <c>File ▸ Reference Workspace…</c> on a workspace
    /// already referenced this way clears it, which promotes the existing alias rather than adding a
    /// second name for the same target.</para>
    ///
    /// <para>False on every entry written before this existed, which is the old behaviour: those
    /// aliases were created BY the whole-workspace gesture and go on rendering as they did.</para>
    ///
    /// <para><b>That last sentence is the house rule for an absent field, and
    /// <see cref="Editable"/> below deliberately breaks it.</b> Do not copy this instinct into the
    /// next field without asking which answer the old behaviour actually was — here it is a
    /// preference someone set; there it is the hazard.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool CellsOnly { get; set; }

    /// <summary>
    /// RC-2 (§7A.2): whether cells reached through this reference may be EDITED in the window that
    /// merely references them. False — read-only — is the default, and
    /// <c>File ▸ Reference Workspace…</c> creates read-only references.
    ///
    /// <para><b>An absent field means READ-ONLY, which deliberately inverts the rule
    /// <see cref="CellsOnly"/> states three lines above.</b> The house instinct for a field that did
    /// not used to exist is "restore the old visible behaviour" — and that instinct is wrong here,
    /// because the old behaviour is not a preference anyone set, it is the hazard. Editing a library
    /// cell through a reference writes the file and records the change in NOBODY's history: not the
    /// designer's workspace (the file is not in it) and not the librarian's (they were not asked).
    /// The fix works for one designer, every other designer goes on simulating the old cell, and the
    /// next publish overwrites it or conflicts with nothing to merge from. Every step of that is
    /// silent, and none of it needs revision control to happen.</para>
    ///
    /// <para>The cost of inverting is one-time friction for anyone who was editing through a
    /// reference: a refusal that carries its remedy, and this flag one click away in the Project
    /// Tree. The alternative is that the workspaces most likely to have accumulated the practice are
    /// the only ones never protected from it.</para>
    ///
    /// <para><b>This is a POLICY, not a filesystem fact</b> — "should circuitRF write here?", which
    /// <c>WorkspaceWritability</c>'s "can circuitRF write here?" cannot answer and does not try to.
    /// §5D R-sl2-A's rule that read-only is never a field in a <c>.cws</c> is about THAT question and
    /// is unchanged: this field says nothing about permissions, and a determined user editing the
    /// library's files outside circuitRF is not being stopped by it (§7A.5).</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Editable { get; set; }

    /// <summary>
    /// RC-9 (§7, §7A.4): the version of the referenced workspace this design was built and verified
    /// against — a commit identity in THAT workspace's own repository. Null, the default, is
    /// <b>unpinned</b>: the reference resolves to whatever the other workspace contains today, which
    /// is the behaviour every <c>.cws</c> written before this field existed already had.
    ///
    /// <para><b>Absent means UNPINNED, which is <see cref="Editable"/>'s inversion put back.</b> The
    /// two fields sit on one record and take opposite defaults, so each has to say why. There the old
    /// behaviour was the hazard — an unrecorded edit into somebody else's library — and inverting it
    /// costs one refusal that carries its remedy. Here the old behaviour is a <i>preference</i>: a
    /// designer who never asked for a pin wants the librarian's corrections, which is the reason they
    /// referenced a workspace rather than copying its cells. Defaulting to pinned would freeze every
    /// existing reference at whatever commit happened to be checked out the first time a new build
    /// opened the design, silently, and the symptom would be a library fix that never arrives.</para>
    ///
    /// <para><b>It is on the ALIAS, not on each cell</b> (R-rc9-9). This record is already the one
    /// place a cross-workspace path is written down exactly once — its own header says so — and
    /// pinning here pins every <c>ws://alias/…</c> through it, consistently. <b>One referenced
    /// workspace is one repository with one commit identity.</b> Pinning per cell would let one design
    /// reference two mutually inconsistent versions of one library: a state nobody wants and nothing
    /// detects.</para>
    ///
    /// <para><b>A pin is not a copy</b> (R-rc9-16). The content still lives in the other workspace;
    /// this records an identity, not bytes. If that workspace is unreachable, has no history, or has
    /// had its history rewritten, the pin cannot be honoured — and that is REPORTED, never fallen back
    /// from to "whatever is there now", which would defeat the whole feature. Content that must travel
    /// with the design is the workspace archive's job, which is a different tool for a different
    /// problem.</para>
    ///
    /// <para>No <c>FormatVersion</c> bump: an absent field on an older <c>.cws</c> loads as null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Pin { get; set; }
}

/// <summary>
/// One referenced PDK: where it is, and what circuitRF settled about it.
///
/// <para><b>Decisions are recorded; translations are not.</b> An import both translates (symbols,
/// parameter interfaces, icons) and decides (which of a dozen library builds, which variant is the
/// default). The translations are the vendor's content and are rebuilt on open. The decisions are
/// tiny, carry no geometry, and are the difference between a workspace that opens the same way twice
/// and one that quietly re-decides — and re-deciding is also the only part with a cost worth caring
/// about (library discovery byte-scans candidate builds).</para>
/// </summary>
public sealed class CwsPdkRef
{
    /// <summary>
    /// The kit's folder — workspace-relative when inside the workspace, absolute otherwise, via
    /// <see cref="WorkspaceRefs.ToStoredRef"/>. A kit is normally outside, so absolute is the common
    /// case, which is why a broken one has to be repairable rather than merely reported.
    /// </summary>
    public string Path { get; set; } = "";

    /// <summary>
    /// The name a netlist asks for this kit by, and the name its parts' virtual references carry.
    /// Not cosmetic: change it and every placed part stops resolving.
    /// </summary>
    public string Provider { get; set; } = "";

    /// <summary>
    /// Which reader translated this kit's symbols. Pins snap to the P=100 connection grid, so a
    /// reader change moves pins — and wires attached to them silently disconnect. The frozen
    /// on-disk symbol used to prevent that; with the translation rebuilt on every open this is what
    /// replaces it. A mismatch is reported and refused, never applied silently.
    /// </summary>
    public int TranslationVersion { get; set; }

    /// <summary>
    /// What circuitRF worked out about how to simulate this kit — the same object a
    /// <c>device-provider.json</c> holds, kept here instead of written beside the kit. Null for a
    /// purely schematic kit with nothing compiled to serve.
    ///
    /// <para>A raw <see cref="JsonNode"/> for the same reason <see cref="CwsFile.DockLayout"/> is
    /// one: a malformed block must not take the rest of the <c>.cws</c> with it, and a block written
    /// by a newer build round-trips verbatim rather than through a lossy subset.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Settings { get; set; }

    /// <summary>
    /// True when this reference is a MODEL-LIBRARY PACKAGE rather than a part kit — a folder that
    /// supplies no placeable parts but does hold the compiled libraries other kits' devices need.
    ///
    /// <para>It exists because a delivery is several part kits beside one shared library package, and
    /// discovery finds that package by ADJACENCY. Reference a kit from anywhere else — a workspace,
    /// say — and the adjacency is gone with nothing on disk left to recover it from. This is the
    /// workspace saying where the models are.</para>
    ///
    /// <para>Absent in an existing <c>.cws</c> reads as false, which is the part-kit case.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsLibraryOnly { get; set; }

    /// <summary>
    /// The corner choices this kit offers, as learned at import. Null or empty for the overwhelming
    /// majority of kits, which state none.
    ///
    /// <para><b>Recorded rather than re-derived, for the same reason <see cref="Settings"/> is.</b>
    /// Working it out means reading every netlist in the kit; a workspace open must not pay that to
    /// answer a question whose answer only changes when the kit itself does. It is also what lets the
    /// Analyses panel know whether to show a Corners block at all without loading anything.</para>
    ///
    /// <para>No <c>FormatVersion</c> bump — an absent field on an older <c>.cws</c> loads as null.</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CwsCornerAxis>? Corners { get; set; }
}

/// <summary>
/// One axis of corner choice a kit offers, as recorded in <c>.cws</c>.
///
/// <para>The mirror of <see cref="CircuitRF.Core.Pdk.PdkCornerAxis"/> in the file format. It is a
/// separate type on purpose: the recorded shape is a persistence contract that has to keep loading,
/// and pinning the domain record straight into JSON would make every future field on it a format
/// change.</para>
/// </summary>
public sealed class CwsCornerAxis
{
    /// <summary>The declaring file, KIT-RELATIVE. A design records its corner against this, so it must
    /// survive the kit moving — which an absolute path would not.</summary>
    public string AxisId { get; set; } = "";

    /// <summary>The file's own stem — the only name the kit gives an axis.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Section names, verbatim and in declaration order — the kit's own vocabulary.</summary>
    public List<string> Options { get; set; } = [];
}

/// <summary>
/// Reads and writes .cws workspace manifest files.
/// Framework-free (no Avalonia) — same pattern as SchematicPersistence.
/// </summary>
public static class WorkspacePersistence
{
    public const int CurrentFormatVersion = 2;

    /// <summary>
    /// The manifest's file name — a dotfile with no stem, which is why a workspace is a FOLDER and
    /// why the per-user sidecar beside it is named <c>.cwsuser</c> by the same convention.
    /// </summary>
    public const string FileName = ".cws";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented              = true,
        DefaultIgnoreCondition     = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// The <c>.cws</c>'s own bytes — the PROJECT half only. The five per-user fields are stripped
    /// here rather than at the callers, so the file this returns is the file that is versioned
    /// (RC-1, <c>docs/design/revision-control.md</c> §3.1).
    ///
    /// <para>Stripped from the serialized JSON rather than from a copied object: see
    /// <see cref="WorkspaceUserPersistence.MovedFieldNames"/> for why the list names what leaves
    /// rather than what stays.</para>
    /// </summary>
    public static string Serialize(CwsFile ws)
    {
        var node = JsonSerializer.SerializeToNode(ws, JsonOpts);
        if (node is not JsonObject obj) return JsonSerializer.Serialize(ws, JsonOpts);

        foreach (var moved in WorkspaceUserPersistence.MovedFieldNames) obj.Remove(moved);
        return obj.ToJsonString(JsonOpts);
    }

    /// <summary>
    /// Writes both halves, non-atomically. <b>The read-only rule lives in
    /// <see cref="SaveToFileAtomic"/>, not here</b> — this overload never had it, and giving it one
    /// now would change the behaviour of the fixtures and tests that are its only callers.
    /// </summary>
    public static void SaveToFile(string path, CwsFile ws)
    {
        File.WriteAllText(path, Serialize(ws));
        WorkspaceUserPersistence.Save(path, WorkspaceUserPersistence.Extract(ws));
    }

    /// <summary>
    /// Atomic write: serializes to a temp file then renames over the target.
    /// A crash mid-write leaves the old file intact (never a half-written .cws).
    ///
    /// <para><b>SL2 R-sl2-5/-6: this is the choke point, and it is the choke point precisely because
    /// it is the LOWEST level.</b> Reads have had one since the beginning (<c>TryLoadCws</c>); writes
    /// had fifteen call sites and no single place to ask a question. R-sl2-5 says a read-only
    /// workspace writes NOTHING — not the dock layout, not the open-document list, not the active
    /// document, not the settled interpreter, not the kit settings, not the tree view state — and a
    /// rule enforced by fifteen callers agreeing is a rule that is true in fourteen places and found
    /// by a user in the fifteenth. Enforcing it here means a SIXTEENTH call site inherits it without
    /// knowing this rule exists.</para>
    ///
    /// <para>Returns <c>false</c> when the write was skipped because the containing directory is not
    /// writable. Skipped SILENTLY, on purpose: every one of those fifteen sites is recording
    /// convenience state about a session, and none of it is worth a diagnostic, a warning banner or
    /// a modal at the end of a session the user is trying to close. Callers that have something
    /// better to say — a repair the user just made by hand, which really is lost — read the return
    /// value; the rest ignore it, which is the correct amount of code for "the dock layout was not
    /// recorded on a library nobody can write to".</para>
    /// </summary>
    public static bool SaveToFileAtomic(string path, CwsFile ws)
    {
        string? dir;
        try { dir = Path.GetDirectoryName(Path.GetFullPath(path)); }
        catch { return false; }

        if (WorkspaceWritability.IsReadOnly(dir)) return false;

        // ONE write became two, inside the choke point (RC-1 R-rc1-9). Splitting at the callers
        // would mean eighteen of them agreeing, which is what SL2's own header says makes a rule
        // true in seventeen places and found by a user in the eighteenth.
        //
        // Atomicity is PER FILE and there is no cross-file transaction (R-rc1-11): the .cws still
        // either lands whole or leaves the old file intact, and a sidecar write that fails after it
        // loses panel positions and nothing else. That is not a gap being tolerated — it is the
        // reason the split put the unimportant half in the second file. The read-only rule above
        // covers both: a read-only workspace writes NEITHER, and the sidecar is skipped as silently
        // as the .cws is.
        AtomicFile.WriteAllText(path, Serialize(ws));
        WorkspaceUserPersistence.Save(path, WorkspaceUserPersistence.Extract(ws));
        return true;
    }

    public static CwsFile Deserialize(string json)
    {
        var ws = JsonSerializer.Deserialize<CwsFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .cws file.");
        if (ws.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException(
                $".cws format_version {ws.FormatVersion} does not match " +
                $"expected {CurrentFormatVersion}. Regenerate the file.");
        return ws;
    }

    /// <summary>
    /// Loads a workspace's configuration — <b>both halves, merged into the one
    /// <see cref="CwsFile"/> shape every caller already holds</b>. The split is a persistence
    /// change, not a model change, so no caller of this method changed for RC-1.
    ///
    /// <para>The merge is here, at the lowest level, rather than in any one view model's own
    /// loader: there are three <c>TryLoadCws</c> helpers and roughly twenty-five direct calls to
    /// this method, so "the read choke point" is this function and nothing above it.</para>
    ///
    /// <para><b>Migration is a read-side default and there is no rewrite pass</b> (R-rc1-12). An
    /// older <c>.cws</c> carrying the five moved fields has no sidecar beside it, so its own values
    /// are honoured exactly as they are today; they land in the <c>.cwsuser</c> on the next save,
    /// and the stale copies left behind in the <c>.cws</c> are dropped by the same save. That is the
    /// whole of the migration — no <c>FormatVersion</c> bump, no upgrade prompt, no batch
    /// conversion.</para>
    /// </summary>
    public static CwsFile LoadFromFile(string path)
    {
        var ws = Deserialize(File.ReadAllText(path));
        if (WorkspaceUserPersistence.TryLoad(path) is { } user) WorkspaceUserPersistence.Merge(ws, user);
        return ws;
    }
}
