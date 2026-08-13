// R-fed-1 / R-fed-2 — the automatic uniform feed, and the passivity gate that would have caught its
// absence.
//
// COST NOTE. Everything here except the two end-to-end cases is GEOMETRY or ALGEBRA and runs in
// microseconds; the two solves use PlanarLineFixtures.Coarse on a short taper for the reason that
// file's own header gives — the property under test (a passive answer, a plane on the drawn edge) is
// not a statement about mesh quality, and a converged mesh would test it no harder.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using RfCore;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarFeedExtensionTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private static readonly GroundedSlab Slab = GroundedSlab.Fr4Starter;
    private static double Required => PlanarCalibrationSettings.Default.EndRunHeights * Slab.HeightM;

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-fed-1 — when a lead is grown, and when it is not
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void AUniformFeedGrowsNothing_AndTheProblemComesBackByReference()
    {
        // The bit-identity guard, and it is the reason this is asserted on REFERENCE rather than on
        // vertex counts: every measured number in src/Engine/Mom/HISTORY.md was taken on a problem
        // that reaches the mesher unchanged, and `Same` is the only assertion that cannot drift.
        var problem = PlanarLineFixtures.Line(Slab, PlanarLineFixtures.Fr4HeroWidthM, 30e-3, 10e9);
        var ports   = PlanarLineFixtures.EndPorts(problem);

        var (extended, leads, notes) = PlanarFeedExtension.Extend(problem, ports);

        Assert.Same(problem, extended);
        Assert.Empty(leads);
        Assert.Empty(notes);
    }

    [Fact]
    public void AFeedSHORTERThanTheCalibrationRunStillGrowsNothing()
    {
        // Running out of metal is a SHORT structure, not a flared one — EndRunCellsFor already
        // clamps to the cells that exist. Getting this wrong would grow a lead on most of the line
        // fixtures in this project and move numbers that were never wrong. 3 mm < the 4.8 mm run.
        var problem = PlanarLineFixtures.Line(Slab, PlanarLineFixtures.Fr4HeroWidthM, 3e-3, 10e9);
        var (extended, leads, _) = PlanarFeedExtension.Extend(problem, PlanarLineFixtures.EndPorts(problem));

        Assert.Same(problem, extended);
        Assert.Empty(leads);
    }

    [Fact]
    public void ATaperGrowsALeadAtBothPorts_SizedToTheShortfallAndNoMore()
    {
        var problem = PlanarLineFixtures.Taper(Slab, 2.9e-3, 8e-3, 30e-3, 10e9);
        var ports   = PlanarLineFixtures.EndPorts(problem);

        var (extended, leads, notes) = PlanarFeedExtension.Extend(problem, ports);
        foreach (var l in leads)
            _out.WriteLine($"port {l.PortNumber}: lead {l.LengthM * 1e3:F3} mm, " +
                           $"already uniform {l.ExistingUniformM * 1e3:F3} mm");
        foreach (string n in notes) _out.WriteLine("  · " + n);

        Assert.Equal(2, leads.Count);

        // The flank is oblique from the very first vertex, so essentially NOTHING was already
        // uniform and the lead is the whole required run. "Essentially" is the scan's own step: it
        // reports the last station that still matched, so it can credit at most one step of
        // gently-flaring metal as uniform — 1/64 of the run, and at the wide end (where a given
        // absolute flare is a smaller FRACTION of the width) that is exactly what happens.
        double step = Required / 64;
        foreach (var l in leads)
        {
            Assert.True(l.ExistingUniformM <= step + 1e-12,
                $"port {l.PortNumber} credited {l.ExistingUniformM * 1e3:F4} mm as uniform");
            Assert.Equal(Required, l.LengthM + l.ExistingUniformM, 9);
        }

        // The drawn edges are still where the user drew them — that is the plane the answer is
        // reported at, and the record is what Peel measures against.
        Assert.Equal(0.0, leads.Single(l => l.PortNumber == 1).DrawnEdgeM, 12);
        Assert.Equal(30e-3, leads.Single(l => l.PortNumber == 2).DrawnEdgeM, 12);

        // Geometry: two vertices added per port, and the structure now reaches Required beyond each
        // drawn edge at exactly the drawn edge's own width.
        var drawn = problem.Layers[0].Polygons[0];
        var grown = extended.Layers[0].Polygons[0];
        Assert.Equal(drawn.Outer.Count + 4, grown.Outer.Count);

        var (gx0, _, gx1, _) = grown.Bounds();
        Assert.Equal(-leads.Single(l => l.PortNumber == 1).LengthM, gx0, 12);
        Assert.Equal(30e-3 + leads.Single(l => l.PortNumber == 2).LengthM, gx1, 12);

        // The lead is the port's own cross-section, not the taper's continued flare: half a
        // millimetre outside the drawn edge the metal is still 2.9 mm wide.
        Assert.True(grown.Contains(-0.5e-3, +1.449e-3));
        Assert.True(grown.Contains(-0.5e-3, -1.449e-3));
        Assert.False(grown.Contains(-0.5e-3, +1.451e-3));
    }

    [Fact]
    public void APartlyUniformFeedGrowsOnlyTheShortfall()
    {
        // A 1 mm uniform stub in front of the flare: the lead must make up 4.8 − 1.0 mm, not 4.8.
        const double lead = 1e-3, len = 20e-3, w = 2.9e-3;
        var taper = PlanarLineFixtures.Taper(Slab, w, 8e-3, len, 10e9);
        var ring  = new List<EmPoint>(taper.Layers[0].Polygons[0].Outer);
        for (int i = 0; i < ring.Count; i++)
            ring[i] = new EmPoint(ring[i].X + lead, ring[i].Y);
        ring.Insert(0, new EmPoint(0, 0.5 * w));
        ring.Add(new EmPoint(0, -0.5 * w));

        var problem = PlanarLineFixtures.Problem(Slab, 10e9, new PlanarPolygon(ring));
        var ports   = PlanarLineFixtures.EndPorts(problem);

        var l1 = PlanarFeedExtension.Extend(problem, ports).Leads.Single(l => l.PortNumber == 1);
        _out.WriteLine($"port 1: had {l1.ExistingUniformM * 1e3:F3} mm, added {l1.LengthM * 1e3:F3} mm");

        Assert.Equal(lead, l1.ExistingUniformM, 4);          // to the uniformity scan's own step
        Assert.Equal(Required - lead, l1.LengthM, 4);
    }

    [Fact]
    public void ALeadThatWouldRunIntoOtherMetalIsDeclinedByName()
    {
        // Declining is not a silent skip: the lead cannot be grown, so the pre-existing limitation
        // is still there, and the note has to say so — this is the case the automatic feed CANNOT
        // fix, which is exactly what CheckFeedClearance is for.
        // The neighbour sits BESIDE the port's own line (y ≥ 0.5 mm) and behind its face, so the
        // march that finds the end face never sees it — but the lead, which is 2.9 mm wide, would
        // run straight through it. A neighbour ON the line would be a different situation entirely:
        // PlanarPorts would resolve the port onto IT, and no amount of feed would help.
        var taper = PlanarLineFixtures.Taper(Slab, 2.9e-3, 8e-3, 20e-3, 10e9);
        var block = PlanarLineFixtures.Rect(-4e-3, 0.5e-3, -1e-3, 3e-3);
        var problem = PlanarLineFixtures.Problem(
            Slab, 10e9, taper.Layers[0].Polygons[0], block);

        var ports = new[]
        {
            new PlanarPort(1, new EmPoint(0, 0), PlanarPortSide.MinX, 50.0),
        };

        var (extended, leads, notes) = PlanarFeedExtension.Extend(problem, ports);
        foreach (string n in notes) _out.WriteLine("  · " + n);

        Assert.Same(problem, extended);
        Assert.Empty(leads);
        Assert.Contains(notes, n => n.Contains("cannot be grown"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-fed-2 — the peel
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PeelingAMatchedLineIsExactlyItsClosedForm()
    {
        // Cascade a known matched section onto a known network by hand, peel it, and get the network
        // back. This is the whole justification for letting the solver add metal: what it adds is
        // removable in closed form, not by a fit.
        var s = new Mat<Complex>(2, 2);
        s[0, 0] = new Complex(0.31, -0.22);
        s[1, 0] = new Complex(0.74, 0.51);
        s[0, 1] = s[1, 0];
        s[1, 1] = new Complex(-0.18, 0.09);

        Complex[] gamma = [new(1.7, 260.0), new(2.9, 310.0)];
        double[]  len   = [3.3e-3, 1.9e-3];

        var withLine = new Mat<Complex>(2, 2);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                withLine[i, j] = s[i, j] * Complex.Exp(-(gamma[i] * len[i] + gamma[j] * len[j]));

        var back = PlanarFeedExtension.Peel(withLine, len, gamma);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                Assert.Equal(0.0, (back[i, j] - s[i, j]).Magnitude, 12);
    }

    [Fact]
    public void PeelingNothingIsBitIdentical()
    {
        // A port that grew no lead must not be multiplied by exp(0) — that is a rounding step for
        // no reason, and R-prt-14's bit-identity is asserted rather than approximated everywhere
        // else in this driver.
        var s = new Mat<Complex>(2, 2);
        s[0, 0] = new Complex(0.4, 0.1);
        s[1, 0] = new Complex(0.9, -0.3);

        var back = PlanarFeedExtension.Peel(s, [0.0, 0.0], [Complex.One, Complex.One]);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
            {
                Assert.Equal(BitConverter.DoubleToInt64Bits(s[i, j].Real),
                             BitConverter.DoubleToInt64Bits(back[i, j].Real));
                Assert.Equal(BitConverter.DoubleToInt64Bits(s[i, j].Imaginary),
                             BitConverter.DoubleToInt64Bits(back[i, j].Imaginary));
            }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // Wired in — asserted at the MESH, which costs milliseconds
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TheResolvedPortLandsOnUniformManhattanMetal_WhichABareTaperNeverDoes()
    {
        // The cheap half of the end-to-end proof, and it is not a proxy: what the calibration needs
        // is a port whose cross-section IS the standard's, and that is a property of the RESOLUTION,
        // not of the solve. Conformal boundary cells make it vivid — on a bare taper the outermost
        // transverse cells follow the oblique flank, R-cut-4 declines their rooftops, and a slice of
        // the drawn face is not driven at all (44.9% of it on the owner's own MKlopf). A Manhattan
        // lead has nothing to decline.
        var settings = PlanarLineFixtures.Coarse with { BoundaryCells = PlanarBoundaryCells.Conformal };
        var problem  = PlanarLineFixtures.Taper(Slab, 2.9e-3, 8e-3, 20e-3, 10e9, segments: 24);
        var ports    = PlanarLineFixtures.EndPorts(problem);

        var bare  = PlanarPorts.ResolveAll(SurfaceMesher.Mesh(problem, settings).Mesh, ports);
        var grown = PlanarPorts.ResolveAll(
            SurfaceMesher.Mesh(PlanarFeedExtension.Extend(problem, ports).Problem, settings).Mesh, ports);

        for (int i = 0; i < 2; i++)
            _out.WriteLine($"port {bare[i].Number}: bare  width {bare[i].WidthM * 1e3:F3} mm, " +
                           $"undriven {bare[i].UndrivenMetalM * 1e6:F1} µm  |  grown width " +
                           $"{grown[i].WidthM * 1e3:F3} mm, undriven {grown[i].UndrivenMetalM * 1e6:F1} µm");

        // UNDRIVEN METAL is the quantity, not the cut-cell count: a cell can follow the flank and
        // still carry its rooftop, and on this fixture the counter reads zero at both ports while a
        // quarter of the face goes undriven. The counter would have made this test pass vacuously.
        Assert.True(bare.All(p => p.UndrivenMetalM > 0),
            "the bare taper was expected to leave metal undriven at both ports — if it no longer " +
            "does, this fixture has stopped exercising what it was written for");

        foreach (var p in grown)
            Assert.Equal(0.0, p.UndrivenMetalM, 12);

        // The plane's width is the DRAWN face's width, at both ends — the lead is the port's own
        // cross-section extruded, never the flare continued.
        Assert.Equal(2.9e-3, grown[0].WidthM, 9);
        Assert.Equal(8e-3,   grown[1].WidthM, 9);

        // …and it sits OUTSIDE the user's metal, which is what leaves room for R-fed-2 to peel back
        // onto the drawn edge rather than into it.
        Assert.True(grown[0].OuterEdgeM < 0);
        Assert.True(grown[1].OuterEdgeM > 20e-3);
    }

    [Fact]
    public void TheClearanceWarningFallsSILENTOnAnExtendedTaper_ButNotOnARealNeighbour()
    {
        // The two halves of the CheckFeedClearance fix, in the one place they interact.
        //
        // R-fed-1 sizes the lead so the feed is uniform for EXACTLY the required run, which puts the
        // DUT's own flare at the region's far boundary with a lateral gap of zero. A near-edge
        // region test re-fires the warning there on every extended taper — an unclearable warning
        // one line below the fix for unclearable warnings. It must be silent here…
        double required = Required;
        var taper = PlanarLineFixtures.Taper(Slab, 2.9e-3, 8e-3, 20e-3, 10e9, segments: 24);
        var ports = PlanarLineFixtures.EndPorts(taper);
        var grownMesh = SurfaceMesher.Mesh(
            PlanarFeedExtension.Extend(taper, ports).Problem, PlanarLineFixtures.Coarse).Mesh;

        foreach (var p in PlanarPorts.ResolveAll(grownMesh, ports))
        {
            string? warn = PlanarPorts.CheckFeedClearance(grownMesh, p, required);
            if (warn is not null) _out.WriteLine(warn);
            Assert.Null(warn);
        }

        // …and it must still be LOUD about an actual neighbour running alongside that same lead.
        // Otherwise "silent" would just mean "switched off", and the distinction this warning now
        // has to draw is precisely: the structure's OWN flare is R-fed-1's job, a neighbour is not.
        var crowded = PlanarLineFixtures.Problem(Slab, 10e9,
            PlanarFeedExtension.Extend(taper, ports).Problem.Layers[0].Polygons[0],
            PlanarLineFixtures.Rect(-required, 1.85e-3, 0, 4e-3));
        var crowdedMesh = SurfaceMesher.Mesh(crowded, PlanarLineFixtures.Coarse).Mesh;

        string? loud = PlanarPorts.CheckFeedClearance(
            crowdedMesh, PlanarPorts.Resolve(crowdedMesh, ports[0]), required);
        _out.WriteLine(loud ?? "(silent — the neighbour was not seen)");
        Assert.NotNull(loud);
    }

    [Fact]
    public void APassiveSweepSaysNothingAboutPassivity()
    {
        // R-prt-15 is wired in and quiet on an answer that IS a network. The loud half needs a
        // genuinely non-passive solve and lives in the opt-in tier below.
        var problem = PlanarLineFixtures.Line(Slab, PlanarLineFixtures.Fr4HeroWidthM, 30e-3, 5e9);
        var result  = new PlanarKernel().Solve(
            problem, PlanarLineFixtures.Coarse, PlanarLineFixtures.EndPorts(problem), [5e9]);

        Assert.DoesNotContain(result.Notes, n => n.StartsWith("NOT PASSIVE"));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // End to end — the failure the owner reported. Category=Benchmark, and it has to be
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Why this cannot be a routine test, measured rather than assumed.</b> Two things fight:
    ///
    /// <para><b>The pathology needs the EDGE MESH and a LOW frequency.</b> The amplification is
    /// 1/a₂₁², and a₂₁ is set by the outermost cell — with the edge mesh off that is a bulk cell and
    /// 1/a₂₁² is a few hundred (measured a₂₁ = 0.061/0.090, σ_max = 0.9962 either way, i.e. no
    /// failure to fix); with it on, the cell is 3% of the width and a₂₁ falls to 0.0125/0.0262. A
    /// gate sized without the edge mesh would pass whether or not R-fed-1 exists.</para>
    ///
    /// <para><b>And the cost is the CALIBRATION STANDARDS, whose length goes as 1/f</b> — they run
    /// 5–10× the DUT's own unknowns here. Four smaller/faster fixtures were tried (2.9 → 5 mm and
    /// 2.9 → 6 mm at 1 and 2 GHz, and a 900 µm GaAs taper at 10 and 20 GHz): all still cost 30–60 s
    /// per solve, and none reproduced — σ_max stayed at 0.993…0.998 because the flare was too mild.
    /// There is no cheap version of this measurement, so it is tagged rather than weakened, and the
    /// routine tier keeps the mesh-level gate above.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "Benchmark")]
    public void ATaperDeembedsToAPassiveNetwork_AndDidNotBeforeTheAutomaticFeed()
    {
        // 2.9 → 8 mm over 14 mm on 1.6 mm FR-4 at 1 GHz, with the edge mesh ON as it ships.
        var settings = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 10, EdgeMesh: true, EdgeCells: 3);
        var problem  = PlanarLineFixtures.Taper(Slab, 2.9e-3, 8e-3, 14e-3, 1e9);
        var ports    = PlanarLineFixtures.EndPorts(problem);
        double[] f   = [1e9];

        // ── BEFORE: mesh the drawn artwork and de-embed it — exactly the path that shipped ───────
        var bare   = SurfaceMesher.Mesh(problem, settings);
        var before = PlanarSolve.Run(problem, bare.Mesh, PlanarPorts.ResolveAll(bare.Mesh, ports), f);
        double sigmaBefore = RFNetwork.Passivity(before.Points[0].S);

        // ── AFTER: the same artwork through the kernel, which grows and peels the feed itself ────
        var after = new PlanarKernel().Solve(problem, settings, ports, f);
        double sigmaAfter = RFNetwork.Passivity(after.Solve.Points[0].S);

        _out.WriteLine($"a₂₁      = {string.Join(", ", before.Points[0].Calibrations.Select(c => c.Box.A21.Magnitude.ToString("G3")))}");
        _out.WriteLine($"before: σ_max {sigmaBefore:F4}  |S11| {before.Points[0].S[0, 0].Magnitude:F4}  " +
                       $"|S21| {before.Points[0].S[1, 0].Magnitude:F4}  (N = {before.UnknownCount})");
        _out.WriteLine($"after : σ_max {sigmaAfter:F4}  |S11| {after.Solve.Points[0].S[0, 0].Magnitude:F4}  " +
                       $"|S21| {after.Solve.Points[0].S[1, 0].Magnitude:F4}  (N = {after.Solve.UnknownCount})");

        // The old path is not merely less accurate — its answer is not a network. Asserted so this
        // fails loudly if the extension is ever quietly disabled, rather than merely drifting.
        Assert.True(sigmaBefore > 1.0 + 1e-3,
            $"the un-extended path was expected to be non-passive; σ_max = {sigmaBefore:F6}");
        Assert.True(sigmaAfter <= 1.0 + 1e-3, $"σ_max = {sigmaAfter:F6} after the automatic feed");

        // A 2.9 → 8 mm taper 0.09 λ_g long is a gentle transformer at 1 GHz: it should mostly pass.
        // The un-extended peel reported |S₁₁| = 0.72 for it.
        Assert.True(after.Solve.Points[0].S[1, 0].Magnitude > 0.9,
            $"|S21| = {after.Solve.Points[0].S[1, 0].Magnitude:F4}");
        Assert.True(after.Solve.Points[0].S[0, 0].Magnitude < 0.35,
            $"|S11| = {after.Solve.Points[0].S[0, 0].Magnitude:F4}");

        Assert.Contains(after.Notes, n => n.Contains("UNIFORM LEAD"));
        Assert.Contains(after.Notes, n => n.Contains("peeled back off"));

        // R-prt-15's loud half, on the run that genuinely produces σ_max > 1 — so the note cannot
        // pass by being unreachable.
        string note = Assert.Single(before.Notes, n => n.StartsWith("NOT PASSIVE"));
        _out.WriteLine(note);
        Assert.DoesNotContain(after.Notes, n => n.StartsWith("NOT PASSIVE"));
    }
}
