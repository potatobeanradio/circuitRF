namespace CircuitRF.WBond.Tests;

/// <summary>
/// A wire lying <b>in</b> the ground plane — the geometry that crashed the editor mid-drag
/// (owner, 2026-08-19: <i>"I was dragging a wire in the wBond host layout, but circuitRF crashed"</i>,
/// with <c>pivot 0.000E+000 at wire 6</c> out of <c>CapacitanceReduction.Compute</c>).
///
/// <para><b>Both matrices go singular on this geometry — the difference is bookkeeping, and that is
/// the finding.</b> <b>L</b> is maintained incrementally and only the MOVED wires' rows are
/// revisited; <b>P</b> is refilled over the whole mesh every republish. So a degenerate wire that is
/// not the one being dragged is invisible to every inductance-side guard and fatal to the
/// capacitance — which is exactly the shape of the crash.</para>
/// </summary>
public class WireInThePlaneTests
{
    private static readonly long DiameterNm = WBondUnits.ToNm(1.0, WBondUnit.Mil);

    private static Wire Flat(double y)
    {
        var wire = new Wire { DiameterNm = DiameterNm, Material = "Gold" };
        wire.Points.Add(Point3.Mils(0, y, 0));
        wire.Points.Add(Point3.Mils(100, y, 0));
        return wire;
    }

    private static Wire Looped(double y) => LoopShape.CreateSeedWire(
        Point3.Mils(0, y, 0), Point3.Mils(100, y, 0), DiameterNm, "Gold",
        loopHeightNm: WBondUnits.ToNm(20.0, WBondUnit.Mil));

    private static WBondDesign Design(params Wire[] wires)
    {
        var design = new WBondDesign();
        var array = new WireArray { Name = "G1" };
        foreach (var wire in wires) array.Wires.Add(wire);
        design.Arrays.Add(array);
        return design;
    }

    /// <summary>
    /// <b>A wire in the plane zeroes its whole row, in BOTH matrices</b> — the plane holds it at zero
    /// potential, and its anti-parallel coincident current image cancels its inductance.
    ///
    /// <para>Zero to machine precision, not always to the last bit: the cancellation is bit-exact
    /// only where direct and image are evaluated identically — a single-filament wire's self term, or
    /// a pair taken by the far kernel. Near pairs sum the two by quadrature in different orders and
    /// leave the last bits behind. That is exactly why the diagnosis uses a relative floor and not a
    /// sign test, and why the owner's log could print a clean <c>0.000E+000</c> at all.</para>
    ///
    /// <para><b>L being singular too is the correction to the obvious story.</b> The image current
    /// ADDS, so it is tempting to conclude the inductance is unaffected — but a horizontal filament's
    /// image is anti-parallel AND coincident, so it cancels. The capacitance is not where the physics
    /// is special; it is only where the arithmetic is redone from scratch every frame.</para>
    /// </summary>
    [Fact]
    public void AWireInThePlane_ZeroesItsRow_InBothMatrices()
    {
        var mesh = WireMesh.Build(Design(Flat(0), Looped(6)));

        var p = PotentialCoefficients.Fill(mesh);
        var l = InductanceMatrix.Fill(mesh);

        // The wire beside it is untouched, so it is the flat wire's ROW that vanishes and not the
        // matrix — and it is the scale everything else is measured against.
        Assert.True(p[1, 1] > 0.0);
        Assert.True(l[1, 1] > 0.0);

        // This wire is one filament, so its self term is subtracted from itself and cancels exactly.
        Assert.Equal(0.0, p[0, 0]);
        Assert.Equal(0.0, l[0, 0]);

        Assert.True(Math.Abs(p[0, 1]) < 1e-13 * p[1, 1], $"P[0,1] = {p[0, 1]:E3} against {p[1, 1]:E3}");
        Assert.True(Math.Abs(l[0, 1]) < 1e-13 * l[1, 1], $"L[0,1] = {l[0, 1]:E3} against {l[1, 1]:E3}");
    }

    /// <summary>
    /// The refusal fires on a wire deep in a design, where the cancellation is <b>not</b> bit-exact —
    /// the case the relative floor exists for, and the case the owner actually hit (their wire was
    /// index 6). A sign test would have passed this through to an anonymous pivot.
    /// </summary>
    [Fact]
    public void TheRefusalFires_OnAFlatWireAmongOthers_NotOnlyOnItsOwn()
    {
        var wires = new Wire[8];
        for (int w = 0; w < wires.Length; w++) wires[w] = Looped(w * 6);
        wires[6] = Flat(6 * 6);

        var ex = Assert.Throws<InvalidOperationException>(
            () => CapacitanceReduction.Create(WireMesh.Build(Design(wires)), parallel: false));

        Assert.Contains("Wire 7 of array 'G1'", ex.Message);
        Assert.Contains("ground plane", ex.Message);
    }

