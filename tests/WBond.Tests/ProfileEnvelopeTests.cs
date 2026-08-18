using System.Diagnostics;

namespace CircuitRF.WBond.Tests;

/// <summary>
/// Tiers 8 and 10 of brief-wbond-wbc §3 — the profile envelope and the WB-C1 costs.
///
/// <para>Tier 9 (binding: detach, re-bind, "N wires following") is <b>retired</b>, 2026-08-18: the
/// <c>LoopProfile</c> object and the binding it existed for were removed, so there is nothing to
/// detach from and no shared shape to count followers of. The band is now keyed on drawability
/// alone.</para>
/// </summary>
public class ProfileEnvelopeTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _out;

    public ProfileEnvelopeTests(Xunit.Abstractions.ITestOutputHelper output) => _out = output;

    /// <summary>An array of identically-arched wires on a common pitch.</summary>
    private static WBondDesign SeededArray(int wires = 6, double loopMil = 20.0, string arrayName = "G1")
    {
        long loopNm = WBondUnits.ToNm(loopMil, WBondUnit.Mil);
        var design = new WBondDesign();

        var array = new WireArray { Name = arrayName };
        for (int i = 0; i < wires; i++)
        {
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, i * 6, 4), Point3.Mils(100, i * 6, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopNm));
        }
        design.Arrays.Add(array);
        return design;
    }

    // ---------------------------------------------------------------- tier 8

    /// <summary>
    /// TIER 8 — every member lies inside the band, at every sampled span.
    /// </summary>
    [Fact]
    public void Tier8_TheBandBracketsEveryMember()
    {
        var design = SeededArray(wires: 8);
        var profile = ProfileEnvelope.Build(design.Arrays[0]);

        Assert.Equal(8, profile.Members.Count);
        Assert.Empty(profile.NonMonotone);
        Assert.NotEmpty(profile.Bands);

        foreach (var band in profile.Bands)
        {
            Assert.True(band.MinHeightNm <= band.MaxHeightNm);

            foreach (int index in profile.Members)
            {
                double h = ProfileEnvelope.HeightAt(design.Arrays[0].Wires[index], band.Span);
                Assert.True(h >= band.MinHeightNm - 1.0 && h <= band.MaxHeightNm + 1.0,
                    $"Wire {index} at span {band.Span:F3} has height {h:F1} nm, outside the band " +
                    $"[{band.MinHeightNm:F1}, {band.MaxHeightNm:F1}].");
            }
        }
    }

    /// <summary>
    /// <b>The band spans EVERY member of the array, whatever its shape</b> (owner, 2026-08-18:
    /// <i>"I want the envelope rendering to always be the entire envelope for that group."</i>).
    ///
    /// <para>Nothing a user can do to a wire takes it out of its own group's envelope. The band was
    /// narrowed twice and both narrowings were the same mistake: first to the members bound to a
    /// <c>LoopProfile</c>, then to the members that are <see cref="ProfileEnvelope.IsProfileEditable"/>
    /// — so dragging one point past its neighbour made that wire's XY path backtrack and it silently
    /// left the band. A backtracking member is REPORTED now, not excluded.</para>
    /// </summary>
    [Fact]
    public void TheBandSpansEveryMember_IncludingABacktrackingOne()
    {
        var design = SeededArray(wires: 3);
        var array = design.Arrays[0];

        // Three visibly different shapes: as seeded, half as tall, and twice as tall.
        WireEdits.ScaleHeightAboutChord(array.Wires[1], 0.5);
        WireEdits.ScaleHeightAboutChord(array.Wires[2], 2.0);

        // A fourth member that doubles back on itself in XY — legal geometry, undrawable here.
        var doglegs = new Wire { DiameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil), Material = "Gold" };
        doglegs.Points.AddRange([
            Point3.Mils(0, 30, 4),
            Point3.Mils(80, 30, 24),
            Point3.Mils(30, 30, 24),
            Point3.Mils(100, 30, 1),
        ]);
        array.Wires.Add(doglegs);

        var profile = ProfileEnvelope.Build(array);

        // All four are members; the backtracking one is reported as such and is a SUBSET of them.
        Assert.Equal([0, 1, 2, 3], profile.Members);
        Assert.Equal([3], profile.NonMonotone);
        Assert.All(profile.NonMonotone, i => Assert.Contains(i, profile.Members));

        // The band's spread at the crest is the real spread between the tallest and shortest member,
        // not a zero-width sliver over one wire.
        double widest = profile.Bands.Max(b => b.MaxHeightNm - b.MinHeightNm);
        double tallest = profile.Bands.Max(b => b.MaxHeightNm);
        Assert.True(widest > 0.5 * tallest,
            $"The band should show the whole spread; widest {widest:F1} nm against a peak of {tallest:F1} nm.");

        // And EVERY member really is inside it, the backtracking one included.
        foreach (var band in profile.Bands)
            foreach (int index in profile.Members)
            {
                double h = ProfileEnvelope.HeightAt(array.Wires[index], band.Span);
                Assert.InRange(h, band.MinHeightNm - 1.0, band.MaxHeightNm + 1.0);
            }
    }

    /// <summary>
    /// TIER 8 — a wire whose XY path BACKTRACKS is not profile-editable, so it is drawn on its own.
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

    /// <summary>
    /// <b>The band never runs outside its own members</b> (owner, 2026-08-18: <i>"envelope rendering
    /// appears a little strange if wires within the same group have a different number of
    /// vertices."</i>).
    ///
    /// <para>Members are piecewise linear and every member's vertices are already sampled, so between
    /// two consecutive samples each member is a straight line — but the ENVELOPE of a set of lines has
    /// a corner wherever two of them cross, and the band drew a straight line from sample to sample.
    /// Near a crossing the drawn maximum therefore ran above every member and the drawn minimum below
    /// every member: <b>a bulge reporting spread the group does not have</b>.</para>
    ///
    /// <para>It was invisible while an array's members shared a vertex lattice — they cross only at
    /// their shared vertices, which are already sampled. Mixed vertex counts interleave two lattices,
    /// so they cross repeatedly in mid-interval and every crossing grew a bulge. Measured before the
    /// fix on this fixture: 2,591 nm.</para>
    ///
    /// <para>The oracle probes BETWEEN the band's own samples, because that is the only place the
    /// error can live — at a sample the band is min/max by construction and any test that only looked
    /// there would pass against the broken version.</para>
    /// </summary>
    [Theory]
    [InlineData(7, 9)]
    [InlineData(5, 11)]
    [InlineData(4, 7)]
    public void TheBand_NeverRunsOutsideItsMembers_AtAnySpan(int firstPoints, int secondPoints)
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var array = new WireArray { Name = "G1" };

        foreach (int n in new[] { firstPoints, secondPoints })
            array.Wires.Add(LoopShape.CreateSeedWire(
                Point3.Mils(0, 0, 4), Point3.Mils(100, 0, 1),
                WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopNm, n));

        var envelope = ProfileEnvelope.Build(array);

        double worst = 0.0, worstSpan = 0.0;

        for (int i = 1; i < envelope.Bands.Count; i++)
        {
            var a = envelope.Bands[i - 1];
            var b = envelope.Bands[i];

            for (int k = 1; k < 8; k++)
            {
                double t = k / 8.0;
                double span = a.Span + (b.Span - a.Span) * t;

                // What the band CLAIMS here — the straight line the renderer draws between its samples.
                double drawnMax = a.MaxHeightNm + (b.MaxHeightNm - a.MaxHeightNm) * t;
                double drawnMin = a.MinHeightNm + (b.MinHeightNm - a.MinHeightNm) * t;

                // What the members actually do.
                double trueMax = double.MinValue, trueMin = double.MaxValue;
                foreach (int index in envelope.Members)
                {
                    double h = ProfileEnvelope.HeightAt(array.Wires[index], span);
                    trueMax = Math.Max(trueMax, h);
                    trueMin = Math.Min(trueMin, h);
                }

                double over = Math.Max(drawnMax - trueMax, trueMin - drawnMin);
                if (over > worst) { worst = over; worstSpan = span; }
            }
        }

        // One nanometre of slack for the rounding in the crossing solve; the defect measured 2,591.
        Assert.True(worst <= 1.0,
            $"The band runs {worst:F0} nm outside every member at span {worstSpan:F4} — it is showing " +
            "spread no wire in the group has.");
    }

    /// <summary>
    /// <b>The band reaches BOTH feet.</b>
    ///
    /// <para>Sample positions are deduplicated with a tolerance, keeping the first of a cluster — so a
    /// member vertex sitting a hair short of 1.0 swallowed the ladder's own final sample and the band
    /// stopped short of the output foot. The two endpoints are the one pair of samples that are not
    /// negotiable.</para>
    /// </summary>
    [Fact]
    public void TheBand_ReachesBothFeet_EvenWithAVertexJustShortOfTheEnd()
    {
        long loopNm = WBondUnits.ToNm(20.0, WBondUnit.Mil);
        var array = new WireArray { Name = "G1" };
        array.Wires.Add(LoopShape.CreateSeedWire(
            Point3.Mils(0, 0, 4), Point3.Mils(100, 0, 1),
            WBondUnits.ToNm(1.0, WBondUnit.Mil), "Gold", loopNm));

        // A wire whose last interior vertex is a whisker from the output foot — 0.9998 of the span.
        var crowded = new Wire { DiameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil), Material = "Gold" };
        crowded.Points.AddRange([
            Point3.Mils(0, 0, 4),
            Point3.Mils(30, 0, 20),
            new Point3(WBondUnits.ToNm(99.98, WBondUnit.Mil), 0, WBondUnits.ToNm(2.0, WBondUnit.Mil)),
            Point3.Mils(100, 0, 1),
        ]);
        array.Wires.Add(crowded);

        var envelope = ProfileEnvelope.Build(array);

        Assert.Equal(0.0, envelope.Bands[0].Span);
        Assert.Equal(1.0, envelope.Bands[^1].Span);
    }

    /// <summary>The band widens when a member is scaled away from the rest.</summary>
    [Fact]
    public void Tier8_ScalingOneMember_WidensTheBand()
    {
        var design = SeededArray(wires: 4);

        double Width(WBondDesign d) =>
            ProfileEnvelope.Build(d.Arrays[0]).Bands.Max(b => b.MaxHeightNm - b.MinHeightNm);

        double before = Width(design);
        WireEdits.ScaleHeightAboutChord(design.Arrays[0].Wires[1], 1.6);
        double after = Width(design);

        Assert.True(after > before + 1.0,
            $"Raising one member must widen the band: {before:F1} nm -> {after:F1} nm.");
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
    /// array at 600 wires.
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

        // Alt-drag on a large array at the stated worst case.
        var big = SeededArray(wires: 200, arrayName: "G1");
        var bigMesh = WireMesh.Build(big);
        var bigFill = IncrementalFill.Create(bigMesh, parallel: true);

        sw.Restart();
        WireEdits.ScaleWires(big.AllWires(), heightFactor: 1.05, spanFactor: 1.0);
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
        _out.WriteLine($"alt-drag geometry, 200 wires:         {geometryMs,8:F2} ms");
        _out.WriteLine($"refill + reduce after that drag:      {refillMs,8:F1} ms  (frame budget 16.67)");
    }
}
