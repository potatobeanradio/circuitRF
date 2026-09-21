// Framework-free technology model — the layer table, substrate stackup, and DRC rules that are
// true of a fabrication process rather than of one cell (docs/design/layout-view.md §2.4).
// The stackup and DRC rules are carried and round-tripped now, consumed later (L5b/L6).

using CircuitRF.Design.Cells;
using CircuitRF.Design.Theming;

namespace CircuitRF.Design.Layout;

/// <summary>
/// Per-layer interchange aliasing (docs/design/layout-view.md §2.4 R7a, §8 R15) — GDSII
/// <c>(layer, datatype)</c> ↔ DXF layer name ↔ Gerber file suffix and X2 file function. Additive and
/// nullable so every existing <c>.ctech</c> loads unchanged (L0a deliberately deferred this field;
/// L4a is "that moment"). A null <see cref="GdsiiLayer"/>/<see cref="GdsiiDatatype"/> means "use
/// <see cref="LayerDef.Key"/> directly" — GDSII identity already equals our own layer key by
/// construction (§2.1 R7), so this alias only matters when a technology wants its GDSII-facing number
/// to differ from its internal key.
///
/// <para><see cref="PcbLayerName"/> (L4d, R-L4d-4) is the board-format counterpart of
/// <see cref="DxfLayerName"/>: the canonical layer name a <c>.kicad_pcb</c> uses (<c>F.Cu</c>,
/// <c>B.SilkS</c>). Declared LAST and with a default so every call site that predates it still
/// compiles, and — like every field before it — additive and nullable, so every existing <c>.ctech</c>
/// loads unchanged with no <c>FormatVersion</c> bump.</para>
/// </summary>
public sealed record InterchangeMapping(
    int? GdsiiLayer,
    int? GdsiiDatatype,
    string? DxfLayerName,
    string? GerberSuffix,
    string? GerberFileFunction,
    string? PcbLayerName = null);

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

