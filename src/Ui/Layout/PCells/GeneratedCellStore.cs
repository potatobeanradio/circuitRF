using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Layout.PCells;

/// <summary>
/// L5, R-L5-1: placing a PCell creates or reuses a generated CELL FOLDER, content-addressed on
/// <c>(GeneratorId, parameter values, technology)</c> — the same triple <see cref="PCellGeometryCache"/>
/// already keys the in-memory geometry cache on (R-pc-4/5). Two placements with identical parameters
/// therefore resolve to the SAME on-disk generated cell automatically; this is also what makes
/// R-L5-2's copy-on-write free: editing an instance's parameters is nothing more than re-resolving
/// <see cref="GetOrCreate"/> with the new values and repointing the instance's own
/// <see cref="LayoutInstance.CellRef"/> — sibling instances, whose <c>CellRef</c> is untouched, are
/// unaffected by construction.
///
/// R-L5-3: generated cells live under a single reserved workspace-root folder
/// (<see cref="ReservedFolderName"/>) rather than beside the user's own cells. R-L5g-9
/// (brief-L5-followups-2.md §4) supersedes R-L5-3's original "one collapsed group node" tree
/// treatment: the folder is now NEVER shown in the Project Tree at all — see
/// <c>WorkspaceScanner.Scan</c>'s own comment at the point the group node used to be built.
/// </summary>
public static class GeneratedCellStore
{
    /// <summary>Dot-prefixed (circuitRF-internal, matching <c>.cws</c>/<c>.ccell</c>/<c>.ctech</c>
    /// convention) workspace-root folder holding every generated cell, across every generator and
    /// every placement in the workspace.
    ///
    /// <para><b>The name itself lives in <see cref="ReservedFolders.GeneratedCells"/></b>, below the
    /// UI firewall, because the headless verbs have to hide the same folder the project tree hides and
    /// a second copy of the string would be free to drift (RND-3 R-rnd3-4). This stays as the spelling
    /// every PCell call site already uses.</para></summary>
    public const string ReservedFolderName = ReservedFolders.GeneratedCells;

    /// <summary>
    /// Returns the absolute cell folder for a PCell generated at <paramref name="parameters"/> against
    /// <paramref name="technology"/>/<paramref name="layerSelection"/>, creating it (and generating its
    /// geometry) the first time this exact combination is requested in this workspace. Reuse is a plain
    /// existence check — the folder name IS the content hash, so a hit means the file already carries
    /// the right geometry; nothing is re-generated or re-verified on a hit.
    /// </summary>
    /// <param name="workspaceRootDir">Absolute workspace root (the folder containing <c>.cws</c>).</param>
    /// <param name="generatorId">A <see cref="PCellRegistry"/> key, e.g. "MLIN".</param>
    /// <param name="parameters">Resolved SI-unit parameter values (pcell-contract.md R2/R7).</param>
    /// <param name="technology">The resolved technology the geometry was generated against, or null.</param>
    /// <param name="techIdentity">A stable string identifying <paramref name="technology"/> for content
    /// addressing — the resolved <c>.ctech</c> path is the natural choice; null/empty means "no
    /// technology". Not used for anything but hashing (the technology CONTENT is what
    /// <paramref name="technology"/> supplies to the generator).</param>
    public static string GetOrCreate(
        string workspaceRootDir,
        string generatorId,
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        string? techIdentity,
        PCellLayerSelection layerSelection,
        PCellGeometryCache? cache = null)
        => GetOrCreate(workspaceRootDir, generatorId, parameters, technology, techIdentity, layerSelection, out _, cache);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _cellsWritten =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How many generated cell FOLDERS have actually been written under
    /// <paramref name="workspaceRootDir"/> — incremented only on a real creation, never on the reuse
    /// path.
    ///
    /// <para>Test instrumentation, and specifically what makes R-pch-9 ("no generated cell is written
    /// during a parameter-handle drag") a COUNTER assertion rather than a timing one. A drag that
    /// wrote a folder per pointer move would leave hundreds of orphaned cells behind — there is no
    /// garbage collection for them by design — and would make the cost of dragging depend on
    /// filesystem latency. Counting is the only way to state that as a fact rather than a hope.</para>
    ///
    /// <para><b>Counted PER WORKSPACE, not per process, and that is not a detail.</b> A single
    /// process-wide counter reads correctly in isolation and is meaningless under a parallel test
    /// run: any other test creating a cell in its own temp workspace perturbs it, so the assertion
    /// silently becomes "nothing anywhere wrote a cell", which is not what anyone meant. Keyed by
    /// root, the count is about the drag under test and nothing else.</para>
    /// </summary>
    public static int CellsWrittenUnder(string workspaceRootDir)
        => _cellsWritten.TryGetValue(NormalizeRoot(workspaceRootDir), out int n) ? n : 0;

