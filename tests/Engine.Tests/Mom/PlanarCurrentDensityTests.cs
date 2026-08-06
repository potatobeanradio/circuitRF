// L8e Tier 4 — D5/R-res-7: the per-cell current-density reduction.
//
// Two halves, and the second is the only PHYSICS check the heat map admits — which is exactly why it
// is worth having, because it is also free. A uniform line's current density must be flat ALONG the
// line and must PEAK at the two transverse edges: that is the 1/√d edge singularity R-msh-5's whole
// edge-mesh argument is about, and this is the first time anything in this codebase has LOOKED at it.
// If the map came out flat across the width, the edge mesh would not be doing what L8b measured it
// doing — and that would be a finding about L8b, not a tolerance to widen.

using System.Numerics;
using NumFlat;
using CircuitRF.Engine.Mom;
using CircuitRF.Engine.Tests.Mom.Support;

namespace CircuitRF.Engine.Tests.Mom;

public class PlanarCurrentDensityTests
{
    private const double FHz = 5e9;

    /// <summary>A short FR-4 line, cheap enough for the routine tier, with the edge mesh ON — the
    /// edge grading is the thing half of these tests are about.</summary>
    private static readonly PlanarMeshSettings Graded =
        new(Auto: false, CellsPerWavelength: 8, EdgeMesh: true, EdgeCells: 3);

    private static (PlanarMesh Mesh, IReadOnlyList<PlanarPortResolution> Ports, Vec<Complex> Currents)
        SolveLine(PlanarMeshSettings? settings = null, double lengthM = 4e-3)
    {
        var problem = PlanarLineFixtures.Fr4Line(lengthM, FHz);
        var (mesh, ports) = PlanarLineFixtures.MeshAndPorts(problem, settings ?? Graded);
        var kernel = PlanarLineFixtures.Kernel(problem.Slab, FHz);
        var ctx    = new PlanarSolveContext(mesh, ports);
        var sol    = ctx.SolveAt(kernel, FHz);
        return (mesh, ports, sol.Currents[0]);          // port 1 driven at 1 V
    }

    // ── The exact identity the reduction is defined by ────────────────────────────────────────

    /// <summary>
    /// <b>The total current crossing a column's CENTRE plane is exactly the MEAN of the currents
    /// crossing that column's two bounding edges.</b> That is what "the rooftop is a linear ramp"
    /// means, and <see cref="PlanarExcitation.LineCurrent"/> reports the edge currents independently
    /// — so this pins the reduction against a quantity computed by a different function, to machine
    /// precision, rather than against itself.
    /// </summary>
    [Fact]
    public void AColumnsCentreCurrent_IsExactlyTheMeanOfItsTwoEdgeCurrents()
    {
        var (mesh, ports, currents) = SolveLine();
        var map = PlanarCurrentDensity.Compute(mesh, currents, 1, FHz);

        double tLo = ports[0].TransverseLines[0], tHi = ports[0].TransverseLines[^1];
        var edges = PlanarExcitation.LineCurrent(mesh, currents, PlanarBasisDirection.X, tLo, tHi);
        Assert.True(edges.Count >= 3, "the fixture needs a few interior cuts to compare against");

        // Column i sits between edge cut i-1 and edge cut i (both interior), for i away from the ends.
        int nx = mesh.GridX.Count - 1;
        int checkedColumns = 0;

        for (int ix = 1; ix < nx - 1; ix++)
        {
            var column = PlanarCurrentDensity.Column(mesh, PlanarBasisDirection.X, ix);
            if (column.Count == 0) continue;

            double left  = mesh.GridX[ix];
            double right = mesh.GridX[ix + 1];

            Complex? iLeft  = FindCut(edges, left);
            Complex? iRight = FindCut(edges, right);
            if (iLeft is null || iRight is null) continue;

            var centre   = PlanarCurrentDensity.ColumnCurrent(mesh, map, column, PlanarBasisDirection.X);
            var expected = 0.5 * (iLeft.Value + iRight.Value);

            Assert.True((centre - expected).Magnitude <= 1e-12 * Math.Max(expected.Magnitude, 1e-30),
                $"column {ix}: {centre} vs {expected}");
            checkedColumns++;
        }

        Assert.True(checkedColumns >= 2, $"only {checkedColumns} interior columns were comparable");
    }

    private static Complex? FindCut(IReadOnlyList<(double Coord, Complex I)> cuts, double at)
    {
        foreach (var (coord, i) in cuts)
            if (Math.Abs(coord - at) <= 1e-15 * Math.Max(Math.Abs(at), 1e-9)) return i;
        return null;
    }