/// <summary>
/// Which SURFACE of a conductor's own stackup band its zero-thickness analysis sheet sits on
/// (brief-em-mim-6-level-reference-surface.md).
///
/// <para><b>The pairing with the absorption direction is the whole point, and it is not a
/// preference.</b> A planar solver meshes a conductor as a sheet at one z, and the band's own
/// thickness has to be given to a neighbouring dielectric — the stackup does not say what fills a
/// metal band where no metal is drawn. Placing the sheet at the BOTTOM and giving the band to the
/// dielectric ABOVE is what makes a microstrip's height come out as the substrate thickness;
/// placing it at the TOP and giving the band to the dielectric BELOW is what makes a capacitor's
/// plate gap come out as the capacitor dielectric. Either way the sheet lands on an interface of
/// the resulting medium by construction, which <c>PlanarProblem.CanSolve</c> requires.</para>
/// </summary>
public enum ConductorSheetSurface { Bottom, Top }

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

    /// <summary>
    /// brief-em-mim-6-level-reference-surface.md: which surface of this conductor's band the
    /// full-wave planar extractor puts its zero-thickness sheet on. <b>Null (and
    /// <see cref="ConductorSheetSurface.Bottom"/>) is the behaviour every technology had before this
    /// field existed, bit-identical</b>; <see cref="ConductorSheetSurface.Top"/> is what a
    /// capacitor's LOWER plate needs, so the modelled plate separation is the capacitor dielectric
    /// alone rather than that dielectric plus the plate's own metal thickness (16x on the shipped
    /// MIM technology). Additive, nullable, no <c>.ctech</c> <c>FormatVersion</c> bump — the
    /// <see cref="Fill"/>/<see cref="SpanFromLayer"/> pattern. Meaningless (ignored) on a
    /// non-Conductor entry, and read by the PLANAR extractor only: the cross-section kernel models
    /// real metal thickness and has no sheet to place.
    /// </summary>
    public ConductorSheetSurface? SheetAt { get; set; }

    /// <summary>
    /// brief-em-mim-7-one-technology.md: names what this dielectric is PATTERNED with — i.e. this is
    /// a thin film that physically exists only where some artwork is (a thin-film capacitor's
    /// dielectric, deposited under its plate and etched away everywhere else), not a laterally
    /// continuous layer of the sandwich.
    ///
    /// <para><b>MIM-11 — the name resolves in TWO namespaces, conductor first.</b> A CONDUCTOR
    /// stackup entry, which is MIM-7's spelling and keeps priority so every technology written
    /// against it behaves bit for bit as it did; otherwise a DRAWING LAYER
    /// (<see cref="Technology.Layers"/>), which is the MASK that defines the film — on a real MMIC
    /// process the insulator is streamed out as its own mask and the plate is deposited on what that
    /// mask left, so the mask is the true statement and the plate was a proxy for it. A name in
    /// NEITHER namespace leaves the film active and is reported; deactivating on a typo would
    /// silently thin the medium.</para>
    ///
    /// <para><b>What reads it, and what it means.</b> The 2.5D premise makes every dielectric
    /// laterally infinite WITHIN a run; it does not force one to be in EVERY run. The planar
    /// extractor therefore carries this dielectric as stated when the run contains what the tie
    /// names — the named conductor is one of the analysis levels, or the layout draws the named mask
    /// — and as AIR (εᵣ 1, tanδ 0, its thickness untouched so nothing above it moves) when it does
    /// not, and says so in the run's notes. That is what lets ONE technology carry a capacitor
    /// module and still solve ordinary interconnect: a run with no capacitor in it is bit-identical
    /// to the same stack without the module. A run that DOES carry the film is told, in its notes,
    /// that the band is modelled across the whole plane and what fraction of the layout the defining
    /// artwork actually covers. Nothing else reads the field: the closed-form microstrip path sums
    /// the stackup as authored, and the kernel's own refusals are unchanged.</para>
    ///
    /// <para><b>Name the conductor directly ABOVE this entry, or the mask that opens it</b> —
    /// either way, the plate the film is deposited under is what the tie is about. Anything else is
    /// expressible and honoured, but the paired rule that reverts that conductor's
    /// <see cref="SheetAt"/> keys on the entry directly BENEATH the dielectric, so a tie pointing
    /// somewhere else is harder to read than it is wrong.</para>
    ///
    /// <para>Additive, nullable, no <c>.ctech</c> <c>FormatVersion</c> bump — the
    /// <see cref="SheetAt"/>/<see cref="SpanFromLayer"/> pattern. Meaningless (ignored) on a
    /// non-Dielectric entry.</para>
    /// </summary>
    public string? PresentWithLayer { get; set; }

    // ── Via (Kind == StackupKind.Via only) — additive, nullable, no .ctech FormatVersion bump ──────

    /// <summary>R-via-2. Null for any non-Via entry.</summary>
    public ViaFillKind? Fill { get; set; }

    /// <summary>Plated wall thickness (DBU) — meaningful only when <see cref="Fill"/> is
    /// <see cref="ViaFillKind.Plated"/>; null/unset for <see cref="ViaFillKind.Solid"/>.</summary>
    public long? WallThicknessDbu { get; set; }

    /// <summary>
    /// GI1 R-gi1-2. Whether this via layer's holes are PLATED — i.e. whether they are metal at all.
    ///
    /// <para><b>This is a separate question from <see cref="Fill"/>, and cannot be folded into
    /// it.</b> <see cref="ViaFillKind"/> is a fill MODEL and both of its values are conductive:
    /// <see cref="ViaFillKind.Plated"/> is a hollow barrel with a wall, <see cref="ViaFillKind.Solid"/>
    /// is a filled one. Neither can express "this hole is not a conductor" — which is what a
    /// non-plated hole is, and what a board's mounting holes are.</para>
    ///
    /// <para><b>Null means plated</b>, so every technology authored before this field existed reads
    /// bit-identically. <c>false</c> is written only when a drill file said so — through its
    /// <c>;TYPE=NON_PLATED</c> section, its <c>TF.FileFunction</c>, or its file name. The
    /// distinction matters because <c>PlanarExtractor.BuildViaBinding</c> turns every via entry into
    /// a conductive <c>PlanarVia</c>: a millimetre-scale non-plated hole modelled as a plated barrel
    /// shorts every layer it passes through, and the run completes cleanly.</para>
    ///
    /// <para>Additive, nullable, no <c>.ctech</c> <c>FormatVersion</c> bump — the
    /// <see cref="SheetAt"/>/<see cref="PresentWithLayer"/>/<see cref="Fill"/> pattern. Meaningless
    /// (ignored) on a non-Via entry.</para>
    /// </summary>
    public bool? Plated { get; set; }

    /// <summary>R-via-3: the two conductor <see cref="StackupLayer.Name"/> values this via spans —
    /// unambiguous on a two-conductor board, undefined (by design — not this brief's problem to solve)
    /// on anything thicker. Unread until L6/L9; added now so the via primitive doesn't force a model
    /// change mid-solver.</summary>
    public string? SpanFromLayer { get; set; }
    public string? SpanToLayer { get; set; }

    /// <summary>
    /// Where this via is DRAWN across the width of a stackup cross-section, as a fraction of the drawn
    /// band from 0 (left edge) to 1 (right edge). Null means "wherever the drawing puts it" — the
    /// automatic lane assignment, and what every technology written before this field means.
    ///
    /// <para><b>This is a drawing position and nothing else.</b> A stackup is a cross-section of a
    /// laterally infinite sandwich; a via entry is a KIND of connection between two named conductors,
    /// not one hole at one place, and every via drawn on its drawing layer is an instance of it. Nothing
    /// in the extraction, the solver or any export may read this field, and a test asserts that a
    /// technology differing only in this value extracts identically (R-stk5-9).</para>
    ///
    /// <para>A fraction rather than a coordinate because the drawn band's width is the pane's, and
    /// changes when the pane is resized; a stored pixel offset would drift every time.</para>
    ///
    /// <para>Additive, nullable, no <c>.ctech</c> <c>FormatVersion</c> bump — the
    /// <see cref="SheetAt"/>/<see cref="PresentWithLayer"/>/<see cref="Fill"/> pattern. Meaningless
    /// (ignored) on a non-Via entry. A hand-edited file carrying a value outside [0, 1] is CLAMPED on
    /// read (<c>TechPersistence</c>), not refused and not honoured — the field cannot put a barrel off
    /// the page, and a drawing position is never worth failing a load over.</para>
    /// </summary>
    public double? DrawLaneFraction { get; set; }
}

