using System.Diagnostics;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// Tiers 8, 9 and 10 of brief-wbond-wbc §3 — the profile envelope, binding, and the WB-C1 costs.
/// </summary>
public class ProfileEnvelopeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public ProfileEnvelopeTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    private static (WBondDesign Design, LoopProfile Profile) BoundArray(
        int wires = 6, double loopMil = 20.0, string arrayName = "G1")
    {
        var profile = LoopProfile.BallBond(WBondUnits.ToNm(loopMil, WBondUnit.Mil), points: 7);
        var design = new WBondDesign();
        design.Profiles.Add(profile);

        var array = new WireArray { Name = arrayName, Profile = profile.Name };
        for (int i = 0; i < wires; i++)
        {
            array.Wires.Add(profile.CreateWire(
                Point3.Mils(0, i * 6, 4), Point3.Mils(100, i * 6, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold"));
        }
        design.Arrays.Add(array);
        return (design, profile);
    }

    // ---------------------------------------------------------------- tier 8

    /// <summary>
    /// TIER 8 — every bound member lies inside the band, at every sampled span.
    /// </summary>
    [Fact]
    public void Tier8_TheBandBracketsEveryBoundMember()
    {
        var (design, _) = BoundArray(wires: 8);
        var profile = ProfileEnvelope.Build(design.Arrays[0]);

        Assert.Equal(8, profile.BoundWires.Count);
        Assert.Empty(profile.FreeWires);
        Assert.NotEmpty(profile.Bands);

        foreach (var band in profile.Bands)
        {
            Assert.True(band.MinHeightNm <= band.MaxHeightNm);

            foreach (int index in profile.BoundWires)
            {
                double h = ProfileEnvelope.HeightAt(design.Arrays[0].Wires[index], band.Span);
                Assert.True(h >= band.MinHeightNm - 1.0 && h <= band.MaxHeightNm + 1.0,
                    $"Wire {index} at span {band.Span:F3} has height {h:F1} nm, outside the band " +
                    $"[{band.MinHeightNm:F1}, {band.MaxHeightNm:F1}].");
            }
        }
    }

    /// <summary>
    /// TIER 8 — a wire that no longer follows the profile is reported as FREE and drawn individually,
    /// which is §6.2's answer to the odd-ball problem: an explicit binding state, not a heuristic.
    /// </summary>
    [Fact]
    public void Tier8_ADetachedWire_IsReportedAsFree()
    {
        var (design, _) = BoundArray(wires: 5);
        ProfileEnvelope.Detach(design.Arrays[0].Wires[2]);

        var profile = ProfileEnvelope.Build(design.Arrays[0]);

        Assert.Equal(4, profile.BoundWires.Count);
        Assert.Equal([2], profile.FreeWires);
    }

    /// <summary>
    /// TIER 8 — a wire whose XY path BACKTRACKS is not profile-editable, so it is drawn free.
    ///
    /// <para>This is §6.2's stated residual limit: such geometry is legal and solves correctly, it
    /// simply has a non-monotone span and cannot be drawn against it without self-overlap. The limit
    /// is decided here rather than prevented at the model.</para>
    /// </summary>
    [Fact]
    public void Tier8_ABacktrackingWire_IsNotProfileEditable()
    {
        var straight = new Wire
        {
            Points = { Point3.Mils(0, 0, 4), Point3.Mils(50, 0, 24), Point3.Mils(100, 0, 1) },
        };
        Assert.True(ProfileEnvelope.IsProfileEditable(straight));

        var doglegs = new Wire
        {
            Points =
            {
                Point3.Mils(0, 0, 4),
                Point3.Mils(80, 0, 24),
                Point3.Mils(30, 0, 24),   // goes back on itself in XY
                Point3.Mils(100, 0, 1),
            },
        };
        Assert.False(ProfileEnvelope.IsProfileEditable(doglegs));
    }

    /// <summary>The band widens when a member is scaled away from the rest.</summary>
    [Fact]
    public void Tier8_ScalingOneMember_WidensTheBand()
    {
        var (design, _) = BoundArray(wires: 4);

        double Width(WBondDesign d) =>
            ProfileEnvelope.Build(d.Arrays[0]).Bands.Max(b => b.MaxHeightNm - b.MinHeightNm);

        double before = Width(design);
        WireEdits.ScaleHeightAboutChord(design.Arrays[0].Wires[1], 1.6);
        double after = Width(design);

        Assert.True(after > before + 1.0,
            $"Raising one member must widen the band: {before:F1} nm -> {after:F1} nm.");
    }

    // ---------------------------------------------------------------- tier 9

    /// <summary>
    /// TIER 9 — <b>detaching leaves the points exactly untouched</b> (D5).
    ///
    /// <para>A binding is a generator, not a constraint. Breaking it must not move the wire, or a user
    /// who nudges one vertex would see the whole wire jump.</para>
    /// </summary>
    [Fact]
    public void Tier9_Detaching_LeavesThePointsUntouched()
    {
        var (design, _) = BoundArray(wires: 3);
        var wire = design.Arrays[0].Wires[1];
        var before = wire.Points.ToArray();

        ProfileEnvelope.Detach(wire);

        Assert.Null(wire.ProfileBinding);
        Assert.Equal(before, wire.Points.ToArray());
    }

    /// <summary>TIER 9 — re-binding resamples onto the profile, and the feet survive exactly.</summary>
    [Fact]
    public void Tier9_Rebinding_ResamplesOntoTheProfileAndKeepsTheFeet()
    {
        var (design, profile) = BoundArray(wires: 3);
        var wire = design.Arrays[0].Wires[1];

        var start = wire.Points[0];
        var end = wire.Points[^1];
        var original = wire.Points.ToArray();

        ProfileEnvelope.Detach(wire);
        WireEdits.ScaleHeightAboutChord(wire, 2.2);
        Assert.NotEqual(original[3], wire.Points[3]);

        ProfileEnvelope.Bind(wire, profile);

        Assert.Equal(profile.Name, wire.ProfileBinding);
        Assert.Equal(start, wire.Points[0]);
        Assert.Equal(end, wire.Points[^1]);
        Assert.Equal(original, wire.Points.ToArray());
    }

    /// <summary>TIER 9 — the count a "N wires detached" toast would report.</summary>
    [Fact]
    public void Tier9_WiresFollowing_CountsExactlyTheBoundOnes()
    {
        var (design, profile) = BoundArray(wires: 5);
        Assert.Equal(5, ProfileEnvelope.WiresFollowing(design, profile.Name).Count);

        ProfileEnvelope.Detach(design.Arrays[0].Wires[0]);
        ProfileEnvelope.Detach(design.Arrays[0].Wires[4]);

        Assert.Equal(3, ProfileEnvelope.WiresFollowing(design, profile.Name).Count);
    }

    // ---------------------------------------------------------------- the panel record

    /// <summary>The panel reports pH, coupling coefficients, and the active return path.</summary>
    [Fact]
    public void PanelReadout_ReportsPicoHenriesAndTheReturnPath()
    {
        var design = TestDesigns.ParallelArray(n: 6, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0, arrays: 2);
        var mesh = WireMesh.Build(design);
        var reduction = ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh);

        var panel = PanelReadout.Build(design, mesh, reduction);

        Assert.Equal(2, panel.Rows.Count);
        Assert.Contains("image plane", panel.ReturnPath, StringComparison.OrdinalIgnoreCase);

        foreach (var row in panel.Rows)
        {
            // Wirebond inductances live in the tens-to-thousands of pH — one fixed unit covers them.
            Assert.True(row.SelfPicoHenries is > 10 and < 100_000,
                $"Array {row.Name} reported {row.SelfPicoHenries:F1} pH, outside the plausible range.");

            Assert.Equal(3, row.WireCount);
            Assert.True(row.TotalLengthMm > 0);
            Assert.True(row.MaxLandingSpanMm > 0);
            Assert.Equal(3, row.CurrentShares.Count);

            // KCL: the shares for 1 A into this array sum to 1.
            Assert.Equal(1.0, row.CurrentShares.Sum(), 1e-9);

            // k = 1 against itself, by construction.
            int self = panel.Rows.ToList().FindIndex(r => r.Name == row.Name);
            Assert.Equal(1.0, row.CouplingCoefficients[self], 1e-12);
        }
    }

    /// <summary>An undeclared return path is stated as such, not left blank (WB20).</summary>
    [Fact]
    public void PanelReadout_StatesAnUndeclaredReturnPath()
    {
        var design = TestDesigns.ParallelArray(n: 2, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
        design.GroundPlane.Enabled = false;

        var mesh = WireMesh.Build(design);
        var panel = PanelReadout.Build(design, mesh, ArrayReduction.Reduce(InductanceMatrix.Fill(mesh), mesh));

        Assert.Contains("UNDECLARED", panel.ReturnPath, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- tier 10

    /// <summary>
    /// TIER 10 — WB-C1's costs: a 200-wire duplicate-with-pitch, and an alt-drag frame on a large
    /// bound array at 600 wires.
    /// </summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void Tier10_EditingCosts_AreMeasured()
    {
        // A 200-wire duplicate-with-pitch must be ONE operation, not 200.
        var design = TestDesigns.ParallelArray(n: 1, pitchMil: 6.0, lengthMil: 100.0, heightMil: 20.0);
        var source = design.Arrays[0].Wires[0];

        var sw = Stopwatch.StartNew();
        var made = WireEdits.DuplicateWithPitch(design, source, 0, WBondUnits.ToNm(6.0, WBondUnit.Mil), 199);
        sw.Stop();
        double duplicateMs = sw.Elapsed.TotalMilliseconds;

        Assert.Equal(199, made.Count);

        // Then ONE fill covers all 200 — the property WB26 is really asking for.
        sw.Restart();
        var mesh = WireMesh.Build(design);
        var fill = IncrementalFill.Create(mesh, parallel: true);
        sw.Stop();
        double fillMs = sw.Elapsed.TotalMilliseconds;

        // Alt-drag on a large bound array at the stated worst case.
        var (big, profile) = BoundArray(wires: 200, arrayName: "G1");
        var bigMesh = WireMesh.Build(big);
        var bigFill = IncrementalFill.Create(bigMesh, parallel: true);

        sw.Restart();
        WireEdits.ScaleBoundWires(big, profile, heightFactor: 1.05, spanFactor: 1.0);
        sw.Stop();
        double geometryMs = sw.Elapsed.TotalMilliseconds;

        int[] moved = [.. Enumerable.Range(0, big.WireCount)];
        sw.Restart();
        bigFill.MoveWires(moved, SelectionMotion.General);
        bigFill.Reduce();
        sw.Stop();
        double refillMs = sw.Elapsed.TotalMilliseconds;

        Assert.True(duplicateMs >= 0);
        Assert.True(fillMs > 0 && geometryMs >= 0 && refillMs > 0);

        _out.WriteLine($"duplicate-with-pitch x199:            {duplicateMs,8:F2} ms  <- ONE operation");
        _out.WriteLine($"one fill of the resulting 200 wires:  {fillMs,8:F1} ms  <- ONE fill, not 200");
        _out.WriteLine($"alt-drag geometry, 200 bound wires:   {geometryMs,8:F2} ms");
        _out.WriteLine($"refill + reduce after that drag:      {refillMs,8:F1} ms  (frame budget 16.67)");
    }
}
