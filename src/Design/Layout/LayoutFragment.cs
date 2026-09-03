// Framework-free clipboard fragment logic (docs/design/layout-view.md §6.4;
// docs/sonnet-briefs/brief-L1f-clipboard.md). This file decides what a paste MEANS — building a
// fragment from a selection, rescaling it across DBU resolutions, reconciling layers against a
// destination technology, and translating shapes for placement. LayoutClipboard.cs (src/Ui/Clipboard/)
// is the Avalonia/system-clipboard I/O layer built on top of this; it contains no rescale or
// reconciliation logic of its own. This split is deliberate (do not collapse it) — it is what lets
// the hard parts of this phase (rescale, reconciliation) get real, headless tests.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Layout;

public static class LayoutFragment
{
    public const string Marker = "circuitrf/layout-clipboard-v1";

    /// <summary>
    /// The clipboard payload — self-describing (R-L1f-1): it carries its source
    /// <see cref="DbuPerMicron"/> and the <see cref="LayerDef"/>s it actually references, so it can
    /// be pasted into a workspace with a different technology, a different resolution, and a
    /// different running process with no dependency on ambient state. Shapes serialize through the
    /// same polymorphic <c>LayoutShape</c> setup as <c>.clay</c>, so holes, edge lists, nets, and
    /// flatten tolerances round-trip with no extra work. <see cref="LayoutInstance"/>s are NOT
    /// carried — nothing can create one until L3 (hierarchy).
    /// </summary>
    public sealed class Payload
    {
        public string? Marker { get; set; }
        public int DbuPerMicron { get; set; }

        /// <summary>The SOURCE document's display unit. Carried for R-rul-6: a ruler's readout is
        /// rendered through <c>LayoutUnits.Format</c> in a document's own display unit, and the
        /// clipboard's graphic export (the PowerPoint path, §9B.9) has no document — without this it
        /// would have to hard-code one, which is the "one WHAT" defect that rule exists to avoid.
        /// Defaults to <see cref="LayoutUnit.Um"/> for a payload written before this field existed.</summary>
        public LayoutUnit DisplayUnit { get; set; } = LayoutUnit.Um;
        public long AnchorX { get; set; }
        public long AnchorY { get; set; }

        /// <summary>Name of the technology the selection was copied FROM, or null with no
        /// technology resolved — one string for legibility (docs/sonnet-briefs/brief-L1g-technology-retarget.md
        /// §3), so the cross-technology mapping dialog can say where the geometry came from. Not new
        /// data: <see cref="Layers"/> already carries that technology's own <see cref="LayerDef"/>s.</summary>
        public string? TechName { get; set; }

        public List<LayerDef> Layers { get; set; } = [];
        public List<LayoutShape> Shapes { get; set; } = [];

        /// <summary>L3a (docs/sonnet-briefs/brief-L3a-instances-and-arrays.md, gate 11) — instances ARE
        /// now carried. <see cref="InstanceCellDirs"/> is parallel to <see cref="Instances"/>: the
        /// SOURCE document's resolved absolute cell directory for that instance's <c>CellRef</c> at
        /// copy time (or null when it could not be resolved there — a broken reference, or a scratch
        /// source document with no stable base directory). This is what lets <see cref="RebaseInstances"/>
        /// compute a NEW relative <c>CellRef</c> correct for the DESTINATION document, rather than
        /// reusing a relative string that only meant something in the source's own directory.
        ///
        /// brief-layout-testing-fixes.md item 2/R-fix-2: a relative-to-destination rebase is only
        /// possible when the DESTINATION document has a stable base directory (a saved <c>.clay</c>) —
        /// pasting into a brand-new, never-saved document (<c>InstanceBaseDir == ""</c>) previously fell
        /// back to keeping the SOURCE's own relative <c>CellRef</c> string unchanged, which resolves
        /// against nothing meaningful and reports broken even though the referenced cell is right there
        /// on disk. The fragment now ALSO carries each instance's cell identity in two base-INDEPENDENT
        /// forms so a paste can still resolve without a destination base directory: <see
        /// cref="InstanceWorkspaceRelativeDirs"/> (portable across a shared workspace) and
        /// <see cref="InstanceCellDirs"/> itself, used directly as an ABSOLUTE <c>CellRef</c> — legal
        /// because <c>Path.Combine(baseDir, cellRef)</c> already ignores <paramref name="baseDir"/>
        /// entirely when <c>cellRef</c> is rooted, so an absolute <c>CellRef</c> resolves correctly
        /// regardless of the destination's base directory, including an empty one.</summary>
        public List<LayoutInstance> Instances { get; set; } = [];
        public List<string?> InstanceCellDirs { get; set; } = [];

