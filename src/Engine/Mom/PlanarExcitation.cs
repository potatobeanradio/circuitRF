// L8d — D1: A PORT IS AN INCIDENCE MATRIX, AND RECIPROCITY IS STRUCTURAL AGAIN.
//
// L8c filled and factored a matrix nobody excited. This is the right-hand side it was waiting for,
// and it is twenty lines because L8c chose the basis normalisation that makes it twenty lines:
//
//   f is normalised to UNIT TOTAL CURRENT ACROSS THE SHARED EDGE (PlanarBasisFunctions' header), so
//   the reaction of a delta-gap of v volts across that edge with the basis that spans it is
//
//       ⟨f_m, E^imp⟩ = ∫ f_m·Ê · v dℓ = v · 1 = v
//
//   exactly — no gap width, no aspect ratio, no quadrature. A port that spans several cells across
//   the feed drives every rooftop in that row with the same v, because they are all part of one
//   conductor at one potential.
//
// With B the N×P incidence matrix carrying ±1 on each port's row:
//
//       V = B·v          (the impressed voltages)
//       i = Bᵀ·I         (the port current is the signed sum of that row's basis currents)
//   ⇒   Y = Bᵀ Z⁻¹ B     — P back-substitutions against ONE factorisation.
//
// SAY THE STRENGTH OF THE RECIPROCITY PRECISELY, as L7b-b's own note insists: Z is symmetric BIT FOR
// BIT (L8c computes m ≤ n and mirrors), so Y is symmetric because BᵀZ⁻¹B is — but it passes through
// a factorisation, so it is symmetric to that routine's tolerance and NOT bit for bit. Claiming
// otherwise would be the overclaim L7b-b explicitly warns against. P7 (2026-08-29) changed WHICH
// factorisation — a complex-symmetric LDLᵀ that exploits the bit-for-bit symmetry above rather than
// a general LU that ignores it — and changed nothing about the strength of the claim.
//
// THE SAME B IS USED FOR THE EXCITATION AND FOR READING THE CURRENT BACK. That is not tidiness: it
// is what makes Y the actual admittance matrix (the pair (v, i) has to be energy-conjugate). A code
// that impresses +1 everywhere and then reads currents back with a side-dependent sign produces a
// smooth, plausible, wrong S₂₁ — a hard π, invisible in a magnitude plot.

using System.Numerics;
using NumFlat;
using RfCore;

namespace CircuitRF.Engine.Mom;

/// <summary>One frequency's excited solution: the port admittance matrix and the currents behind it.</summary>
/// <param name="Y">P×P admittance, <c>Y = BᵀZ⁻¹B</c>.</param>
/// <param name="Currents">
/// <c>Currents[j]</c> is the full basis-current vector when port <i>j</i> alone is driven at 1 V.
/// Kept because Tier 1's independent γ oracle reads the travelling wave straight off it — and
/// because L8e's current-density heat map will need exactly this.
/// </param>
public sealed record PlanarPortSolution(Mat<Complex> Y, IReadOnlyList<Vec<Complex>> Currents);

public static class PlanarExcitation
{
    /// <summary>The right-hand side for driving one port at 1 V — B's column <i>j</i>.</summary>
    public static Vec<Complex> RightHandSide(int unknownCount, PlanarPortResolution port)
    {
        ArgumentNullException.ThrowIfNull(port);
        var rhs = new Vec<Complex>(unknownCount);
        foreach (int m in port.BasisIndices) rhs[m] = port.IncidenceSign;
        return rhs;
    }

    /// <summary>
    /// <c>Y = BᵀZ⁻¹B</c> — one factorisation, P back-substitutions (D8). The factorisation is
    /// <see cref="PlanarSystem.Factor"/>'s, computed once and cached on the system.
    /// </summary>
    public static PlanarPortSolution Solve(PlanarSystem system, IReadOnlyList<PlanarPortResolution> ports)
        => Solve((IPlanarOperator)system, ports);

