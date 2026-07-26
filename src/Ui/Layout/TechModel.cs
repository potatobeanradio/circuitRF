// Framework-free technology model — the layer table, substrate stackup, and DRC rules that are
// true of a fabrication process rather than of one cell (docs/design/layout-view.md §2.4).
// The stackup and DRC rules are carried and round-tripped now, consumed later (L5b/L6).

using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout;

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
}

public enum StackupKind { Dielectric, Conductor, Via }
public enum BoundaryCondition { Open, Ground }

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
}

public sealed class Stackup
{
    public BoundaryCondition Top { get; set; } = BoundaryCondition.Open;
    public BoundaryCondition Bottom { get; set; } = BoundaryCondition.Ground;

    /// <summary>Ordered TOP to BOTTOM.</summary>
    public List<StackupLayer> Layers { get; set; } = [];
}

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
    public List<LayerDef> Layers { get; set; } = [];
    public Stackup Stackup { get; set; } = new();
    public List<DrcRule> DrcRules { get; set; } = [];
}
