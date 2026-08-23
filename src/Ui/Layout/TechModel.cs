// Framework-free technology model — the layer table, substrate stackup, and DRC rules that are
// true of a fabrication process rather than of one cell (docs/design/layout-view.md §2.4).
// The stackup and DRC rules are carried and round-tripped now, consumed later (L5b/L6).

using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout;

/// <summary>
/// Per-layer interchange aliasing (docs/design/layout-view.md §2.4 R7a, §8 R15) — GDSII
/// <c>(layer, datatype)</c> ↔ DXF layer name ↔ Gerber file suffix and X2 file function. Additive and
/// nullable so every existing <c>.ctech</c> loads unchanged (L0a deliberately deferred this field;
/// L4a is "that moment"). A null <see cref="GdsiiLayer"/>/<see cref="GdsiiDatatype"/> means "use
/// <see cref="LayerDef.Key"/> directly" — GDSII identity already equals our own layer key by
/// construction (§2.1 R7), so this alias only matters when a technology wants its GDSII-facing number
/// to differ from its internal key. Only the GDSII fields are functionally exercised by L4a; DXF/Gerber
/// fields are inert scaffolding for L4b/L4c.
/// </summary>
public sealed record InterchangeMapping(
    int? GdsiiLayer,
    int? GdsiiDatatype,
    string? DxfLayerName,
    string? GerberSuffix,
    string? GerberFileFunction);

/// <summary>
/// A stipple: the repeating on/off mask a layer's fill is painted through.
///
/// <para><b>This is how a process makes its layers tellable apart, and colour alone does not do
/// it.</b> A real layer table runs to hundreds of rows over a few dozen colours — one measured open
/// vendor kit has 377 layers sharing 38 fill colours, so all but four of them collide with something
/// — and what separates them on screen is the pattern, not the hue. Reading the colour and dropping
/// the pattern renders a process's whole table as a few dozen indistinguishable washes.</para>
///
/// <para>Held on the <see cref="Technology"/> as a named table rather than inline on each layer,
/// mirroring how process files state it: dozens of layers share one stipple, and a table keeps them
/// sharing it through an edit instead of drifting apart.</para>
/// </summary>
public sealed class FillPattern
{
    /// <summary>What the process calls it. Also the key <see cref="LayerDef.FillPattern"/> names, so
    /// it must be unique within a technology — the import makes it so.</summary>
    public string Name { get; set; } = "";

    /// <summary>The mask, one string per row, <c>*</c> set and anything else clear. Square, and at
    /// most <see cref="MaxSize"/> on a side.</summary>
    public List<string> Rows { get; set; } = [];

    /// <summary>Above this a stipple stops reading as a texture and starts reading as geometry, and
    /// the per-pattern bitmap stops being free. Process files in practice use 8, 16 or 32.</summary>
    public const int MaxSize = 32;

    /// <summary>The mask's side length, or 0 when it states nothing usable.</summary>
    public int Size => Rows.Count is > 0 and <= MaxSize && Rows.Count == Rows[0].Length ? Rows.Count : 0;

    /// <summary>True when row <paramref name="y"/>, column <paramref name="x"/> is painted.</summary>
    public bool IsSet(int y, int x) => Rows[y][x] == '*';

    /// <summary>True when NO texel is set — the mask paints nothing at all.
    ///
    /// <para>A process file states "outline only" this way as readily as by a hollow flag, and the
    /// two must not be told apart by the renderer: painting a fill through an empty mask draws
    /// nothing, which is already the right answer.</para></summary>
    public bool IsBlank
    {
        get
        {
            foreach (var row in Rows)
                foreach (char c in row)
                    if (c == '*') return false;
            return true;
        }
    }
}