        /// <summary>Parallel to <see cref="Instances"/> — the source cell directory's path relative to
        /// the SOURCE document's OWN workspace root at copy time, or null when no workspace root was
        /// resolvable there (a loose/no-workspace source) or the instance's cell dir itself is null.
        /// Preferred over the plain absolute fallback when the DESTINATION also resolves to a workspace
        /// root, since it stays correct even if the two documents' workspace happens to live at a
        /// different absolute location (e.g. a shared workspace checked out at two different paths).</summary>
        public List<string?> InstanceWorkspaceRelativeDirs { get; set; } = [];

        /// <summary>docs/design/layout-view.md §9B.9 — the copied RULER annotations. <b>No layer
        /// reconciliation applies</b> (a ruler has no layer, R-rul-1), but the endpoints ARE
        /// coordinates and are rescaled with everything else by <see cref="Rescale"/>: a ruler pasted
        /// into a document at a different <c>DbuPerMicron</c> must still measure the same PHYSICAL
        /// distance.</summary>
        public List<RulerAnnotation> Rulers { get; set; } = [];
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = false,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // ── Build ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a self-describing fragment from a selection. The anchor is the selection's own bbox
    /// min, in source DBU — this is what a live paste-ghost anchors to the snapped cursor.
    /// <paramref name="tech"/>'s <see cref="LayerDef"/>s are captured only for layers the selection
    /// actually uses (never the whole technology) so the fragment stays small and self-contained.
    /// Shapes are deep-cloned — later mutation of the source selection never affects the fragment.
    /// </summary>
    public static Payload Build(IReadOnlyList<LayoutShape> shapes, Technology? tech, int dbuPerMicron) =>
        Build(shapes, [], [], [], tech, dbuPerMicron);

    /// <summary>L3a overload — also carries instances. <paramref name="instanceCellDirs"/> is parallel
    /// to <paramref name="instances"/> (see <see cref="Payload.InstanceCellDirs"/>'s doc comment).
    /// <paramref name="instanceWorkspaceRelativeDirs"/> defaults to empty (older callers, or a caller
    /// with no workspace root to compute it against) — <see cref="RebaseInstances"/> tolerates that,
    /// falling straight through to the absolute-path fallback.</summary>
    public static Payload Build(
        IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutInstance> instances, IReadOnlyList<string?> instanceCellDirs,
        Technology? tech, int dbuPerMicron) =>
        Build(shapes, instances, instanceCellDirs, [], tech, dbuPerMicron);

    /// <summary>brief-layout-testing-fixes.md item 2/R-fix-2 overload — also carries each instance's
    /// workspace-relative cell dir (see <see cref="Payload.InstanceWorkspaceRelativeDirs"/>).</summary>
    public static Payload Build(
        IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutInstance> instances, IReadOnlyList<string?> instanceCellDirs,
        IReadOnlyList<string?> instanceWorkspaceRelativeDirs, Technology? tech, int dbuPerMicron)
        => Build(shapes, instances, instanceCellDirs, instanceWorkspaceRelativeDirs, [], tech, dbuPerMicron);

    /// <summary>docs/design/layout-view.md §9B.9 overload — also carries the selection's RULERS.</summary>
    public static Payload Build(
        IReadOnlyList<LayoutShape> shapes, IReadOnlyList<LayoutInstance> instances, IReadOnlyList<string?> instanceCellDirs,
        IReadOnlyList<string?> instanceWorkspaceRelativeDirs, IReadOnlyList<RulerAnnotation> rulers,
        Technology? tech, int dbuPerMicron, LayoutUnit displayUnit = LayoutUnit.Um)
    {
        var bbox = Bbox.Empty;
        foreach (var s in shapes) bbox = bbox.Union(LayoutGeometry.BboxOf(s));
        foreach (var i in instances) bbox = bbox.Union(new Bbox(i.X, i.Y, i.X, i.Y));
        foreach (var r in rulers)
            bbox = bbox.Union(new Bbox(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2),
                                       Math.Max(r.X1, r.X2), Math.Max(r.Y1, r.Y2)));

