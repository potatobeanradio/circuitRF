// MIM-2/MIM-7 — the GaAs starter technology states a thin-film (MIM) capacitor, and both capacitor
// forms extract as ordinary multi-level planar EM problems.
//
// What this file is FOR. Until MIM-2 no shipped technology could express a MIM capacitor at all, so
// every capability the rest of the series builds had no in-tree structure to run on. The shipped
// MMIC technology now can — the plain MMIC starter plus a plate conductor (MIM Metal), the thin
// dielectric under it (MIM Dielectric) and the plate's connection up to the routing metal (MIM Via)
// — and these fixtures are the shapes a user would draw on it. They are built in CODE on the real
// shipped technology rather than committed as artwork, for the reason RegionViaExtractionTests
// states: a fixture that restates the technology is a second copy of it that drifts.
//
// IT WAS A SECOND TECHNOLOGY FROM MIM-2 TO MIM-7, and the reason it no longer is, is the point of
// the gate below. Stating a capacitor dielectric between Metal1 and Metal2 made every airbridge
// post refuse to solve (a WHOLE-RUN refusal from the kernel, not a dropped shape) and moved a
// Metal1 microstrip's Z0 by 2.8% — neither acceptable silently on the technology every existing
// MMIC workspace copied. Both costs came from the film being present in runs that contain no
// capacitor. StackupLayer.PresentWithLayer ties the film to its plate, so it enters the medium only
// when the plate is one of the run's ANALYSIS LEVELS, and an interconnect-only run extracts
// BIT-IDENTICALLY to the same run on a stack with no module at all — which is what
// AnAirbridgePost_SolvesOnTheOneTechnology_AndExtractsIdenticallyToTheModuleFreeStack asserts.
//
// NOTHING IN THE EXTRACTOR IS PER-CAPACITOR, and the two-capacitor fixture is here to hold that
// shut: every shape on a selected level is taken and every region on a via entry is taken, so a
// matching section made of several capacitors and the lines between them — which is what an MMIC
// actually contains — needs no new machinery. If that ever stops being true, this is where it shows.

