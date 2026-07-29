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

// L5b forward hook (docs/sonnet-briefs/brief-via-primitive-and-stackup.md §5, DRC): annular ring —
// (ViaShape.PadSize - ViaShape.DrillSize) / 2 — is the natural third DrcRuleKind after MinWidth and
// MinSpacing, and it is expressible ONLY because a via's pad and drill are one object (§1's own framing
// for why ViaShape exists at all). Not added now — L5b is not built yet — but the rule kind belongs
// here when it lands, not bolted on elsewhere.
public enum DrcRuleKind { MinWidth, MinSpacing }
public enum DrcSeverity { Error, Warning }

public sealed class DrcRule
{
    public string Name { get; set; } = "";
    public DrcRuleKind Kind { get; set; }
    public LayerKey Layer { get; set; }
    public long ValueDbu { get; set; }
    public DrcSeverity Severity { get; set; } = DrcSeverity.Error;
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
    public Stackup Stackup { get; set; } = new();
    public List<DrcRule> DrcRules { get; set; } = [];
}