        var seen = new HashSet<LayerKey>();
        var layers = new List<LayerDef>();
        foreach (var s in shapes)
        {
            if (!seen.Add(s.Layer)) continue;
            var def = tech?.Layers.FirstOrDefault(l => l.Key == s.Layer);
            if (def is not null) layers.Add(def);
        }

        return new Payload
        {
            Marker           = Marker,
            DbuPerMicron     = dbuPerMicron,
            DisplayUnit      = displayUnit,
            AnchorX          = bbox.IsEmpty ? 0 : bbox.MinX,
            AnchorY          = bbox.IsEmpty ? 0 : bbox.MinY,
            TechName         = tech?.Name,
            Layers           = layers,
            Shapes           = shapes.Select(LayoutGeometry.Clone).ToList(),
            Instances        = instances.Select(LayoutGeometry.Clone).ToList(),
            InstanceCellDirs = instanceCellDirs.ToList(),
            InstanceWorkspaceRelativeDirs = instanceWorkspaceRelativeDirs.ToList(),
            Rulers           = rulers.Select(r => r.Clone()).ToList(),
        };
    }

    // ── Serialize / Deserialize (marker-guarded) ────────────────────────────

    public static string Serialize(Payload payload) => JsonSerializer.Serialize(payload, JsonOpts);