    /// <summary>
    /// The OUTERMOST cell carries half the port current, and that is correct rather than a bug: the
    /// end cell is covered by one rooftop, whose ramp is at half height at the cell's own centre.
    /// Pinned so nobody "fixes" it into a full port current later.
    /// </summary>
    [Fact]
    public void TheOutermostCell_CarriesHalfThePortCurrent_ByConstruction()
    {
        var (mesh, ports, currents) = SolveLine();
        var map = PlanarCurrentDensity.Compute(mesh, currents, 1, FHz);

        var port = ports[0];
        int outerIx = mesh.Cells[mesh.Bases[port.BasisIndices[0]].CellA].IX;

        var column = PlanarCurrentDensity.Column(mesh, PlanarBasisDirection.X, outerIx);
        var centre = PlanarCurrentDensity.ColumnCurrent(mesh, map, column, PlanarBasisDirection.X);
        var portCurrent = PlanarExcitation.PortCurrent(currents, port);

        Assert.Equal(0.5 * portCurrent.Magnitude, centre.Magnitude, 9);
    }

    // ── The physics check, and it is free ─────────────────────────────────────────────────────

    /// <summary>
    /// <b>Tier 4's second half.</b> On a uniform line, |J| must be flat ALONG the line and PEAK at
    /// the two transverse edges — the 1/√d edge current the edge mesh exists to resolve. A map that
    /// came out flat across the width would mean the edge mesh is not doing what L8b measured.
    /// </summary>
    [Fact]
    public void AUniformLinesMap_IsFlatAlongTheLine_AndPeaksAtItsEdges()
    {
        var (mesh, _, currents) = SolveLine();
        var map = PlanarCurrentDensity.Compute(mesh, currents, 1, FHz);

        int nx = mesh.GridX.Count - 1;
        int midIx = nx / 2;

        var column = PlanarCurrentDensity.Column(mesh, PlanarBasisDirection.X, midIx)
                                         .OrderBy(c => mesh.Cells[c].CenterY)
                                         .ToList();
        Assert.True(column.Count >= 5, $"the transverse cut has only {column.Count} cells");

        double edgeLo = map.Magnitude[column[0]];
        double edgeHi = map.Magnitude[column[^1]];
        double middle = map.Magnitude[column[column.Count / 2]];

        Assert.True(edgeLo > middle,
            $"|J| at the low-y edge ({edgeLo:G4} A/m) must exceed the middle ({middle:G4} A/m) — " +
            "the 1/√d edge current is what the edge mesh exists for.");
        Assert.True(edgeHi > middle,
            $"|J| at the high-y edge ({edgeHi:G4} A/m) must exceed the middle ({middle:G4} A/m).");

        // …and symmetric about the centreline, because the line is — but only to ~1%, and the
        // reason is already on the record rather than being slack invented here: L8b's edge grading
        // is not EXACTLY mirror-symmetric (L8d measured the same asymmetry end-to-end, where it cost
        // the two ports of a plain microstrip their shared calibration). Measured transversely on
        // this fixture it is 0.15%. Asserting bit-symmetry would be asserting a property the mesher
        // does not have; asserting nothing would let a real one-sided defect through.
        Assert.True(Math.Abs(edgeLo - edgeHi) <= 0.01 * Math.Max(edgeLo, edgeHi),
            $"|J| at the two edges should match to ~1%: {edgeLo:G6} vs {edgeHi:G6} A/m");

        // ── "Uniform along the line" is about the PROFILE, not about the magnitude ────────────
        //
        // A measured correction to the obvious version of this assertion, recorded rather than
        // quietly worked around. Driving port 1 at 1 V leaves every OTHER port at 0 V — that is what
        // Y = BᵀZ⁻¹B means — so the line is fed at one end and SHORTED at the other, and the total
        // current genuinely varies strongly along it: a standing wave, exactly the one L8d's Tier 1
        // had to least-squares around because triples straddling a current null divide by ~0.
        // Measured on this fixture, two adjacent columns differ by 43%. So the honest statement of
        // "uniform along the line" is that the transverse PROFILE SHAPE does not depend on where the
        // cut is taken — which is the property a uniform line actually has, and which a wrong
        // reduction (say, one that forgot the ½, or divided by the wrong extent) would break.
        var other = PlanarCurrentDensity.Column(mesh, PlanarBasisDirection.X, midIx + 1)
                                        .OrderBy(c => mesh.Cells[c].CenterY)
                                        .ToList();
        Assert.Equal(column.Count, other.Count);

        double sumA = column.Sum(c => map.Magnitude[c]);
        double sumB = other.Sum(c => map.Magnitude[c]);
        Assert.True(sumA > 0 && sumB > 0);

        for (int k = 0; k < column.Count; k++)
        {
            double a = map.Magnitude[column[k]] / sumA;
            double b = map.Magnitude[other[k]]  / sumB;
            Assert.True(Math.Abs(a - b) <= 0.02 * Math.Max(a, b),
                $"the transverse profile must not depend on where along the line it is cut " +
                $"(cell {k}: {a:G4} vs {b:G4} of the column total)");
        }
    }

