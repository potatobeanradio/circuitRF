// MIM-6 — a conductor's analysis sheet learns which SURFACE of its own stackup band it sits on, and
// the band's absorption direction follows that choice.
//
// WHY THE FIELD EXISTS. The full-wave planar kernel solves a conductor as a zero-thickness sheet, so
// the band's own thickness has to be given to a neighbouring dielectric — the stackup does not say
// what fills a metal band where no metal is drawn. Placing the sheet at the BOTTOM and giving the
// band to the dielectric ABOVE is right for everything the extractor saw before a capacitor: it is
// what makes a microstrip's height come out as the substrate thickness. Between two capacitor plates
// it is wrong — the lower plate's whole metal thickness lands INSIDE the gap, and the shipped MIM
// technology solved a 0.2 µm process separation as 3.2 µm (MIM-2's finding 1, 16x).
//
// WHAT THIS FILE HOLDS SHUT, in the order the brief states it:
//   1. Unset and explicit Bottom are the SAME extraction, on every shipped technology — the whole
//      guarantee that this field is additive. Asserted level-by-level, region-by-region and
//      note-by-note rather than by a spot value, because "nothing changed" is the claim.
//   2. Top moves the sheet and reverses the absorption, and the medium it produces still has every
//      level on one of its interfaces (which PlanarProblem.CanSolve requires — the pairing is not a
//      convention, it is what keeps the problem solvable).
//   3. The field survives a .ctech round trip and a technology merge, and the editor row commits it
//      undoably.
//
// The shipped MIM technology's own numbers are asserted in MimCapacitorTests, next to the capacitor
// fixtures they are about; this file is about the mechanism.

using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;

namespace CircuitRF.Ui.Tests.Em;