    private static string NormalizeRoot(string dir)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)); }
        catch { return dir; }
    }

    /// <summary>
    /// Same as <see cref="GetOrCreate(string,string,IReadOnlyDictionary{string,PCellValue},Technology?,string?,PCellLayerSelection,PCellGeometryCache?)"/>
    /// but also surfaces <paramref name="diagnostics"/> — the generator's own <see cref="PCellResult.Diagnostics"/>
    /// (e.g. R-klp-10's curvature warning), non-null only on an ACTUAL generation (a cache hit means
    /// the geometry — and whatever it would have warned about — was already reported the first time
    /// this exact cell was created, so nothing re-surfaces on reuse). Every caller that places or
    /// regenerates a PCell instance should call THIS overload, not the plain one, so a generator's own
    /// diagnostics are never silently dropped (brief-L5-followups-2.md §2.2's own finding: a generator
    /// can compute a real warning and have nothing anywhere ever surface it — this is the fix).
    /// </summary>
    public static string GetOrCreate(
        string workspaceRootDir,
        string generatorId,
        IReadOnlyDictionary<string, PCellValue> parameters,
        Technology? technology,
        string? techIdentity,
        PCellLayerSelection layerSelection,
        out IReadOnlyList<string>? diagnostics,
        PCellGeometryCache? cache = null)
    {
        diagnostics = null;
        if (!PCellRegistry.TryGet(generatorId, out var generator))
            throw new ArgumentException($"Unknown PCell generator '{generatorId}'.", nameof(generatorId));

        string cellName = BuildCellName(workspaceRootDir, generatorId, parameters, techIdentity, layerSelection);
        string genRoot  = Path.Combine(workspaceRootDir, ReservedFolderName);
        string cellDir  = Path.Combine(genRoot, cellName);
        string clayPath = Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), cellName + CellFolder.ViewExtension(ViewType.Layout));

        if (File.Exists(clayPath))
            return cellDir;

        var result = (cache ?? new PCellGeometryCache())
            .GetOrGenerate(generatorId, generator, parameters, technology, layerSelection);
        diagnostics = result.Diagnostics;

        // ── R-lvs1-5b: the terminal map, and the refusal that comes with it ──────────────────────
        //
        // GENERATE FIRST, THEN CREATE THE FOLDER. The refusal below must leave nothing behind: a
        // half-written cell folder with no `.clay` in it is the state `File.Exists(clayPath)` above
        // reads as "not created yet", so it would be retried forever and would sit in the workspace
        // meanwhile.
        //
        // pcell-contract.md R3 already requires a generated pin's name to match the symbol's pin, and
        // until now nothing checked it. A generator that disagrees produced a cell that silently could
        // not be compared, and the mismatch surfaced — if at all — as wrong connectivity much later.
        var terminals = TerminalMap.FromGeneratorPins(
            [.. result.Pins.Select(p => p.Name)], SymbolPinNamesOf(generatorId), out string? refusal);

        if (refusal is not null)
            throw new InvalidOperationException(
                $"The PCell '{generatorId}' cannot be created: {refusal}");

        Directory.CreateDirectory(genRoot);
        CellFolder.CreateCellFolder(genRoot, cellName);
        _cellsWritten.AddOrUpdate(NormalizeRoot(workspaceRootDir), 1, (_, n) => n + 1);

        var view = new LayoutView
        {
            DbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
            DisplayUnit  = technology?.DefaultDisplayUnit ?? LayoutUnit.Um,
            SnapDbu      = technology?.DefaultSnapDbu ?? 1000,
            AngleMode    = AngleMode.AnyAngle,
            TechRef      = null,
            PCellOrigin  = new PCellOrigin(generatorId, parameters,
                                           result.ComputedParameters, result.ComputedValues,
                                           result.UnreadParameters),
        };
        view.Shapes.AddRange(result.Shapes);

        long labelHeight = technology?.DefaultLabelHeightDbu is > 0
            ? technology.DefaultLabelHeightDbu
            : 5000; // 5 um fallback at the standard 1 DBU = 1 nm resolution — matches the app-wide
                    // hardcoded-default convention documented for LabelShape elsewhere.
        foreach (var pin in result.Pins)
        {
            // TWO records, deliberately, because they answer different questions. The pin is the
            // CONNECTIVITY: name, position, connecting width and outward direction — everything
            // needed to join to it, persisted so an instance of this cell is connectable without
            // re-running the generator. The label is the visible TEXT beside it. Writing only the
            // label is what previously lost width and direction at the disk boundary.
            view.Pins.Add(new LayoutPin
            {
                Name       = pin.Name,
                X          = pin.X,
                Y          = pin.Y,
                WidthDbu   = pin.WidthDbu,
                OutwardDeg = pin.OutwardDirectionDeg,
                Layer      = pin.Layer,
            });

            view.Shapes.Add(new LabelShape
            {
                X      = pin.X,
                Y      = pin.Y,
                Text   = pin.Name,
                Height = labelHeight,
                Layer  = pin.Layer,
                IsPort = true,
            });
        }

        LayoutPersistence.SaveToFile(clayPath, view);

        // R-lvs1-5b: written as DECLARED, from the generator's own pins. A generated cell has no
        // symbol view of its own — the symbol is the one its generator is registered with — so
        // deriving this later would have nothing to derive from.
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.Terminals = TerminalMap.ToBlock(terminals!);
        CellPersistence.SaveToFile(ccellPath, ccell);

        return cellDir;
    }

    /// <summary>
    /// The pin names of the symbol <paramref name="generatorId"/> is registered with, in PORT order —
    /// what R-lvs1-5b pairs the generator's own pins against.
    ///
    /// <para>Empty for a generator that has no registered symbol, which is not a defect: a built-in
    /// land pattern is artwork for a part whose symbol is chosen at placement, and a kit's generator
    /// brings its own. There is then nothing to disagree with, and the generator's own pin order is
    /// the port order.</para>
    /// </summary>
    private static IReadOnlyList<string> SymbolPinNamesOf(string generatorId)
        => LayoutToSchematicGenerator.TryGetSymbolKind(generatorId, out var kind)
            ? [.. SymbolPortDefs.For(kind).Select(p => p.Name)]
            : [];

    /// <summary>
    /// brief-L5-followups-2.md §4.2/R-L5g-6: records (or refreshes) <paramref name="view"/>'s own
    /// regeneration record for the generated cell at <paramref name="cellDir"/> — call this immediately
    /// after every <see cref="GetOrCreate"/> invoked from a layout context, so that layout carries
    /// everything needed to rebuild the cell later if the generated-cells folder is ever deleted
    /// (workspace close/open, R-L5g-7) or the file simply goes missing. Keyed by the cell's own folder
    /// name — the same content hash <see cref="BuildCellName"/> already computes — so re-recording an
    /// already-known cell (the common "two instances share one cell" case, R-L5-1) is a harmless
    /// overwrite with identical content, not a growing table.
    /// </summary>
    /// <param name="workspaceRootDir">Where the snapshot's technology is recorded RELATIVE to — see
    /// <see cref="CanonicalTechIdentity"/>. Null records <paramref name="techIdentity"/> as given,
    /// which is only right for an identity that is not a path.</param>
    public static void RecordSnapshot(
        LayoutView view, string cellDir, string generatorId,
        IReadOnlyDictionary<string, PCellValue> parameters, string? techIdentity, PCellLayerSelection layerSelection,
        string? workspaceRootDir = null)
    {
        string cellName = Path.GetFileName(cellDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (workspaceRootDir is not null) techIdentity = CanonicalTechIdentity(workspaceRootDir, techIdentity);
        view.PCellSnapshots[cellName] = new PCellSnapshot(
            generatorId, new Dictionary<string, PCellValue>(parameters), techIdentity,
            layerSelection.SignalLayerNameOverride, layerSelection.GroundLayerNameOverride);
    }

    // ── The technology identity is WORKSPACE-RELATIVE (field report, 2026-09-23) ─────────────
    //
    // It used to be the absolute .ctech path of whichever machine placed the cell — and it is both
    // hashed into the cell's folder name and recorded in the layout's snapshot, which is the ONLY
    // thing the cell is rebuilt from, because `.generated-cells/` is a cache nobody commits. So on
    // any other machine, or after the workspace folder moved, the rebuild found no technology: a
    // reported Gerber board opened with all ~50 of its footprint instances unresolved and its pads
    // gone. The shipped PDK PCells example carried the author's own absolute path the same way.
    //
    // A technology inside the workspace is now named by its path RELATIVE to the workspace root,
    // '/'-separated, in both places. An identity recorded the old way is re-rooted where the file
    // is no longer there: the LONGEST trailing run of its segments that names a file under this
    // workspace, tried even where the recorded file still exists (a copied workspace uses its own).
    // A technology outside the workspace with no copy inside it is left absolute — there is no
    // portable spelling for it, and it is found where it was. Changing the spelling changes the
    // hash, which is a one-time rename on first open that GeneratedCellsLifecycle.Regenerate
    // already performs (it repoints the instances and saves the layout), exactly as it does after
    // a generator edit.

    /// <summary>
    /// The spelling a cell's name and snapshot use for <paramref name="techIdentity"/> — relative to
    /// <paramref name="workspaceRootDir"/> where the technology is inside it.
    /// </summary>
    public static string? CanonicalTechIdentity(string workspaceRootDir, string? techIdentity)
    {
        if (string.IsNullOrEmpty(techIdentity)) return techIdentity;
        if (!IsRootedAnywhere(techIdentity)) return techIdentity.Replace('\\', '/');

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRootDir)) + Path.DirectorySeparatorChar;
        string full = techIdentity;
        try { if (Path.IsPathRooted(techIdentity)) full = Path.GetFullPath(techIdentity); } catch { }

        if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return full[root.Length..].Replace('\\', '/');

        // Recorded on another machine, or before the workspace was moved or COPIED. Tried before the
        // recorded path itself, because a copy's own technology is the one it was copied with — the
        // shipped example, copied out of the repository, still found the repository's file and kept
        // naming it.
        string[] segments = techIdentity.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        for (int first = 1; first < segments.Length; first++)
        {
            string tail = string.Join('/', segments[first..]);
            if (File.Exists(Path.Combine(root, tail))) return tail;
        }
        return techIdentity;
    }

    /// <summary>The file <paramref name="techIdentity"/> names — the canonical spelling resolved
    /// against <paramref name="workspaceRootDir"/>, or null for no technology.</summary>
    public static string? ResolveTechPath(string workspaceRootDir, string? techIdentity)
    {
        string? canonical = CanonicalTechIdentity(workspaceRootDir, techIdentity);
        if (string.IsNullOrEmpty(canonical)) return null;
        return IsRootedAnywhere(canonical) ? canonical : Path.GetFullPath(Path.Combine(workspaceRootDir, canonical));
    }

    /// <summary>Rooted on THIS platform or on Windows — a snapshot written on Windows is read on
    /// macOS and Linux, where <c>C:\…</c> is otherwise a relative path.</summary>
    private static bool IsRootedAnywhere(string path) =>
        Path.IsPathRooted(path)
        || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>
    /// R-L5g-9/10 (brief-L5-followups-2.md §4): true when <paramref name="absolutePath"/> sits under a
    /// reserved <see cref="ReservedFolderName"/> folder — "infrastructure, not content." Checked by
    /// path SEGMENT, not by prefix against any one specific workspace root, so it holds regardless of
    /// which workspace (or a foreign/loose path) the file happens to live under. The one gate that
    /// closes the "second entry point" for double-clicking a PCell and ending up inside its
    /// hierarchy (brief-L5-followups-2.md §3/R-L5g-5): opening a generated cell's own <c>.clay</c>
    /// directly — via a file picker, a stale <c>.cws</c> <c>OpenDocuments</c> entry, or (formerly) the
    /// Project Tree's now-removed Generated Cells group — was never push-in at all, so it never went
    /// through <c>LayoutHierarchyResolver.CanPushInto</c>'s <see cref="PCellOrigin"/> check; this is
    /// the independent gate that catches it.
    /// </summary>
    public static bool IsUnderGeneratedCellsFolder(string absolutePath)
        => ReservedFolders.IsUnderGeneratedCells(absolutePath);

    /// <summary>True when a generated cell for this exact key already exists on disk — lets a caller
    /// check reuse-vs-create without invoking the generator (used by tests asserting R-L5-1's "one
    /// cell per unique parameter set").</summary>
    public static bool Exists(
        string workspaceRootDir, string generatorId,
        IReadOnlyDictionary<string, PCellValue> parameters,
        string? techIdentity, PCellLayerSelection layerSelection)
    {
        string cellName = BuildCellName(workspaceRootDir, generatorId, parameters, techIdentity, layerSelection);
        string clayPath = Path.Combine(workspaceRootDir, ReservedFolderName, cellName,
            CellFolder.LayoutSubFolder, cellName + CellFolder.ViewExtension(ViewType.Layout));
        return File.Exists(clayPath);
    }

    // ── Content addressing ───────────────────────────────────────────────────

    /// <summary>
    /// A stamp of what the technology at <paramref name="techIdentity"/> currently SAYS, or empty
    /// when there is no readable file there.
    ///
    /// <para>Memoized on the file's own write time and length, so the ordinary hit path — including
    /// a parameter-handle drag, which calls <see cref="GetOrCreate"/> per probe — costs a stat and a
    /// dictionary lookup rather than a read. The digest itself is recomputed the moment either
    /// changes, which is what makes an edit in the technology editor actually invalidate.</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Ticks, long Length, string Key)>
        _techContentKeys = new(StringComparer.OrdinalIgnoreCase);

    private static string TechnologyContentKey(string? techIdentity)
    {
        if (string.IsNullOrEmpty(techIdentity)) return "";

        try
        {
            var info = new FileInfo(techIdentity);
            if (!info.Exists) return "";

            long ticks = info.LastWriteTimeUtc.Ticks;
            long length = info.Length;
            if (_techContentKeys.TryGetValue(techIdentity, out var cached)
                && cached.Ticks == ticks && cached.Length == length)
                return cached.Key;

            string key = StampOf(File.ReadAllText(techIdentity));
            _techContentKeys[techIdentity] = (ticks, length, key);
            return key;
        }
        catch
        {
            // An unreadable technology is not a reason to refuse to name a cell. Falling back to the
            // pre-content-key name is the same answer this method gives for "no technology at all".
            return "";
        }
    }

    /// <summary>
    /// Everything a technology says that could reach GEOMETRY, and nothing it says about how that
    /// geometry is DRAWN.
    ///
    /// <para><b>Why the distinction is load-bearing rather than tidy.</b> This stamp is part of a
    /// generated cell's name, so anything it includes is something that renames every cell in the
    /// workspace when it changes — which regenerates them all and rewrites every layout that places
    /// them. Layer visibility is toggled constantly, while looking at a design; a stamp over the raw
    /// file would turn hiding a layer into a full rebuild. It cannot possibly change the artwork:
    /// generated shapes carry layer KEYS, and colour, stipple, opacity, draw order, visibility and
    /// selectability are all resolved live by the renderer from the technology as it stands.</para>
    ///
    /// <para><b>Written as an exclusion list on purpose.</b> A field added to a technology later is
    /// included by default, so the failure mode of forgetting to update this is an unnecessary
    /// regeneration — not artwork drawn against a process that has since changed. Add to this list
    /// only what the renderer alone consumes.</para>
    ///
    /// <para>Re-serialised rather than hashed as text, so reformatting the file — which
    /// <c>TechPersistence</c> may do on any save — is not mistaken for a change to it.</para>
    /// </summary>
    private static string StampOf(string ctechJson)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(ctechJson);
        if (node is System.Text.Json.Nodes.JsonObject root)
        {
            // The stipple table itself: named by layers through FillPattern, and consumed only when
            // filling one.
            root.Remove("FillPatterns");

            if (root["Layers"] is System.Text.Json.Nodes.JsonArray layers)
                foreach (var layer in layers)
                    if (layer is System.Text.Json.Nodes.JsonObject o)
                        foreach (var presentational in _renderOnlyLayerFields)
                            o.Remove(presentational);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(node?.ToJsonString() ?? ctechJson)))[..12]
                      .ToLowerInvariant();
    }

    /// <summary>What a <c>LayerDef</c> says about drawing, as opposed to about the process.</summary>
    private static readonly string[] _renderOnlyLayerFields =
        ["Visible", "Selectable", "Color", "FillOpacity", "FillPattern", "ZOrder"];

    private static string BuildCellName(
        string workspaceRootDir, string generatorId, IReadOnlyDictionary<string, PCellValue> parameters,
        string? techIdentity, PCellLayerSelection layerSelection)
    {
        // The WORKSPACE-RELATIVE spelling, so a cell has the same name on every machine the
        // workspace is opened on — see CanonicalTechIdentity.
        techIdentity = CanonicalTechIdentity(workspaceRootDir, techIdentity);

        // The folder name IS this hash, and a placed instance's CellRef names that folder — so this
        // encoding is a compatibility surface, not an implementation detail. PCellValue.ToString
        // writes a Real exactly as the pre-contract-v2 code wrote a double, which is what keeps every
        // already-placed instance in an existing workspace resolving after the widening; see that
        // method's own doc comment.
        var sb = new StringBuilder();
        sb.Append(generatorId).Append('|');
        foreach (var kv in parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.Append(kv.Key).Append('=').Append(kv.Value.ToString()).Append(';');
        sb.Append('|').Append(techIdentity ?? "");
        sb.Append('|').Append(layerSelection.SignalLayerNameOverride ?? "")
          .Append(',').Append(layerSelection.GroundLayerNameOverride ?? "");
        // R-L5f-5-follow-up: the generator's own content version (PCellRegistry.GeneratorVersion) is
        // part of the hash so a fixed generator never resolves to a stale, pre-fix on-disk cell — see
        // PCellRegistry's own doc comment on _generatorVersions for the full story.
        // Byte-identical to the previous Append(int) for every built-in — see
        // PCellRegistry.GeneratorContentKey. For a script-backed generator this is a hash of the
        // script itself, which is what makes editing one actually invalidate the cells it produced.
        sb.Append('|').Append(PCellRegistry.GeneratorContentKey(generatorId));
        // And the technology's own CONTENT, for the same reason and on the same terms. techIdentity
        // above is the .ctech PATH, which does not change when the file behind it is edited — and
        // circuitRF ships the editor that edits it. That gap was invisible while the whole folder was
        // wiped on every open; once generated cells survive a session
        // (GeneratedCellsLifecycle.WipeOnOpenAndClose), an in-place edit to a technology would
        // otherwise resolve straight back to artwork drawn against the old layers.
        //
        // Appended only when there is something to append, so every existing cell whose identity is
        // not a readable file — which is every one in a test fixture, and any workspace with no
        // technology — keeps the name it already has.
        string techKey = TechnologyContentKey(ResolveTechPath(workspaceRootDir, techIdentity));
        if (techKey.Length > 0) sb.Append('|').Append(techKey);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        string hex  = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return $"{FolderSafe(generatorId)}_{hex}";
    }

    /// <summary>
    /// <paramref name="generatorId"/> with every character a cell folder may not carry replaced.
    ///
    /// <para><b>Why this is not a no-op, and why it does not break the compatibility surface above.</b>
    /// A built-in's id is letters (<c>MLIN</c>, <c>MKLOPF</c>) and passes through UNCHANGED, so every
    /// instance already placed in every existing workspace keeps resolving to the folder it names —
    /// which is the property <see cref="BuildCellName"/>'s own comment is protecting. But a footprint
    /// id is <c>smt:0402@N</c>, and <c>:</c> is in <c>NameValidator</c>'s disallowed set (it is not a
    /// path character on Windows and is a foot-gun on the others). Without this,
    /// <c>CellFolder.CreateCellFolder</c> refuses and the whole placement fails with a sentence about
    /// a folder name, which is not what the user did.</para>
    ///
    /// <para>The IDENTITY is the hash, which is computed from the raw id above — so two ids that
    /// sanitize to the same prefix still produce two different folders, and nothing collides.</para>
    /// </summary>
    private static string FolderSafe(string generatorId)
    {
        if (NameValidator.IsValid(generatorId)) return generatorId;

        var sb = new StringBuilder(generatorId.Length);
        foreach (char c in generatorId)
            sb.Append(c <= 0x1F || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '-' : c);
        string safe = sb.ToString().TrimEnd(' ', '.');
        return safe.Length == 0 ? "pcell" : safe;
    }
}