public sealed class LayerDef
{
    public LayerKey Key { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Literal RGBA — a layer's color is process data, not a themeable role
    /// (docs/design/layout-view.md §2.2). Reuses the existing framework-free theming <see cref="Rgba"/>.</summary>
    public Rgba Color { get; set; }

    public double FillOpacity { get; set; } = 0.35;
    public int ZOrder { get; set; }
    public bool Visible { get; set; } = true;

    /// <summary>Visible-but-locked is a distinct, useful state from Visible.</summary>
    public bool Selectable { get; set; } = true;

    public string? Purpose { get; set; }

    /// <summary>
    /// The <see cref="FillPattern.Name"/> this layer's fill is painted through, or null for a plain
    /// solid fill — which is what every layer said before stipples existed, and still renders exactly
    /// as it did.
    ///
    /// <para>A name rather than an index into <see cref="Technology.FillPatterns"/>: an index is
    /// invalidated by reordering the table, silently and in a way that repaints layers rather than
    /// failing. A name that resolves to nothing falls back to a solid fill.</para>
    /// </summary>
    public string? FillPattern { get; set; }

    /// <summary>Null = no interchange overrides declared (GDSII import/export falls back to
    /// <see cref="Key"/> directly). Additive, no <c>.ctech</c> <c>FormatVersion</c> bump.</summary>
    public InterchangeMapping? Interchange { get; set; }
}

public enum StackupKind { Dielectric, Conductor, Via }
public enum BoundaryCondition { Open, Ground }

/// <summary>docs/sonnet-briefs/brief-via-primitive-and-stackup.md R-via-2: a via's fill model is a
/// PROCESS parameter (a fab plates or fills a whole board to one specification), so it lives here on
/// the stackup, never on <see cref="ViaShape"/> — nobody configures fill/wall thickness per via just to
/// run a simulation. RF: a plated wall a few µm thick is many skin depths above a few GHz, so an EM
/// solver may reasonably treat Plated and Solid identically — L9 is not required to read this field.
/// Thermal: first-order — a hollow plated via has a small fraction of a filled one's conductive
/// cross-section, and thermal via arrays are sized on exactly that difference. Carried for thermal even
/// though RF can ignore it; do not "simplify away" as unused.</summary>
public enum ViaFillKind { Plated, Solid }

public sealed class StackupLayer
{
    public StackupKind Kind { get; set; }
    public string Name { get; set; } = "";
    public long ThicknessDbu { get; set; }

    // Dielectric
    public double Epsr { get; set; } = 1.0;
    public double TanD { get; set; }
    public double Mur { get; set; } = 1.0;

    // Conductor
    public double SigmaSm { get; set; }

    /// <summary>Which drawing layers map onto this stackup layer.</summary>
    public List<LayerKey> DrawingLayers { get; set; } = [];

    /// <summary>docs/sonnet-briefs/brief-L5a-pcell-contract-and-microstrip.md R-pc-9: marks a
    /// <see cref="StackupKind.Conductor"/> entry as a ground-reference plane, so a microstrip
    /// component's default substrate resolution ("topmost conductor, nearest ground-DESIGNATED
    /// conductor beneath") has something other than stack position to key on — an intervening,
    /// unmarked signal conductor (e.g. an MMIC's second metal level) must never be mistaken for
    /// ground. Additive, default false, no <c>.ctech</c> <c>FormatVersion</c> bump. Meaningless
    /// (ignored) on a non-Conductor entry.</summary>
    public bool IsGroundReference { get; set; }

    // ── Via (Kind == StackupKind.Via only) — additive, nullable, no .ctech FormatVersion bump ──────

    /// <summary>R-via-2. Null for any non-Via entry.</summary>
    public ViaFillKind? Fill { get; set; }

    /// <summary>Plated wall thickness (DBU) — meaningful only when <see cref="Fill"/> is
    /// <see cref="ViaFillKind.Plated"/>; null/unset for <see cref="ViaFillKind.Solid"/>.</summary>
    public long? WallThicknessDbu { get; set; }

    /// <summary>R-via-3: the two conductor <see cref="StackupLayer.Name"/> values this via spans —
    /// unambiguous on a two-conductor board, undefined (by design — not this brief's problem to solve)
    /// on anything thicker. Unread until L6/L9; added now so the via primitive doesn't force a model
    /// change mid-solver.</summary>
    public string? SpanFromLayer { get; set; }
    public string? SpanToLayer { get; set; }
}

public sealed class Stackup
{
    public BoundaryCondition Top { get; set; } = BoundaryCondition.Open;
    public BoundaryCondition Bottom { get; set; } = BoundaryCondition.Ground;

    /// <summary>Ordered TOP to BOTTOM.</summary>
    public List<StackupLayer> Layers { get; set; } = [];
}

// Annular ring — (ViaShape.PadSize - ViaShape.DrillSize) / 2 — is still unbuilt, and is expressible
// ONLY because a via's pad and drill are one object (brief-via-primitive-and-stackup.md §1). It
// belongs in DrcRuleKind when it lands, not bolted on elsewhere.

/// <summary>
/// What a rule measures.
///
/// <para><b>These are measurement kinds, not rule names.</b> A process states hundreds of rules;
/// they are instances of a much smaller set of measurements applied to different regions at
/// different values. Growing this enum covers more of a deck — but the OPERAND is what unlocked
/// most of it, because real rules measure DERIVED regions (<see cref="DrcRule.RegionA"/>) built by
/// boolean algebra and topological selection, not bare drawing layers.</para>
/// </summary>
public enum DrcRuleKind
{
    /// <summary>Minimum width of the region — its narrowest internal dimension.</summary>
    MinWidth,