public class SheetReferenceSurfaceTests
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    private static RectShape Rect(LayerKey layer, double x0, double y0, double x1, double y1) =>
        new() { Layer = layer, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

    private static LabelShape Port(LayerKey layer, double x, double y, string name) =>
        new() { Layer = layer, X = Um(x), Y = Um(y), Text = name, Height = Um(4), IsPort = true };

    /// <summary>A two-level MMIC shape: a Metal1 line, a Metal2 line over it, and the airbridge post
    /// joining them — enough geometry that every part of the medium arithmetic is exercised.</summary>
    private static List<LayoutShape> MmicTwoLevel() =>
    [
        Rect(new LayerKey(1, 0),   0, 0, 120, 100),
        Rect(new LayerKey(1, 0), 180, 0, 300, 100),
        Rect(new LayerKey(2, 0),  20, 0, 280, 100),
        Rect(new LayerKey(3, 0),  40, 30,  80,  70),
        Port(new LayerKey(1, 0),   0, 50, "P1"),
        Port(new LayerKey(1, 0), 300, 50, "P2"),
    ];

    /// <summary>The same MMIC technology with its CAPACITOR in the run: plate artwork puts "MIM
    /// Metal" among the analysis levels, which is what keeps MIM-7's tied dielectric active (and
    /// therefore keeps Metal1's shipped <c>Top</c> in force). Without plate artwork the tie
    /// deactivates and the shipped <c>Top</c> is reverted for the run — the case
    /// <see cref="MmicTwoLevel"/> covers.</summary>
    private static List<LayoutShape> MmicCapacitor() =>
    [
        Rect(new LayerKey(1, 0),   0, 20,  30, 30),
        Rect(new LayerKey(9, 0),  20, 20,  30, 30),
        Rect(new LayerKey(10, 0), 22, 22,  28, 28),
        Rect(new LayerKey(2, 0),  22, 22,  60, 28),
        Port(new LayerKey(1, 0),   0, 25, "P1"),
        Port(new LayerKey(2, 0),  60, 25, "P2"),
    ];

    /// <summary>A PCB microstrip — one level, so it also covers the path where the general medium is
    /// deliberately NOT built.</summary>
    private static List<LayoutShape> PcbLine() =>
    [
        Rect(new LayerKey(1, 0), 0, 0, 20000, 3000),
        Port(new LayerKey(1, 0), 0, 1500, "P1"),
        Port(new LayerKey(1, 0), 20000, 1500, "P2"),
    ];

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 1 — unset IS Bottom, on every shipped technology
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The additive guarantee, asserted rather than asserted-about.</b> Every conductor entry of
    /// each shipped technology is given an EXPLICIT <c>Bottom</c> and the same artwork re-extracted;
    /// the two problems must agree on every quantity the solver reads and on every note the user
    /// sees. If a future edit makes "unset" and "bottom" diverge anywhere, this is where it shows —
    /// and it shows as the whole extraction, not as one z that happened to be checked.
    /// </summary>
    [Theory]
    [InlineData("MmicGaAs")]
    [InlineData("MmicGaAsCapacitor")]
    [InlineData("Pcb2Layer")]
    public void UnsetSheetAt_AndExplicitBottom_AreTheSameExtraction(string starter)
    {
        var (tech, shapes) = starter switch
        {
            "MmicGaAs"          => (StarterTechnologies.MmicGaAs(),  MmicTwoLevel()),
            "MmicGaAsCapacitor" => (StarterTechnologies.MmicGaAs(),  MmicCapacitor()),
            _                   => (StarterTechnologies.Pcb2Layer(), PcbLine()),
        };

        var asAuthored = PlanarExtractor.Extract(shapes, tech, Dbu, 20e9);
        Assert.True(asAuthored.Ok, asAuthored.Refusal);

        var forced = starter.StartsWith("MmicGaAs", StringComparison.Ordinal)
            ? StarterTechnologies.MmicGaAs()
            : StarterTechnologies.Pcb2Layer();
        // Only where the technology says NOTHING — the MMIC starter ships Metal1 at Top on purpose,
        // and overwriting that would make this test assert the opposite of what it is for.
        foreach (var l in forced.Stackup.Layers)
            if (l.Kind == StackupKind.Conductor && l.SheetAt is null)
                l.SheetAt = ConductorSheetSurface.Bottom;

        var explicitBottom = PlanarExtractor.Extract(shapes, forced, Dbu, 20e9);
        Assert.True(explicitBottom.Ok, explicitBottom.Refusal);

        AssertSameExtraction(asAuthored, explicitBottom);
    }

    /// <summary>
    /// The MMIC technology ships ONE conductor at <c>Top</c>, and clearing it must put a CAPACITOR
    /// run back exactly where MIM-2 measured it — 100 / 103.2 / 106 µm with the whole 3.2 µm at
    /// εᵣ 6.8. That is the other half of "additive": the new behaviour is reachable only by the new
    /// field, and removing the field removes the behaviour with nothing left behind.
    /// </summary>
    [Fact]
    public void ClearingTheShippedTopChoice_RestoresTheOldGeometryExactly()
    {
        var tech = StarterTechnologies.MmicGaAs();
        tech.Stackup.Layers.Single(l => l.Name == "Metal1").SheetAt = null;

        var r = PlanarExtractor.Extract(MmicCapacitor(), tech, Dbu, 20e9);
        Assert.True(r.Ok, r.Refusal);

        Assert.Equal(100.0,  r.Problem!.LevelZ(0) * 1e6, 6);
        Assert.Equal(103.2,  r.Problem!.LevelZ(1) * 1e6, 6);
        Assert.Equal(106.0,  r.Problem!.LevelZ(2) * 1e6, 6);
        var between = r.Problem!.EffectiveStack.Layers.Single(l => Math.Abs(l.Material.EpsR - 6.8) < 1e-9);
        Assert.Equal(3.2e-6, between.ThicknessM, 12);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 2 — Top moves the sheet AND reverses the absorption
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The pairing, on a stackup built for the purpose so the two halves can be seen apart.</b>
    /// Ground, 10 µm of εᵣ 4, a 2 µm conductor, 1 µm of εᵣ 9, a 2 µm conductor, 10 µm of air.
    ///
    /// <para>With both sheets at the bottom the levels are 10 and 13 µm and the εᵣ 9 region reads
    /// 3 µm — the lower conductor's own 2 µm plus the 1 µm that is actually there. With the LOWER
    /// conductor at <c>Top</c> the levels are 12 and 13, the εᵣ 9 region reads its true 1 µm, and the
    /// 2 µm went the other way: the εᵣ 4 substrate now reads 12. Nothing is created or destroyed —
    /// the band moved from one neighbour to the other, which is exactly what the field selects.</para>
    /// </summary>
    [Fact]
    public void Top_MovesTheSheetUp_AndGivesTheBandToTheDielectricBelow()
    {
        var bottom = TwoLevelProbeTech(ConductorSheetSurface.Bottom);
        var top    = TwoLevelProbeTech(ConductorSheetSurface.Top);
        var shapes = ProbeArtwork();

        var b = PlanarExtractor.Extract(shapes, bottom, Dbu, 20e9);
        Assert.True(b.Ok, b.Refusal);
        Assert.Equal(10.0, b.Problem!.LevelZ(0) * 1e6, 6);
        Assert.Equal(13.0, b.Problem!.LevelZ(1) * 1e6, 6);
        AssertRegions(b, (4.0, 10.0), (9.0, 3.0));
        Assert.Equal(10e-6, b.Problem!.Slab.HeightM, 12);

        var t = PlanarExtractor.Extract(shapes, top, Dbu, 20e9);
        Assert.True(t.Ok, t.Refusal);
        Assert.Equal(12.0, t.Problem!.LevelZ(0) * 1e6, 6);
        Assert.Equal(13.0, t.Problem!.LevelZ(1) * 1e6, 6);
        AssertRegions(t, (4.0, 12.0), (9.0, 1.0));

        // The slab — what the de-embedding is referenced to — follows the sheet, not the band.
        Assert.Equal(12e-6, t.Problem!.Slab.HeightM, 12);

        // …and the thing the pairing exists for: every level is still on an interface of its own
        // medium, which is what PlanarProblem.CanSolve requires and what a sheet moved WITHOUT its
        // absorption would break.
        Assert.True(t.Problem!.CanSolve().Ok, t.Problem!.CanSolve().Reason);
        Assert.True(t.Problem!.LevelIsOnSlabTop(0));
    }

    /// <summary>The reported thickness is still the BAND's — <c>Top</c> chooses where the sheet is,
    /// not how thick the metal is, and the cross-section kernel and interchange both read that
    /// number.</summary>
    [Fact]
    public void Top_DoesNotChangeTheConductorsOwnThickness()
    {
        var t = PlanarExtractor.Extract(ProbeArtwork(), TwoLevelProbeTech(ConductorSheetSurface.Top), Dbu, 20e9);
        Assert.True(t.Ok, t.Refusal);
        Assert.Equal(2e-6, t.Problem!.Layers[0].ThicknessM, 12);
        Assert.Equal(2e-6, t.Problem!.Layers[1].ThicknessM, 12);
    }

    /// <summary>
    /// <b>The notes name the surface, not only the z.</b> A level at 12 µm on a conductor whose band
    /// runs 10 to 12 is either a mistake or a deliberate reference-surface choice, and the run notes
    /// are the only place a user can tell which.
    /// </summary>
    [Fact]
    public void TheNotesSayWhichSurfaceEachLevelSitsOn()
    {
        var t = PlanarExtractor.Extract(ProbeArtwork(), TwoLevelProbeTech(ConductorSheetSurface.Top), Dbu, 20e9);
        Assert.True(t.Ok, t.Refusal);

        var levels = Assert.Single(t.Notes, n => n.Contains("conductor level(s) at z =", StringComparison.Ordinal));
        Assert.Contains("12 µm (top of 'Lower')", levels, StringComparison.Ordinal);
        Assert.Contains("13 µm (bottom of 'Upper')", levels, StringComparison.Ordinal);

        var b = PlanarExtractor.Extract(ProbeArtwork(), TwoLevelProbeTech(ConductorSheetSurface.Bottom), Dbu, 20e9);
        var bottomNote = Assert.Single(b.Notes, n => n.Contains("conductor level(s) at z =", StringComparison.Ordinal));
        Assert.Contains("10 µm (bottom of 'Lower')", bottomNote, StringComparison.Ordinal);
    }

    /// <summary>Meaningless on a non-conductor entry, and ignored rather than honoured — a
    /// hand-edited <c>.ctech</c> that puts <c>SheetAt</c> on a dielectric or a via must extract
    /// exactly as if it had said nothing.</summary>
    [Fact]
    public void SheetAtOnANonConductorEntry_IsIgnored()
    {
        var plain = StarterTechnologies.MmicGaAs();
        var meddled = StarterTechnologies.MmicGaAs();
        foreach (var l in meddled.Stackup.Layers)
            if (l.Kind != StackupKind.Conductor) l.SheetAt = ConductorSheetSurface.Top;

        AssertSameExtraction(
            PlanarExtractor.Extract(MmicTwoLevel(), plain, Dbu, 20e9),
            PlanarExtractor.Extract(MmicTwoLevel(), meddled, Dbu, 20e9));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 1 — persistence, merge, editor
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Round-trips through <c>.ctech</c>, and is ABSENT from the file when unset — an
    /// additive field that wrote itself into every conductor row of every technology would be churn
    /// in a hand-edited format and would claim a decision nobody made.</summary>
    [Fact]
    public void SheetAt_RoundTripsThroughCtech_AndIsAbsentWhenUnset()
    {
        var tech = StarterTechnologies.MmicGaAs();
        string json = TechPersistence.Serialize(tech);

        Assert.Equal(1, CountOccurrences(json, "\"SheetAt\""));
        Assert.Contains("\"SheetAt\": \"Top\"", json, StringComparison.Ordinal);

        var back = TechPersistence.Deserialize(json);
        foreach (var (a, b) in tech.Stackup.Layers.Zip(back.Stackup.Layers))
            Assert.Equal(a.SheetAt, b.SheetAt);
        Assert.Equal(ConductorSheetSurface.Top, back.Stackup.Layers.Single(l => l.Name == "Metal1").SheetAt);
        Assert.Null(back.Stackup.Layers.Single(l => l.Name == "Metal2").SheetAt);
    }

    /// <summary>A <c>.ctech</c> written before the field existed loads with it unset — which is the
    /// same extraction it always had.</summary>
    [Fact]
    public void ActechWithNoSheetAt_LoadsUnset()
    {
        // Pcb2Layer, because the MMIC starter now ships one deliberate Top (MIM-6, kept by MIM-7).
        var back = TechPersistence.Deserialize(TechPersistence.Serialize(StarterTechnologies.Pcb2Layer()));
        Assert.All(back.Stackup.Layers, l => Assert.Null(l.SheetAt));
    }

    /// <summary>The merge carries it. A stackup entry that differs ONLY here is a real conflict —
    /// the two solve at different heights — so it is named in the conflict description as well.</summary>
    [Fact]
    public void TechnologyMerge_CarriesSheetAt_AndNamesItInAConflict()
    {
        var target = StarterTechnologies.MmicGaAs();
        var source = StarterTechnologies.MmicGaAs();
        // The shipped starter now carries Metal1 = Top (MIM-6), so the DIFFERENCE has to be made
        // here: an older technology of the same shape that says nothing, merged from one that does.
        target.Stackup.Layers.Single(l => l.Name == "Metal1").SheetAt = null;
        source.Stackup.Layers.Single(l => l.Name == "Metal1").SheetAt = ConductorSheetSurface.Top;

        var conflicts = TechnologyMerge.FindConflicts(target, source, TechSection.Stackup);
        var metal1 = Assert.Single(conflicts, c => c.Key == "Stackup|Metal1");
        Assert.Contains("sheet at top", metal1.Theirs, StringComparison.Ordinal);
        Assert.DoesNotContain("sheet at", metal1.Mine, StringComparison.Ordinal);

        TechnologyMerge.Merge(target, source, TechSection.Stackup, TechMergeMode.Replace);
        Assert.Equal(ConductorSheetSurface.Top,
                     target.Stackup.Layers.Single(l => l.Name == "Metal1").SheetAt);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    /// <summary>Each region identified by its εᵣ (not by index — the whole point is that the region
    /// COUNT is the same and only the thicknesses move), asserted in µm.</summary>
    private static void AssertRegions(PlanarExtractionResult r, params (double EpsR, double Um)[] want)
    {
        foreach (var (epsr, um) in want)
        {
            var region = Assert.Single(r.Problem!.EffectiveStack.Layers,
                                       l => Math.Abs(l.Material.EpsR - epsr) < 1e-9);
            Assert.Equal(um, region.ThicknessM * 1e6, 9);
        }
    }

    private static readonly LayerKey ProbeLower = new(1, 0), ProbeUpper = new(2, 0);

    private static List<LayoutShape> ProbeArtwork() =>
    [
        Rect(ProbeLower, 0, 0, 200, 20),
        Rect(ProbeUpper, 0, 0, 200, 20),
        Port(ProbeLower,   0, 10, "P1"),
        Port(ProbeLower, 200, 10, "P2"),
    ];

    /// <summary>Ground / 10 µm εᵣ 4 / 2 µm metal / 1 µm εᵣ 9 / 2 µm metal / 10 µm air. The two
    /// dielectrics have distinct εᵣ so each region can be identified by material rather than by
    /// index, and the intervening one is THINNER than the metal below it — which is what makes the
    /// absorption direction visible instead of a rounding difference.</summary>
    private static Technology TwoLevelProbeTech(ConductorSheetSurface lower) => new()
    {
        Name = "sheet-surface probe",
        DefaultDisplayUnit = LayoutUnit.Um,
        DefaultSnapDbu = Um(1),
        DefaultFlattenTolDbu = Um(1),
        Layers =
        [
            new LayerDef { Key = ProbeLower, Name = "Lower", ZOrder = 1, Purpose = "drawing" },
            new LayerDef { Key = ProbeUpper, Name = "Upper", ZOrder = 2, Purpose = "drawing" },
        ],
        Stackup = new Stackup
        {
            Top = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Ground,
            Layers =
            [
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Air",   ThicknessDbu = Um(10), Epsr = 1.0 },
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Upper", ThicknessDbu = Um(2),
                                   SigmaSm = 4.1e7, DrawingLayers = [ProbeUpper] },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Interlayer", ThicknessDbu = Um(1), Epsr = 9.0 },
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Lower", ThicknessDbu = Um(2),
                                   SigmaSm = 4.1e7, DrawingLayers = [ProbeLower], SheetAt = lower },
                new StackupLayer { Kind = StackupKind.Dielectric, Name = "Substrate", ThicknessDbu = Um(10), Epsr = 4.0 },
                new StackupLayer { Kind = StackupKind.Conductor,  Name = "Ground", ThicknessDbu = Um(2),
                                   SigmaSm = 4.1e7, IsGroundReference = true },
            ],
        },
    };

    /// <summary>Every quantity the solver reads and every note the user sees.</summary>
    private static void AssertSameExtraction(PlanarExtractionResult a, PlanarExtractionResult b)
    {
        Assert.Equal(a.Ok, b.Ok);
        Assert.Equal(a.Refusal, b.Refusal);
        Assert.Equal(a.Notes, b.Notes);
        if (a.Problem is null) return;

        var (p, q) = (a.Problem!, b.Problem!);
        Assert.Equal(p.Slab.HeightM, q.Slab.HeightM, 15);
        Assert.Equal(p.Slab.Material.EpsR, q.Slab.Material.EpsR, 12);
        Assert.Equal(p.Layers.Select(l => l.Name), q.Layers.Select(l => l.Name));
        Assert.Equal(p.PolygonCount, q.PolygonCount);
        Assert.Equal(p.RequiresGeneralKernel, q.RequiresGeneralKernel);

        for (int i = 0; i < p.Layers.Count; i++)
        {
            Assert.Equal(p.LevelZ(i), q.LevelZ(i), 15);
            Assert.Equal(p.Layers[i].ThicknessM, q.Layers[i].ThicknessM, 15);
        }

        Assert.Equal(p.EffectiveStack.Layers.Count, q.EffectiveStack.Layers.Count);
        foreach (var (x, y) in p.EffectiveStack.Layers.Zip(q.EffectiveStack.Layers))
        {
            Assert.Equal(x.ThicknessM, y.ThicknessM, 15);
            Assert.Equal(x.Material.EpsR, y.Material.EpsR, 12);
            Assert.Equal(x.Material.TanD, y.Material.TanD, 12);
            Assert.Equal(x.Material.MuR,  y.Material.MuR,  12);
        }

        Assert.Equal(p.ViaList.Count, q.ViaList.Count);
        foreach (var (x, y) in p.ViaList.Zip(q.ViaList))
        {
            Assert.Equal(x.LowerLayerIndex, y.LowerLayerIndex);
            Assert.Equal(x.UpperLayerIndex, y.UpperLayerIndex);
            Assert.Equal(x.Polygons.Count, y.Polygons.Count);
        }
    }
}
