// The shipped starter technologies (docs/design/layout-view.md §2.4). Pcb2Layer/MmicGaAs differ
// only in data, never in code path — if a per-market branch ever appears here, something is
// modelled in the wrong place. MmicGaAsMim is MmicGaAs plus three stackup entries, built by
// DERIVING from it rather than by restating it, so the shared part cannot drift. Empty() (added for L0d's "New Technology…" picker) is deliberately
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
        DefaultLabelHeightDbu = Um(5),
    };

    private static long Um(decimal v) => LayoutUnits.ToDbu(v, LayoutUnit.Um, Dbu);
    private static long Mm(decimal v) => LayoutUnits.ToDbu(v, LayoutUnit.Mm, Dbu);
    private static long Mil(decimal v) => LayoutUnits.ToDbu(v, LayoutUnit.Mil, Dbu);
    private static long Nm(decimal v) => LayoutUnits.ToDbu(v, LayoutUnit.Nm, Dbu);

    /// <summary>A layer's board-format alias and nothing else — every other interchange field stays
    /// unstated, exactly as it is in the shipped <c>.ctech</c> files.</summary>
    private static InterchangeMapping Pcb(string layerName) => new(null, null, null, null, null, layerName);

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
            DefaultLabelHeightDbu = Mil(40),
            // A conventional PTH: 12 mil drill, 24 mil pad (a 6 mil annular ring, comfortably above
            // most fabs' minimum).
            DefaultViaPadDbu = Mil(24),
            DefaultViaDrillDbu = Mil(12),
            Layers =
            [
                // The PcbLayerName aliases are what make Import Board land a board's copper on THIS
                // technology's copper instead of minting eight new layers beside it (R-L4d-4 —
                // PcbLayerReconciliation looks for exactly this and falls back to a synthetic key when
                // it finds none). They are declared here, and identically in the shipped
                // resources/technologies/pcb-*.ctech, because a board's own layer names are the only
                // handle the format gives us: it has names and no numeric key circuitRF could adopt.
                new LayerDef { Key = topCopper,       Name = "Top Copper",       Color = new Rgba(0xC8, 0x7A, 0x3E), ZOrder = 8, Purpose = "drawing", Interchange = Pcb("F.Cu") },
                new LayerDef { Key = bottomCopper,    Name = "Bottom Copper",    Color = new Rgba(0x8A, 0x50, 0x28), ZOrder = 7, Purpose = "drawing", Interchange = Pcb("B.Cu") },
                new LayerDef { Key = soldermaskTop,    Name = "Soldermask Top",    Color = new Rgba(0x1E, 0x6B, 0x3C), ZOrder = 6, Purpose = "drawing", Interchange = Pcb("F.Mask") },
                new LayerDef { Key = soldermaskBottom, Name = "Soldermask Bottom", Color = new Rgba(0x15, 0x50, 0x2C), ZOrder = 5, Purpose = "drawing", Interchange = Pcb("B.Mask") },
                new LayerDef { Key = silkTop,    Name = "Silk Top",    Color = new Rgba(0xF2, 0xF2, 0xF2), ZOrder = 4, Purpose = "drawing", Interchange = Pcb("F.SilkS") },
                new LayerDef { Key = silkBottom, Name = "Silk Bottom", Color = new Rgba(0xC8, 0xC8, 0xC8), ZOrder = 3, Purpose = "drawing", Interchange = Pcb("B.SilkS") },
                // No alias on Drill: the reader's own synthetic drill layer is literally called "Drill",
                // so it already matches this layer by name with nothing declared.
                new LayerDef { Key = drill,   Name = "Drill",   Color = new Rgba(0x20, 0x20, 0x20), ZOrder = 2, Purpose = "drawing" },
                new LayerDef { Key = outline, Name = "Outline", Color = new Rgba(0xFF, 0xD5, 0x00), ZOrder = 1, Purpose = "drawing", Interchange = Pcb("Edge.Cuts") },
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
                        // R-pc-9: the natural ground-reference plane for a PCB microstrip's default
                        // (zero-configuration) substrate resolution — Top Copper is the topmost
                        // conductor, Bottom Copper is the nearest ground-designated conductor beneath.
                        IsGroundReference = true,
                    },
                    // R-via-4 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md): a plated through-hole
                    // spanning both copper layers across the FR-4, bound to the Drill drawing layer — so
                    // drawing a Via tool placement here (or converting a bare Circle on Drill, R-via-6)
                    // needs zero technology editing.
                    new StackupLayer
                    {
                        Kind = StackupKind.Via, Name = "Plated Through-Hole",
                        DrawingLayers = [drill],
                        Fill = ViaFillKind.Plated, WallThicknessDbu = Um(25),
                        SpanFromLayer = "Top Copper (1 oz)", SpanToLayer = "Bottom Copper (1 oz)",
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
            DefaultLabelHeightDbu = Um(5),
            // A typical backside via through 100 µm GaAs: 60 µm drill, 80 µm pad.
            DefaultViaPadDbu = Um(80),
            DefaultViaDrillDbu = Um(60),
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
            // §3.1 (docs/sonnet-briefs/brief-via-primitive-and-stackup.md): top to bottom, Metal2 / air
            // (εr=1) / Metal1 / GaAs / backside ground — an airbridge needs no new primitive, only this
            // complete two-metal-level stack (a horizontal conductor on Metal2, air beneath it, posts to
            // Metal1) plus the two via entries below. Replaces the earlier single "Plated Gold" conductor
            // that (incorrectly) mapped BOTH Metal1 and Metal2 onto one stackup entry.
            Stackup = new Stackup
            {
                Top = BoundaryCondition.Open,
                Bottom = BoundaryCondition.Ground,
                Layers =
                [
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "Metal2",
                        ThicknessDbu = Um(3), SigmaSm = 4.1e7,
                        DrawingLayers = [metal2],
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Dielectric, Name = "Air",
                        ThicknessDbu = Um(3), Epsr = 1.0, TanD = 0,
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "Metal1",
                        ThicknessDbu = Um(3), SigmaSm = 4.1e7,
                        DrawingLayers = [metal1],
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Dielectric, Name = "GaAs",
                        ThicknessDbu = Um(100), Epsr = 12.9, TanD = 0.0006,
                        DrawingLayers = [substrate],
                    },
                    new StackupLayer
                    {
                        Kind = StackupKind.Conductor, Name = "Backside Metal",
                        ThicknessDbu = Um(3), SigmaSm = 4.1e7,
                        // R-pc-9: the ground-reference plane. NOTE this stack's topmost conductor is
                        // Metal2 (an airbridge/crossover level, §3.1), not Metal1 — a genuinely
                        // three-conductor stack, which the design doc calls out as the case the
                        // per-instance Signal/Ground override exists for (unambiguous only on a
                        // two-conductor board or MMIC). A zero-config MLIN on this starter therefore
                        // defaults to Metal2↔Backside Metal; an MLIN meant for Metal1 (the
                        // conventional MMIC RF routing layer) needs the explicit override.
                        IsGroundReference = true,
                    },
                    // R-via-4: two via entries, because the stackup now carries two metal levels.
                    new StackupLayer
                    {
                        Kind = StackupKind.Via, Name = "Backside Via",
                        DrawingLayers = [backsideVia],
                        Fill = ViaFillKind.Plated, WallThicknessDbu = Um(3),
                        SpanFromLayer = "Metal1", SpanToLayer = "Backside Metal",
                    },
                    new StackupLayer
                    {
                        // §3.1: the posts at each end of an airbridge — a short, structural gold pillar
                        // through the air gap, electroplated solid rather than a hollow barrel (unlike
                        // the backside via, which genuinely traverses 100 µm of substrate).
                        Kind = StackupKind.Via, Name = "Metal1-Metal2 Post",
                        DrawingLayers = [via],
                        Fill = ViaFillKind.Solid,
                        SpanFromLayer = "Metal1", SpanToLayer = "Metal2",
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

    /// <summary>
    /// <b>The MMIC starter WITH thin-film (MIM) capacitor support</b> — docs/sonnet-briefs/
    /// brief-em-mim-2-gaas-starter-mim.md. Built from <see cref="MmicGaAs"/> and three added stackup
    /// entries, so the two can never disagree about the parts they share.
    ///
    /// <para><b>Why this is a SEPARATE technology rather than three entries added to the starter,
    /// which is what MIM-2 originally asked for.</b> The capacitor dielectric is a real layer of the
    /// medium, and putting one between Metal1 and Metal2 has two measured consequences that the
    /// brief's "a 50-300 nm sheet perturbs an interconnect-scale stack negligibly" does not survive:
    /// </para>
    /// <list type="number">
    ///   <item><b>Every Metal1-Metal2 airbridge post stops solving.</b> The post now spans two
    ///   regions of the medium (the capacitor dielectric, then the air above the plate metal), and
    ///   <c>PlanarKernel.CanSolve</c> refuses a via that crosses a dielectric interface — its
    ///   closed-form z-integral is written in one region's asymptotic coefficients. That is a whole-run
    ///   REFUSAL, not a dropped shape.</item>
    ///   <item><b>A Metal1 microstrip moves.</b> The 0.2 µm εr 6.8 sheet sits directly on the metal, so
    ///   ε_eff rises ~1.7% and Z₀ falls ~2.8% (49.62 → 48.25 Ω on the acceptance line).</item>
    /// </list>
    /// <para>Both are fine in a technology whose whole point is capacitors, and neither is acceptable
    /// as a silent change to the technology every existing MMIC workspace already copied. So the
    /// airbridge starter stays exactly as it was, and this is the one to pick when a design has MIM
    /// capacitors in it. <b>Do not mix airbridges and capacitors in one EM run on it</b> — see the
    /// user documentation's Stackup page.</para>
    /// </summary>
    public static Technology MmicGaAsMim()
    {
        var mimMetal = new LayerKey(9, 0);
        var mimVia   = new LayerKey(10, 0);

        var tech = MmicGaAs();
        tech.Name = "MMIC GaAs + MIM";

        // The capacitor's own two drawing layers, appended. Nothing existing is renumbered: a design
        // drawn on the plain starter opens on this one with every layer meaning what it did.
        //
        // The plate metal draws ABOVE both interconnect metals (ZOrder 10) because a capacitor's top
        // plate is a small patch sitting on a Metal1 bottom plate that is usually larger — under it,
        // it would be invisible in the one view where it matters. Its via takes the one free ZOrder
        // slot below the metals (3), beside the other connection layers.
        tech.Layers.Add(new LayerDef { Key = mimMetal, Name = "MIM Metal", Color = new Rgba(0xD0, 0x60, 0x70), ZOrder = 10, Purpose = "drawing" });
        tech.Layers.Add(new LayerDef { Key = mimVia,   Name = "MIM Via",   Color = new Rgba(0x70, 0x30, 0x38), ZOrder = 3,  Purpose = "drawing" });

        // 2.55 µm, not 3 — the plate metal (0.25) and its dielectric (0.2) take the difference out of
        // the air gap, so Metal2 still sits exactly 3 µm above Metal1.
        var air = tech.Stackup.Layers.Single(l => l.Name == "Air");
        air.ThicknessDbu = Um(2.55m);

        int metal1Index = tech.Stackup.Layers.FindIndex(l => l.Name == "Metal1");
        tech.Stackup.Layers.InsertRange(metal1Index,
        [
            new StackupLayer
            {
                Kind = StackupKind.Conductor, Name = "MIM Metal",
                ThicknessDbu = Um(0.25m), SigmaSm = 4.1e7,
                DrawingLayers = [mimMetal],
            },
            new StackupLayer
            {
                // Named "MIM Dielectric" and deliberately NOT "Cap Dielectric". The starter has always
                // carried "Cap Dielectric" and "Nitride" DRAWING layers, unbound and ignored — mask
                // documentation a process deck carries — and they stay exactly that. A stackup
                // DIELECTRIC is a different kind of thing: never drawn, no artwork, and laterally
                // infinite by the 2.5D premise (docs/design/mom-engine.md §10.12). Two names is what
                // stops the next reader asking which one the solver reads.
                Kind = StackupKind.Dielectric, Name = "MIM Dielectric",
                ThicknessDbu = Um(0.2m), Epsr = 6.8, TanD = 0.001,
            },
        ]);

        // ── MIM-6 — Metal1's EM sheet sits on the TOP of its band, not the bottom ────────────────
        //
        // This is the ONE field that makes the shipped capacitor solve at the separation the process
        // states. A level is a zero-thickness sheet and its band's thickness is absorbed into a
        // neighbouring dielectric; with the default (bottom / absorb upward) Metal1's own 3 µm of
        // metal lands INSIDE the plate gap, and the solver sees 3.2 µm where the process says 0.2 —
        // 16x. With the sheet on the top surface the gap is the MIM Dielectric alone, and Metal1's
        // band is absorbed into the GaAs below it.
        //
        // The stated cost, taken deliberately: a Metal1 microstrip's EM substrate on THIS technology
        // is 103 µm of GaAs rather than 100 — ~3%, bought against a 16x capacitance error. The plain
        // starter says nothing here and is untouched.
        tech.Stackup.Layers.Single(l => l.Name == "Metal1").SheetAt = ConductorSheetSurface.Top;

        // The top plate's connection up to the routing metal. Drawn as a REGION (MIM-1) rather than a
        // point via — a plate connection is a patch nearly as large as the plate, not a barrel.
        tech.Stackup.Layers.Add(new StackupLayer
        {
            Kind = StackupKind.Via, Name = "MIM Via",
            DrawingLayers = [mimVia],
            Fill = ViaFillKind.Solid,
            SpanFromLayer = "MIM Metal", SpanToLayer = "Metal2",
        });

        return tech;
    }
}