    /// <summary>Minimum gap between distinct conductors of the SAME region.</summary>
    MinSpacing,

    /// <summary>Minimum gap between region A and region B — two different regions.</summary>
    MinSeparation,

    /// <summary>
    /// Minimum margin by which region A must extend beyond region B on every side. A is the
    /// enclosing region, B the enclosed one — a contact enclosed by the metal over it.
    /// </summary>
    MinEnclosure,

    /// <summary>Minimum extent over which region A and region B must overlap where they meet.</summary>
    MinOverlap,

    /// <summary>
    /// Minimum width of a concave gap WITHIN one polygon — a slot or a re-entrant corner.
    /// Distinct from <see cref="MinSpacing"/>, which measures between separate conductors: a notch
    /// is a gap spacing cannot see, because both of its sides belong to the same conductor.
    /// </summary>
    MinNotch,

    /// <summary>
    /// Minimum enclosed AREA of each polygon of the region.
    ///
    /// <para><b><see cref="DrcRule.ValueDbu"/> holds square DBU for this kind</b>, not a length —
    /// the one place that field is not a distance. Stated here rather than inferred, because a
    /// value silently read in the wrong unit is off by the resolution SQUARED (a million at the
    /// default) and would either report everything or nothing.</para>
    /// </summary>
    MinArea,

    /// <summary>
    /// Minimum PERIMETER of each polygon of the region, in DBU.
    ///
    /// <para>A deck's own edge-length rules select on an EDGE collection; this measures the whole
    /// polygon's boundary, which is the question this model can answer honestly. An edge-level rule
    /// is reported as unsupported rather than approximated by this one.</para>
    /// </summary>
    MinPerimeter,

    /// <summary>
    /// The fraction of each window the region must cover, checked over a sliding square window.
    ///
    /// <para><see cref="DrcRule.WindowDbu"/> is the window side and <see cref="DrcRule.MinRatio"/>
    /// / <see cref="DrcRule.MaxRatio"/> are the bounds. Unlike every other kind this one is not a
    /// distance at all, which is why it carries its own fields rather than overloading
    /// <see cref="DrcRule.ValueDbu"/> — a density stated as a length would be nonsense.</para>
    /// </summary>
    Density,

    /// <summary>
    /// Maximum ratio of connected metal AREA to the gate area it is attached to, per net.
    ///
    /// <para>Region A is the metal, region B the gate. <see cref="DrcRule.MaxRatio"/> is the limit.
    /// This is the one rule kind that is meaningless without net identity — it asks a question about
    /// a whole net, not about any pair of shapes — which is why it could not exist before
    /// <c>DrcConnectivity</c>.</para>
    /// </summary>
    AntennaRatio,
}

/// <summary>
/// Which pairs a spacing rule applies to.
///
/// <para>A process states a large share of its spacing rules TWICE, at different values: two pieces
/// of one net may legally sit closer than two that could short together. Without this, a checker
/// must pick one value — the same-net value passes genuine shorts, the different-net value fails
/// correct artwork.</para>
/// </summary>
public enum DrcNetScope
{
    /// <summary>Every pair, regardless of net. The default, and what a rule that says nothing means.</summary>
    Any,

    /// <summary>Only pairs on the same net.</summary>
    SameNet,

    /// <summary>Only pairs on different nets.</summary>
    DifferentNet,
}

public enum DrcSeverity { Error, Warning }

public sealed class DrcRule
{
    public string Name { get; set; } = "";
    public DrcRuleKind Kind { get; set; }

    /// <summary>
    /// The layer a violation's marker is attributed to, and — when <see cref="RegionA"/> is null —
    /// the region the rule measures.
    ///
    /// <para>It stays a plain <see cref="LayerKey"/> even now that a rule can measure a derived
    /// region, because a marker has to belong SOMEWHERE for the panel to group by and the renderer
    /// to colour. "The violation is on Metal1" is information a user acts on, and an arbitrary
    /// expression has no single layer to infer it from.</para>
    /// </summary>
    public LayerKey Layer { get; set; }

    /// <summary>
    /// The region this rule measures, as a <c>DrcLayerExprParser</c> expression. Null means "just
    /// <see cref="Layer"/>" — which is what every hand-authored rule and every pre-v2 `.ctech`
    /// says, so those keep working untouched and the field stays additive.
    /// </summary>
    public string? RegionA { get; set; }

    /// <summary>
    /// The second region, for the two-region kinds. Null for the one-region kinds; a two-region
    /// kind with no <c>RegionB</c> is reported as unusable rather than silently measured against
    /// itself.
    /// </summary>
    public string? RegionB { get; set; }

