// The shipped starter technologies (docs/design/layout-view.md §2.4). Pcb2Layer/MmicGaAs differ
// only in data, never in code path — if a per-market branch ever appears here, something is
// modelled in the wrong place. Empty() (added for L0d's "New Technology…" picker) is deliberately
// bare — a starting point for a from-scratch process, not a third market preset.

using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout;

public static class StarterTechnologies
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>A minimal, valid, empty Technology — no layers, no stackup, no DRC rules.
    /// Named by the caller (New Technology… always overwrites Name before saving).</summary>
    public static Technology Empty() => new()
    {
        Name = "Untitled",
        DefaultDisplayUnit = LayoutUnit.Um,
        DefaultSnapDbu = Um(1),
        DefaultFlattenTolDbu = Um(1),
    };

    private static long Um(decimal v) => LayoutUnits.ToDbu(v, LayoutUnit.Um, Dbu);
    private static long Mm(decimal v) => LayoutUnits.ToDbu(v, LayoutUnit.Mm, Dbu);
    private static long Mil(decimal v) => LayoutUnits.ToDbu(v, LayoutUnit.Mil, Dbu);
    private static long Nm(decimal v) => LayoutUnits.ToDbu(v, LayoutUnit.Nm, Dbu);

    public static Technology Pcb2Layer()
    {
        var topCopper    = new LayerKey(1, 0);
        var bottomCopper = new LayerKey(2, 0);
        var soldermaskTop    = new LayerKey(3, 0);
        var soldermaskBottom = new LayerKey(4, 0);
        var silkTop    = new LayerKey(5, 0);
        var silkBottom = new LayerKey(6, 0);
        var drill   = new LayerKey(7, 0);
        var outline = new LayerKey(8, 0);

        var tech = new Technology
        {
            Name = "PCB 2-Layer",
            DefaultDisplayUnit = LayoutUnit.Mil,
            DefaultSnapDbu = Mil(1),
            DefaultFlattenTolDbu = Um(1),
            Layers =
            [
                new LayerDef { Key = topCopper,       Name = "Top Copper",       Color = new Rgba(0xC8, 0x7A, 0x3E), ZOrder = 8, Purpose = "drawing" },
                new LayerDef { Key = bottomCopper,    Name = "Bottom Copper",    Color = new Rgba(0x8A, 0x50, 0x28), ZOrder = 7, Purpose = "drawing" },
                new LayerDef { Key = soldermaskTop,    Name = "Soldermask Top",    Color = new Rgba(0x1E, 0x6B, 0x3C), ZOrder = 6, Purpose = "drawing" },
                new LayerDef { Key = soldermaskBottom, Name = "Soldermask Bottom", Color = new Rgba(0x15, 0x50, 0x2C), ZOrder = 5, Purpose = "drawing" },
                new LayerDef { Key = silkTop,    Name = "Silk Top",    Color = new Rgba(0xF2, 0xF2, 0xF2), ZOrder = 4, Purpose = "drawing" },
                new LayerDef { Key = silkBottom, Name = "Silk Bottom", Color = new Rgba(0xC8, 0xC8, 0xC8), ZOrder = 3, Purpose = "drawing" },
                new LayerDef { Key = drill,   Name = "Drill",   Color = new Rgba(0x20, 0x20, 0x20), ZOrder = 2, Purpose = "drawing" },
                new LayerDef { Key = outline, Name = "Outline", Color = new Rgba(0xFF, 0xD5, 0x00), ZOrder = 1, Purpose = "drawing" },
            ],
            Stackup = new Stackup
            {
                Top = BoundaryCondition.Open,
                Bottom = BoundaryCondition.Ground,
                Layers =
                [
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "Top Copper (1 oz)",
                        ThicknessDbu = Um(35), SigmaSm = 5.8e7,
                        DrawingLayers = [topCopper],
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Dielectric, Name = "FR-4",
                        ThicknessDbu = Mm(1.6m), Epsr = 4.4, TanD = 0.02,
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "Bottom Copper (1 oz)",
                        ThicknessDbu = Um(35), SigmaSm = 5.8e7,
                        DrawingLayers = [bottomCopper],
                    },
                ],
            },
            DrcRules =
            [
                new DrcRule { Name = "Top Copper Min Width",      Kind = DrcRuleKind.MinWidth,   Layer = topCopper,    ValueDbu = Mil(6) },
                new DrcRule { Name = "Top Copper Min Spacing",    Kind = DrcRuleKind.MinSpacing, Layer = topCopper,    ValueDbu = Mil(6) },
                new DrcRule { Name = "Bottom Copper Min Width",   Kind = DrcRuleKind.MinWidth,   Layer = bottomCopper, ValueDbu = Mil(6) },
                new DrcRule { Name = "Bottom Copper Min Spacing", Kind = DrcRuleKind.MinSpacing, Layer = bottomCopper, ValueDbu = Mil(6) },
            ],
        };

        return tech;
    }

    public static Technology MmicGaAs()
    {
        var metal1   = new LayerKey(1, 0);
        var metal2   = new LayerKey(2, 0);
        var via      = new LayerKey(3, 0);
        var resistor = new LayerKey(4, 0);
        var capDielectric = new LayerKey(5, 0);
        var nitride  = new LayerKey(6, 0);
        var substrate    = new LayerKey(7, 0);
        var backsideVia  = new LayerKey(8, 0);

        var tech = new Technology
        {
            Name = "MMIC GaAs",
            DefaultDisplayUnit = LayoutUnit.Um,
            DefaultSnapDbu = Nm(5),
            DefaultFlattenTolDbu = Nm(10),
            Layers =
            [
                new LayerDef { Key = metal1,       Name = "Metal1",        Color = new Rgba(0xE0, 0xB0, 0x40), ZOrder = 8, Purpose = "drawing" },
                new LayerDef { Key = metal2,       Name = "Metal2",        Color = new Rgba(0xC0, 0x80, 0x20), ZOrder = 9, Purpose = "drawing" },
                new LayerDef { Key = via,          Name = "Via",           Color = new Rgba(0x90, 0x90, 0x90), ZOrder = 7, Purpose = "drawing" },
                new LayerDef { Key = resistor,     Name = "Resistor",      Color = new Rgba(0x60, 0x40, 0xA0), ZOrder = 6, Purpose = "drawing" },
                new LayerDef { Key = capDielectric, Name = "Cap Dielectric", Color = new Rgba(0x30, 0x90, 0xB0), ZOrder = 5, Purpose = "drawing" },
                new LayerDef { Key = nitride,      Name = "Nitride",       Color = new Rgba(0x40, 0x70, 0x40), ZOrder = 4, Purpose = "drawing" },
                new LayerDef { Key = substrate,    Name = "Substrate",     Color = new Rgba(0x50, 0x50, 0x50), ZOrder = 1, Purpose = "drawing" },
                new LayerDef { Key = backsideVia,  Name = "Backside Via",  Color = new Rgba(0x20, 0x20, 0x60), ZOrder = 2, Purpose = "drawing" },
            ],
            Stackup = new Stackup
            {
                Top = BoundaryCondition.Open,
                Bottom = BoundaryCondition.Ground,
                Layers =
                [
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "Plated Gold",
                        ThicknessDbu = Um(3), SigmaSm = 4.1e7,
                        DrawingLayers = [metal1, metal2],
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Dielectric, Name = "GaAs",
                        ThicknessDbu = Um(100), Epsr = 12.9, TanD = 0.0006,
                        DrawingLayers = [substrate],
                    },
                ],
            },
            DrcRules =
            [
                new DrcRule { Name = "Metal1 Min Width",   Kind = DrcRuleKind.MinWidth,   Layer = metal1, ValueDbu = Um(4) },
                new DrcRule { Name = "Metal1 Min Spacing", Kind = DrcRuleKind.MinSpacing, Layer = metal1, ValueDbu = Um(4) },
                new DrcRule { Name = "Metal2 Min Width",   Kind = DrcRuleKind.MinWidth,   Layer = metal2, ValueDbu = Um(4) },
                new DrcRule { Name = "Metal2 Min Spacing", Kind = DrcRuleKind.MinSpacing, Layer = metal2, ValueDbu = Um(4) },
            ],
        };

        return tech;
    }
}
