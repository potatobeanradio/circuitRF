// R-pcal7 — a port feed that runs beside its OWN net.
// `docs/sonnet-briefs/brief-em-pcal7-own-net-feed-neighbourhood.md`; the measurements and the
// reasoning are in `src/Engine/Mom/RESOLVED.md` §PCAL7-OWNNET.
//
// WHAT IS GATED HERE, AND WHERE. The DECISIONS are what the three code changes make, they are what
// went wrong, and they cost milliseconds: which metal is a neighbour, how long a lead is grown, and
// which run is refused. Those are all in the routine tier. The nH numbers that say the decisions
// were the RIGHT ones need a de-embedded solve of a DUT and its standards apiece — 3 s to 100 s
// each, measured — so they carry Category=Benchmark, which is this repository's own rule for a test
// at or over ~5 s. A decision gate that is cheap and a physics gate that is not is the split the
// L8/L9 phase gates already use.

using System.Numerics;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;
using NumFlat;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.Mom;

public sealed class OwnNetFeedNeighbourhoodTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _out = output;

    private const double F = 2e9;

    /// <summary>Extend → mesh → resolve → measure, which is the order <c>PlanarKernel.Solve</c> runs
    /// them in and the only order in which the artwork scan and the mesh check can be compared.</summary>
    private static (IReadOnlyList<PlanarFeedLead> Leads,
                    IReadOnlyList<PlanarFeedClearance> Clearances,
                    PlanarMesh Mesh, PlanarProblem Grown)
        Setup(PlanarProblem problem, PlanarPort[] ports, PlanarMeshSettings? mesh = null)
    {
        var cal = PlanarCalibrationSettings.Default;
        var (grown, leads, _) = PlanarFeedExtension.Extend(problem, ports, cal);
        var report = SurfaceMesher.Mesh(grown, mesh ?? PlanarOwnNetFixtures.Mesh, leads: leads);
        var resolved = PlanarPorts.ResolveAll(report.Mesh, ports);
        var conductors = PlanarConductors.Of(report.Mesh);
        double h = problem.Slab.HeightM;

        var clearances = new List<PlanarFeedClearance>();
        foreach (var p in resolved)
            if (PlanarPorts.MeasureFeedClearance(
                    report.Mesh, p, resolved,
                    endRunM: cal.EndRunHeights * h,
                    drivenRequiredM: cal.DrivenNeighbourClearanceHeights * h,
                    passiveRequiredM: cal.PassiveNeighbourClearanceHeights * h,
                    slabHeightM: h, conductors: conductors) is { } c)
                clearances.Add(c);

        return (leads, clearances, report.Mesh, grown);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-pcal7-1 — WHICH METAL IS A NEIGHBOUR
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The three fixtures with nothing inside either feed's clearance grow no lead and measure
    /// clear</b>, which is R-pcal4-1's own invariant restated for this change: a feed that reaches
    /// none of this machinery must produce the same problem object it always did. Their de-embedded
    /// answers are asserted bit-identical in the benchmark tier below; this is the cheap half, and
    /// it is the half that would fail first if the neighbour predicate ever widened again.
    /// </summary>
    [Theory]
    [InlineData("Straight")]
    [InlineData("ViaHop")]
    public void AFeedWithNothingBesideIt_GrowsNoLeadAndMeasuresClear(string name)
    {
        var (problem, ports) = PlanarOwnNetFixtures.Fixture(name, F);
        var (leads, clearances, _, grown) = Setup(problem, ports);

        Assert.Empty(leads);
        Assert.Same(problem, grown);                      // the SAME object: no lead, no rebuild
        Assert.All(clearances, c => Assert.False(c.Breached));
        Assert.All(clearances, c => Assert.Equal(PlanarNeighbourClass.None, c.Neighbour));
    }

    /// <summary>
    /// <b>A bend's corner is the port's own net, is CONTIGUOUS with the feed, and is still not a
    /// neighbour.</b> This is the half of the old skip that was right and had to survive: metal in
    /// line with the feed is a change of CROSS-SECTION, R-fed-1 grows a collinear lead for it and
    /// peels it exactly. Removing the skip without this distinction re-fires PCAL2's refusal on
    /// every taper and every pad in the repository — measured, on <c>EmDeembedCeilingTests</c>' own
    /// 13.1 mm → 299 µm taper, before the band rule was written.
    /// </summary>
    [Fact]
    public void AFlareOnThePortsOwnNet_IsNotANeighbour()
    {
        var (problem, ports) = PlanarOwnNetFixtures.Fixture("Bend", F);
        var (leads, clearances, _, _) = Setup(problem, ports);

        Assert.All(clearances, c => Assert.False(c.Breached));
        Assert.Equal(2, leads.Count);
        Assert.All(leads, l => Assert.False(l.GrownForNeighbour));    // the cross-section, not a neighbour
        _out.WriteLine($"bend leads: {string.Join(", ", leads.Select(l => $"{l.PortNumber}: {l.LengthM * 1e6:0.###} µm"))}");

        // The same thing one step out: a straight-sided taper, which is the shape R-fed-1 exists for
        // and the shape PCAL1 measured passive with this check silent.
        var taper = PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter, 2.9e-3, 0.8e-3, 20e-3, 5e9);
        var tPorts = PlanarLineFixtures.EndPorts(taper);
        var (_, tClear, _, _) = Setup(taper, tPorts, PlanarLineFixtures.Coarse);
        Assert.All(tClear, c => Assert.False(c.Breached));
    }

    /// <summary>
    /// <b>The feed's own cross-section is found on a HIGH-SIDE port too, when the layout carries
    /// metal beyond it.</b>
    ///
    /// <para><c>FeedBands</c> starts its walk at the outermost cell of the port's own feed, and the
    /// index of that cell is NOT the index a coordinate lookup returns for a port fed from the high
    /// side: the lookup names the cell that STARTS at a gridline, while the feed's outermost cell is
    /// the one that ENDS there. The two coincide — by accident — whenever the port's own metal is the
    /// outermost metal in the layout, because the lookup then clamps onto the last cell; and that is
    /// true of every fixture in this file and of the taper above. Draw anything past the port and the
    /// grid extends, the walk starts on a column of empty space, breaks at once, and the whole
    /// exemption returns <c>null</c> — which puts the port's own flare straight back into the
    /// neighbour class R-pcal7-1 exists to take it out of.</para>
    ///
    /// <para>Asserted on the band directly rather than on a clearance verdict, because the band only
    /// has to hold a verdict up where a rim cell's own metal reaches past its grid rectangle, and no
    /// fixture here is that shape. The band is the decision; whether a given rim needs it is the
    /// geometry's business.</para>
    /// </summary>
    [Fact]
    public void TheFeedsOwnBand_IsFoundOnAHighSidePort_WhenTheGridRunsPastIt()
    {
        var line   = PlanarLineFixtures.Line(GroundedSlab.Fr4Starter, 2.9e-3, 40e-3, 5e9);
        var tPorts = PlanarLineFixtures.EndPorts(line);

        // Far enough out to change nothing but where the grid ends — past any lead either port
        // grows and well outside the passive clearance across the feed — and WIDER than the line, so
        // it does not become the narrowest run and re-pitch the whole grid.
        var withIsland = line with
        {
            Layers = [line.Layers[0] with
            {
                Polygons = [.. line.Layers[0].Polygons,
                            PlanarLineFixtures.Rect(60e-3, 40e-3, 64e-3, 44e-3)],
            }],
        };

        static IReadOnlyList<int> BandColumnsOfPort2(PlanarProblem problem, PlanarPort[] ports)
        {
            var cal = PlanarCalibrationSettings.Default;
            var (grown, leads, _) = PlanarFeedExtension.Extend(problem, ports, cal);
            var report = SurfaceMesher.Mesh(grown, PlanarLineFixtures.Coarse, leads: leads);
            var p = PlanarPorts.ResolveAll(report.Mesh, ports).Single(q => q.Number == 2);

            var bands = PlanarPorts.FeedBands(
                report.Mesh, p,
                alongX: p.Direction == PlanarBasisDirection.X,
                fromLow: p.Side is PlanarPortSide.MinX or PlanarPortSide.MinY,
                tLo: p.TransverseLines[0], tHi: p.TransverseLines[^1],
                endRunM: cal.EndRunHeights * problem.Slab.HeightM);

            return bands is null ? [] : [.. bands.Keys.Order()];
        }

        var bare   = BandColumnsOfPort2(line,       tPorts);
        var island = BandColumnsOfPort2(withIsland, tPorts);
        _out.WriteLine($"port 2 band columns: {bare.Count} bare, {island.Count} with an island past it");

        // NOT the same COUNT: a second polygon is a second run for the per-axis pitch rule, so the
        // grid it lands on is finer along x and the same end run is more columns of it. What the
        // defect did was return NO band at all.
        Assert.NotEmpty(bare);
        Assert.NotEmpty(island);
    }

    /// <summary>
    /// <b>The port's own net running BESIDE the feed is a neighbour, at the DRIVEN threshold.</b>
    /// The whole defect: on all three of these the metal is the port's own conductor, it is
    /// separated from the feed by a gap, and PCAL2 used to report the feed clear.
    /// </summary>
    [Theory]
    [InlineData("Splay",   350.0)]
    [InlineData("Coupled",  50.0)]
    [InlineData("Hair50",   50.0)]
    public void OwnNetMetalBesideTheFeed_IsADrivenNeighbour(string name, double expectedGapUm)
    {
        var (problem, ports) = PlanarOwnNetFixtures.Fixture(name, F);
        var (_, clearances, mesh, _) = Setup(problem, ports);

        // One conducting component carrying both ports — so every neighbour here IS the port's own
        // net, and the old skip removed all of it.
        var conn = PlanarConductors.Of(mesh);
        var resolved = PlanarPorts.ResolveAll(mesh, ports);
        Assert.Equal(conn.LabelsOf(mesh, resolved[0]).ToArray(), conn.LabelsOf(mesh, resolved[1]).ToArray());

        Assert.Equal(2, clearances.Count);
        foreach (var c in clearances)
        {
            _out.WriteLine($"{name} port {c.PortNumber}: {c.Neighbour} at {c.NearestM * 1e6:0.###} µm");
            Assert.True(c.Breached);
            Assert.Equal(PlanarNeighbourClass.Driven, c.Neighbour);
            Assert.Equal(expectedGapUm, c.NearestM * 1e6, 3);

            // …and the refusal says WHERE, not only how far (round-7 field report).
            Assert.NotNull(c.NearestXM);
            Assert.Contains(", at (", c.Breach(), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <b>A port face a little wider on one side than the copper behind it is still one feed.</b>
    /// A placed part's footprint pad lying over an imported board's own pad, offset so it stands
    /// proud of it on one edge, used to end the feed's band at the first column where that sliver
    /// was empty — and the port's own pad beside the profile came back as a neighbour 0 µm away and
    /// refused the run (round-7 field report).
    /// </summary>
    [Fact]
    public void AFootprintPadProudOfTheBoardPadOnOneSide_IsNotANeighbour()
    {
        const double w = 2.9e-3, len = 20e-3;
        var problem = PlanarLineFixtures.Problem(GroundedSlab.Fr4Starter, 5e9,
            PlanarLineFixtures.Rect(0, 0, len, w),                                  // the board's line and pad
            PlanarLineFixtures.Rect(len - 1.5e-3, -0.4e-3, len + 0.05e-3, w - 0.6e-3)); // the footprint pad
        PlanarPort[] ports =
        [
            new(1, new EmPoint(0, 0.5 * w), PlanarPortSide.MinX, 50.0),
            new(2, new EmPoint(len + 0.05e-3, 0.4 * w), PlanarPortSide.MaxX, 50.0),
        ];

        var (_, clearances, _, _) = Setup(problem, ports);

        Assert.All(clearances, c => Assert.False(c.Breached, c.Breach()));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-pcal7-2 — THE LEAD THE NEIGHBOURHOOD ASKS FOR
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The reported artwork, unchanged, and both of its feeds come out clear.</b> The MMIC coil
    /// is the part this was reported on — `MmicCoilFixture` is the shipped PCell example's own
    /// generated artwork — and the length rule is exact rather than a heuristic: port 1's first
    /// offending metal is the next turn, 8 µm across and ~40 µm in, so the lead is the end run less
    /// that. Port 2 is the other half of the rule: its pad ENDS 30 µm in and the coil body begins
    /// 10 µm later, which <c>UniformRun</c> reads as a short feed and says nothing about.
    /// </summary>
    [Fact]
    public void TheCoilsLeadsClearItsOwnTurns()
    {
        var problem = MmicCoilFixture.Coil(F);
        PlanarPort[] ports =
        [
            new(1, new EmPoint(-110e-6, -125e-6), PlanarPortSide.MinY, 50.0) { LayerIndex = 0 },
            new(2, new EmPoint(-155e-6,  -46e-6), PlanarPortSide.MinX, 50.0) { LayerIndex = 0 },
        ];

        var mesh = new PlanarMeshSettings(Auto: false, CellsPerWavelength: 20, EdgeMesh: false,
                                          MinCellsAcrossConductor: 2);
        var (leads, clearances, _, _) = Setup(problem, ports, mesh);

        Assert.Equal(2, leads.Count);
        foreach (var l in leads)
        {
            _out.WriteLine($"port {l.PortNumber}: {l.LengthM * 1e6:0.###} µm, " +
                           $"neighbour {l.NeighbourAcrossM * 1e6:0.###} µm across at " +
                           $"{l.NeighbourAtM * 1e6:0.###} µm in");
            Assert.True(l.GrownForNeighbour);
            // endRun (3 h = 300 µm) less where the offending metal starts, to the scan's own step.
            Assert.InRange(l.LengthM * 1e6, 255, 275);
        }

        // Port 1's obstruction is the next turn, 8 µm away — the number in the report.
        var p1 = leads.Single(l => l.PortNumber == 1);
        Assert.Equal(8.0, p1.NeighbourAcrossM * 1e6, 3);

        Assert.All(clearances, c => Assert.False(c.Breached));
    }

    /// <summary>
    /// <b>Where extension cannot clear the obstruction it is DROPPED, not lengthened.</b> On a
    /// parallel own-net run the neighbour's own lead grows with the port's, so the shortfall never
    /// falls; one fixed-point step detects that and the port keeps only whatever its cross-section
    /// asked for. Without it the lead would grow without limit and so would the mesh.
    /// </summary>
    [Theory]
    [InlineData("Coupled")]
    [InlineData("Splay")]
    [InlineData("Hair50")]
    public void AParallelObstruction_GrowsNoNeighbourhoodLead(string name)
    {
        var (problem, ports) = PlanarOwnNetFixtures.Fixture(name, F);
        var (leads, _, _, _) = Setup(problem, ports);
        Assert.All(leads, l => Assert.False(l.GrownForNeighbour));
    }

    /// <summary>
    /// <b>The note says WHICH shortfall grew the lead.</b> "60 µm on top of 239 µm it already had"
    /// means the metal changes width at the plane; a lead grown because a coil turn runs 8 µm away
    /// is a different fact with a different remedy, and a user chasing one must not be handed the
    /// other.
    /// </summary>
    [Fact]
    public void TheFeedNote_SaysWhichShortfallGrewTheLead()
    {
        var problem = MmicCoilFixture.Coil(F);
        PlanarPort[] ports =
        [
            new(1, new EmPoint(-110e-6, -125e-6), PlanarPortSide.MinY, 50.0) { LayerIndex = 0 },
            new(2, new EmPoint(-155e-6,  -46e-6), PlanarPortSide.MinX, 50.0) { LayerIndex = 0 },
        ];
        var (_, _, notes) = PlanarFeedExtension.Extend(problem, ports);
        string note = Assert.Single(notes);
        _out.WriteLine(note);

        Assert.Contains("have other metal beside them inside", note);
        Assert.Contains("to clear other metal", note);
        Assert.DoesNotContain("on top of", note);

        // …and the cross-section wording is still what a taper gets.
        var taper = PlanarLineFixtures.Taper(GroundedSlab.Fr4Starter, 2.9e-3, 0.8e-3, 20e-3, 5e9);
        var tNotes = PlanarFeedExtension.Extend(taper, PlanarLineFixtures.EndPorts(taper)).Notes;
        Assert.Contains("changes cross-section", Assert.Single(tNotes));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // R-pcal7-3 — THE OWN-NET CALIBRATION GROUP
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>Two terminals of one inductor brought out side by side form a calibration group.</b> The
    /// decline that used to stop it said a standard reproducing own-net metal would be "two
    /// conductors the structure shorts together somewhere this profile cannot see". The error box is
    /// a local property of the cross-section and the excitation, and the DUT's topology beyond the
    /// reference plane does not enter it — which the coupled-line control in the benchmark tier
    /// measures rather than asserts.
    /// </summary>
    [Theory]
    [InlineData("Coupled")]
    [InlineData("Splay")]
    public void TwoOwnNetFeedsAtOnePlane_FormACalibrationGroup(string name)
    {
        var (problem, ports) = PlanarOwnNetFixtures.Fixture(name, F);
        var (_, _, mesh, grown) = Setup(problem, ports);
        var resolved = PlanarPorts.ResolveAll(mesh, ports);
        var conductors = PlanarConductors.Of(mesh);
        var cal = PlanarCalibrationSettings.Default;

        var profile = PlanarPorts.TryFormCalibrationGroup(
            mesh, resolved[0], resolved,
            PlanarCalibration.EndRunCellsFor(resolved[0], grown.Slab),
            cal.DrivenNeighbourClearanceHeights * grown.Slab.HeightM,
            cal.MaxCalibrationGroupSize, conductors, out string? declined);

        Assert.Null(declined);
        Assert.NotNull(profile);
        Assert.Equal([1, 2], profile.PortNumbers.Order());
        _out.WriteLine(profile.Describe(SurfaceMesher.DefaultLengthFormat));
    }

    /// <summary>
    /// <b>A group's members are grown to ONE lead length</b>, so <c>CommonPeelLength</c>'s gate —
    /// which is right and must stay — no longer fires on a part whose two pads differ by microns.
    /// At HEAD this fixture's leads were 243.75 µm and 262.5 µm.
    /// </summary>
    [Fact]
    public void AGroupsMembers_AreGrownToOneLeadLength()
    {
        var (problem, ports) = PlanarOwnNetFixtures.Fixture("UnevenPads", F);
        var (leads, _, _, _) = Setup(problem, ports);

        Assert.Equal(2, leads.Count);
        foreach (var l in leads)
            _out.WriteLine($"port {l.PortNumber}: {l.LengthM * 1e6:0.###} µm " +
                           $"(own cross-section asked {l.SectionShortfallM * 1e6:0.###} µm, " +
                           $"peer floor {l.PeerFloorM * 1e6:0.###} µm)");

        Assert.Equal(leads[0].LengthM, leads[1].LengthM, 12);
        Assert.NotEqual(leads[0].SectionShortfallM, leads[1].SectionShortfallM);
        Assert.Contains(leads, l => l.GrownForPeer);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // WHAT MUST KEEP REFUSING
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A U-turn INSIDE the run the standard replaces is refused, and that is the right answer.</b>
    /// No lead and no group can reproduce a feed that bends within the length the standard copies —
    /// the group declines it by name on its own uniformity rule — and the refusal is a strict
    /// improvement on the 159 nH this used to publish in silence.
    /// </summary>
    [Fact]
    public void AUTurnInsideTheEndRun_IsRefused()
    {
        var (problem, ports) = PlanarOwnNetFixtures.Fixture("Hair50", F);
        var (grown, leads, _) = PlanarFeedExtension.Extend(problem, ports);
        var mesh = SurfaceMesher.Mesh(grown, PlanarOwnNetFixtures.Mesh).Mesh;
        var resolved = PlanarPorts.ResolveAll(mesh, ports);

        var ex = Assert.Throws<PlanarFeedClearanceRefusedException>(
            () => PlanarSolve.Run(grown, mesh, resolved, [F], leads: leads));

        _out.WriteLine(ex.Message);
        Assert.Contains("feeds are not isolated", ex.Message);
        Assert.All(ex.Breaches, b => Assert.Equal(PlanarNeighbourClass.Driven, b.Neighbour));

        // PCAL4's own uniformity rule is what declines the group, and the decline travels with the
        // refusal so the user can see why the machinery that exists to fix this did not.
        Assert.Contains("not uniform over the", ex.Message);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // M4 — THE CEILING
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>A mesh pushed past the ceiling says which part of it the solver added.</b> Every other
    /// remedy in that refusal is a setting or a piece of the user's own artwork; a lead is neither,
    /// and a user measuring their part against the refusal's numbers would be measuring a structure
    /// that is not in their layout.
    /// </summary>
    [Fact]
    public void TheCeilingRefusal_NamesAGrownLead()
    {
        var problem = MmicCoilFixture.Coil(F);
        PlanarPort[] ports =
        [
            new(1, new EmPoint(-110e-6, -125e-6), PlanarPortSide.MinY, 50.0) { LayerIndex = 0 },
            new(2, new EmPoint(-155e-6,  -46e-6), PlanarPortSide.MinX, 50.0) { LayerIndex = 0 },
        ];

        var (grown, leads, _) = PlanarFeedExtension.Extend(problem, ports);
        var report = SurfaceMesher.Mesh(grown, PlanarMeshSettings.Default, leads: leads);

        Assert.False(report.CanSolve);
        _out.WriteLine(report.Refusal);
        Assert.Contains("NOT your artwork", report.Refusal);
        Assert.Contains("port 1", report.Refusal);
        Assert.Contains("port 2", report.Refusal);

        // Without the leads the same message says nothing about them, so an ordinary refused mesh
        // gains no sentence.
        var bare = SurfaceMesher.Mesh(problem, PlanarMeshSettings.Default);
        Assert.False(bare.CanSolve);
        Assert.DoesNotContain("NOT your artwork", bare.Refusal);
    }
}
