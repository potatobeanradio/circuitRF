// What an Impedance Analysis reviews, and the review saved on the layout (brief-impedance-2). The field
// report's two boards held every trace to 50 Ω because the analysis had no idea which traces were meant
// to be controlled; on both, trace WIDTH alone separated the RF path from the routing, and width is how
// a fabrication drawing's impedance note is written ("457 µm on L1: 50 Ω controlled"), so width is the
// first thing a scope can say.

using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// Which traces an Impedance Analysis reviews.
///
/// <para><b>A trace is in scope when its layer is analysed, AND (no selector is set OR it matches any
/// selector), AND (no width class is set for its layer OR its dominant width is in one).</b> Widths are
/// a FILTER; <see cref="Regions"/>, <see cref="Picks"/> and <see cref="Nets"/> (brief-impedance-4) are
/// SELECTORS — the three ways a reviewer points at a board: draw round the RF area, mark these traces,
/// name the net.</para>
///
/// <para><b>Pointing at anything narrows EVERY layer</b> (brief-impedance-6): a region belongs to one
/// layer, so with regions drawn on Top alone no other layer is reviewed. <see cref="WholeLayers"/> is
/// how a reviewer says "and all of this one" without lassoing a whole layer; it chooses only while
/// some other selector is set, because with none every analysed layer is reviewed whole anyway.</para>
///
/// <para><b>Scope selects TRACES, never COPPER.</b> Every cross-section still sees every conductor on
/// every layer; scope decides only which traces are cut, solved and reported.</para>
///
/// <para><b>Nets are where schematic back-annotation lands.</b> A trace's net is read from
/// <see cref="LayoutShape.Net"/> on the copper under its middle, so anything that writes that property —
/// a Gerber X2 <c>%TO.N</c> attribute, a board netlist applied on import, and later a schematic's
/// back-annotation — makes the RF net selectable here with no change to this type.</para>
/// </summary>
public sealed class TraceImpedanceScope
{
    /// <summary>The width classes under review, per layer; empty means every width on every layer.</summary>
    public List<TraceWidthSelector> Widths { get; set; } = [];

    /// <summary>Areas (R-imp4-1): a trace any part of whose centre line lies inside one is selected,
    /// whole — on the region's own layer (brief-impedance-6), or on every layer when it names none.</summary>
    public List<TraceScopeRegion> Regions { get; set; } = [];

    /// <summary>Points on copper (R-imp4-2), resolved to traces at every run — never a stored list of
    /// shapes, so a pick survives edits and re-imports while copper is still there.</summary>
    public List<TracePick> Picks { get; set; } = [];

    /// <summary>Net names (R-imp4-3): a trace on copper carrying one of them is selected.</summary>
    public List<string> Nets { get; set; } = [];

    /// <summary>Layers reviewed whole while other selectors narrow the rest (brief-impedance-6), by
    /// name. Inert with no region, pick or net set — kept, so deleting the last region and drawing
    /// another does not lose the reviewer's choice.</summary>
    [JsonIgnore]
    public List<string> WholeLayers { get; set; } = [];

    /// <summary><see cref="WholeLayers"/> as the <c>.clay</c> holds it: absent when empty, so a review
    /// saved before brief-impedance-6 is written back byte for byte.</summary>
    [JsonPropertyName("WholeLayers"), EditorBrowsable(EditorBrowsableState.Never)]
    public List<string>? WholeLayersSaved
    {
        get => WholeLayers.Count == 0 ? null : WholeLayers;
        set => WholeLayers = value ?? [];
    }

    /// <summary>Whether any selector is set — with none, every trace passes the selector clause.
    /// <see cref="WholeLayers"/> is not one on its own: it widens what the others narrow.</summary>
    public bool HasSelectors => Regions.Count > 0 || Picks.Count > 0 || Nets.Count > 0;

    /// <summary>Whether nothing is selected or filtered — the same as no scope at all.</summary>
    public bool IsEmpty => Widths.Count == 0 && !HasSelectors && WholeLayers.Count == 0;