public sealed class Stackup
{
    public BoundaryCondition Top { get; set; } = BoundaryCondition.Open;
    public BoundaryCondition Bottom { get; set; } = BoundaryCondition.Ground;

    /// <summary>Ordered TOP to BOTTOM.</summary>
    public List<StackupLayer> Layers { get; set; } = [];

    /// <summary>
    /// GI3 R-gi3-7: the board's OVERALL thickness as some other document stated it — a Gerber job
    /// file's <c>BoardThickness</c>, today — carried so the editor can show it beside the sum of
    /// <see cref="StackupLayer.ThicknessDbu"/> over the non-via entries and say when the two disagree.
    ///
    /// <para><b>It is a second opinion, never a constraint, and nothing derives geometry from it.</b>
    /// No extractor reads it: the stack a solver sees is built from the entries, exactly as before. The
    /// two numbers disagreeing is INFORMATION — a stack transcribed row by row and never added up does
    /// not match the board it came from — and which of them is wrong is not something the application
    /// knows, so it reports the disagreement and corrects neither.</para>
    ///
    /// <para>Null means nothing stated one, which is the case for every hand-authored technology and
    /// for every import whose files carry no overall thickness. Additive, nullable, no <c>.ctech</c>
    /// <c>FormatVersion</c> bump — the <see cref="StackupLayer.Fill"/>/<see cref="StackupLayer.SheetAt"/>
    /// pattern.</para>
    /// </summary>
    public long? BoardThicknessDbu { get; set; }