    /// <summary>
    /// The rule's threshold, in DBU — except for <see cref="DrcRuleKind.MinArea"/>, where it is
    /// SQUARE DBU. See that member for why the exception is stated rather than inferred.
    /// </summary>
    public long ValueDbu { get; set; }

    /// <summary>
    /// For <see cref="DrcRuleKind.Density"/>: the side of the square window the ratio is measured
    /// over, in DBU. Null everywhere else.
    ///
    /// <para>A density rule without a window is meaningless — "40% metal" is true of some window
    /// size and false of another — so the window is part of the rule, not a checker setting.</para>
    /// </summary>
    public long? WindowDbu { get; set; }

    /// <summary>For <see cref="DrcRuleKind.Density"/>: the minimum permitted coverage, 0..1.</summary>
    public double? MinRatio { get; set; }

    /// <summary>For <see cref="DrcRuleKind.Density"/>: the maximum permitted coverage, 0..1.</summary>
    public double? MaxRatio { get; set; }

    /// <summary>
    /// Which pairs a spacing or separation rule applies to. <see cref="DrcNetScope.Any"/> on every
    /// other kind, and on any rule that does not say — so this is additive and inert until used.
    /// </summary>
    public DrcNetScope NetScope { get; set; } = DrcNetScope.Any;

    public DrcSeverity Severity { get; set; } = DrcSeverity.Error;

    /// <summary>
    /// True when this kind requires <see cref="RegionB"/>.
    ///
    /// <para><c>[JsonIgnore]</c> is load-bearing, not tidiness: System.Text.Json serializes
    /// get-only properties by default, so without it every `.ctech` would gain a
    /// <c>"NeedsSecondRegion"</c> field derived from data already in the file — noise in a
    /// hand-edited format, and a value that would be silently ignored on read while looking
    /// authoritative.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsSecondRegion =>
        Kind is DrcRuleKind.MinSeparation or DrcRuleKind.MinEnclosure or DrcRuleKind.MinOverlap
             or DrcRuleKind.AntennaRatio;
}

public sealed class Technology
{
    public string Name { get; set; } = "";
    public LayoutUnit DefaultDisplayUnit { get; set; }
    public long DefaultSnapDbu { get; set; }
    public long DefaultFlattenTolDbu { get; set; }

    /// <summary>Default height (DBU) for a newly-placed <see cref="LabelShape"/> — a drafting
    /// convention of the process/board, deliberately NOT viewport-relative (docs/sonnet-briefs/
    /// brief-layout-label-fix-and-text-flatten.md R-lbl-1): unlike the bitmap brief's R-bmp-4, a label's
    /// size should stay consistent across a design and across sessions, not depend on how far the user
    /// happened to be zoomed in when they typed it. 0 = unset — <c>LayoutEditorViewModel</c> falls back
    /// to a hardcoded 5 µm only when no technology resolves at all.</summary>
    public long DefaultLabelHeightDbu { get; set; }

    /// <summary>docs/sonnet-briefs/brief-via-primitive-and-stackup.md §4.1: "pad and drill default from
    /// the technology" — the Via tool's own defaults, same additive-scalar pattern as
    /// <see cref="DefaultSnapDbu"/>/<see cref="DefaultLabelHeightDbu"/> rather than a new per-layer
    /// field, since a process typically has one conventional via size even when its stackup carries
    /// several <see cref="StackupKind.Via"/> entries. 0 = unset; the Via tool falls back to a small
    /// hardcoded default only when no technology resolves at all (mirrors the label-height fallback).</summary>
    public long DefaultViaPadDbu { get; set; }
    public long DefaultViaDrillDbu { get; set; }

    public List<LayerDef> Layers { get; set; } = [];

    /// <summary>The stipples <see cref="LayerDef.FillPattern"/> names, by <see cref="FillPattern.Name"/>.
    /// Empty — every technology authored before stipples existed — means every layer fills solid.</summary>
    public List<FillPattern> FillPatterns { get; set; } = [];

    public Stackup Stackup { get; set; } = new();
    public List<DrcRule> DrcRules { get; set; } = [];

    /// <summary>The stipple <paramref name="name"/> resolves to, or null for none/unknown.
    ///
    /// <para>A name that resolves to nothing yields null rather than throwing: a layer referring to a
    /// pattern the table no longer holds should draw as a solid fill, which is a visible, recoverable
    /// state, not a technology that cannot be opened.</para></summary>
    public FillPattern? FindFillPattern(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var p in FillPatterns)
            if (string.Equals(p.Name, name, StringComparison.Ordinal) && p.Size > 0) return p;
        return null;
    }
}