    /// <summary>
    /// Marker-guarded parse. Any text without the marker — arbitrary text, a truncated/malformed
    /// JSON blob, or a symbol-clipboard payload (a different, incompatible JSON shape) — is a clean
    /// no-op: returns false, never throws. This is what makes cross-editor paste (symbol into
    /// layout, or vice versa) a silent no-op rather than a confusing partial paste.
    /// </summary>
    public static bool TryDeserialize(string? text, out Payload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        try
        {
            var candidate = JsonSerializer.Deserialize<Payload>(text, JsonOpts);
            if (candidate is null || candidate.Marker != Marker) return false;
            payload = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Rescale (R-L1f-2) ────────────────────────────────────────────────────

    public sealed record RescaleResult(
        IReadOnlyList<LayoutShape> Shapes, long AnchorX, long AnchorY, IReadOnlyList<string> Warnings,
        IReadOnlyList<RulerAnnotation>? Rulers = null);

    /// <summary>
    /// Rescales a fragment's shapes (and anchor) from its source <see cref="Payload.DbuPerMicron"/>
    /// to <paramref name="destDbuPerMicron"/>. Same resolution -&gt; shapes are cloned unchanged.
    /// Different resolution -&gt; every coordinate is scaled by the exact ratio; where that ratio is
    /// non-integer, or a specific coordinate does not divide evenly, the rescaled value is rounded
    /// and the affected shape is named in <see cref="RescaleResult.Warnings"/> — paste always
    /// proceeds regardless. This is the deliberate opposite of
    /// <see cref="LayoutScaling.TryChangeResolution"/>, which refuses on any lossy coordinate:
    /// <c>TryChangeResolution</c> mutates an existing design in place, so a silent snap would be a
    /// real loss; this operation only ever ADDS new geometry the user can undo in one keystroke, so
    /// warning and proceeding is the right default.
    /// </summary>
    public static RescaleResult Rescale(Payload payload, int destDbuPerMicron)
    {
        var shapes = payload.Shapes.Select(LayoutGeometry.Clone).ToList();
        var rulers = payload.Rulers.Select(r => r.Clone()).ToList();


        if (destDbuPerMicron == payload.DbuPerMicron || payload.DbuPerMicron <= 0)
            return new RescaleResult(shapes, payload.AnchorX, payload.AnchorY, [], rulers);

        var warnings = new List<string>();

        for (int i = 0; i < shapes.Count; i++)
        {
            bool lossy = false;

            long ScaleTrack(long v)
            {
                decimal scaled = (decimal)v * destDbuPerMicron / payload.DbuPerMicron;
                long rounded = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
                if (scaled != rounded) lossy = true;
                return rounded;
            }

            LayoutCoordinateWalk.Transform(shapes[i], LayoutCoordinateTransform.Uniform(ScaleTrack));
            if (lossy)
                warnings.Add(
                    $"{ShapeLabel(shapes[i], i)}: rescaled from {payload.DbuPerMicron} to " +
                    $"{destDbuPerMicron} DBU/µm with rounding.");
        }

        bool anchorLossy = false;
        long ScaleAnchor(long v)
        {
            decimal scaled = (decimal)v * destDbuPerMicron / payload.DbuPerMicron;
            long rounded = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
            if (scaled != rounded) anchorLossy = true;
            return rounded;
        }
        long ax = ScaleAnchor(payload.AnchorX);
        long ay = ScaleAnchor(payload.AnchorY);
        _ = anchorLossy; // the anchor is internal placement math, not user-visible geometry — no separate warning

        // §9B.9 / R-L1f-2: a ruler's endpoints are coordinates like any other, and rescaling them is
        // exactly what makes a pasted ruler still measure the SAME PHYSICAL DISTANCE in a document at
        // a different resolution. Rounding is reported no more loudly than a shape's is (the paste is
        // one Ctrl+Z away), and the reported number follows the endpoints by construction, since it is
        // computed rather than stored.
        long ScalePlain(long v)
        {
            decimal scaled = (decimal)v * destDbuPerMicron / payload.DbuPerMicron;
            return (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
        }
        foreach (var r in rulers)
        {
            r.X1 = ScalePlain(r.X1); r.Y1 = ScalePlain(r.Y1);
            r.X2 = ScalePlain(r.X2); r.Y2 = ScalePlain(r.Y2);
            // The world text height is a LENGTH in the source document's units and must travel the
            // same way; the point size is a screen quantity and is resolution-independent, so it is
            // deliberately left alone.
            if (r.TextHeightDbu > 0) r.TextHeightDbu = Math.Max(1, ScalePlain(r.TextHeightDbu));
        }

        return new RescaleResult(shapes, ax, ay, warnings, rulers);
    }

    private static string ShapeLabel(LayoutShape shape, int index) => $"{shape.GetType().Name} #{index}";

    // ── Layer reconciliation (R-L1f-3) ──────────────────────────────────────

    public enum LayerReconciliationAction { KeepUnknown, MapToExisting, AddToTechnology }

    public readonly record struct LayerReconciliationChoice(LayerReconciliationAction Action, LayerKey? MapTarget = null);

    public sealed record ReconciliationResult(IReadOnlyList<LayoutShape> Shapes, IReadOnlyList<LayerDef> LayersToAdd);

    /// <summary>
    /// Applies reconciliation choices (R-L1f-3). A layer the destination technology already defines
    /// needs no choice at all — the shape keeps its <see cref="LayerKey"/> and the renderer resolves
    /// it against the DESTINATION's own <see cref="LayerDef"/> (its color/name win, since it is the
    /// destination's technology). For an absent layer: Keep-as-unknown (the default, or simply no
    /// choice supplied) is a no-op — the shape keeps its key and renders via <c>FallbackPalette</c>;
    /// Map rewrites the shape's <see cref="LayerKey"/> to the chosen destination layer; Add leaves
    /// the shape's key alone and returns the fragment's own <see cref="LayerDef"/> for the caller to
    /// install into the destination technology through the live-technology mechanism — this method
    /// never mutates or persists a <see cref="Technology"/> itself. Nothing is ever dropped: every
    /// input shape produces exactly one output shape.
    /// </summary>
    public static ReconciliationResult ApplyReconciliation(
        IReadOnlyList<LayoutShape> shapes,
        IReadOnlyList<LayerDef> fragmentLayers,
        IReadOnlyDictionary<LayerKey, LayerReconciliationChoice>? choices)
    {
        var result = new List<LayoutShape>(shapes.Count);
        var layersToAdd = new List<LayerDef>();
        var addedKeys = new HashSet<LayerKey>();

        foreach (var shape in shapes)
        {
            var clone = LayoutGeometry.Clone(shape);
            if (choices is not null && choices.TryGetValue(shape.Layer, out var choice))
            {
                switch (choice.Action)
                {
                    case LayerReconciliationAction.MapToExisting when choice.MapTarget is { } target:
                        clone.Layer = target;
                        break;

                    case LayerReconciliationAction.AddToTechnology:
                        if (addedKeys.Add(shape.Layer))
                        {
                            var def = fragmentLayers.FirstOrDefault(l => l.Key == shape.Layer);
                            if (def is not null) layersToAdd.Add(def);
                        }
                        break;

                    // KeepUnknown (and MapToExisting with no target): no-op — the clone already
                    // carries the shape's original LayerKey.
                }
            }
            result.Add(clone);
        }

        return new ReconciliationResult(result, layersToAdd);
    }

    // ── Placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// Translates every shape by <paramref name="dx"/>/<paramref name="dy"/>, deep-cloning first so
    /// the caller's input shapes are never mutated — used both for the live paste-ghost preview
    /// (called on every pointer move) and the final placement commit.
    /// </summary>
    public static IReadOnlyList<LayoutShape> Translate(IReadOnlyList<LayoutShape> shapes, long dx, long dy)
    {
        var result = new List<LayoutShape>(shapes.Count);
        foreach (var s in shapes)
        {
            var clone = LayoutGeometry.Clone(s);
            if (dx != 0 || dy != 0) LayoutGeometry.TranslateBy(clone, dx, dy);
            result.Add(clone);
        }
        return result;
    }

    /// <summary>Ruler analogue of <see cref="Translate(IReadOnlyList{LayoutShape}, long, long)"/> —
    /// the SAME translation shapes and instances get, so a fragment's three kinds can never drift
    /// apart mid-placement (§9B.9).</summary>
    public static IReadOnlyList<RulerAnnotation> Translate(IReadOnlyList<RulerAnnotation> rulers, long dx, long dy)
    {
        var result = new List<RulerAnnotation>(rulers.Count);
        foreach (var r in rulers)
        {
            var clone = r.Clone();
            clone.TranslateBy(dx, dy);   // endpoints AND a hand-placed readout, in one place
            result.Add(clone);
        }
        return result;
    }

    /// <summary>Instance analogue of <see cref="Translate(IReadOnlyList{LayoutShape}, long, long)"/>.</summary>
    public static IReadOnlyList<LayoutInstance> Translate(IReadOnlyList<LayoutInstance> instances, long dx, long dy)
    {
        var result = new List<LayoutInstance>(instances.Count);
        foreach (var i in instances)
        {
            var clone = LayoutGeometry.Clone(i);
            if (dx != 0 || dy != 0) LayoutGeometry.TranslateBy(clone, dx, dy);
            result.Add(clone);
        }
        return result;
    }

    // ── Instance CellRef rebasing (gate 11 — cross-layout paste) ────────────────────────────────

    /// <summary>
    /// Recomputes each instance's <see cref="LayoutInstance.CellRef"/> as a path relative to
    /// <paramref name="destBaseDir"/> (the destination document's own <see
    /// cref="LayoutEditorViewModel.InstanceBaseDir"/>), using the SOURCE-resolved absolute cell
    /// directory captured in <paramref name="cellDirs"/> at copy time. An instance whose source
    /// directory is unknown (a broken reference at copy time, or a scratch source document), or
    /// whose relative path cannot be computed (e.g. a different drive on Windows), keeps its ORIGINAL
    /// <c>CellRef</c> string unchanged — a best-effort fallback that may or may not still resolve in
    /// the destination; either way, R-L3a-1's placeholder rendering reports it plainly rather than
    /// this method silently producing a wrong path. Same-directory copy/paste (the common case) always
    /// rebases exactly, since the relative path from a directory to itself and back is well-defined.
    /// </summary>
    public static IReadOnlyList<LayoutInstance> RebaseInstances(
        IReadOnlyList<LayoutInstance> instances, IReadOnlyList<string?> cellDirs, string destBaseDir) =>
        RebaseInstances(instances, cellDirs, [], destBaseDir, null);

    /// <summary>
    /// brief-layout-testing-fixes.md item 2/R-fix-2 — a relative-to-destination rebase (below) is only
    /// possible when the destination document HAS a stable base directory. Pasting into a brand-new,
    /// never-saved document (<paramref name="destBaseDir"/> is <c>""</c>) previously fell all the way
    /// through to keeping the source's own relative <c>CellRef</c> string, which resolves against
    /// nothing meaningful there and reports broken even though the referenced cell is right there on
    /// disk — reproduced directly and fixed here, not assumed. Resolution order per instance:
    /// <list type="number">
    /// <item>Relative to <paramref name="destBaseDir"/>, from the SOURCE's own resolved absolute cell
    /// dir (<paramref name="cellDirs"/>) — the precise, existing behavior, unchanged, preferred whenever
    /// the destination has a real base directory to compute against.</item>
    /// <item>Otherwise, if the destination ALSO resolves to a workspace root
    /// (<paramref name="destWorkspaceRootDir"/>) and this instance's source cell dir was captured as
    /// workspace-relative (<paramref name="workspaceRelativeDirs"/>), combine the two into an ABSOLUTE
    /// <c>CellRef</c> — correct even across two workspace checkouts at different absolute locations,
    /// and immediately resolvable with no destination base directory at all (an absolute <c>CellRef</c>
    /// resolves regardless of <c>baseDir</c>, since <c>Path.Combine(baseDir, cellRef)</c> already
    /// ignores <c>baseDir</c> when <c>cellRef</c> is rooted).</item>
    /// <item>Otherwise, if the source's own absolute cell dir is known, use IT directly as an absolute
    /// <c>CellRef</c> — works immediately even with no workspace and no destination base directory.</item>
    /// <item>Otherwise (already broken at copy time) keep the original <c>CellRef</c> string unchanged —
    /// R-L3a-1's placeholder rendering reports it plainly rather than this method silently producing a
    /// wrong path.</item>
    /// </list>
    /// Same-directory copy/paste (the common case) still rebases exactly via step 1, since the relative
    /// path from a directory to itself and back is well-defined.
    /// </summary>
    public static IReadOnlyList<LayoutInstance> RebaseInstances(
        IReadOnlyList<LayoutInstance> instances, IReadOnlyList<string?> cellDirs,
        IReadOnlyList<string?> workspaceRelativeDirs, string destBaseDir, string? destWorkspaceRootDir)
    {
        var result = new List<LayoutInstance>(instances.Count);
        for (int i = 0; i < instances.Count; i++)
        {
            var clone = LayoutGeometry.Clone(instances[i]);
            string? sourceCellDir = i < cellDirs.Count ? cellDirs[i] : null;
            string? workspaceRelativeDir = i < workspaceRelativeDirs.Count ? workspaceRelativeDirs[i] : null;

            // A ws:// reference is base-independent already, and every rebasing form below would
            // turn it into a path — losing the alias, and with it the technology check and the kit
            // walk-up that only an explicitly named workspace can answer (MW2 R-mw2-2). It is kept
            // verbatim; a paste into a workspace that does not declare the alias reports NotFound,
            // which is the honest answer rather than a silently repointed instance.
            if (Workspace.ExternalCellRef.IsExternalRef(clone.CellRef))
            {
                result.Add(clone);
                continue;
            }

            if (sourceCellDir is { Length: > 0 } && destBaseDir is { Length: > 0 })
            {
                try
                {
                    clone.CellRef = Path.GetRelativePath(Path.GetFullPath(destBaseDir), Path.GetFullPath(sourceCellDir));
                    result.Add(clone);
                    continue;
                }
                catch
                {
                    // Fall through to the base-independent forms below.
                }
            }

            if (workspaceRelativeDir is { Length: > 0 } && destWorkspaceRootDir is { Length: > 0 })
            {
                try
                {
                    clone.CellRef = Path.GetFullPath(Path.Combine(destWorkspaceRootDir, workspaceRelativeDir));
                    result.Add(clone);
                    continue;
                }
                catch
                {
                    // Fall through to the plain absolute fallback below.
                }
            }

            if (sourceCellDir is { Length: > 0 })
                clone.CellRef = sourceCellDir; // absolute — resolves regardless of the dest base dir
            // else: keep the original CellRef unchanged — see the doc comment's final fallback note.

            result.Add(clone);
        }
        return result;
    }
}
