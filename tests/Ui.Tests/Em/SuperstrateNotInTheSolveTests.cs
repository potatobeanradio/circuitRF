// ================================================================
//  SuperstrateNotInTheSolveTests.cs — ANT-12 §1a: the measurement, and what it found
//
//  The brief asked for one run before anything was written about a cover layer, because a uniform
//  superstrate LOOKS representable: metal at an interior interface turns the general kernel on,
//  Dcim.CanFit needs only a half-space on top, and MIM-4 already de-embeds a buried level. It
//  anticipated two outcomes — "it runs" (the Can list gains it) or "it refuses" (the Cannot list
//  gains it, quoting the refusal).
//
//  IT DID NEITHER. The run completed, produced a perfectly ordinary answer, and the cover layer had
//  no effect whatsoever: a 0.5 mm, eps_r = 3.0 radome over the shipped 5.8 GHz patch gave
//  s-parameters BIT-IDENTICAL to the uncovered run at every one of 11 frequencies, with the same
//  pattern and the same power budget. The cause is in the EXTRACTION and not in the physics —
//  PlanarExtractor.BuildMediumStack stops at the topmost analysis level's own sheet and terminates
//  the stack in air there, so anything the technology declares above the metal is discarded.
//
//  So this file holds both halves of what ANT-12 did about it: the identity (which is the finding),
//  and the WARNING that now says it out loud (which is the fix a silent drop needs). A refusal would
//  have been wrong — everything below the metal is still right, and a uniform superstrate is
//  genuinely inside what a layered medium can express, so this is a gap rather than an impossibility.
// ================================================================

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public sealed class SuperstrateNotInTheSolveTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;
    private static readonly LayerKey Top = new(1, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>Ground / 762 µm εᵣ 3.66 / top copper / [optional 500 µm εᵣ 3.0 cover] / open.</summary>
    private static Technology Tech(bool withCover)
    {
        var layers = new List<StackupLayer>();
        if (withCover)
            layers.Add(new StackupLayer
            {
                Kind = StackupKind.Dielectric, Name = "Radome", ThicknessDbu = Um(500),
                Epsr = 3.0, TanD = 0.005,
            });
        layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Top Copper", ThicknessDbu = Um(35),
            SigmaSm = 5.8e7, DrawingLayers = [Top],
        });
        layers.Add(new StackupLayer
        {
            Kind = StackupKind.Dielectric, Name = "Substrate", ThicknessDbu = Um(762),
            Epsr = 3.66, TanD = 0.0037,
        });
        layers.Add(new StackupLayer
        {
            Kind = StackupKind.Conductor, Name = "Bottom Copper", ThicknessDbu = Um(35),
            SigmaSm = 5.8e7, IsGroundReference = true,
        });

        return new Technology
        {
            Name = withCover ? "covered" : "bare",
            DefaultDisplayUnit   = LayoutUnit.Um,
            DefaultSnapDbu       = Um(1),
            DefaultFlattenTolDbu = Um(1),
            Layers = [new LayerDef { Key = Top, Name = "Top Copper", ZOrder = 1, Purpose = "drawing" }],
            Stackup = new Stackup
            {
                Top = BoundaryCondition.Open, Bottom = BoundaryCondition.Ground, Layers = layers,
            },
        };
    }

    private static List<LayoutShape> Patch() =>
    [
        new RectShape { Layer = Top, X1 = 0, Y1 = 0, X2 = Um(16940), Y2 = Um(13300) },
        new RectShape { Layer = Top, X1 = Um(7630), Y1 = Um(-5000), X2 = Um(9310), Y2 = 0 },
        new LabelShape
        {
            Layer = Top, X = Um(8470), Y = Um(-5000), Text = "P1", Height = Um(800),
            IsPort = true, PortDirection = LayoutRotation.R90,
        },
    ];

    /// <summary>
    /// <b>The finding, as an identity.</b> Every number the solver reads out of the extraction is the
    /// same with the cover and without it — the slab, the stack, the level heights, the terminations.
    /// That is a stronger statement than "the answer moved by less than a tolerance", and it is the
    /// one that rules out "the cover is modelled weakly".
    /// </summary>
    [Fact]
    public void AUniformCoverOverTheTopMetal_ChangesNothingInTheExtraction()
    {
        var bare    = PlanarExtractor.Extract(Patch(), Tech(false), Dbu, 6.3e9);
        var covered = PlanarExtractor.Extract(Patch(), Tech(true),  Dbu, 6.3e9);
        Assert.True(bare.Ok, bare.Refusal);
        Assert.True(covered.Ok, covered.Refusal);

        var a = bare.Problem!;
        var b = covered.Problem!;

        Assert.Equal(a.Slab.HeightM,          b.Slab.HeightM);
        Assert.Equal(a.Slab.Material.EpsR,    b.Slab.Material.EpsR);
        Assert.Equal(a.Slab.Material.TanD,    b.Slab.Material.TanD);
        Assert.Equal(a.EffectiveStack.LayerCount, b.EffectiveStack.LayerCount);
        Assert.Equal(a.EffectiveStack.Top.Kind,   b.EffectiveStack.Top.Kind);
        Assert.Equal(a.RequiresGeneralKernel,     b.RequiresGeneralKernel);
        for (int i = 0; i < a.EffectiveStack.LayerCount; i++)
            Assert.Equal(a.EffectiveStack.MaterialOfRegion(i).EpsR,
                         b.EffectiveStack.MaterialOfRegion(i).EpsR);
    }

    /// <summary>
    /// <b>And it is no longer silent.</b> The warning names the layer, names the level it sits above,
    /// says the answer is the UNCOVERED one, and says what a cover would have done — because the
    /// failure mode is a user who draws a radome into the stackup, gets a plausible resonance, and has
    /// no way to know it is the bare-board resonance.
    /// </summary>
    [Fact]
    public void ACoverAboveTheTopLevel_IsWarnedAboutByName()
    {
        var covered = PlanarExtractor.Extract(Patch(), Tech(true), Dbu, 6.3e9);
        Assert.True(covered.Ok, covered.Refusal);

        string warning = Assert.Single(covered.Notes.Where(n => n.Contains("'Radome'")));
        Assert.StartsWith("WARNING:", warning);
        Assert.Contains("NOT in this solve", warning);
        Assert.Contains("Top Copper", warning);
        Assert.Contains("UNCOVERED", warning);

        // The bare run says nothing, so the warning cannot be noise a reader learns to skip.
        var bare = PlanarExtractor.Extract(Patch(), Tech(false), Dbu, 6.3e9);
        Assert.DoesNotContain(bare.Notes, n => n.Contains("NOT in this solve"));
    }
}