    /// <summary>The counterpart, and the reason the previous test is not vacuous: with the edge mesh
    /// OFF the transverse profile is far flatter, because a uniform mesh cannot resolve the
    /// singularity at all.</summary>
    [Fact]
    public void WithTheEdgeMeshOff_TheEdgePeakIsMarkedlyWeaker()
    {
        double Ratio(PlanarMeshSettings settings)
        {
            var (mesh, _, currents) = SolveLine(settings);
            var map = PlanarCurrentDensity.Compute(mesh, currents, 1, FHz);
            int midIx = (mesh.GridX.Count - 1) / 2;
            var column = PlanarCurrentDensity.Column(mesh, PlanarBasisDirection.X, midIx)
                                             .OrderBy(c => mesh.Cells[c].CenterY).ToList();
            return map.Magnitude[column[0]] / map.Magnitude[column[column.Count / 2]];
        }

        double graded = Ratio(Graded);
        double flat   = Ratio(new PlanarMeshSettings(Auto: false, CellsPerWavelength: 8, EdgeMesh: false));

        Assert.True(graded > flat,
            $"edge/middle |J| ratio: graded {graded:G4}, uniform {flat:G4} — the graded mesh must " +
            "resolve more of the edge crowding, or the edge mesh is not earning its cells.");
    }

    // ── R-res-8: the scale is SHOWN, with its units and its normalisation ─────────────────────

    [Fact]
    public void TheScaleCaption_StatesUnitsNormalisationPortAndFrequency()
    {
        var (mesh, _, currents) = SolveLine();
        var map = PlanarCurrentDensity.Compute(mesh, currents, 1, FHz);

        Assert.Contains("A/m",        map.ScaleCaption, StringComparison.Ordinal);
        Assert.Contains("normalised", map.ScaleCaption, StringComparison.Ordinal);
        Assert.Contains("port 1",     map.ScaleCaption, StringComparison.Ordinal);
        Assert.Contains("Hz",         map.ScaleCaption, StringComparison.Ordinal);
        // D5's own scoping, said to the user rather than only to the code.
        Assert.Contains("One port at a time", map.ScaleCaption, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalised_IsZeroToOne_AndNeverNaNOnAFlatMap()
    {
        var mesh = PlanarLineFixtures.MeshAndPorts(
            PlanarLineFixtures.Fr4Line(4e-3, FHz), PlanarLineFixtures.Coarse).Mesh;

        var zero = PlanarCurrentDensity.Compute(mesh, new Vec<Complex>(mesh.Bases.Count), 1, FHz);
        Assert.Equal(0, zero.MaxMagnitude);
        for (int c = 0; c < mesh.Cells.Count; c++) Assert.Equal(0, zero.Normalised(c));

        var (m2, _, currents) = SolveLine();
        var map = PlanarCurrentDensity.Compute(m2, currents, 1, FHz);
        for (int c = 0; c < m2.Cells.Count; c++)
        {
            double t = map.Normalised(c);
            Assert.InRange(t, 0, 1);
        }
        Assert.Equal(1.0, map.Normalised(ArgMax(map.Magnitude)), 12);
    }

    private static int ArgMax(IReadOnlyList<double> v)
    {
        int best = 0;
        for (int i = 1; i < v.Count; i++) if (v[i] > v[best]) best = i;
        return best;
    }

    [Fact]
    public void ASolutionFromADifferentMesh_IsRefusedRatherThanReducedSilently()
    {
        var (mesh, _, _) = SolveLine();
        var ex = Assert.Throws<ArgumentException>(
            () => PlanarCurrentDensity.Compute(mesh, new Vec<Complex>(mesh.Bases.Count + 1), 1, FHz));
        Assert.Contains("not the same solve", ex.Message, StringComparison.Ordinal);
    }
}