using System.Numerics;
using CircuitRF.Engine.Mom;
using NumFlat;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Layout;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Ui.Layout.PCells;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public class MimCapacitorTests(ITestOutputHelper output)
{
    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    private static readonly LayerKey Metal1      = new(1, 0);
    private static readonly LayerKey Metal2      = new(2, 0);
    private static readonly LayerKey Post        = new(3, 0);    // -> "Metal1-Metal2 Post"
    private static readonly LayerKey BacksideVia = new(8, 0);    // -> "Backside Via"
    private static readonly LayerKey MimMetal    = new(9, 0);    // -> the "MIM Metal" conductor
    private static readonly LayerKey MimVia      = new(10, 0);   // -> "MIM Via"

    // MIM-11 — the nitride MASK, which is what the shipped stackup's film is now tied to
    // (PresentWithLayer: Nitride). Every fixture below that draws a plate draws one, because that is
    // what a layout off `KIT_MIMCAP` contains and a fixture that skipped it would be testing a
    // capacitor nobody can fabricate.
    //
    // DRAWN COINCIDENT WITH THE TOP PLATE here, where the kit draws it one minimum feature larger.
    // Nothing reads the mask's EXTENT except the coverage percentage in the run's own note — it is
    // never meshed and is in no matrix — so the fixtures keep their existing footprints rather than
    // growing 4 µm on every side and colliding with the feeds they were sized against.
    private static readonly LayerKey Nitride     = new(6, 0);

    private static long Um(double v) => (long)Math.Round(v * Dbu);

    /// <summary>|z|², without the square root <c>Complex.Magnitude</c> would take.</summary>
    private static double Sq(Complex z) => z.Real * z.Real + z.Imaginary * z.Imaginary;

    private static RectShape Rect(LayerKey layer, double x0, double y0, double x1, double y1) =>
        new() { Layer = layer, X1 = Um(x0), Y1 = Um(y0), X2 = Um(x1), Y2 = Um(y1) };

    private static LabelShape Port(LayerKey layer, double x, double y, string name) =>
        new() { Layer = layer, X = Um(x), Y = Um(y), Text = name, Height = Um(4), IsPort = true };

    // ── The three fixtures ────────────────────────────────────────────────────────────────────
    //
    // All three are 10 x 10 µm plates on 6 µm feeds — small enough that the raw-solve smoke test
    // below can afford one, large enough that every drawn edge clears the starter's own 4 µm
    // minimum width.

    /// <summary>
    /// <b>The SHUNT form.</b> Bottom plate on Metal1 sitting over a backside via, so it is held at
    /// the ground plane; top plate on MIM Metal; the feed lands on Metal2 through a MIM Via region.
    /// One port, because the far terminal of a shunt element is the ground plane itself.
    /// </summary>
    private static List<LayoutShape> ShuntCapacitor() =>
    [
        Rect(Metal1,      18, 18, 32, 32),      // bottom plate, 2 µm larger than the top all round
        Rect(BacksideVia, 20, 20, 30, 30),      // …grounded through the substrate
        Rect(MimMetal,    20, 20, 30, 30),      // top plate
        Rect(Nitride,     20, 20, 30, 30),      // …and the mask that puts the film under it
        Rect(MimVia,      22, 22, 28, 28),      // its connection up to the routing metal
        Rect(Metal2,      22, 22, 70, 28),      // the feed
        Port(Metal2, 70, 25, "P1"),
    ];

    /// <summary>
    /// <b>The SERIES form.</b> The feed arrives on Metal1, which IS the bottom plate — no backside
    /// via anywhere — and leaves on Metal2 through the plate via. Two ports.
    /// </summary>
    private static List<LayoutShape> SeriesCapacitor() =>
    [
        Rect(Metal1,   0, 20, 30, 30),          // feed in, continuous with the bottom plate
        Rect(MimMetal, 20, 20, 30, 30),         // top plate
        Rect(Nitride,  20, 20, 30, 30),
        Rect(MimVia,   22, 22, 28, 28),
        Rect(Metal2,   22, 22, 60, 28),         // feed out
        Port(Metal1,  0, 25, "P1"),
        Port(Metal2, 60, 25, "P2"),
    ];

    /// <summary>
    /// <b>The MMIC acceptance shape — two series capacitors joined by a Metal1 line.</b> A capacitor
    /// is never used alone; a matching section is capacitors and the lines between them. One Metal1
    /// shape carries both bottom plates AND the line, and the two plate connections are two regions
    /// on ONE stackup via entry.
    /// </summary>
    private static List<LayoutShape> TwoCapacitorsOnALine() =>
    [
        Rect(Metal2,   0, 22, 28, 28),          // feed in
        Rect(MimVia,   22, 22, 28, 28),
        Rect(MimMetal, 20, 20, 30, 30),         // cap A top plate
        Rect(Nitride,  20, 20, 30, 30),
        Rect(Metal1,   20, 20, 70, 30),         // both bottom plates and the line joining them
        Rect(MimMetal, 60, 20, 70, 30),         // cap B top plate
        Rect(Nitride,  60, 20, 70, 30),
        Rect(MimVia,   62, 22, 68, 28),
        Rect(Metal2,   62, 22, 100, 28),        // feed out
        Port(Metal2,   0, 25, "P1"),
        Port(Metal2, 100, 25, "P2"),
    ];

    private static PlanarExtractionResult Extract(List<LayoutShape> shapes, double fHz = 20e9)
        => PlanarExtractor.Extract(shapes, StarterTechnologies.MmicGaAs(), Dbu, fHz);

    /// <summary>
    /// <b>Today's plain starter, DERIVED from the shipped one rather than restated.</b> MIM-7's
    /// bit-identity gate needs the pre-module stack to compare against, and a hand-written copy of
    /// it would be a second representation that drifts — the same objection that keeps every fixture
    /// in this file built from the real <c>Technology</c>. Removing the module is exactly the four
    /// edits MIM-2 made in reverse: the two drawing layers, the three stackup entries, the air gap
    /// they were paid for out of, and MIM-6's sheet-surface choice on Metal1.
    /// </summary>
    private static Technology WithoutTheMimModule()
    {
        var tech = StarterTechnologies.MmicGaAs();
        tech.Layers.RemoveAll(l => l.Name.StartsWith("MIM", StringComparison.Ordinal));
        tech.Stackup.Layers.RemoveAll(l => l.Name.StartsWith("MIM", StringComparison.Ordinal));
        tech.Stackup.Layers.Single(l => l.Name == "Air").ThicknessDbu = 3 * Dbu;
        tech.Stackup.Layers.Single(l => l.Name == "Metal1").SheetAt = null;
        return tech;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 1 — the technology itself
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The three new stackup entries, and the one existing value that had to move with them.
    ///
    /// <para><b>The air gap pays for the addition.</b> Metal2 sat 3 µm above Metal1 and still does:
    /// the 0.25 µm plate metal and the 0.2 µm dielectric under it come out of the air, not out of
    /// the airbridge's height. Asserting the SUM rather than the air thickness alone is what makes
    /// that a statement about the structure instead of about one number.</para>
    /// </summary>
    [Fact]
    public void TheStarterCarriesAPlateConductor_AThinDielectric_AndAPlateVia()
    {
        var tech = StarterTechnologies.MmicGaAs();

        var plate = Assert.Single(tech.Stackup.Layers, l => l.Name == "MIM Metal");
        Assert.Equal(StackupKind.Conductor, plate.Kind);
        Assert.Equal(250, plate.ThicknessDbu);                       // 0.25 µm
        Assert.Equal(4.1e7, plate.SigmaSm);
        Assert.False(plate.IsGroundReference);
        Assert.Equal(MimMetal, Assert.Single(plate.DrawingLayers));

        var thin = Assert.Single(tech.Stackup.Layers, l => l.Name == "MIM Dielectric");
        Assert.Equal(StackupKind.Dielectric, thin.Kind);
        Assert.Equal(200, thin.ThicknessDbu);                        // 0.2 µm
        Assert.Equal(6.8, thin.Epsr, 6);
        Assert.Equal(0.001, thin.TanD, 6);

        // A stackup dielectric is never drawn — it is laterally infinite by the 2.5D premise, so it
        // has no artwork at all. The starter's "Cap Dielectric" and "Nitride" DRAWING layers are a
        // different kind of thing and stay unbound, which is why the entry is not called either.
        Assert.Empty(thin.DrawingLayers);
        Assert.DoesNotContain(tech.Stackup.Layers, l => l.Name == "Cap Dielectric");
        Assert.Contains(tech.Layers, l => l.Name == "Cap Dielectric");
        Assert.Contains(tech.Layers, l => l.Name == "Nitride");
        Assert.DoesNotContain(tech.Stackup.Layers,
            l => l.DrawingLayers.Contains(new LayerKey(5, 0)) || l.DrawingLayers.Contains(new LayerKey(6, 0)));

        var via = Assert.Single(tech.Stackup.Layers, l => l.Name == "MIM Via");
        Assert.Equal(StackupKind.Via, via.Kind);
        Assert.Equal(ViaFillKind.Solid, via.Fill);
        Assert.Equal("MIM Metal", via.SpanFromLayer);
        Assert.Equal("Metal2", via.SpanToLayer);
        Assert.Equal(MimVia, Assert.Single(via.DrawingLayers));

        // MIM-6 — the field that makes the capacitor solve at the separation the process states.
        // Metal1's analysis sheet is on the TOP of its band, so the gap between the plate sheets is
        // the MIM Dielectric alone. Every other entry says nothing, which means Bottom.
        var metal1 = Assert.Single(tech.Stackup.Layers, l => l.Name == "Metal1");
        Assert.Equal(ConductorSheetSurface.Top, metal1.SheetAt);
        Assert.All(tech.Stackup.Layers.Where(l => l.Name != "Metal1"), l => Assert.Null(l.SheetAt));

        // MIM-7 — the ONE field that lets all of the above live on the technology every MMIC
        // workspace copies. The film is patterned: it exists under its mask and nowhere else, so a
        // run whose layout draws none carries air in its place and puts Metal1's sheet back on the
        // bottom of its band. Exactly one entry carries a tie.
        //
        // MIM-11 — and it names the NITRIDE MASK, which is the thing that actually defines where the
        // film is; "MIM Metal" was a proxy for it. The conductor spelling still resolves, so an
        // older workspace's own copy of this stackup keeps working.
        Assert.Equal("Nitride", thin.PresentWithLayer);
        Assert.All(tech.Stackup.Layers.Where(l => l.Name != "MIM Dielectric"),
                   l => Assert.Null(l.PresentWithLayer));

        // Metal2 still sits exactly 3 µm above Metal1: 2.55 air + 0.25 plate + 0.2 dielectric.
        var air = Assert.Single(tech.Stackup.Layers, l => l.Name == "Air");
        Assert.Equal(2550, air.ThicknessDbu);
        Assert.Equal(3000, air.ThicknessDbu + plate.ThicknessDbu + thin.ThicknessDbu);

        Assert.Empty(TechValidation.Validate(tech));
    }

    /// <summary>
    /// <b>The two representations of this technology must not drift.</b> The authored
    /// <c>.ctech</c> is the artifact that ships and that a new workspace copies;
    /// <see cref="StarterTechnologies.MmicGaAs"/> is the one every test builds on. They were in step
    /// before MIM-2 and are asserted to be in step after it — field by field, not by count.
    ///
    /// <para><b>The one deliberate difference is <c>Name</c></b>: the file carries the full
    /// "MMIC GaAs (2 Layer Metal + MIM, 100um)" a picker shows, the code carries the short
    /// "MMIC GaAs".</para>
    /// </summary>
    [Fact]
    public void TheAuthoredCtechAndTheInCodeStarter_StateTheSameTechnology()
    {
        var code = StarterTechnologies.MmicGaAs();
        var file = ShippedTechnologies.Load("mmic-GaAs_2LM_100um");

        Assert.Equal(code.DefaultDisplayUnit,    file.DefaultDisplayUnit);
        Assert.Equal(code.DefaultSnapDbu,        file.DefaultSnapDbu);
        Assert.Equal(code.DefaultFlattenTolDbu,  file.DefaultFlattenTolDbu);
        Assert.Equal(code.DefaultLabelHeightDbu, file.DefaultLabelHeightDbu);
        Assert.Equal(code.DefaultViaPadDbu,      file.DefaultViaPadDbu);
        Assert.Equal(code.DefaultViaDrillDbu,    file.DefaultViaDrillDbu);

        Assert.Equal(code.Layers.Count, file.Layers.Count);
        foreach (var (a, b) in code.Layers.Zip(file.Layers))
        {
            Assert.Equal(a.Key, b.Key);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.ZOrder, b.ZOrder);
            Assert.Equal((a.Color.R, a.Color.G, a.Color.B, a.Color.A), (b.Color.R, b.Color.G, b.Color.B, b.Color.A));
            Assert.Equal(a.FillOpacity, b.FillOpacity, 6);
        }

        Assert.Equal(code.Stackup.Top,    file.Stackup.Top);
        Assert.Equal(code.Stackup.Bottom, file.Stackup.Bottom);
        Assert.Equal(code.Stackup.Layers.Count, file.Stackup.Layers.Count);
        foreach (var (a, b) in code.Stackup.Layers.Zip(file.Stackup.Layers))
        {
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.ThicknessDbu, b.ThicknessDbu);
            Assert.Equal(a.Epsr, b.Epsr, 9);
            Assert.Equal(a.TanD, b.TanD, 9);
            Assert.Equal(a.SigmaSm, b.SigmaSm, 3);
            Assert.Equal(a.IsGroundReference, b.IsGroundReference);
            Assert.Equal(a.SheetAt, b.SheetAt);
            Assert.Equal(a.PresentWithLayer, b.PresentWithLayer);
            Assert.Equal(a.Fill, b.Fill);
            Assert.Equal(a.WallThicknessDbu, b.WallThicknessDbu);
            Assert.Equal(a.SpanFromLayer, b.SpanFromLayer);
            Assert.Equal(a.SpanToLayer, b.SpanToLayer);
            Assert.Equal(a.DrawingLayers, b.DrawingLayers);
        }

        Assert.Equal(code.DrcRules.Count, file.DrcRules.Count);
        foreach (var (a, b) in code.DrcRules.Zip(file.DrcRules))
        {
            Assert.Equal(a.Name, b.Name);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Layer, b.Layer);
            Assert.Equal(a.ValueDbu, b.ValueDbu);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 2 — the three extraction fixtures
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The three levels, and the z the extractor puts them at.</b> Stated in absolute µm above
    /// the ground plane rather than as differences, because the whole modelling claim of a MIM
    /// capacitor is where its two plates are.
    ///
    /// <para><b>Re-pointed at MIM-6.</b> These numbers were 100 / 103.2 / 106 µm and were MIM-2's
    /// finding 1: a level's sheet sat at the BOTTOM of its conductor band, so Metal1's own 3 µm of
    /// metal landed inside the plate gap and a 0.2 µm process separation solved as 3.2 µm — 16x.
    /// The MIM technology now puts Metal1's sheet on the TOP of its band and absorbs that band into
    /// the GaAs below, so the levels are 103 / 103.2 / 106 and the region between the plates is the
    /// capacitor dielectric alone. The stated cost is the region UNDER Metal1: 103 µm of GaAs rather
    /// than 100, ~3%, asserted below so it stays a deliberate number rather than a surprise.</para>
    /// </summary>
    [Fact]
    public void AMimCapacitorExtractsAsThreeLevels_Metal1_MimMetal_Metal2()
    {
        var r = Extract(SeriesCapacitor());
        Assert.True(r.Ok, r.Refusal);
        var p = r.Problem!;

        Assert.Equal(["Metal1", "MIM Metal", "Metal2"], p.Layers.Select(l => l.Name));
        Assert.Equal(103.0,   p.LevelZ(0) * 1e6, 6);
        Assert.Equal(103.2,   p.LevelZ(1) * 1e6, 6);
        Assert.Equal(106.0,   p.LevelZ(2) * 1e6, 6);

        // The lowest level is on the slab top, which is what keeps a Metal1-fed structure inside the
        // de-embedding's own domain (a Metal2-fed one is MIM-4's business).
        Assert.True(p.LevelIsOnSlabTop(0));
        Assert.True(p.RequiresGeneralKernel);
        Assert.True(p.CanSolve().Ok, p.CanSolve().Reason);
        Assert.True(new PlanarKernel().CanSolve(p).Ok, new PlanarKernel().CanSolve(p).Reason);

        // The medium between the plates IS the capacitor dielectric and nothing else: 0.2 µm at
        // εr 6.8, the number the process states.
        var stack = p.EffectiveStack;
        var between = stack.Layers.Single(l => Math.Abs(l.Material.EpsR - 6.8) < 1e-9);
        Assert.Equal(0.2e-6, between.ThicknessM, 12);

        // …and the 3 µm of Metal1 went the other way — into the GaAs, which now reaches 103 µm.
        // That is the whole of MIM-6's cost, and it is here rather than in a comment.
        Assert.Equal(103e-6, stack.Layers[0].ThicknessM, 12);
        Assert.Equal(12.9, stack.Layers[0].Material.EpsR, 9);
        Assert.Equal(103e-6, p.Slab.HeightM, 12);

        // The run's own notes name the surface each sheet sits on, so a level at 103 µm on a band
        // that runs 100-103 reads as a choice rather than as an error.
        Assert.Contains(r.Notes, n => n.Contains("103 µm (top of 'Metal1')", StringComparison.Ordinal));

        output.WriteLine("series MIM: levels at " +
            string.Join(", ", Enumerable.Range(0, p.Layers.Count).Select(i => $"{p.LevelZ(i) * 1e6:G6} µm")) +
            $"; medium {stack}");
    }

    /// <summary>
    /// <b>MIM-7's other identity: a CAPACITOR run on the merged technology is the run the retired
    /// second technology produced, number for number.</b>
    ///
    /// <para>The airbridge gate above pins the deactivated side against the module-free stack; this
    /// pins the ACTIVE side against what <c>mmic-GaAs_2LM_100um_MIM</c> extracted on 2026-08-30,
    /// captured from a run BEFORE the two technologies were merged. Written as literals rather than
    /// as a comparison because the object it compares against no longer exists — which is exactly
    /// why the numbers had to be taken first.</para>
    /// </summary>
    [Fact]
    public void TheCapacitorRun_IsWhatTheRetiredSecondTechnologyProduced()
    {
        var r = Extract(SeriesCapacitor());
        Assert.True(r.Ok, r.Refusal);
        var p = r.Problem!;

        Assert.Equal(["Metal1", "MIM Metal", "Metal2"], p.Layers.Select(l => l.Name));
        Assert.Equal(103.0e-6,  p.LevelZ(0), 15);
        Assert.Equal(103.2e-6,  p.LevelZ(1), 15);
        Assert.Equal(106.0e-6,  p.LevelZ(2), 15);
        Assert.Equal([3e-6, 0.25e-6, 3e-6], p.Layers.Select(l => Math.Round(l.ThicknessM, 12)));
        Assert.All(p.Layers, l => Assert.Equal(4.1e7, l.SigmaSm, 3));

        Assert.Equal(103e-6, p.Slab.HeightM, 15);
        Assert.Equal(12.9,   p.Slab.Material.EpsR, 12);
        Assert.Equal(0.0006, p.Slab.Material.TanD, 12);

        var stack = p.EffectiveStack;
        Assert.Equal(3, stack.LayerCount);
        Assert.Equal([103e-6, 0.2e-6, 2.8e-6],
                     stack.Layers.Select(l => Math.Round(l.ThicknessM, 12)));
        Assert.Equal([12.9, 6.8, 1.0],    stack.Layers.Select(l => l.Material.EpsR));
        Assert.Equal([0.0006, 0.001, 0.0], stack.Layers.Select(l => l.Material.TanD));

        var via = Assert.Single(p.ViaList);
        Assert.Equal(1, via.LowerLayerIndex);
        Assert.Equal(2, via.UpperLayerIndex);
        Assert.Equal(3.6e-11, Assert.Single(via.Polygons).Area(), 15);

        Assert.True(p.RequiresGeneralKernel);
        Assert.True(p.LevelIsOnSlabTop(0));
        Assert.True(new PlanarKernel().CanSolve(p).Ok);
        Assert.DoesNotContain(r.Notes, n => n.Contains("is a patterned thin film", StringComparison.Ordinal));
    }

    /// <summary>The plate connection is a drawn REGION on a via entry (MIM-1), meshed at the outline
    /// it was drawn — not squared, and not a point via.</summary>
    [Fact]
    public void ThePlateConnection_IsARegionViaBetweenMimMetalAndMetal2()
    {
        var r = Extract(SeriesCapacitor());
        Assert.True(r.Ok, r.Refusal);

        var via = Assert.Single(r.Problem!.ViaList);
        Assert.Equal(1, via.LowerLayerIndex);           // MIM Metal
        Assert.Equal(2, via.UpperLayerIndex);           // Metal2
        Assert.False(via.ToGround);

        var poly = Assert.Single(via.Polygons);
        Assert.Equal(6e-6 * 6e-6, poly.Area(), 15);     // the 22…28 µm square, as drawn

        Assert.Contains(r.Notes, n => n.Contains("drawn region(s) became the footprints", StringComparison.Ordinal));
        Assert.DoesNotContain(r.Notes, n => n.Contains("EQUAL-AREA", StringComparison.Ordinal));
    }

    /// <summary>The shunt form's bottom plate reaches the ground plane through a backside via
    /// region, which becomes the ground ATTACHMENT basis rather than a level-to-level via.</summary>
    [Fact]
    public void TheShuntForm_GroundsItsBottomPlateThroughABacksideViaRegion()
    {
        var r = Extract(ShuntCapacitor());
        Assert.True(r.Ok, r.Refusal);
        var p = r.Problem!;

        Assert.Equal(["Metal1", "MIM Metal", "Metal2"], p.Layers.Select(l => l.Name));
        Assert.Equal(2, p.ViaList.Count);

        var ground = Assert.Single(p.ViaList, v => v.ToGround);
        Assert.Equal(PlanarVia.GroundTerminal, ground.LowerLayerIndex);
        Assert.Equal(0, ground.UpperLayerIndex);                 // it lands on Metal1
        Assert.Equal(10e-6 * 10e-6, Assert.Single(ground.Polygons).Area(), 15);

        var plateVia = Assert.Single(p.ViaList, v => !v.ToGround);
        Assert.Equal(1, plateVia.LowerLayerIndex);
        Assert.Equal(2, plateVia.UpperLayerIndex);

        Assert.Contains(r.Notes, n => n.Contains("BACKSIDE vias", StringComparison.Ordinal));

        // …and both reach the mesh as real vertical unknowns rather than vanishing.
        var mesh = SurfaceMesher.Mesh(p);
        Assert.True(mesh.CanSolve, mesh.Refusal);
        Assert.True(mesh.ViaUnknownCount >= 2, $"{mesh.ViaUnknownCount} vertical unknown(s)");
    }

    /// <summary>
    /// <b>The MMIC acceptance shape, and the fact it exists to check: nothing in the extractor is
    /// per-capacitor.</b> Two capacitors joined by a Metal1 line produce the same three levels, and
    /// their two plate connections group into ONE <c>PlanarVia</c> carrying two footprints — because
    /// grouping is by stackup ENTRY, which is what stops two overlapping drawn regions from doubling
    /// the metal in the cell they share (MIM-1). A per-capacitor extractor would produce two.
    /// </summary>
    [Fact]
    public void TwoCapacitorsJoinedByALine_NeedNoPerCapacitorMachinery()
    {
        var r = Extract(TwoCapacitorsOnALine());
        Assert.True(r.Ok, r.Refusal);
        var p = r.Problem!;

        Assert.Equal(["Metal1", "MIM Metal", "Metal2"], p.Layers.Select(l => l.Name));
        Assert.Equal(2, p.Layers[1].Polygons.Count);            // two top plates
        Assert.Single(p.Layers[0].Polygons);                    // one Metal1 shape: plates AND line
        Assert.Equal(2, p.Layers[2].Polygons.Count);            // two Metal2 feeds

        var via = Assert.Single(p.ViaList);                     // ONE via entry, TWO footprints
        Assert.Equal(2, via.Polygons.Count);
        Assert.Equal(1, via.LowerLayerIndex);
        Assert.Equal(2, via.UpperLayerIndex);
        Assert.Contains(r.Notes, n => n.Contains("2 drawn region(s) became the footprints of 1 via",
                                                 StringComparison.Ordinal));

        Assert.True(p.CanSolve().Ok, p.CanSolve().Reason);
        var mesh = SurfaceMesher.Mesh(p);
        Assert.True(mesh.CanSolve, mesh.Refusal);
        output.WriteLine($"two-cap acceptance shape: N = {mesh.UnknownCount} " +
                         $"({mesh.ViaUnknownCount} vertical) over {p.Layers.Count} levels");
    }

    /// <summary>
    /// <b>An airbridge post and a capacitor plate in ONE run — the case that had no legal setting.</b>
    ///
    /// <para>Adding MIM Metal to the level list is what puts a level BETWEEN Metal1 and Metal2, so
    /// the Metal1-Metal2 Post spans levels 0 and 2. It used to be dropped, and this file used to
    /// assert that drop and call the two-level list an escape hatch. It is not one: the shipped
    /// spiral's underpass is two such posts and the shipped MIM capacitor draws one of its own, so
    /// on this technology the choice was a capacitor with no inductor or an inductor with no
    /// capacitor. A user's L-C resonator solved as a flat open either way.</para>
    ///
    /// <para>The barrel is now built as a CHAIN — one vertical basis per gap, with the post's own
    /// cross-section carried onto each level it passes through, which is what a post passing through
    /// that height physically is. Three via records here: the plate via, and the post's two halves.
    /// No warning, because nothing was dropped.</para>
    /// </summary>
    [Fact]
    public void AnAirbridgePost_ChainsThroughMimMetalRatherThanBeingDropped()
    {
        var shapes = SeriesCapacitor();
        shapes.Add(Rect(Post, 40, 22, 46, 28));
        shapes.Add(Rect(Metal1, 38, 20, 48, 30));       // something for the post to stand on

        var r = Extract(shapes);
        Assert.True(r.Ok, r.Refusal);

        Assert.Equal(3, r.Problem!.Layers.Count);

        // The plate via (MIM Metal -> Metal2) plus the post's two gaps.
        Assert.Equal(3, r.Problem!.ViaList.Count);
        Assert.Equal(2, r.Problem!.ViaList.Count(v => v.LowerLayerIndex == 1 && v.UpperLayerIndex == 2));
        Assert.Single(r.Problem!.ViaList, v => v.LowerLayerIndex == 0 && v.UpperLayerIndex == 1);

        // The intervening level carries the barrel's footprint: the top plate, plus the post.
        var mim = r.Problem!.Layers[1];
        Assert.Equal("MIM Metal", mim.Name);
        Assert.Equal(2, mim.Polygons.Count);

        Assert.Empty(r.Warnings);
        Assert.Contains(r.Notes, n => n.Contains("built as a CHAIN", StringComparison.Ordinal));
    }

    /// <summary>…and naming only Metal1 and Metal2 still gives the post a single basis, unchanged —
    /// the two-level path is the one every existing gate runs on and the chain must not reach it.
    /// The capacitor plate is not in that run, which is now a choice rather than the only option.
    /// </summary>
    [Fact]
    public void NamingOnlyTheInterconnectLevels_KeepsTheAirbridgePostOnOneBasis()
    {
        var shapes = SeriesCapacitor();
        shapes.Add(Rect(Post, 40, 22, 46, 28));
        shapes.Add(Rect(Metal1, 38, 20, 48, 30));

        var r = PlanarExtractor.Extract(shapes, StarterTechnologies.MmicGaAs(), Dbu, 20e9,
            new EmExtractionSettings(AnalysisLevelNames: ["Metal1", "Metal2"]));
        Assert.True(r.Ok, r.Refusal);

        Assert.Equal(["Metal1", "Metal2"], r.Problem!.Layers.Select(l => l.Name));
        var post = Assert.Single(r.Problem!.ViaList);
        Assert.Equal(0, post.LowerLayerIndex);
        Assert.Equal(1, post.UpperLayerIndex);
        Assert.DoesNotContain(r.Notes, n => n.Contains("not ADJACENT in the analysis", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>EM-SEV — the run the user actually had: the plate level left out, and the capacitor gone
    /// with it.</b>
    ///
    /// <para>This is the same artwork and the same technology as
    /// <see cref="NamingOnlyTheInterconnectLevels_RestoresTheAirbridgePost"/>, which asserts that the
    /// escape hatch works. It does — and what it COSTS is asserted here, because until EM-SEV the
    /// cost was reported as three sentences of thirty-five, at the weight of the core count, and a
    /// user ran it for eleven minutes and got a flat open across the band with no idea why.</para>
    ///
    /// <para><b>Three mechanisms remove the capacitor and all three are warnings now</b>
    /// (R-emsev-1/2/3): the plate's artwork is not meshed, the plate via that joins it to Metal2 is
    /// discarded, and the patterned film deactivates because its plate is not in the run. The three
    /// are independent — none is derivable from the others — so all three are checked.</para>
    ///
    /// <para><b>The structure is severed, and that is shown from the extracted problem rather than
    /// from a solve.</b> A raw solve of this shape reads −46 dB, which is an argument about a number;
    /// what the extraction says is categorical: the two analysis levels are joined by NO via at all,
    /// and the dielectric that would have coupled them is air. That is what "electrically separate"
    /// means here, it costs milliseconds, and a solve could not make it stronger.</para>
    /// </summary>
    [Fact]
    public void LeavingThePlateLevelOut_WarnsTwice_AndSeversTheCapacitor()
    {
        var r = PlanarExtractor.Extract(
            SeriesCapacitor(), StarterTechnologies.MmicGaAs(), Dbu, 20e9,
            new EmExtractionSettings(AnalysisLevelNames: ["Metal1", "Metal2"]));

        Assert.True(r.Ok, r.Refusal);
        foreach (var w in r.Warnings) output.WriteLine("WARNING  " + w);

        // 1 — the plate's artwork is drawn and not solved, and the warning NAMES the level.
        Assert.Contains(r.Warnings, w =>
            w.Contains("carry artwork but are NOT in this", StringComparison.Ordinal) &&
            w.Contains("'MIM Metal'", StringComparison.Ordinal));

        // 2 — the via the designer drew between the plate and Metal2 is discarded, and the warning
        //     names the conductor it lands on. (This is the `wrongGround` arm rather than the
        //     `unknownLevels` one: the plate IS a stackup conductor, it is simply neither an
        //     analysis level nor the ground reference. R-emsev-2 moved all three arms together
        //     precisely because which one fires is an accident of the stackup, not of the defect.)
        Assert.Contains(r.Warnings, w =>
            w.Contains("via shape(s) span a conductor (MIM Metal)", StringComparison.Ordinal) &&
            w.Contains("NOT in this answer", StringComparison.Ordinal));

        // 3 — MIM-11 REMOVED THE THIRD WARNING, and the trade is worth stating rather than
        //     quietly losing. While the film was tied to the PLATE CONDUCTOR, leaving that
        //     conductor out of the levels also took the film out of the medium, and that produced a
        //     third sentence about the capacitor. The tie now names the nitride MASK, which is the
        //     true statement of where a thin film is — and this layout draws nitride, so the film is
        //     in the medium whatever the level list says. That is correct: excluding a level from a
        //     run does not etch the wafer. The capacitor is still reported as missing, twice, by the
        //     two warnings above, and both name the level to put back.
        Assert.DoesNotContain(r.Warnings, w => w.Contains("is a patterned thin film", StringComparison.Ordinal));
        Assert.Contains(r.Notes, n =>
            n.Contains("CARRIES the patterned thin film", StringComparison.Ordinal) &&
            n.Contains("'Nitride' artwork covers", StringComparison.Ordinal));

        // The structure, not the prose: nothing joins Metal1 to Metal2.
        Assert.Equal(["Metal1", "Metal2"], r.Problem!.Layers.Select(l => l.Name));
        Assert.Empty(r.Problem!.ViaList);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 3 — one raw solve, as a WIRING gate
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The most compact series capacitor that still meshes sanely: every conductor is 10 µm
    /// wide, so the mesher's own "at least 4 cells across the narrowest conductor" gives 2.5 µm cells
    /// rather than the sub-micron ones a 6 µm feed would force. Solve cost is O(N²) in a kernel that
    /// costs milliseconds per pair, so the fixture's width is the whole cost.</summary>
    private static List<LayoutShape> CompactSeriesCapacitor() =>
    [
        Rect(Metal1,    0, 0, 20, 10),          // feed in, continuous with the bottom plate
        Rect(MimMetal, 10, 0, 20, 10),          // top plate, 10 x 10 µm
        Rect(Nitride,  10, 0, 20, 10),
        Rect(MimVia,   10, 0, 20, 10),          // the plate connection, as large as the plate
        Rect(Metal2,   10, 0, 30, 10),          // feed out
        Port(Metal1,  0, 5, "P1"),
        Port(Metal2, 30, 5, "P2"),
    ];

    /// <summary>
    /// <b>The raw solve runs; what comes back is passive, reciprocal, capacitive — and its
    /// transmission must COLLAPSE when the plate via is deleted.</b>
    /// De-embedding is OFF on purpose: port 2 is on Metal2, which is not the slab top, and a
    /// de-embedded port off the slab top is MIM-4's business, not this brief's.
    ///
    /// <para><b>It deliberately carries NO magnitude band</b>, and the finding-2 retraction (foot of
    /// this file) sharpened the original reason: a raw solve's numbers are dominated by each port's
    /// own ~0.3 fF discontinuity, so any band on them — the brief's own against the 0.2 um
    /// dielectric, one against the 3.2 um the extractor modelled before MIM-6 closed finding 1, or
    /// one around the value measured — would gate the port, not the capacitor. The original "connected" assertions
    /// (C &gt; 0 and |S21| &gt; 1e-6) had the same disease, measured directly on 2026-08-30: both
    /// PASS with the plate via deleted, because the port discontinuity is itself a small positive
    /// capacitance and a broadside leak. The gate is therefore the L9-gate comparison shape —
    /// |S21| with the plate via against |S21| of the same artwork without it — which the
    /// discontinuity cannot fake, because it is common to both runs.</para>
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void ARawSolveOfASeriesMim_IsPassive_Reciprocal_AndCapacitive()
    {
        const double f = 20e9;
        var shapes = CompactSeriesCapacitor();

        var r = Extract(shapes, f);
        Assert.True(r.Ok, r.Refusal);
        var problem = r.Problem!;
        Assert.Equal(3, problem.Layers.Count);
        Assert.Single(problem.ViaList);

        var ports = EmPortExtraction.Extract(shapes, problem, Dbu);
        Assert.True(ports.Ok, ports.Refusal);
        Assert.Equal(2, ports.Ports.Count);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var solved = new PlanarKernel().Solve(
            problem, new PlanarMeshSettings(Auto: false, CellsPerWavelength: 6, EdgeMesh: false),
            ports.Ports, [f], PlanarSolveSettings.Default with { Deembed = false });
        var s = solved.Solve.Points[0].RawS;

        // ── Reciprocity. A structure of nothing but isotropic dielectric and metal is reciprocal;
        //    if it is not, the fill or the excitation is wrong, and no accuracy question is involved.
        Assert.Equal(s[0, 1].Real,      s[1, 0].Real,      9);
        Assert.Equal(s[0, 1].Imaginary, s[1, 0].Imaginary, 9);

        // ── Passivity, as the largest singular value of S — i.e. √λmax of SᴴS, in closed form for a
        //    2 x 2 Hermitian matrix rather than through a decomposition.
        double h11 = Sq(s[0, 0]) + Sq(s[1, 0]);
        double h22 = Sq(s[0, 1]) + Sq(s[1, 1]);
        var    h12 = Complex.Conjugate(s[0, 0]) * s[0, 1] + Complex.Conjugate(s[1, 0]) * s[1, 1];
        double tr  = h11 + h22;
        double det = h11 * h22 - Sq(h12);
        double sigmaMax = Math.Sqrt(0.5 * (tr + Math.Sqrt(Math.Max(0, tr * tr - 4 * det))));
        Assert.True(sigmaMax <= 1 + 1e-9, $"largest singular value of S is {sigmaMax:G8} — not passive");

        // ── Y = (I - S)(I + S)⁻¹ / Z0, by hand for a 2 x 2.
        Complex a11 = 1 - s[0, 0], a12 = -s[0, 1], a21 = -s[1, 0], a22 = 1 - s[1, 1];
        Complex b11 = 1 + s[0, 0], b12 =  s[0, 1], b21 =  s[1, 0], b22 = 1 + s[1, 1];
        Complex bDet = b11 * b22 - b12 * b21;
        Complex y21 = (a21 * b22 - a22 * b21) / bDet / 50.0;

        // A series capacitor's transfer admittance is -jωC, so C is read off Im(Y21) with a sign.
        double cF = -y21.Imaginary / (2 * Math.PI * f);
        double area = 10e-6 * 10e-6;
        // MIM-6: the extractor's geometry and the process data are now the SAME separation, so
        // there is one parallel-plate reference to print rather than two. It is a reference, not a
        // gate — whether the numerics survive a 0.2 µm gap against micron cells is MIM-3's ladder.
        double parallelPlate = 8.8541878128e-12 * 6.8 * area / 0.2e-6;

        output.WriteLine(
            $"series MIM raw solve: N = {solved.Solve.UnknownCount}, {sw.ElapsedMilliseconds} ms, " +
            $"S21 = {s[1, 0]}, C from Im(Y21) = {cF * 1e15:G5} fF; " +
            $"ε₀εᵣA/d = {parallelPlate * 1e15:G5} fF at the 0.2 µm dielectric the extractor now " +
            $"models (ratio {cF / parallelPlate:G4}).");

        Assert.True(cF > 0, $"the series path is not capacitive: C = {cF * 1e15:G5} fF");

        // ── The wiring gate: the plate via must DOMINATE the broadside leak ──────────────────
        //
        // Same artwork, plate via deleted: all that is left is the capacitive leak from feed to
        // feed, plus both ports' own discontinuities — which are common to the two runs and so
        // cancel out of the comparison. This is the L9 phase gate's own shape (HISTORY.md §L9,
        // "the vias carry the current"), scaled to the capacitor. A dropped via, a dropped level or
        // a footprint that misses the plate makes the two runs equal, and that is the regression
        // this exists to catch — the retracted C > 0 / |S21| > 1e-6 pair caught none of them.
        var noVia = CompactSeriesCapacitor();
        noVia.RemoveAll(sh => sh is RectShape rect && rect.Layer == MimVia);
        var rNo = Extract(noVia, f);
        Assert.True(rNo.Ok, rNo.Refusal);
        Assert.Empty(rNo.Problem!.ViaList);
        var portsNo = EmPortExtraction.Extract(noVia, rNo.Problem!, Dbu);
        Assert.True(portsNo.Ok, portsNo.Refusal);
        var solvedNo = new PlanarKernel().Solve(
            rNo.Problem!, new PlanarMeshSettings(Auto: false, CellsPerWavelength: 6, EdgeMesh: false),
            portsNo.Ports, [f], PlanarSolveSettings.Default with { Deembed = false });

        double s21With    = Complex.Abs(s[1, 0]);
        double s21Without = Complex.Abs(solvedNo.Solve.Points[0].RawS[1, 0]);
        output.WriteLine($"plate-via comparison: |S21| with = {s21With:E3}, without = {s21Without:E3}, " +
                         $"ratio = {s21With / s21Without:F2} (N = {solvedNo.Solve.UnknownCount} without)");

        Assert.True(s21With > 1.5 * s21Without,
            $"|S21| with the plate via ({s21With:E3}) does not dominate the via-less broadside leak " +
            $"({s21Without:E3}). The via is the only conducting path onto the top plate — if these are " +
            "comparable, the vertical basis is carrying no current or the via never reached the mesh.");
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MIM-8 — the end-to-end gate: the capacitor a user draws returns its capacitance
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>The README's own "60 µm capacitor" — 60 × 60 µm of top plate at the shipped 0.2 µm,
    /// which is 1.0838 pF, on a bottom plate grown by the enclosure, with the plate's post and its
    /// Metal2 strap. The strap leaves NORTHWARD across the plate edge, which is the escape
    /// <c>KIT_MIMCAP</c> itself uses.</summary>
    private static List<LayoutShape> SixtyMicronSeriesCapacitor() =>
    [
        Rect(Metal1,    0,  0, 64, 64),        // bottom plate, 2 µm of enclosure all round
        Rect(MimMetal,  2,  2, 62, 62),        // top plate — W x L, and the plate that sets C
        Rect(Nitride,   2,  2, 62, 62),
        Rect(MimVia,   25, 25, 39, 39),        // the post up to Metal2
        Rect(Metal2,   25, 25, 39, 90),        // the strap that carries the top plate away
    ];

    [Fact]
    public void MIM8_TheShippedSizeCapacitor_ReturnsItsCapacitance_AndResonatesWithTheSpiral()
    {
        // THE GATE THIS BRIEF EXISTS FOR. The artwork is the shipped technology's own capacitor at
        // the size a 1.0838 pF part is drawn at, extracted by the extractor Simulate calls and
        // filled by the fill a solve uses.
        //
        // WHY THE INSTRUMENT IS ELECTROSTATIC AND NOT A DE-EMBEDDED TWO-PORT. §MIM-3 finding 4
        // measured that a raw S reading in this engine is the PORT and not the structure — a
        // matched 50 Ω line reads |S21| = 0.0706 — and its de-embedded instrument had to truncate
        // the stack at the upper plate to get a port at all, which is not this technology. What the
        // user's run got wrong is a CAPACITANCE, so the gate reads a capacitance, off the ω → 0
        // potential-coefficient matrix the fill itself builds. There is no port in it to be wrong.
        var r = Extract(SixtyMicronSeriesCapacitor(), 10e9);
        Assert.True(r.Ok, r.Refusal);
        var problem = r.Problem!;
        Assert.Equal(["Metal1", "MIM Metal", "Metal2"], problem.Layers.Select(l => l.Name));

        var (after, cell, n) = TerminalCapacitance(problem, 0.5);
        var (before, _, _)   = TerminalCapacitance(problem, 0.0);

        const double closed = 8.8541878128e-12 * 6.8 * 60e-6 * 60e-6 / 0.2e-6;   // 1.0838 pF
        output.WriteLine($"60 µm series MIM: N = {n}, cell = {cell * 1e6:G3} µm " +
                         $"(cell/separation {cell / 0.2e-6:G4}); C = {before * 1e12:F4} pF before, " +
                         $"{after * 1e12:F4} pF after; ε₀εᵣA/d = {closed * 1e12:F4} pF " +
                         $"(ratio {before / closed:F3} -> {after / closed:F3})");

        // BEFORE: an OPEN, and with the wrong sign — which is what a user saw as a flat −46 dB
        // across the band with nothing in the report to say why.
        Assert.True(before < 0,
            $"expected the pre-MIM-8 arithmetic to report a negative capacitance here; got " +
            $"{before * 1e12:F4} pF.");

        // AFTER: within 10% of ε₀εᵣA/d. The allowance is FRINGING plus the strap: a 60 µm plate at
        // 0.2 µm carries about 2% of edge field by Palmer's correction, and the Metal2 strap adds a
        // couple of fF of its own over the bottom plate. Both are real capacitance the drawn cell
        // has, so the band is one-sided in spirit; it is written two-sided because the measured
        // excess never reaches it.
        Assert.InRange(after / closed, 0.90, 1.10);

        // ── and the same number read out where the user was looking: in series with their 3.8 nH
        //    spiral this is a resonance at 2.48 GHz, and they saw a flat open across the band. The
        //    spiral is carried at its STATED inductance because the spiral was never the part that
        //    failed, and 5% in frequency is 10% in C — the same gate, in the quantity that was
        //    reported. |S21| of L and C in series between 50 Ω ports.
        //
        //    MIM-10 (2026-09-16) MEASURED that spiral rather than stating it, and the real resonance
        //    is 2.75 GHz — the coil has no single inductance (5.8 / 3.5 / 3.1 nH at 1 / 2 / 3 GHz)
        //    and 3.8 nH is its value nowhere near the answer. That does not weaken this gate, whose
        //    subject is C: the 3.8 nH here is a CONSTANT chosen so a 10% error in C shows as 5% in
        //    frequency. It is not circuitRF's answer for that resonator —
        //    MimResonatorCompositionTests is.
        const double l = 3.8e-9;
        double best = 0, atF = 0;
        for (double f = 1.5e9; f <= 3.5e9; f += 5e6)
        {
            double w = 2 * Math.PI * f;
            var zSeries = Complex.ImaginaryOne * (w * l - 1.0 / (w * after));
            double s21 = Complex.Abs(2 * 50.0 / (2 * 50.0 + zSeries));
            if (s21 > best) { best = s21; atF = f; }
        }
        output.WriteLine($"with a 3.8 nH spiral: peak |S21| = {best:F4} at {atF / 1e9:F3} GHz " +
                         $"(design intent: ≈ 0 dB at 2.48 GHz)");

        Assert.InRange(atF / 1e9, 2.48 * 0.95, 2.48 * 1.05);
        Assert.True(best > 0.99, $"the resonance is not a through: peak |S21| = {best:F4}.");
    }

    /// <summary>
    /// The capacitance between the series capacitor's two TERMINALS — Metal1 (the feed and the
    /// bottom plate) against MIM Metal plus Metal2 (the top plate and its strap, one conductor
    /// through the post).
    ///
    /// <para><c>PlanarStaticLimitTests</c>' own instrument, on the multi-level P: hold one terminal
    /// at 1 V and the other at 0 V, solve for the charges, and report minus the charge that lands on
    /// the second. A genuine entry of the physical capacitance matrix, and it needs no port, no
    /// reference impedance and no wave.</para>
    /// </summary>
    private static (double C, double Cell, int N) TerminalCapacitance(PlanarProblem problem,
                                                                      double shallowImageCells)
    {
        var st   = PlanarFillSettings.Default with { ShallowImageCells = shallowImageCells };
        var mesh = SurfaceMesher.Mesh(problem,
                        new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: false)).Mesh;
        var cores = PlanarFill.BuildCores(mesh, st);
        var set   = new PlanarKernelSet(
                        new LayeredSpectralGreens(problem.EffectiveStack, problem.MaxFrequencyHz),
                        st.Order).For(cores);
        var p = PlanarFill.ScalarPotentialMatrix(cores, set, PlanarLevels.From(problem));

        int m = mesh.Cells.Count;
        var a   = new Mat<Complex>(m, m);
        var rhs = new Vec<Complex>(m);
        for (int i = 0; i < m; i++)
        {
            rhs[i] = mesh.Cells[i].LayerIndex == 0 ? Complex.One : Complex.Zero;
            for (int j = 0; j < m; j++) a[i, j] = p[i, j] / EmConstants.Eps0;
        }
        var q = a.Lu().Solve(rhs);

        Complex onTop = Complex.Zero;
        for (int i = 0; i < m; i++) if (mesh.Cells[i].LayerIndex != 0) onTop += q[i];
        return (-onTop.Real, mesh.Cells[0].Width, mesh.Bases.Count);
    }

    [Fact]
    public void MIM8_AnInterconnectRunWhoseMeshRESOLVESItsGap_IsFilledBitIdentically()
    {
        // MIM-7 gated the module-free stack on EXTRACTION — every number the solver reads. MIM-8
        // changes what the solver DOES with them, so the claim has to be made one layer in.
        //
        // AND IT IS A NARROWER CLAIM THAN THE BRIEF ASSUMED, which is worth stating rather than
        // arranging around. The brief expected "a run with no thin film is untouched". The acting
        // condition is not the film, it is whether the mesh resolves the fitted images — cell
        // against level separation, the same quantity MIM-3's note already reports — and an
        // AIRBRIDGE meshed coarsely is in that regime with 6 µm of air and no film anywhere. On the
        // 300 × 100 µm fixture at its own default mesh (30 µm cells, cell/separation 5) the
        // treatment fires and the cross-level block improves 8.9e-5 -> 1.7e-6. Measured, and an
        // improvement; it is simply not identity.
        //
        // What IS identity is a run the mesh resolves, and that is what this asserts: the same
        // shape drawn small, where the default four-cells-across rule puts the pitch well under the
        // 6 µm the two levels are apart.
        var shapes = new List<LayoutShape>
        {
            Rect(Metal1,  0,  0, 12, 12),
            Rect(Metal1, 20,  0, 32, 12),
            Rect(Metal2,  4,  0, 28, 12),
            Rect(Post,    4,  4,  8,  8),
            Rect(Post,   22,  4, 26,  8),
        };
        var r = PlanarExtractor.Extract(shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        Assert.True(r.Ok, r.Refusal);
        var problem = r.Problem!;

        // Two cells across rather than four, so the fill is a sixteenth of the work — this test is
        // about a bit pattern and not about accuracy, and a 6 µm pitch against a 6 µm gap is still
        // inside the regime where nothing is shallow (the ladder has the treatment firing from
        // cell/separation ≈ 2.5 up).
        var mesh = SurfaceMesher.Mesh(problem,
                        new PlanarMeshSettings(Auto: false, CellsPerWavelength: 6, EdgeMesh: false,
                                               MinCellsAcrossConductor: 2)).Mesh;

        static Mat<Complex> FillAt(PlanarProblem p, PlanarMesh mesh, double shallow)
        {
            var st    = PlanarFillSettings.Default with { ShallowImageCells = shallow };
            var cores = PlanarFill.BuildCores(mesh, st);
            var set   = new PlanarKernelSet(
                            new LayeredSpectralGreens(p.EffectiveStack, p.MaxFrequencyHz), st.Order)
                        .For(cores);
            return PlanarFill.FillMultiLevel(cores, set, PlanarLevels.From(p),
                                             2 * Math.PI * p.MaxFrequencyHz);
        }

        var off = FillAt(problem, mesh, 0.0);
        var on  = FillAt(problem, mesh, PlanarFillSettings.Default.ShallowImageCells);

        double cell = 0;
        foreach (var c in mesh.Cells) cell = Math.Max(cell, Math.Max(c.Width, c.Height));
        double separation = problem.LevelZ(1) - problem.LevelZ(0);

        int differ = 0;
        for (int i = 0; i < off.RowCount; i++)
            for (int j = 0; j < off.ColCount; j++)
                if (off[i, j] != on[i, j]) differ++;

        output.WriteLine($"airbridge on the shipped technology: N = {mesh.Bases.Count}, " +
                         $"cell {cell * 1e6:G3} µm over {separation * 1e6:G3} µm " +
                         $"(cell/separation {cell / separation:G3}), " +
                         $"{differ} of {off.RowCount * off.ColCount} entries differ");

        Assert.True(cell / separation < 2.0,
            $"this fixture is supposed to RESOLVE its own gap; cell/separation is {cell / separation:G3}.");
        Assert.Equal(0, differ);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MILESTONE 4 — what the capacitor dielectric COSTS, and why it is a separate technology
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>MIM-7's gate: the same airbridge artwork SOLVES on the one shipped technology, and the
    /// problem it extracts to is BIT-IDENTICAL to the module-free stack's.</b>
    ///
    /// <para>This test asserted the opposite from MIM-2 to MIM-7. A capacitor dielectric between
    /// Metal1 and Metal2 puts a Metal1-Metal2 post across a dielectric interface, and
    /// <c>PlanarKernel.CanSolve</c> refuses such a via by name — its closed-form z-integral is
    /// written in ONE region's asymptotic coefficients. That is a WHOLE-RUN refusal, not a dropped
    /// shape, which is why the module could not simply be added to the starter and why circuitRF
    /// shipped two MMIC technologies.</para>
    ///
    /// <para><b>The tie removes the premise rather than the refusal.</b> MIM-7 left the kernel
    /// untouched (PSV-3 later taught it to integrate a via across an interface, see
    /// <c>PsvMimPostTests</c>); there is simply no interface here, because this
    /// layout draws no "Nitride" (MIM-11's mask, and before it no "MIM Metal" plate), so the film
    /// enters the medium as air and Metal1's sheet goes back to the bottom of its band. Both
    /// spellings of the tie answer this fixture the same way, which is what makes it the identity
    /// gate for either. What that buys is
    /// asserted the strongest way available: not "it solves", but "every number the solver reads is
    /// the number the module-free stack produces".</para>
    /// </summary>
    [Fact]
    public void AnAirbridgePost_SolvesOnTheOneTechnology_AndExtractsIdenticallyToTheModuleFreeStack()
    {
        var shapes = new List<LayoutShape>
        {
            Rect(Metal1, 0,   0, 120, 100),
            Rect(Metal1, 180, 0, 300, 100),
            Rect(Metal2, 20,  0, 280, 100),
            Rect(Post,   40, 30,  80,  70),
            Rect(Post,  220, 30, 260,  70),
            Port(Metal1, 0,   50, "P1"),
            Port(Metal1, 300, 50, "P2"),
        };

        var shipped = PlanarExtractor.Extract(shapes, StarterTechnologies.MmicGaAs(), Dbu, 30e9);
        var plain   = PlanarExtractor.Extract(shapes, WithoutTheMimModule(),          Dbu, 30e9);
        Assert.True(shipped.Ok, shipped.Refusal);
        Assert.True(plain.Ok,   plain.Refusal);

        var a = shipped.Problem!;
        var b = plain.Problem!;

        // Levels: two of them, at the pre-module z's — 100 and 106 µm, NOT MIM-6's 103.
        Assert.Equal(["Metal1", "Metal2"], a.Layers.Select(l => l.Name));
        Assert.Equal(b.Layers.Select(l => l.Name), a.Layers.Select(l => l.Name));
        Assert.Equal(100.0, a.LevelZ(0) * 1e6, 12);
        Assert.Equal(106.0, a.LevelZ(1) * 1e6, 12);
        for (int i = 0; i < b.Layers.Count; i++)
        {
            Assert.Equal(b.LevelZ(i), a.LevelZ(i));                       // exact, not to a tolerance
            Assert.Equal(b.Layers[i].ThicknessM, a.Layers[i].ThicknessM);
            Assert.Equal(b.Layers[i].Polygons.Count, a.Layers[i].Polygons.Count);
        }

        // The medium: two regions, 100 µm of GaAs and 6 µm of air. The film's own 0.2 µm band and
        // the plate metal's 0.25 µm are still THERE in the stackup — they merge away because the
        // deactivated film is air and the metal band is absorbed into air on either side.
        Assert.Equal(b.EffectiveStack.LayerCount, a.EffectiveStack.LayerCount);
        for (int i = 0; i < b.EffectiveStack.LayerCount; i++)
        {
            Assert.Equal(b.EffectiveStack.Layers[i].ThicknessM,       a.EffectiveStack.Layers[i].ThicknessM);
            Assert.Equal(b.EffectiveStack.Layers[i].Material.EpsR,    a.EffectiveStack.Layers[i].Material.EpsR);
            Assert.Equal(b.EffectiveStack.Layers[i].Material.TanD,    a.EffectiveStack.Layers[i].Material.TanD);
            Assert.Equal(b.EffectiveStack.Layers[i].Material.MuR,     a.EffectiveStack.Layers[i].Material.MuR);
        }
        Assert.Equal(b.Slab.HeightM,        a.Slab.HeightM);
        Assert.Equal(b.Slab.Material.EpsR,  a.Slab.Material.EpsR);
        Assert.Equal(b.Slab.Material.TanD,  a.Slab.Material.TanD);

        // The vias: ONE PlanarVia carrying both posts' footprints — the posts are drawn REGIONS on
        // one stackup entry, and MIM-1 groups by entry.
        var postA = Assert.Single(a.ViaList);
        var postB = Assert.Single(b.ViaList);
        Assert.Equal(2, postA.Polygons.Count);
        Assert.Equal(postB.LowerLayerIndex, postA.LowerLayerIndex);
        Assert.Equal(postB.UpperLayerIndex, postA.UpperLayerIndex);
        Assert.Equal(postB.Polygons.Select(q => q.Area()), postA.Polygons.Select(q => q.Area()));

        // …and the kernel accepts it, which is the capability MIM-2 could not keep.
        var verdict = new PlanarKernel().CanSolve(a);
        Assert.True(verdict.Ok, verdict.Reason);
        Assert.True(new PlanarKernel().CanSolve(b).Ok);

        // The deactivation is REPORTED. A tie that switched off silently would be exactly the class
        // of failure the extractor's dropped-artwork note exists to prevent: a medium the user did
        // not author and cannot see.
        var note = Assert.Single(shipped.Notes, n => n.Contains("is a patterned thin film", StringComparison.Ordinal));
        Assert.Contains("'MIM Dielectric'", note, StringComparison.Ordinal);
        // MIM-11 — it names the MASK this layout draws none of, not the plate conductor.
        Assert.Contains("'Nitride' mask", note, StringComparison.Ordinal);
        Assert.Contains("as AIR", note, StringComparison.Ordinal);
        Assert.Contains("'Metal1'", note, StringComparison.Ordinal);
        Assert.DoesNotContain(plain.Notes, n => n.Contains("is a patterned thin film", StringComparison.Ordinal));

        // EM-SEV R-emsev-3 — and on THIS run it stays a NOTE. The plate's absence is correct here:
        // no artwork was drawn on 'MIM Metal', so the film genuinely is not present and the run is
        // the module-free one it claims to be. The whole interconnect case must report ZERO
        // warnings, or the class stops meaning anything on the runs where it does.
        Assert.Empty(shipped.Warnings);
        Assert.DoesNotContain(note, shipped.Warnings);

        output.WriteLine($"airbridge on the one technology: levels {a.LevelZ(0) * 1e6:G6} / " +
                         $"{a.LevelZ(1) * 1e6:G6} µm, medium {a.EffectiveStack}, kernel accepts");
    }

    /// <summary>
    /// <b>What the merged technology costs the CLOSED-FORM path, measured rather than asserted to be
    /// nothing.</b>
    ///
    /// <para>MIM-7's tie is read by the EM extractor and by nothing else. <c>SubstrateResolver</c> —
    /// the Hammerstad-Jensen path a microstrip PCell uses — sums the stackup's dielectric bands as
    /// authored, has no notion of an analysis level, and so cannot ask the question the tie answers.
    /// That leaves ONE real, permanent difference between the merged technology and the pre-module
    /// starter, and it is here rather than in a comment: <b>a Metal2 line's closed-form substrate is
    /// 102.75 µm instead of 103</b>, because the module put 0.25 µm of plate METAL where 0.25 µm of
    /// air used to be and only dielectric bands are summed. −0.24% in height, with ε_eff a shade
    /// higher; the EM path, which is what an EM run publishes, is bit-identical (see the airbridge
    /// gate above). Teaching the closed form the tie would not close it either — skipping the film
    /// gives 102.55 µm, which is further away, because the missing 0.25 µm is metal, not
    /// dielectric.</para>
    ///
    /// <para><b>A Metal1 line is unaffected on both paths</b>: every band the module added is ABOVE
    /// Metal1, so its substrate is the GaAs alone. That is the line the 2.8% Z₀ shift of MIM-2 was
    /// measured on, and it is the shift the tie removes.</para>
    ///
    /// <para><b>MIM-6's recorded divergence stands, and now applies only to capacitor runs.</b> On a
    /// run that DOES analyse the plate, the EM extractor puts Metal1's sheet on the top of its band
    /// and solves 103 µm of GaAs while the closed form still says 100 — one metal thickness apart,
    /// deliberately. Hammerstad-Jensen models REAL, finite-thickness metal and takes that thickness
    /// as its own parameter <c>t</c>; its h is the physical substrate, ground plane to the underside
    /// of the metal, which is what the process states. The extractor's h is where a ZERO-thickness
    /// sheet was placed — a discretisation position, not a dimension — and feeding it to the closed
    /// form would count Metal1's 3 µm twice. The divergence is bounded by one metal thickness by
    /// construction, and the run's notes print the number the solver used.</para>
    /// </summary>
    [Fact]
    public void TheClosedFormPathDoesNotReadTheTie_AndTheOnlyCostIsAMetal2LineBy025Micron()
    {
        var tech  = StarterTechnologies.MmicGaAs();
        var plain = WithoutTheMimModule();

        foreach (var t in new[] { tech, plain })
        {
            var (byDefault, noFailure, _) = SubstrateResolver.ResolveElectrical(t, PCellLayerSelection.Default);
            Assert.Null(noFailure);
            Assert.Equal("Metal2", byDefault!.SignalConductorName);
            Assert.Equal("Backside Metal", byDefault.GroundConductorName);
            Assert.False(byDefault.IsStripline);

            // Metal1 is below every band the module added, so ITS substrate is the GaAs alone on
            // both — closed form and, for a run with no plate in it, the EM extractor too.
            var (metal1, noFailure2, _) =
                SubstrateResolver.ResolveElectrical(t, new PCellLayerSelection("Metal1", null));
            Assert.Null(noFailure2);
            Assert.Equal("Backside Metal", metal1!.GroundConductorName);
            Assert.Equal(100e-6, metal1.HeightMeters, 12);
            Assert.Equal(12.9, metal1.RelativePermittivity, 9);
        }

        // The one measured cost of the merge, stated as the actual numbers.
        var withModule = SubstrateResolver.ResolveElectrical(tech,  PCellLayerSelection.Default).Substrate!;
        var without    = SubstrateResolver.ResolveElectrical(plain, PCellLayerSelection.Default).Substrate!;
        Assert.Equal(103.0e-6,  without.HeightMeters, 12);
        Assert.Equal(102.75e-6, withModule.HeightMeters, 12);
        Assert.True(withModule.RelativePermittivity > without.RelativePermittivity);

        // The plate metal is a signal conductor like any other, and resolves against the same plane.
        var (plate, noFailure3, _) = SubstrateResolver.ResolveElectrical(
            tech, new PCellLayerSelection("MIM Metal", null));
        Assert.Null(noFailure3);
        Assert.Equal("Backside Metal", plate!.GroundConductorName);
        Assert.False(plate.IsStripline);

        // ── The EM path, both ways round ──────────────────────────────────────────────────────
        //
        // A bare Metal1 line has no plate artwork, so the tie deactivates and the EM slab is the
        // pre-module 100 µm — identical to the module-free stack and in step with the closed form.
        var line      = MicrostripOn(tech);
        var linePlain = MicrostripOn(plain);
        Assert.Equal(linePlain.Problem!.Slab.HeightM, line.Problem!.Slab.HeightM);
        Assert.Equal(100e-6, line.Problem!.Slab.HeightM, 12);
        Assert.Equal(12.9,   line.Problem!.Slab.Material.EpsR, 9);

        // A CAPACITOR run is where MIM-6's divergence lives: 103 µm of GaAs in the solver, 100 in
        // the closed form.
        var cap = Extract(SeriesCapacitor());
        Assert.True(cap.Ok, cap.Refusal);
        Assert.Equal(103e-6, cap.Problem!.Slab.HeightM, 12);
        Assert.Equal(100e-6, SubstrateResolver.ResolveElectrical(
            tech, new PCellLayerSelection("Metal1", null)).Substrate!.HeightMeters, 12);

        output.WriteLine(
            $"closed form Metal2 substrate: {without.HeightMeters * 1e6:F3} µm without the module, " +
            $"{withModule.HeightMeters * 1e6:F3} µm with it (εr {without.RelativePermittivity:F4} -> " +
            $"{withModule.RelativePermittivity:F4}); EM Metal1 slab {line.Problem!.Slab.HeightM * 1e6:F1} µm " +
            $"interconnect-only, {cap.Problem!.Slab.HeightM * 1e6:F1} µm with the plate analysed");
    }

    /// <summary>A bare Metal1 line, for reading the EM extractor's own substrate height.</summary>
    // ══════════════════════════════════════════════════════════════════════════════════════════
    // MIM-9 — the shipped MMIC capacitor is REFUSED, and `check` says so without solving
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The wiring gate for R-emsev-4, on the real technology and the real artwork.</b>
    ///
    /// <para>The bound itself is measured and gated in <c>Engine.Tests</c>
    /// (<c>MimThinLayerTests.M9_1</c>); what this asks is the other half, and it is the half that
    /// silently fails: <c>EmRunService.Preflight</c> is what <c>circuitrf check</c> runs, and a
    /// preflight that PASSES a setup the run refuses is exactly the drift R-aut4-2 exists to
    /// prevent. It also fills no matrix and solves nothing, so it costs a second rather than
    /// minutes.</para>
    ///
    /// <para>The findings come back WITH the refusal — a bare refusal takes away the sentence that
    /// explains the scale it was measured on.</para>
    /// </summary>
    [Fact]
    public void M9_APlatePairPastTheFullWaveFloorIsRefusedBeforeAMatrixIsFilled()
    {
        // A 60 x 60 µm plate pair on the shipped 0.2 µm film, fed on Metal1 at both ends. The size
        // is the one a 1.0838 pF part is drawn at and the one MIM-12 measured.
        //
        // WHAT IS PAST THE FLOOR IS THE GEOMETRY AGAINST THE FILM, NOT THE TECHNOLOGY, and this
        // fixture is deliberately the one that shows it. Both of this file's other 0.2 µm
        // capacitors are let through and correctly so: the 10 µm plate meshes at cell/separation
        // 12.5, and the via-strapped 60 µm one at 18.3, because the MIM Via and the Metal2 strap
        // put gridlines INSIDE the plate and cut the straddling cell to 3.667 µm. Take those away
        // and the plate is meshed on its own width alone.
        //
        // MIM-14 MOVED THE FLOOR FROM 40 TO 200 AND THIS FIXTURE HAD TO MOVE WITH IT, which is
        // worth stating rather than quietly editing: at the mesher's DEFAULT four cells across, a
        // 60 µm plate over a 0.2 µm film is cell/separation 75, and 75 was past the old floor and
        // is inside the new one. It is now meshed at ONE cell across — cell/separation 300 — which
        // is what "past the floor" means since MIM-14: a plate the mesh is not resolving at all.
        // Leaving the old artwork with the old assertion would have gated on a run that is now
        // permitted, which is the failure R-aut4-2 exists to prevent one level down.
        var view = new LayoutView { DbuPerMicron = Dbu };
        foreach (var shape in (List<LayoutShape>)
        [
            Rect(Metal1,    0, 124, 100, 140),   // feed in
            Rect(Metal1,  100, 100, 164, 164),   // bottom plate
            Rect(Metal1,  164, 124, 264, 140),   // feed out
            Rect(MimMetal, 102, 102, 162, 162),  // top plate
            Rect(Nitride,  102, 102, 162, 162),
            Port(Metal1,    0, 132, "P1"),
            Port(Metal1,  264, 132, "P2"),
        ]) view.Shapes.Add(shape);

        var pre = EmRunService.Preflight(
            new EmSetup
            {
                Name       = "mim",
                LayoutRef  = "mim.clay",
                PlanarMesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: false,
                                                    MinCellsAcrossConductor: 1),
                Frequency  = new Core.Design.FrequencySpec("1", "1", 1, Core.Design.SweepKind.Linear,
                                                           "GHz", "GHz"),
            },
            new EmLayoutSource("mim.clay", view, StarterTechnologies.MmicGaAs(), Dbu));

        output.WriteLine(pre.Refusal ?? "(not refused)");
        Assert.False(pre.Ok);
        Assert.Contains("cell/separation = 300", pre.Refusal!, StringComparison.Ordinal);
        Assert.Contains("past the 200 this solve is measured over", pre.Refusal!, StringComparison.Ordinal);
        Assert.Contains("thicken the film", pre.Refusal!, StringComparison.Ordinal);

        // The scale sentence rides with it rather than being replaced by it.
        Assert.Contains(pre.Findings, f => f.Text.Contains("CELL/SEPARATION", StringComparison.Ordinal));
    }

    private static PlanarExtractionResult MicrostripOn(Technology tech)
    {
        var r = PlanarExtractor.Extract(
            [Rect(Metal1, 0, 0, 400, 70), Port(Metal1, 0, 35, "P1"), Port(Metal1, 400, 35, "P2")],
            tech, Dbu, 20e9);
        Assert.True(r.Ok, r.Refusal);
        return r;
    }
}

// ══════════════════════════════════════════════════════════════════════════════════════════════
// FINDING 1 — CLOSED at MIM-6 (2026-08-30): the plate separation is the dielectric alone
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// MIM-2 measured this file's three levels at z = 100 / 103.2 / 106 um: `PlanarExtractor` placed a
// level's zero-thickness sheet at the BOTTOM of its conductor band and absorbed the band into the
// dielectric ABOVE, so Metal1's own 3 um of metal landed INSIDE the plate gap and a 0.2 um process
// separation solved as 3.2 um — 16x. It was not fixable by authoring: TechValidation requires a
// positive thickness on every band, and Metal1's sheet was pinned by the microstrip case.
//
// MIM-6 gave a conductor entry a reference SURFACE (`StackupLayer.SheetAt`), with the absorption
// direction paired to it so every sheet still lands on an interface of the medium by construction.
// The shipped MIM technology sets Metal1 = Top; the levels above are now 103 / 103.2 / 106 and the
// region between the plates is 0.2 um of er 6.8. Bottom (and unset) is the old behaviour, bit
// identical — SheetReferenceSurfaceTests holds that, and holds the mechanism generally.
//
// THE STATED COST, asserted rather than commented: the region under Metal1 is 103 um of GaAs
// instead of 100 (~3%), and on this technology the closed-form microstrip path still says 100 —
// see AMetal1Microstrip_...'s own note for why SubstrateResolver is deliberately not taught the
// field. Full record: src/Design/RESOLVED.md and src/Ui/RESOLVED.md.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// FINDING 2 — RETRACTED (2026-08-30): the constant 0.30 fF was the PORT DISCONTINUITY
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// This block originally concluded that the cross-level plate-to-plate term was "missing or
// negligible in the full-wave planar kernel". Wrong — the defect was in the measurement, not the
// kernel. The gap-ladder table (one-port shunt capacitor, C read off Im(Y11), 0.30 fF to within 1%
// across a 32x change in the modelled separation) was read off a RAW, un-de-embedded solve, and a
// raw edge port's own discontinuity is a ~0.3 fF SERIES element standing in front of everything
// behind it: series(0.3 fF, C_plate) is ~0.3 fF for every C_plate well above 0.3 fF — including the
// table's slight upward trend toward that limit as the plate capacitance grows. The original "NOT
// the feeds" argument discriminated nothing: a port discontinuity's capacitance is every bit as
// frequency-independent as the plate's.
//
// The controls that pinned it (scratch-harness solves, 2026-08-30):
//   * Engine-direct, a one-level 400 um line at 10 GHz: de-embedded |S21| = 0.9858 while the SAME
//     solve's raw |S21| = 0.0000. Raw S measures the ports, not the structure — the solver's own
//     ceiling refusal says raw is "for diagnostics only".
//   * The L9 phase-gate record (src/Engine/Mom/HISTORY.md §L9): a via-bridged two-level structure
//     transmits de-embedded |S21| = 0.9993 against 0.0502 with the posts removed, at N = 1,023 —
//     vias conduct and cross-level coupling is present through the calibrated path.
//   * Region vias behave identically to the L9-validated point-via path on the same shunt fixture
//     (0.199 vs 0.204 fF — both the port discontinuity), so MIM-1 is not implicated either.
//
// What remains true: a raw solve cannot read a femtofarad-scale element behind its own port
// discontinuity — of ANY structure, not of MIM capacitors specifically. Hence the solve test above
// gates the with-via/without-via COMPARISON, which the discontinuity (common to both runs) cannot
// fake, and still carries no magnitude band. Finding 1 was brief MIM-6 (level reference surface)
// and is CLOSED, 2026-08-30 — which is what makes MIM-3's ladder measurable: a 0.2 um plate gap is
// now what the solver actually sees, so its physics tier measures the real regime rather than a
// 3.2 um one. Thin-separation ACCURACY is still MIM-3's, and this brief claims none of it.
// Full narrative: src/Ui/RESOLVED.md §MIM-2 and §MIM-6.