    /// <summary>
    /// <b>A fill that throws has already mutated the mesh</b>, and the editor has to know it: this is
    /// what turned a refused edit into a crash two gestures later. <c>MoveWires</c> re-flattens the
    /// moved wires into the mesh and writes their rows into <b>L</b> before the factor update
    /// discovers the matrix is singular, so "the edit was refused" does not mean "nothing happened" —
    /// and the mesh is what the capacitance is refilled from on every later frame.
    ///
    /// <para>Pinned rather than fixed here on purpose. Making the incremental fill transactional means
    /// snapshotting a row of the mesh, a row of <b>L</b> and the whole factor on every drag frame,
    /// which is a cost paid always to serve a path taken almost never; the caller rebuilding once on
    /// the error path is the cheaper half of the same guarantee
    /// (<c>WBondViewModel.RebuildAfterFailedFill</c>).</para>
    /// </summary>
    [Fact]
    public void AFailedMoveWires_HasAlreadyMutatedTheMesh()
    {
        var design = Design(Looped(0), Looped(6), Looped(12));
        var mesh = WireMesh.Build(design);
        var fill = IncrementalFill.Create(mesh);

        // Flatten the middle wire onto the plane, exactly as a profile drag through z = 0 would.
        var wire = design.AllWires().ToList()[1];
        for (int i = 0; i < wire.Points.Count; i++)
            wire.Points[i] = new Point3(wire.Points[i].X, wire.Points[i].Y, 0);

        Assert.Throws<InvalidOperationException>(() => fill.MoveWires([1], SelectionMotion.General));

        // The throw did not roll the mesh back: the flattened wire is in it, so the capacitance —
        // which is refilled over the WHOLE mesh — now refuses. That is the state the editor used to
        // keep and then crash on, one frame later and from a call nothing had guarded.
        var ex = Assert.Throws<InvalidOperationException>(
            () => CapacitanceReduction.Create(mesh, parallel: false));
        Assert.Contains("ground plane", ex.Message);
    }

    /// <summary>
    /// The refusal <b>names the wire and its array</b> and says what to do about it. The message the
    /// owner actually got named the inductance matrix (which was fine) and offered two causes
    /// (duplicate geometry, zero length) that were both absent — so the reader had nothing to act on.
    /// </summary>
    [Fact]
    public void TheRefusal_NamesTheWire_AndWhatToDoAboutIt()
    {
        var design = Design(Looped(0), Looped(6), Flat(12));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CapacitanceReduction.Create(WireMesh.Build(design), parallel: false));

        Assert.Contains("Wire 3 of array 'G1'", ex.Message);
        Assert.Contains("ground plane", ex.Message);
        Assert.Contains("loop height", ex.Message);
        Assert.DoesNotContain("inductance", ex.Message);
    }

    /// <summary>
    /// With the plane off there is no image, nothing to be singular, and nothing to refuse — the
    /// guard must not fire on a design that has no capacitance to compute in the first place.
    /// </summary>
    [Fact]
    public void WithNoGroundPlane_AFlatWireIsNotRefused()
    {
        var design = Design(Flat(0));
        design.GroundPlane.Enabled = false;

        Assert.Null(CapacitanceReduction.Create(WireMesh.Build(design), parallel: false));
    }

    /// <summary>
    /// The <b>other</b> singular geometry still reports, and now under the right matrix's name.
    /// Two wires on identical points leave P's diagonal positive, so this one is caught by the
    /// factorisation rather than by the diagonal pre-check — which is exactly why the pre-check is an
    /// addition to that message and not a replacement for it.
    /// </summary>
    [Fact]
    public void CoincidentWires_StillReport_AndNameThePotentialCoefficientMatrix()
    {
        var design = Design(Looped(0), Looped(0));

        var ex = Assert.Throws<InvalidOperationException>(
            () => CapacitanceReduction.Create(WireMesh.Build(design), parallel: false));

        Assert.Contains("potential-coefficient matrix (capacitance)", ex.Message);
        Assert.Contains("same geometry", ex.Message);
    }

    /// <summary>The inductance path's own message is unchanged — it was never the one that lied.</summary>
    [Fact]
    public void TheInductanceMessage_IsUnchanged()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => CholeskyFactor.Factor([0.0], 1));

        Assert.Contains("The inductance matrix is not positive definite", ex.Message);
    }
}