    /// <summary>
    /// <b>M5 — the same port algebra against ANY operator</b>, so the dense LU and the accelerated
    /// GMRES share one <c>Y = BᵀZ⁻¹B</c> rather than two copies of it. The comment at the top of this
    /// file is about a sign convention that produces a plausible, wrong S₂₁ when it drifts; a second
    /// copy of this loop is precisely where it would drift.
    ///
    /// <para>What DOES change with the operator is the strength of the reciprocity claim, and it is
    /// already stated correctly above: Z is symmetric bit for bit, so Y is symmetric — through a
    /// factorisation, hence to that routine's tolerance. Through GMRES it is to the ITERATIVE
    /// tolerance instead, which is looser and is the honest thing to say about it.</para>
    /// </summary>
    public static PlanarPortSolution Solve(IPlanarOperator system, IReadOnlyList<PlanarPortResolution> ports)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(ports);
        if (ports.Count == 0) throw new ArgumentException("A solve needs at least one port.", nameof(ports));

        int p = ports.Count;
        var rhs = new Vec<Complex>[p];
        for (int j = 0; j < p; j++) rhs[j] = RightHandSide(system.Size, ports[j]);

        // P7 — ONE call, so an operator that can stream its factor once for all P vectors does.
        // The default implementation on IPlanarOperator is the per-port loop this replaced.
        var currents = system.Solve(rhs);

        var y = new Mat<Complex>(p, p);
        for (int j = 0; j < p; j++)
            for (int i = 0; i < p; i++)
            {
                Complex sum = Complex.Zero;
                var col = currents[j];
                foreach (int m in ports[i].BasisIndices) sum += col[m];
                y[i, j] = ports[i].IncidenceSign * sum;
            }

        return new PlanarPortSolution(y, currents);
    }

    /// <summary>
    /// The RAW s-parameters — port discontinuity and all. <b>R-prt-4: this is never the answer.</b>
    /// It is what the calibration consumes and what the diagnostics show; the published result comes
    /// out of <see cref="PlanarDeembed"/>.
    /// </summary>
    public static Mat<Complex> RawScattering(Mat<Complex> y, Complex[] z0)
        => RFNetwork.YToS(y, z0);

    /// <summary>The declared reference impedances of a port list, in port order.</summary>
    public static Complex[] ReferenceImpedances(IReadOnlyList<PlanarPortResolution> ports)
    {
        var z = new Complex[ports.Count];
        for (int i = 0; i < ports.Count; i++) z[i] = ports[i].Z0;
        return z;
    }

    /// <summary>
    /// The total current crossing the port's own cut, for a given solution column. This is
    /// <c>i = Bᵀ I</c> for one port and it is what Tier 1's travelling-wave oracle marches along the
    /// line — see <see cref="LineCurrent"/>.
    /// </summary>
    public static Complex PortCurrent(Vec<Complex> currents, PlanarPortResolution port)
    {
        Complex sum = Complex.Zero;
        foreach (int m in port.BasisIndices) sum += currents[m];
        return port.IncidenceSign * sum;
    }

    /// <summary>
    /// The total longitudinal current crossing each internal gridline of a uniform run — the
    /// discrete <c>I(z)</c>. Tier 1 reads γ straight out of this with no calibration, no T-matrix
    /// and no error box anywhere in the path.
    ///
    /// <para>Summed ACROSS the width rather than sampled on one row: the transverse profile is the
    /// same at every station in the uniform region, so summing is the same quantity with less
    /// discretisation noise — and it is exactly the current a transmission-line model means.</para>
    /// </summary>
    /// <returns>(the cut coordinate, the current there), ordered by coordinate.</returns>
    public static IReadOnlyList<(double Coord, Complex I)> LineCurrent(
        PlanarMesh mesh, Vec<Complex> currents, PlanarBasisDirection direction,
        double transverseLo, double transverseHi)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        bool alongX = direction == PlanarBasisDirection.X;

        var byCut = new SortedDictionary<double, Complex>();
        for (int b = 0; b < mesh.Bases.Count; b++)
        {
            var bs = mesh.Bases[b];
            if (bs.Direction != direction) continue;

            var a = mesh.Cells[bs.CellA];
            double t0 = alongX ? a.YMin : a.XMin;
            double t1 = alongX ? a.YMax : a.XMax;
            if (t0 < transverseLo - 1e-15 || t1 > transverseHi + 1e-15) continue;

            double cut = alongX ? a.XMax : a.YMax;
            byCut.TryGetValue(cut, out var acc);
            byCut[cut] = acc + currents[b];
        }

        var list = new List<(double, Complex)>(byCut.Count);
        foreach (var kv in byCut) list.Add((kv.Key, kv.Value));
        return list;
    }
}