    /// <summary>Whether <paramref name="layerName"/> is reviewed whole.</summary>
    public bool IsWhole(string layerName) => WholeLayers.Contains(layerName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any selector can choose a trace on <paramref name="layerName"/> — false means that
    /// layer is not reviewed at all while selectors are set (the panel says so on the layer's row). A
    /// net or a connected pick can reach any layer, so either counts for every one.</summary>
    public bool Reaches(string layerName) =>
        !HasSelectors || IsWhole(layerName) || Nets.Count > 0
        || Regions.Any(r => r.IsOn(layerName))
        || Picks.Any(p => p.Extent == TracePickExtent.Connected || string.Equals(p.LayerName, layerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The width classes set for <paramref name="layerName"/>, by name, ignoring case.</summary>
    public IEnumerable<TraceWidthSelector> WidthsOn(string layerName) =>
        Widths.Where(w => string.Equals(w.LayerName, layerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether a trace on <paramref name="layerName"/> whose dominant width is
    /// <paramref name="widthMicrons"/> passes the WIDTH filter. The selectors are geometry, and are
    /// matched by the analysis itself.</summary>
    public bool Includes(string layerName, double widthMicrons)
    {
        bool any = false;
        foreach (var w in WidthsOn(layerName))
        {
            any = true;
            if (w.Matches(widthMicrons)) return true;
        }
        return !any;
    }

    public TraceImpedanceScope Clone() => new()
    {
        Widths = [.. Widths],
        Regions = [.. Regions.Select(r => r with { Xy = [.. r.Xy] })],
        Picks = [.. Picks],
        Nets = [.. Nets],
        WholeLayers = [.. WholeLayers],
    };

    /// <summary>Whether <paramref name="other"/> selects and filters the same traces — list by list, in
    /// order, a region by its name and vertices. Null is the empty scope.</summary>
    public static bool Same(TraceImpedanceScope? a, TraceImpedanceScope? b)
    {
        a ??= new TraceImpedanceScope();
        b ??= new TraceImpedanceScope();
        return a.Widths.SequenceEqual(b.Widths) && a.Picks.SequenceEqual(b.Picks)
            && a.Nets.SequenceEqual(b.Nets, StringComparer.Ordinal)
            && a.WholeLayers.SequenceEqual(b.WholeLayers, StringComparer.Ordinal)
            && a.Regions.Count == b.Regions.Count
            && a.Regions.Zip(b.Regions).All(p => p.First.Name == p.Second.Name && p.First.LayerName == p.Second.LayerName
                                                 && p.First.Xy.SequenceEqual(p.Second.Xy));
    }
}

/// <summary>
/// One area under review (R-imp4-1a): a polygon in DBU, as flat x,y pairs, with an optional name, on the
/// copper layer <paramref name="LayerName"/> names (brief-impedance-6) — null for every layer, which is
/// what a region saved before regions had layers reads as. A region never cuts a trace short — a trace
/// crossing its edge is reviewed whole, because cutting it there would invent an open end at the
/// region's edge.
/// </summary>
public sealed record TraceScopeRegion(string? Name, long[] Xy, string? LayerName = null)
{
    public int VertexCount => Xy.Length / 2;

    /// <summary>Whether this region selects on <paramref name="layerName"/>: its own layer, or any
    /// layer when it names none.</summary>
    public bool IsOn(string layerName) =>
        LayerName is null || string.Equals(LayerName, layerName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether (<paramref name="x"/>, <paramref name="y"/>) is inside — even-odd, which for the
    /// simple polygons a rectangle or a simplified lasso makes is plain inside.</summary>
    public bool Contains(double x, double y)
    {
        bool inside = false;
        int n = VertexCount;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = Xy[2 * i], yi = Xy[2 * i + 1], xj = Xy[2 * j], yj = Xy[2 * j + 1];
            if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>Whether any part of the segment (a → b) lies inside: an end inside, or the segment
    /// crossing an edge.</summary>
    public bool Touches(double ax, double ay, double bx, double by)
    {
        if (Contains(ax, ay) || Contains(bx, by)) return true;
        int n = VertexCount;
        for (int i = 0, j = n - 1; i < n; j = i++)
            if (Cross(ax, ay, bx, by, Xy[2 * j], Xy[2 * j + 1], Xy[2 * i], Xy[2 * i + 1])) return true;
        return false;
    }

    private static bool Cross(double ax, double ay, double bx, double by, double cx, double cy, double dx, double dy)
    {
        static double Side(double px, double py, double qx, double qy, double rx, double ry) =>
            (qx - px) * (ry - py) - (qy - py) * (rx - px);
        double d1 = Side(cx, cy, dx, dy, ax, ay), d2 = Side(cx, cy, dx, dy, bx, by);
        double d3 = Side(ax, ay, bx, by, cx, cy), d4 = Side(ax, ay, bx, by, dx, dy);
        return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
    }

    /// <summary>The rectangle between two corners, DBU.</summary>
    public static TraceScopeRegion Rectangle(string? name, long x0, long y0, long x1, long y1, string? layerName = null) =>
        new(name, [Math.Min(x0, x1), Math.Min(y0, y1), Math.Max(x0, x1), Math.Min(y0, y1),
                   Math.Max(x0, x1), Math.Max(y0, y1), Math.Min(x0, x1), Math.Max(y0, y1)], layerName);
}

/// <summary>How much a <see cref="TracePick"/> selects.</summary>
public enum TracePickExtent
{
    /// <summary>The trace whose copper holds the point, on the pick's layer.</summary>
    Trace,

    /// <summary>Every trace on the copper galvanically joined to the point, across layers through vias —
    /// the partition DRC and railRF use. A series part breaks it.</summary>
    Connected,
}

/// <summary>A point on copper (R-imp4-2a), DBU, on the layer named — resolved to traces at every run.</summary>
public sealed record TracePick(string LayerName, long X, long Y, TracePickExtent Extent);

/// <summary>One width class under review: traces on <paramref name="LayerName"/> whose dominant width is
/// within <paramref name="ToleranceMicrons"/> of <paramref name="NominalMicrons"/>. The layer is named,
/// as the CLI's <c>--layers</c> names it.</summary>
public sealed record TraceWidthSelector(string LayerName, double NominalMicrons, double ToleranceMicrons)
{
    public bool Matches(double widthMicrons) => Math.Abs(widthMicrons - NominalMicrons) <= ToleranceMicrons + 1e-6;

    /// <summary>A selector for a width typed by hand (<c>--width Top=457</c>): the survey's own merge
    /// tolerance around it.</summary>
    public static TraceWidthSelector Around(string layerName, double nominalMicrons) =>
        new(layerName, nominalMicrons, TraceImpedanceAnalysis.MergeToleranceMicrons(nominalMicrons));
}

/// <summary>
/// The Impedance Analysis settings and scope, saved on the layout (<see cref="LayoutView.ImpedanceReview"/>)
/// so a re-run — in the editor or headlessly — reviews what the last one reviewed.
///
/// <para><b>Review state, not artwork</b>: changing it marks the layout dirty and is not undoable, on
/// <see cref="LayoutView.DrcWaivers"/>' terms.</para>
/// </summary>
public sealed class TraceImpedanceReview
{
    public double TargetOhms { get; set; } = TraceImpedanceOptions.DefaultTargetOhms;
    public double TolerancePercent { get; set; } = TraceImpedanceOptions.DefaultTolerancePercent;
    public double WarningPercent { get; set; } = TraceImpedanceOptions.DefaultWarningPercent;

    /// <summary>The highest frequency the traces carry, Hz, or null for off.</summary>
    public double? MaxFrequencyHz { get; set; }

    /// <summary>Trace within this many µm of the land of a via on it is not checked, 0 for all of it;
    /// null — the default, and absent from the file — for
    /// <see cref="TraceImpedanceOptions.DefaultViaTransitionMicrons"/>.</summary>
    public double? ViaTransitionMicrons { get; set; }

    /// <summary><see cref="ViaTransitionMicrons"/>, or the default where none was saved. A method, not a
    /// property: the review is serialised whole into the <c>.clay</c>, and a computed property would be
    /// written into every file that has one.</summary>
    public double ViaTransitionOrDefault() => ViaTransitionMicrons ?? TraceImpedanceOptions.DefaultViaTransitionMicrons;

    /// <summary>The copper layers analysed, BY NAME; null for every copper layer.</summary>
    public List<string>? Layers { get; set; }

    /// <summary>Which traces are reviewed; null for every trace.</summary>
    public TraceImpedanceScope? Scope { get; set; }

    /// <summary>
    /// The options a run of <paramref name="saved"/> uses — the defaults where nothing is saved — against
    /// <paramref name="tech"/>: the ONE function <c>circuitrf impedance</c> builds its options from before its
    /// flags override them, and the Layout Review's impedance check runs them as they are (brief-review-5
    /// R-rev5-1). A saved layer name the technology no longer has as copper is handed to
    /// <paramref name="skippedLayer"/> and skipped; with none left, every copper layer is analysed. The scope
    /// is a copy, so a caller may narrow it for one run.
    /// </summary>
    public static TraceImpedanceOptions OptionsFor(TraceImpedanceReview? saved, Technology tech, Action<string>? skippedLayer = null)
    {
        ArgumentNullException.ThrowIfNull(tech);
        IReadOnlyList<LayerKey>? layers = null;
        if (saved?.Layers is { Count: > 0 } names)
        {
            var copper = CopperLayerNames(tech);
            var keys = new List<LayerKey>();
            foreach (string name in names)
            {
                if (CopperLayer(tech, copper, name) is { } def) keys.Add(def.Key);
                else skippedLayer?.Invoke(name);
            }
            if (keys.Count > 0) layers = keys;
        }
        return new TraceImpedanceOptions
        {
            TargetOhms = saved?.TargetOhms ?? TraceImpedanceOptions.DefaultTargetOhms,
            TolerancePercent = saved?.TolerancePercent ?? TraceImpedanceOptions.DefaultTolerancePercent,
            WarningPercent = saved?.WarningPercent ?? TraceImpedanceOptions.DefaultWarningPercent,
            MaxFrequencyHz = saved?.MaxFrequencyHz,
            Layers = layers,
            ViaTransitionMicrons = saved?.ViaTransitionOrDefault() ?? TraceImpedanceOptions.DefaultViaTransitionMicrons,
            Scope = saved?.Scope?.Clone(),
        };
    }

    /// <summary>The names of <paramref name="tech"/>'s copper layers: the drawing layers bound to a stackup conductor.</summary>
    public static List<string> CopperLayerNames(Technology tech)
    {
        var bound = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).SelectMany(l => l.DrawingLayers).ToHashSet();
        return [.. tech.Layers.Where(l => bound.Contains(l.Key)).Select(l => l.Name)];
    }

    /// <summary>The copper layer <paramref name="name"/> names, ignoring case, or null.</summary>
    public static LayerDef? CopperLayer(Technology tech, IReadOnlyCollection<string> copperNames, string name)
    {
        var def = tech.Layers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase));
        return def is not null && copperNames.Contains(def.Name) ? def : null;
    }

    public TraceImpedanceReview Clone() => new()
    {
        TargetOhms = TargetOhms, TolerancePercent = TolerancePercent, WarningPercent = WarningPercent,
        MaxFrequencyHz = MaxFrequencyHz, ViaTransitionMicrons = ViaTransitionMicrons,
        Layers = Layers is null ? null : [.. Layers], Scope = Scope?.Clone(),
    };

    /// <summary>Whether <paramref name="other"/> says the same thing — so a dialog closed with nothing
    /// changed does not mark the layout dirty. A null or empty scope and a null layer list are the
    /// same as their absence; <paramref name="other"/> null is the default review.</summary>
    public bool SameAs(TraceImpedanceReview? other)
    {
        other ??= new TraceImpedanceReview();
        static bool SameList<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b) =>
            (a is null || a.Count == 0) ? (b is null || b.Count == 0) : b is not null && a.SequenceEqual(b);
        return TargetOhms == other.TargetOhms && TolerancePercent == other.TolerancePercent
            && WarningPercent == other.WarningPercent && MaxFrequencyHz == other.MaxFrequencyHz
            && ViaTransitionOrDefault() == other.ViaTransitionOrDefault()
            && SameList(Layers, other.Layers) && TraceImpedanceScope.Same(Scope, other.Scope);
    }
}

// ── the survey ──────────────────────────────────────────────────────────────────────────────────

/// <summary>What <see cref="TraceImpedanceAnalysis.Survey(IReadOnlyList{LayoutShape}, Technology, int,
/// TraceImpedanceOptions, CircuitRF.Engine.RunControl?)"/> found: the traces on each layer grouped by
/// width, with nothing solved but one typical cut per class.</summary>
public sealed record TraceWidthSurvey
{
    /// <summary>Why nothing was surveyed, or null.</summary>
    public string? Refusal { get; init; }

    public bool Ok => Refusal is null;

    public IReadOnlyList<TraceWidthLayer> Layers { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Every net name stated on a shape on a copper layer, sorted; empty when the artwork
    /// carries none — the panel then offers no Nets list at all (R-imp4-3a).</summary>
    public IReadOnlyList<string> Nets { get; init; } = [];

    /// <summary>The typical-Z0 cuts solved — one per class.</summary>
    public int SolveCount { get; init; }

    public TimeSpan Elapsed { get; init; }

    internal static TraceWidthSurvey Refused(string why) => new() { Refusal = why };
}

/// <summary>One surveyed layer.</summary>
public sealed record TraceWidthLayer
{
    public LayerKey Layer { get; init; }
    public string Name { get; init; } = "";

    /// <summary>Narrowest first.</summary>
    public IReadOnlyList<TraceWidthClass> Classes { get; init; } = [];
    public int PoursSkipped { get; init; }

    /// <summary>Traces on this layer whose copper carries no net, or several (R-imp4-3b) — which no net
    /// selector can choose. Zero when the artwork carries no nets at all.</summary>
    public int NetlessTraces { get; init; }
}

/// <summary>
/// The traces on one layer whose dominant widths are within max(1 %, 1 µm) of one another. Widths and
/// lengths in µm.
/// </summary>
public sealed record TraceWidthClass
{
    /// <summary>The length-weighted mean of the dominant widths merged.</summary>
    public double NominalMicrons { get; init; }
    public double MinMicrons { get; init; }
    public double MaxMicrons { get; init; }
    public int TraceCount { get; init; }
    public double TotalLengthMicrons { get; init; }

    /// <summary>ONE cross-section at the middle of the class's longest trace — "typical", and said so
    /// wherever it is shown: it is not the class's Z0, which the analysis measures trace by trace.</summary>
    public double? TypicalZ0 { get; init; }

    /// <summary>Why there is no typical Z0, or null.</summary>
    public string? TypicalRefusal { get; init; }

    /// <summary>The selector that picks exactly this class on <paramref name="layerName"/>: its nominal
    /// with a tolerance wide enough to take every width merged into it.</summary>
    public TraceWidthSelector Selector(string layerName) => new(
        layerName, Math.Round(NominalMicrons, 3),
        Math.Round(Math.Max(TraceImpedanceAnalysis.MergeToleranceMicrons(NominalMicrons),
                            Math.Max(NominalMicrons - MinMicrons, MaxMicrons - NominalMicrons)), 3));

    /// <summary>"457 µm", "99.5–101 µm".</summary>
    public string WidthText => MaxMicrons - MinMicrons < 0.05
        ? $"{Um(NominalMicrons)} µm" : $"{Um(MinMicrons)}–{Um(MaxMicrons)} µm";

    private static string Um(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}

// ── choosing a scope from a survey ──────────────────────────────────────────────────────────────

/// <summary>One row of the width list a reviewer ticks: a surveyed class, or a class saved in the scope
/// that the survey no longer finds (<see cref="Class"/> null) — kept and shown, never silently dropped
/// (R-imp2-3c), because the artwork changing under a saved scope is exactly what the reviewer must
/// see.</summary>
public sealed record TraceWidthRow(string LayerName, TraceWidthClass? Class, TraceWidthSelector Selector, bool Ticked)
{
    public bool Missing => Class is null;
}

/// <summary>The rows a survey and a saved scope make, and the scope ticked rows make — kept out of any
/// window so the dialog and the panel that replaces it (brief-impedance-3) share them.</summary>
public static class TraceWidthRows
{
    /// <summary>Every surveyed class, ticked when the saved scope selects it — none is ticked from its
    /// Z0 (R-imp2-1c: a trace drawn at the wrong width is what the review exists to catch) — then every
    /// saved class that matches nothing surveyed, ticked, as a missing row.</summary>
    public static List<TraceWidthRow> Merge(TraceWidthSurvey survey, TraceImpedanceScope? saved)
    {
        var rows = new List<TraceWidthRow>();
        foreach (var layer in survey.Layers)
            foreach (var c in layer.Classes)
                rows.Add(new TraceWidthRow(layer.Name, c, c.Selector(layer.Name),
                    saved?.WidthsOn(layer.Name).Any(w => w.Matches(c.NominalMicrons)) == true));

        foreach (var w in saved?.Widths ?? [])
        {
            var layer = survey.Layers.FirstOrDefault(l => string.Equals(l.Name, w.LayerName, StringComparison.OrdinalIgnoreCase));
            if (layer is null || !layer.Classes.Any(c => w.Matches(c.NominalMicrons)))
                rows.Add(new TraceWidthRow(w.LayerName, null, w, true));
        }
        return rows;
    }

    /// <summary>The scope the ticked rows say: each ticked row's selector.</summary>
    public static TraceImpedanceScope ScopeOf(IEnumerable<TraceWidthRow> ticked) =>
        new() { Widths = [.. ticked.Select(r => r.Selector)] };
}
