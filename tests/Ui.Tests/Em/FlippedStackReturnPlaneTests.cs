// ================================================================
//  FlippedStackReturnPlaneTests.cs — RP-3: a return plane ABOVE the levels, solved upside down.
//
//  User report, 2026-09-12. A 4-layer board imported from Gerber, ground plane on an inner layer,
//  a structure on the BOTTOM conductor. R-em-4 wants a ground-designated conductor beneath the
//  lowest level and there is none, so the run either fell back to the Stackup.Bottom = Ground
//  boundary — a plane further away than the real one, reported as a note and wrong by the ratio of
//  the two heights — or refused outright when the level sat on that boundary and the slab came out
//  zero. The user's question was the right one: why can a port not simply return through the plane
//  above it?
//
//  It can. Reflecting a structure in a horizontal plane is an exact symmetry of an isotropic
//  medium, so the answer the kernel wants is one z-arithmetic flip away, and the alternative the
//  user was left with — hand-authoring a second .ctech whose stackup is typed in backwards — is a
//  copy of the process data that nothing keeps in step with the original.
//
//  The gate that matters is the last one here: the flipped run and the hand-mirrored technology
//  must produce the SAME MEDIUM, number for number. That is the promise the feature makes, and a
//  test that only asserted "it now runs" would not hold it.
// ================================================================

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Tests.Em;

public sealed class FlippedStackReturnPlaneTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static readonly LayerKey TopArt    = new(1, 0);
    private static readonly LayerKey BottomArt = new(2, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);
    private static double M(double um) => um * 1e-6;

    // The two dielectrics are DELIBERATELY different in both thickness and permittivity. On a
    // symmetric board every mirror-arithmetic mistake still lands on the right number, which is
    // exactly the sort of fixture that cannot fail.
    private const double UpperUm = 400, UpperEps = 3.0, UpperTan = 0.004;
    private const double LowerUm = 250, LowerEps = 4.4, LowerTan = 0.02;
    private const double CondUm  = 35,  PlaneUm  = 18;

    /// <summary>
    /// Top Copper / 400 µm εᵣ 3.0 / GND plane (designated) / 250 µm εᵣ 4.4 / Bottom Copper, open
    /// above and <c>Stackup.Bottom = Ground</c> below — which is what a Gerber import writes, and
    /// is the wrong reference for anything drawn on the bottom conductor.
    /// </summary>
    private static Technology Tech()
    {
        List<StackupLayer> layers =
        [
            new() { Kind = StackupKind.Conductor, Name = "Top Copper", ThicknessDbu = Um(CondUm),
                    SigmaSm = 5.8e7, DrawingLayers = [TopArt] },
            new() { Kind = StackupKind.Dielectric, Name = "Upper Dielectric",
                    ThicknessDbu = Um(UpperUm), Epsr = UpperEps, TanD = UpperTan },
            new() { Kind = StackupKind.Conductor, Name = "GND plane", ThicknessDbu = Um(PlaneUm),
                    SigmaSm = 5.8e7, IsGroundReference = true },
            new() { Kind = StackupKind.Dielectric, Name = "Lower Dielectric",
                    ThicknessDbu = Um(LowerUm), Epsr = LowerEps, TanD = LowerTan },
            new() { Kind = StackupKind.Conductor, Name = "Bottom Copper", ThicknessDbu = Um(CondUm),
                    SigmaSm = 5.8e7, DrawingLayers = [BottomArt] },
        ];

        return new Technology
        {
            Name = "inner-plane board",
            DefaultDisplayUnit   = LayoutUnit.Um,
            DefaultSnapDbu       = Um(1),
            DefaultFlattenTolDbu = Um(1),
            Layers =
            [
                new LayerDef { Key = TopArt,    Name = "Top Copper",    ZOrder = 1, Purpose = "drawing" },
                new LayerDef { Key = BottomArt, Name = "Bottom Copper", ZOrder = 2, Purpose = "drawing" },
            ],
            Stackup = new Stackup
            {
                Top = BoundaryCondition.Open, Bottom = BoundaryCondition.Ground, Layers = layers,
            },
        };
    }

    /// <summary>The same technology as a user would have had to type it in backwards: the stackup
    /// reversed and the two boundary conditions swapped with it. Nothing else differs — the drawing
    /// layers, the materials and the thicknesses are the same objects' values.</summary>
    private static Technology HandMirrored()
    {
        var t = Tech();
        t.Name = "inner-plane board (mirrored by hand)";
        t.Stackup = new Stackup
        {
            Top    = BoundaryCondition.Ground,     // was Bottom
            Bottom = BoundaryCondition.Open,       // was Top
            Layers = [.. Enumerable.Reverse(t.Stackup.Layers)],
        };
        return t;
    }

    private static List<LayoutShape> TraceOn(LayerKey layer) =>
    [
        new RectShape { Layer = layer, X1 = 0, Y1 = 0, X2 = Um(8000), Y2 = Um(500) },
        new LabelShape { Layer = layer, X = 0, Y = Um(250), Text = "P1", Height = Um(300), IsPort = true },
        new LabelShape { Layer = layer, X = Um(8000), Y = Um(250), Text = "P2", Height = Um(300), IsPort = true },
    ];

    private static PlanarExtractionResult Extract(
        Technology tech, List<LayoutShape> art, EmExtractionSettings? settings = null)
        => PlanarExtractor.Extract(art, tech, Dbu, 10e9, settings);

    private static string? FlipNote(PlanarExtractionResult r) => r.Notes.FirstOrDefault(
        n => n.StartsWith("THIS RUN IS SOLVED WITH THE STACKUP FLIPPED", StringComparison.Ordinal));

    // ── The report ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The reported case, and what it used to do.</b> A trace on the bottom conductor of a board
    /// whose only plane is an inner layer. Unflipped the level sits ON the Stackup.Bottom boundary,
    /// the slab is zero high and the run is refused; flipped it is an ordinary microstrip over the
    /// lower dielectric. The slab must be the LOWER dielectric alone — its own thickness and its own
    /// εᵣ — which is the assertion that separates a real mirror from a plane picked by luck.
    /// </summary>
    [Fact]
    public void ABottomLayerTrace_ReturnsThroughTheInnerPlane_WithTheStackFlipped()
    {
        var r = Extract(Tech(), TraceOn(BottomArt));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal("GND plane", r.ReturnPlane!.ConductorName);
        Assert.True(r.ReturnPlane.Flipped);
        Assert.False(r.ReturnPlane.Overridden);

        Assert.Equal(M(LowerUm), r.Problem!.Slab.HeightM, 12);
        Assert.Equal(LowerEps,   r.Problem.Slab.Material.EpsR, 12);
        Assert.Equal(LowerTan,   r.Problem.Slab.Material.TanD, 12);
    }

    /// <summary>The flip is not a silent correction. It says what it did, why the stackup's own
    /// orientation could not be used, and — because every height in the notes that follow is now
    /// measured the other way up — what the datum is.</summary>
    [Fact]
    public void TheFlip_IsAnnounced_WithItsReasonAndItsDatum()
    {
        var r = Extract(Tech(), TraceOn(BottomArt));

        string note = Assert.IsType<string>(FlipNote(r));
        Assert.Equal(note, r.Notes[0]);                          // first, because it reframes the rest
        Assert.Contains("'Bottom Copper'", note, StringComparison.Ordinal);
        Assert.Contains("EXACT symmetry", note, StringComparison.Ordinal);
        Assert.Contains("this run alone", note, StringComparison.Ordinal);
        Assert.Contains("downward from the TOP", note, StringComparison.Ordinal);

        // The stack's own total, so a reader can subtract a quoted height and land on the Stackup tab.
        double totalUm = 2 * CondUm + PlaneUm + UpperUm + LowerUm;
        Assert.Contains($"{totalUm:G4} µm thick", note, StringComparison.Ordinal);
    }

    // ── The gate: a flip must equal the .ctech the user no longer has to write ────────────────

    /// <summary>
    /// <b>The promise, as an identity.</b> Everything a solver reads out of the extraction — the
    /// slab, the level heights, the medium's regions and interfaces, the vias — must be the same
    /// whether the flip was done by this extractor or by a technology whose stackup was typed in
    /// backwards. Anything less and the feature is an approximation of the workaround it replaces.
    /// </summary>
    [Fact]
    public void TheFlippedRun_AndAHandMirroredTechnology_AreTheSameMedium()
    {
        var flipped = Extract(Tech(),         TraceOn(BottomArt));
        var byHand  = Extract(HandMirrored(), TraceOn(BottomArt));

        Assert.True(flipped.Ok, flipped.Refusal);
        Assert.True(byHand.Ok,  byHand.Refusal);
        Assert.False(byHand.ReturnPlane!.Flipped, "the hand-mirrored stack needs no flip of its own");

        Assert.Equal(Signature(byHand.Problem!), Signature(flipped.Problem!));
        Assert.Equal(byHand.ReturnPlane.ConductorName, flipped.ReturnPlane!.ConductorName);
        Assert.Equal(byHand.ReturnPlane.TopM,          flipped.ReturnPlane.TopM, 12);
    }

    /// <summary>Everything about the extracted medium a caller could observe, as one string —
    /// <c>PlanarProblem</c> is a record over ARRAYS and its own equality is reference equality on
    /// the level list, which would pass on two entirely different problems.</summary>
    private static string Signature(PlanarProblem p)
    {
        var s = p.EffectiveStack;
        return string.Join(" | ",
            [
                $"slab={p.Slab.HeightM:R}/{p.Slab.Material.EpsR:R}/{p.Slab.Material.TanD:R}",
                $"levels={p.Layers.Count}",
                .. Enumerable.Range(0, p.Layers.Count).Select(i => $"z{i}={p.LevelZ(i):R}"),
                $"medium={s}",
                $"regions={s.RegionCount}",
                .. Enumerable.Range(0, s.RegionCount).Select(i => $"m{i}={s.MaterialOfRegion(i).EpsR:R}"),
                .. Enumerable.Range(0, s.InterfaceZ.Count).Select(i => $"i{i}={s.InterfaceZ[i]:R}"),
                $"vias={p.ViaList.Count}",
            ]);
    }

    // ── What must NOT flip ───────────────────────────────────────────────────────────────────

    /// <summary>An ordinary microstrip has a plane beneath it and reaches none of this. Asserted so
    /// the flip cannot start firing on runs that were always right.</summary>
    [Fact]
    public void AnOrdinaryTopLayerTrace_IsNotFlipped()
    {
        var r = Extract(Tech(), TraceOn(TopArt));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal("GND plane", r.ReturnPlane!.ConductorName);
        Assert.False(r.ReturnPlane.Flipped);
        Assert.Null(FlipNote(r));
        Assert.Equal(M(UpperUm), r.Problem!.Slab.HeightM, 12);
    }

    /// <summary>
    /// <b>The user's actual file, and the case a flip cannot rescue.</b> A Gerber import brings in
    /// the artwork of BOTH outer layers, so the plane ends up between the analysis levels — and no
    /// orientation of the stack puts a mid-stack plane beneath all of them. The note must say that,
    /// and must point at the LEVEL SET rather than at a stackup that is already correct.
    /// </summary>
    [Fact]
    public void APlaneBetweenTheLevels_IsNotFlipped_AndTheNoteNamesTheRemedy()
    {
        var r = Extract(Tech(), [.. TraceOn(TopArt), .. TraceOn(BottomArt)]);

        Assert.False(r.Ok);
        Assert.Null(FlipNote(r));

        string note = Assert.Single(
            r.Notes, n => n.Contains("is BELOW every ground-designated conductor", StringComparison.Ordinal));
        Assert.Contains("'GND plane' lies BETWEEN this run's analysis levels", note, StringComparison.Ordinal);
        Assert.Contains("two decoupled structures", note, StringComparison.Ordinal);
        Assert.Contains("analysis levels to the conductors on ONE side", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// MIM-6 and MIM-7 write a surface and a film tie in the stackup's own orientation, and
    /// reflecting them is a modelling decision rather than an arithmetic one. The flip stands down —
    /// and says so, because a run that silently declined to fix itself is indistinguishable from one
    /// that never could.
    /// </summary>
    [Fact]
    public void APatternedFilmTie_StandsTheFlipDown_AndSaysSo()
    {
        var tech = Tech();
        tech.Stackup.Layers.First(l => l.Name == "Lower Dielectric").PresentWithLayer = "Bottom Copper";

        var r = Extract(tech, TraceOn(BottomArt));

        Assert.Null(FlipNote(r));
        string note = Assert.Single(
            r.Notes, n => n.Contains("which this run would normally solve by mirroring", StringComparison.Ordinal));
        Assert.Contains("'Lower Dielectric'", note, StringComparison.Ordinal);
        Assert.StartsWith("WARNING:", note, StringComparison.Ordinal);
    }

    // ── The explicit spelling ────────────────────────────────────────────────────────────────

    /// <summary>
    /// RP-1's own field must not be strictly weaker than the rule it overrides. Naming the plane
    /// above the levels is the more explicit form of exactly what the automatic path now does, so
    /// it flips too — and R-rp1-3's refusal survives for the case no orientation can express, which
    /// the test above covers.
    /// </summary>
    [Fact]
    public void AnExplicitReturnPlaneAboveTheLevels_IsHonouredByFlipping()
    {
        var r = Extract(Tech(), TraceOn(BottomArt),
                        new EmExtractionSettings(GroundStackupLayerName: "GND plane"));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal("GND plane", r.ReturnPlane!.ConductorName);
        Assert.True(r.ReturnPlane.Flipped);
        Assert.True(r.ReturnPlane.Overridden);
        Assert.Equal(M(LowerUm), r.Problem!.Slab.HeightM, 12);
    }

    /// <summary>R-rp1-4 is unchanged by the flip: a conductor the technology does not designate is
    /// accepted, terminates the medium, and is reported as the override it is. Here it is the far
    /// side of the board, so the slab is everything between — including the plane's own metal, which
    /// is absorbed rather than modelled.</summary>
    [Fact]
    public void AFlippedRun_MayStillNameANonDesignatedConductor()
    {
        var r = Extract(Tech(), TraceOn(BottomArt),
                        new EmExtractionSettings(GroundStackupLayerName: "Top Copper"));

        Assert.True(r.Ok, r.Refusal);
        Assert.Equal("Top Copper", r.ReturnPlane!.ConductorName);
        Assert.True(r.ReturnPlane.Flipped);
        Assert.Equal(M(LowerUm + PlaneUm + UpperUm), r.Problem!.Slab.HeightM, 12);
        Assert.Contains(r.Notes, n => n.Contains("NOT marked as a ground reference", StringComparison.Ordinal));
    }
}
