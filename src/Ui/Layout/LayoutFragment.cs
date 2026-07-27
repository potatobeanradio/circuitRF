// Framework-free clipboard fragment logic (docs/design/layout-view.md §6.4;
// docs/sonnet-briefs/brief-L1f-clipboard.md). This file decides what a paste MEANS — building a
// fragment from a selection, rescaling it across DBU resolutions, reconciling layers against a
// destination technology, and translating shapes for placement. LayoutClipboard.cs (src/Ui/Clipboard/)
// is the Avalonia/system-clipboard I/O layer built on top of this; it contains no rescale or
// reconciliation logic of its own. This split is deliberate (do not collapse it) — it is what lets
// the hard parts of this phase (rescale, reconciliation) get real, headless tests.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Layout;

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
        public long AnchorX { get; set; }
        public long AnchorY { get; set; }

        /// <summary>Name of the technology the selection was copied FROM, or null with no
        /// technology resolved — one string for legibility (docs/sonnet-briefs/brief-L1g-technology-retarget.md
        /// §3), so the cross-technology mapping dialog can say where the geometry came from. Not new
        /// data: <see cref="Layers"/> already carries that technology's own <see cref="LayerDef"/>s.</summary>
        public string? TechName { get; set; }

        public List<LayerDef> Layers { get; set; } = [];
        public List<LayoutShape> Shapes { get; set; } = [];
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
    public static Payload Build(IReadOnlyList<LayoutShape> shapes, Technology? tech, int dbuPerMicron)
    {
        var bbox = Bbox.Empty;
        foreach (var s in shapes) bbox = bbox.Union(LayoutGeometry.BboxOf(s));

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
            Marker       = Marker,
            DbuPerMicron = dbuPerMicron,
            AnchorX      = bbox.IsEmpty ? 0 : bbox.MinX,
            AnchorY      = bbox.IsEmpty ? 0 : bbox.MinY,
            TechName     = tech?.Name,
            Layers       = layers,
            Shapes       = shapes.Select(LayoutGeometry.Clone).ToList(),
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
        IReadOnlyList<LayoutShape> Shapes, long AnchorX, long AnchorY, IReadOnlyList<string> Warnings);

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

        if (destDbuPerMicron == payload.DbuPerMicron || payload.DbuPerMicron <= 0)
            return new RescaleResult(shapes, payload.AnchorX, payload.AnchorY, []);

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

        return new RescaleResult(shapes, ax, ay, warnings);
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
}
