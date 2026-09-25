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

    /// <summary>
    /// brief-em3d-2 R-em3d2-2: the <see cref="TechMaterial.Name"/> of the material this entry is
    /// made of, or null for "this entry's own four numbers are the whole statement" — which is what
    /// every technology written before this field means.
    ///
    /// <para><b>Resolved on read, in <c>TechPersistence</c> and nowhere else.</b> The loader
    /// overwrites <see cref="Epsr"/>/<see cref="TanD"/>/<see cref="Mur"/> (a dielectric) or
    /// <see cref="SigmaSm"/> (a conductor or via, from <see cref="TechMaterial.Sigma20"/>, at 20 °C)
    /// with the named material's values, so the ~30 readers of those four numbers are correct with
    /// no change. <b>Nothing else may read this field to pick a value</b>; a source scan holds that
    /// (it may be read by <c>TechPersistence</c>, <c>TechValidation</c>, the stackup editor, and the
    /// 3D generator). A name the technology does not define leaves the entry's own numbers in force
    /// and is a <c>check</c> error, never an exception.</para>
    ///
    /// <para>Additive, nullable, no <c>.ctech</c> <c>FormatVersion</c> bump.</para>
    /// </summary>
    public string? Material { get; set; }

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

    /// <summary>
    /// The geometric device-recognition deck — <b>beside <see cref="DrcRules"/> and for the same
    /// reason</b> (brief-lvs-14-recognition.md R-lvs14-2): what a NiCr resistor looks like is a
    /// property of the process.
    ///
    /// <para><b>Empty is the ordinary case and is not an error</b> (R-lvs14-1d). A technology with
    /// no block cannot recognise anything, and recognition is off by default anyway — the primary
    /// reading is the instance that is already in the file.</para>
    /// </summary>
    public List<DeviceRule> DeviceRules { get; set; } = [];

    /// <summary>
    /// The process's own named constants, which a <see cref="DeviceRule"/>'s parameter formula may
    /// refer to (R-lvs14-2b). Empty is ordinary.
    /// </summary>
    public List<TechConstant> Constants { get; set; } = [];

    /// <summary>
    /// The process's named materials (brief-em3d-2 R-em3d2-1, docs/design/em-3d.md §4.1a) — what a
    /// <see cref="StackupLayer.Material"/> or a <see cref="TechBody.Material"/> names. Empty is
    /// ordinary and is every technology written before the list existed.
    /// </summary>
    public List<TechMaterial> Materials { get; set; } = [];

    /// <summary>
    /// 3D-only solids the stackup has no row for — mould compound, a lid, a die attach
    /// (brief-em3d-2 R-em3d2-3). <b>No planar extractor reads this list</b>, and a separate list
    /// rather than a new <see cref="StackupKind"/> is what makes that true by construction: the
    /// planar path walks <see cref="Stackup"/> and has nothing to skip.
    /// </summary>
    public List<TechBody> Bodies { get; set; } = [];

    /// <summary>The material <paramref name="name"/> names, compared ordinal and case-insensitive
    /// (as <c>WireMaterials.ByName</c> does), or null for none/unknown.</summary>
    public TechMaterial? FindMaterial(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var m in Materials)
            if (string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

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

// ── The device-recognition deck (brief-lvs-14-recognition.md R-lvs14-2) ───────────────────────
//
// It sits BESIDE DrcRules, in the same file, for LvsToleranceRule's reason: recognition is a
// property of the PROCESS — what a NiCr resistor looks like on this wafer — and not of the
// comparison. What the deck may say is deliberately small, and every part of it is something that
// already exists: the region is a `DrcLayerExpr`, the formulas go through the one expression
// engine, and the kind is brief 4's own `DeviceKind`.
//
// ── EVERY FIELD IS A STRING, AND THAT IS THE POINT ───────────────────────────────────────────
//
// `Kind` is not the enum. System.Text.Json throws on an enum member it does not know, so a deck
// carrying a typo would make the whole TECHNOLOGY unloadable — the layer table, the stackup and
// every DRC rule with it. R-lvs14-2d says an unknown kind is a `check` error listing the real
// ones, which it cannot be if the file never opens. The same argument keeps `Body` and
// `Terminals` as text: a malformed expression is one unusable RULE, reported by name.

/// <summary>
/// One declared constant of the process — <b>what a recognition formula multiplies by</b>
/// (R-lvs14-2b): a sheet resistance, a capacitance per unit area.
/// </summary>
/// <remarks>
/// <b>On the TECHNOLOGY rather than on the rule</b>, because that is what it is: one wafer has one
/// sheet resistance, and several rules may read it. It is an EXPRESSION with a unit beside it,
/// resolved through the one expression engine exactly as a <c>.ccell</c> parameter default is —
/// never by parsing the number out of the text.
/// </remarks>
public sealed class TechConstant
{
    /// <summary>The name a formula refers to it by.</summary>
    public string Name { get; set; } = "";

    /// <summary>Its value, as an expression. May refer to another constant; a cycle is caught by
    /// the expression engine's own cycle detection and reported by <see cref="TechValidation"/>.</summary>
    public string Expression { get; set; } = "";

    /// <summary>The unit the expression is written in — <c>Ohm</c>, <c>F</c>. Null is a bare
    /// number, which is SI by construction.</summary>
    public string? Unit { get; set; }
}

// ── Named materials and 3D bodies (brief-em3d-2, docs/design/em-3d.md §4.1a) ─────────────────
//
// There is no 3D materials file: a 3D problem takes its materials from the same .ctech the layout
// and the planar solvers read. Every property is NULLABLE and null means NOT STATED — a material is
// not a dielectric or a conductor by nature (gold is a conductor in one place and nothing in
// another), so what it must state is decided where it is USED, and reported there by `check`.

/// <summary>One named material of the process.</summary>
public sealed class TechMaterial
{
    /// <summary>The key; unique within the technology, compared ordinal and case-insensitive.</summary>
    public string Name { get; set; } = "";

    /// <summary>Relative permittivity. Required where the material is used as a dielectric.</summary>
    public double? Epsr { get; set; }

    /// <summary>Optional direction-dependent εr, xx/yy/zz, for laminates and crystalline
    /// substrates. When present a 3D solver reads this and not <see cref="Epsr"/>; the planar
    /// solvers, which have no direction, always read <see cref="Epsr"/>.</summary>
    public double[]? EpsrTensor { get; set; }

    /// <summary>Loss tangent.</summary>
    public double? TanD { get; set; }

    /// <summary>Relative permeability.</summary>
    public double? Mur { get; set; }

    /// <summary>Conductivity at 20 °C, S/m. Required where the material is used as a conductor.
    /// A stackup entry naming this material takes THIS value as its σ — at 20 °C, so no planar
    /// answer moves; a 3D setup states its own operating temperature.</summary>
    public double? Sigma20 { get; set; }

    /// <summary>Temperature coefficient of resistance at 20 °C, 1/K — the pair
    /// <see cref="Sigma20"/> makes, as a bond-wire metal already carries it:
    /// σ(T) = σ₂₀ / (1 + α₂₀·(T − 20)).</summary>
    public double? Alpha20 { get; set; }

    /// <summary>
    /// <b>A placeholder: carried and validated, read by nothing yet.</b> Electrical conductivity
    /// against temperature, as (°C, S/m) points in increasing temperature — for the thermal solver
    /// (em-3d.md §9) and for any electrical solve that later wants more than the linear
    /// <see cref="Alpha20"/> model.
    ///
    /// <para>Every solver today reads <see cref="Sigma20"/> (and, for bond wires, <see cref="Alpha20"/>).
    /// How the table and the α₂₀ pair relate when both are stated — which wins, and whether they must
    /// agree at 20 °C — is deliberately undecided, and <c>check</c> says so at info rather than
    /// letting a stated table look like it is in force.</para>
    /// </summary>
    public List<TechTemperaturePoint>? SigmaVsTemp { get; set; }

    /// <summary>Thermal conductivity, W/(m·K), for the thermal solver (em-3d.md §9). Carried, read
    /// by nothing yet.</summary>
    public double? ThermalK { get; set; }

    /// <summary>
    /// <b>A placeholder: carried and validated, read by nothing yet.</b> Thermal conductivity
    /// against temperature, as (°C, W/(m·K)) points in increasing temperature — the k(T) a
    /// substrate's loss of conductivity as it heats needs (em-3d.md §9.2). How it relates to
    /// <see cref="ThermalK"/> is decided with the thermal solver, not here.
    /// </summary>
    public List<TechTemperaturePoint>? ThermalKVsTemp { get; set; }

    /// <summary>Mass density, kg/m³ — for the thermal solver and for parity with a bond-wire
    /// metal.</summary>
    public double? DensityKgM3 { get; set; }

    /// <summary>Specific heat, J/(kg·K), for the thermal solver. Carried, read by nothing yet.</summary>
    public double? SpecificHeat { get; set; }
}

/// <summary>One point of a temperature-dependent property table: the property's value at
/// <see cref="TempC"/>. The unit of <see cref="Value"/> is the property's own.</summary>
public sealed class TechTemperaturePoint
{
    /// <summary>Temperature, °C.</summary>
    public double TempC { get; set; }

    /// <summary>The property's value at <see cref="TempC"/>, in the property's own unit.</summary>
    public double Value { get; set; }
}

/// <summary>
/// A 3D-only solid (brief-em3d-2 R-em3d2-3): something that exists in a package but has no row in
/// a planar stackup. Read by the 3D generator only; the planar extractors never see one.
/// </summary>
public sealed class TechBody
{
    /// <summary>Unique among bodies AND stackup entries, because the 3D generator names solids by
    /// it.</summary>
    public string Name { get; set; } = "";

    /// <summary>A <see cref="Technology.Materials"/> name. Required.</summary>
    public string Material { get; set; } = "";

    /// <summary>A stackup entry's <see cref="StackupLayer.Name"/>: the body's bottom face is that
    /// entry's top.</summary>
    public string SitsOn { get; set; } = "";

    /// <summary>The body's height.</summary>
    public long ThicknessDbu { get; set; }

    /// <summary>The drawing layers whose shapes give the body's outline. <b>Empty means the whole
    /// problem laterally</b> — an overmold.</summary>
    public List<LayerKey> OutlineLayers { get; set; } = [];
}

/// <summary>
/// One rule of the recognition deck — <b>what a device of this process LOOKS LIKE</b>
/// (R-lvs14-2).
/// </summary>
/// <remarks>
/// <b>Read only when a run asks for it</b> (R-lvs14-1d): recognition is off by default, per run
/// and per technology, because a design circuitRF authored already carries its devices as
/// instances and re-recognising them from geometry is less reliable than reading the instance that
/// is right there.
/// </remarks>
public sealed class DeviceRule
{
    /// <summary>What to call this rule in a report. Never matched on.</summary>
    public string Name { get; set; } = "";

    /// <summary>The canonical <c>DeviceKind</c>, by name (R-lvs14-2d). An unknown one is a
    /// <c>check</c> error listing the real ones — never a fallback.</summary>
    public string Kind { get; set; } = "";

    /// <summary>
    /// The region whose connected components are the candidate devices, as a
    /// <c>DrcLayerExprParser</c> expression — <b>the same grammar a DRC rule's region uses</b>
    /// (R-lvs14-2a).
    /// </summary>
    public string Body { get; set; } = "";

    /// <summary>
    /// The terminal layers, IN ORDER — one expression each, in the same grammar. The order is the
    /// rule's own and is what orders the recognised device's terminals before position does
    /// (R-lvs14-3b).
    /// </summary>
    /// <remarks>A single expression may be written as a bare string in the `.ctech`; the converter
    /// reads either spelling, because the file is hand-edited and both read naturally.</remarks>
    [System.Text.Json.Serialization.JsonConverter(typeof(StringOrStringsConverter))]
    public List<string> Terminals { get; set; } = [];

    /// <summary>
    /// What this device's values are, as formulas over the MEASURED geometry (<c>Length</c>,
    /// <c>Width</c>, <c>Area</c>, <c>Perimeter</c>, all SI) and the technology's own
    /// <see cref="Technology.Constants"/> — through the one expression engine (R-lvs14-2b).
    /// </summary>
    /// <remarks>
    /// <b>Empty claims nothing</b> (R-lvs14-4b), and brief 10's compare-only-where-both-claim
    /// already answers that.
    /// </remarks>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Reads a JSON string OR an array of strings into a <c>List&lt;string&gt;</c>.
/// </summary>
/// <remarks>
/// <b>For the file, not for the model.</b> The `.ctech` is hand-edited and a one-terminal-layer
/// rule reads naturally as <c>"Terminals": "1/0"</c> while a two-layer one reads naturally as an
/// array. Writing always emits the array, so a round trip is stable.
/// </remarks>
public sealed class StringOrStringsConverter
    : System.Text.Json.Serialization.JsonConverter<List<string>>
{
    public override List<string> Read(
        ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.String)
            return [reader.GetString() ?? ""];

        var list = new List<string>();
        if (reader.TokenType != System.Text.Json.JsonTokenType.StartArray) return list;

        while (reader.Read() && reader.TokenType != System.Text.Json.JsonTokenType.EndArray)
            if (reader.TokenType == System.Text.Json.JsonTokenType.String)
                list.Add(reader.GetString() ?? "");

        return list;
    }

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer, List<string> value,
        System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (string s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}