    /// <summary>
    /// Whether the Stackup tab's CROSS-SECTION pane is expanded. Null means expanded, which is what
    /// every <c>.ctech</c> written before this field means and what a new one opens as.
    ///
    /// <para><b>A view state, and the only kind of view state this file carries</b> — the same
    /// judgement <see cref="StackupLayer.DrawLaneFraction"/> already made, for the same reason: the
    /// two panes are how a particular stackup is best looked at, that answer belongs to the stackup
    /// rather than to the installation, and a user who dedicated the window to the drawing expects it
    /// dedicated again next time they open THAT technology. Nothing downstream of the editor may read
    /// it: no extractor, no solver, no export.</para>
    ///
    /// <para>Additive, nullable, no <c>.ctech</c> <c>FormatVersion</c> bump — the
    /// <see cref="BoardThicknessDbu"/>/<see cref="StackupLayer.DrawLaneFraction"/> pattern.</para>
    /// </summary>
    public bool? DrawingPaneExpanded { get; set; }

    /// <summary>Whether the Stackup tab's CARD pane is expanded. Null means expanded — see
    /// <see cref="DrawingPaneExpanded"/>, which this is the other half of.</summary>
    public bool? CardPaneExpanded { get; set; }

    /// <summary>The sum of <see cref="StackupLayer.ThicknessDbu"/> over the Conductor and Dielectric
    /// entries — the stack's own height. <b>Via entries are excluded</b>: a via has no z band of its
    /// own (it traverses the dielectrics between the conductors it spans), which is why
    /// <c>PlanarExtractor.BuildStack</c> skips them and why <c>TechValidation</c> does not require a
    /// thickness on one.
    ///
    /// <para><c>[JsonIgnore]</c> for <see cref="DrcRule.NeedsSecondRegion"/>'s reason: get-only
    /// properties serialize by default, and a derived total written into every <c>.ctech</c> would be
    /// noise that looks authoritative and is silently ignored on read.</para></summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public long TotalThicknessDbu
    {
        get
        {
            long total = 0;
            foreach (var l in Layers)
                if (l.Kind != StackupKind.Via) total += l.ThicknessDbu;
            return total;
        }
    }
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

/// <summary>
/// One process's answer to "how close is close enough" for one <see cref="UnitDimension"/> —
/// brief-lvs-10-properties.md R-lvs10-3b.
/// </summary>
/// <remarks>
/// <b>Per DIMENSION, never per parameter</b> (R-lvs10-8). A per-parameter table is a configuration
/// surface with no evidence behind it, and the evidence that exists — the quantisation spread
/// measured off <c>examples/LVS/Bias tee/</c> — is a property of the dimension.
///
/// <para>Both fields are optional and a row may state either or both: a value passes when it is
/// within EITHER, so a row stating only <see cref="Relative"/> is a pure percentage and a row
/// stating only <see cref="Absolute"/> is a pure floor. Stating neither is exact, which is a real
/// answer and is what every dimension with no measurement behind it gets.</para>
/// </remarks>
public sealed class LvsToleranceRule
{
    /// <summary>Which dimension this row is about.</summary>
    public UnitDimension Dimension { get; set; }

    /// <summary>A fraction of the larger of the two values — <c>0.01</c> is one percent.</summary>
    public double? Relative { get; set; }

    /// <summary>An absolute difference, <b>in the dimension's SI base unit</b> (ohms, farads,
    /// metres) — the two sides are already resolved SI by the time anything compares them.</summary>
    public double? Absolute { get; set; }
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

    /// <summary>
    /// Per-dimension property tolerances for LVS — <b>beside <see cref="DrcRules"/> and for the
    /// same reason</b> (brief-lvs-10-properties.md R-lvs10-3b): a PCB 1 % part and an MMIC
    /// thin-film resistor are not held to the same number, and the number is a property of the
    /// PROCESS rather than of the comparison.
    ///
    /// <para>Empty — every technology authored before LVS existed — means the shipped default
    /// table, which <c>LvsPropertyTolerances</c> holds in one place. A row here REPLACES the
    /// default for that one dimension and leaves the rest alone.</para>
    /// </summary>
    public List<LvsToleranceRule> LvsTolerances { get; set; } = [];

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
