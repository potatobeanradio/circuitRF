// LFE — THE TRUE EDGE PORT: THE SOURCE RETURNS THROUGH THE GROUND PLANE, SO a₂₁ DOES NOT VANISH WITH ω.
//
// L8d's edge port is a delta gap at the shared edge of the two outermost cells. Its other terminal is
// the outer cell itself, which carries nothing but the port rooftop's own charge pulse — so the
// source sees that cell's capacitance to ground IN SERIES, a₂₁ ∝ ω at the bottom of a band, and the
// peel divides every raw error by |a₂₁|². LFP §3 proved no inversion of that raw measurement can do
// better (the peel is within 2× of the information bound), and §6 measured that a ground-referenced
// port has no wall at all. HISTORY.md §D2 named the shape: "a half-rooftop at the boundary carrying
// the injected current". This file builds it.
//
// THE CONSTRUCTION. For every rooftop m on an edge port's cut, drop the divergence pulse it puts on
// the OUTER cell. The port basis then carries charge only on the inner side, and the charge it moves
// is supplied by the source, whose other terminal is the ground plane — exactly what the ground
// attachment basis already does one dimension over (its single pulse is balanced by the plane's
// image, never by a second pulse on the metal). Tested against the impressed field, the port row now
// reads v = φ_inner + jω∫A·f, i.e. the voltage from the GROUND to the line, plus the drop along the
// feed. The vector part of the rooftop, including its ramp across the outer cell, is untouched: that
// ramp is where the injected current turns from vertical to horizontal, and it is error box.
//
// THE ALGEBRA, AND WHY NOTHING IN THE FILL CHANGES. The scalar block is σ·D P Dᵀ (σ = 1/(jωε₀), D the
// N×cells divergence incidence, P D4's cell-pulse potential). Removing one pulse per port rooftop is
// D' = D − U Sᵀ, where U (N×K) selects the K port rooftops with the outer pulse's own sign and S
// (cells×K) selects their outer cells. Then
//
//     Z' = Z + σ·( −U Wᵀ − W Uᵀ + U Q Uᵀ ),   W = D P S (N×K),   Q = Sᵀ P S (K×K)
//
// a symmetric rank-2K update, applied by Woodbury against the factorisation Z ALREADY HAS:
//
//     Z' = Z + L M Lᵀ,  L = [U W],  M = σ[[Q, −I], [−I, 0]],  M⁻¹ = σ⁻¹[[0, −I], [−I, −Q]]
//     Z'⁻¹b = y − X (M⁻¹ + LᵀX)⁻¹ Lᵀ y,   y = Z⁻¹b,  X = Z⁻¹L
//
// So the dense fill, the LDLᵀ, the accelerator and every core cache are byte-for-byte what they were,
// a run with SeriesGap does not enter this file at all, and the only new integrals are the K columns
// of P the update needs — read through the fill's OWN on-demand P (PlanarPulsePotential), so the
// pulse removed is the pulse the fill put there to the last few ulps.
//
// WHAT IT COSTS: 2K extra right-hand sides per frequency against one factorisation (K = the port
// rooftops, ~10-30 a port), K columns of P, and one dense 2K×2K solve.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>LFE — what an edge port's source returns through.</b> See
/// <see cref="PlanarFillSettings.EdgePortReturn"/> and <see cref="PlanarGroundedEdgePort"/>.
/// </summary>
public enum PlanarEdgePortReturn
{
    /// <summary>L8d's series delta gap against the floating outermost cell. <c>a₂₁ ∝ ω</c> at the
    /// bottom of a band. Every number recorded before LFE.</summary>
    SeriesGap = 0,

    /// <summary>The true edge port: the outer cell's port charge is supplied by the source, which
    /// returns through the ground plane. <c>a₂₁</c> stays O(1) as ω → 0.</summary>
    Ground = 1,
}

/// <summary>
/// <b>LFE — the ground-returning edge port, as an exact low-rank update of the port solve.</b> The
/// file header derives it.
/// </summary>
public static class PlanarGroundedEdgePort
{
    /// <summary>One port rooftop whose outer pulse is removed: the basis, its outer cell, and the
    /// sign that pulse carries in <see cref="PlanarBasisFunctions.Halves"/>.</summary>
    public readonly record struct Terminal(int Basis, int OuterCell, double Sign);

    /// <summary>
    /// <b>Which ports take the grounded return.</b> A ground-referenced EDGE port only: an internal
    /// port already returns through a via, an internal delta gap has metal on both sides and nothing
    /// to ground, and a two-cut (conductor-referenced) port returns through its own drawn conductor.
    /// </summary>
    public static bool Applies(PlanarPortResolution port) =>
        port.Kind == PlanarPortKind.Edge && port.Negative is null &&
        port.Reference == PlanarPortReference.GroundPlane;

    /// <summary>The port rooftops whose outer pulse is removed, over every port that
    /// <see cref="Applies"/>, in port then transverse order.</summary>
    /// <param name="include">brief-icp-2a — which of those ports to ground (null: every one). A run whose edge ports are
    /// series gaps at this frequency still grounds its Uncalibrated Ports, and only them.</param>
    public static IReadOnlyList<Terminal> Terminals(PlanarMesh mesh, IReadOnlyList<PlanarPortResolution> ports,
                                                    Func<PlanarPortResolution, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(ports);
        var list = new List<Terminal>();
        foreach (var port in ports)
        {
            if (!Applies(port) || include is not null && !include(port)) continue;
            bool x = port.Direction == PlanarBasisDirection.X;
            foreach (int m in port.BasisIndices)
            {
                var (a, b) = PlanarBasisFunctions.Halves(mesh, mesh.Bases[m]);
                // The OUTER cell is the one nearer the metal's own outer edge. Asked of the cells'
                // centres rather than of an index order, so a port on either end of either axis needs
                // no case of its own.
                double ca = Centre(mesh.Cells[a.CellIndex], x), cb = Centre(mesh.Cells[b.CellIndex], x);
                var outer = Math.Abs(ca - port.OuterEdgeM) <= Math.Abs(cb - port.OuterEdgeM) ? a : b;
                list.Add(new Terminal(m, outer.CellIndex, outer.Sign));
            }
        }
        return list;
    }

    private static double Centre(PlanarCell c, bool x) => x ? 0.5 * (c.XMin + c.XMax) : 0.5 * (c.YMin + c.YMax);

    /// <summary>
    /// <c>Y = Bᵀ Z′⁻¹ B</c> for the grounded edge port, against <paramref name="system"/>'s own
    /// factorisation of the UNMODIFIED <c>Z</c>. <paramref name="pulse"/> is D4's
    /// <c>P[cellA, cellB]</c> — the very cell-pulse potential the fill assembled its scalar block
    /// from. Falls through to <see cref="PlanarExcitation.Solve(IPlanarOperator, IReadOnlyList{PlanarPortResolution})"/>
    /// unchanged when no port applies.
    /// </summary>
    public static PlanarPortSolution Solve(IPlanarOperator system, PlanarMesh mesh,
                                           IReadOnlyList<PlanarPortResolution> ports,
                                           Func<int, int, Complex> pulse, double omega,
                                           Func<PlanarPortResolution, bool>? include = null)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(pulse);

        var terms = Terminals(mesh, ports, include);
        if (terms.Count == 0) return PlanarExcitation.Solve(system, ports);

        int n = system.Size, k = terms.Count, p = ports.Count;
        if (n != mesh.Bases.Count)
            throw new ArgumentException("Unknown-count mismatch.", nameof(system));     // an internal invariant

        // ── W = D P S: the potential every basis's own two pulses put on each outer cell ────────
        var outerCells = new int[k];
        for (int t = 0; t < k; t++) outerCells[t] = terms[t].OuterCell;

        int cells = mesh.Cells.Count;
        var col = new Complex[k][];
        PlanarFill.ForRowsOf(PlanarFillSettings.Default, k, t =>
        {
            var c = new Complex[cells];
            for (int j = 0; j < cells; j++) c[j] = pulse(outerCells[t], j);
            col[t] = c;
        });

        var halves = new (RooftopHalf A, RooftopHalf B)[n];
        for (int i = 0; i < n; i++) halves[i] = PlanarBasisFunctions.Halves(mesh, mesh.Bases[i]);

        // L = [U W], as 2K column vectors.
        var l = new Vec<Complex>[2 * k];
        for (int t = 0; t < k; t++)
        {
            var u = new Vec<Complex>(n);
            u[terms[t].Basis] = terms[t].Sign;
            l[t] = u;

            var w = new Vec<Complex>(n);
            var ct = col[t];
            for (int i = 0; i < n; i++)
            {
                var (ha, hb) = halves[i];
                w[i] = ha.Sign * ct[ha.CellIndex] + hb.Sign * ct[hb.CellIndex];
            }
            l[k + t] = w;
        }

        // ── one call for B's P columns and L's 2K: the operator streams its factor once ────────
        var rhs = new Vec<Complex>[p + 2 * k];
        for (int j = 0; j < p; j++) rhs[j] = PlanarExcitation.RightHandSide(n, ports[j]);
        for (int c = 0; c < 2 * k; c++) rhs[p + c] = l[c];
        var sol = system.Solve(rhs);

        // ── G = M⁻¹ + Lᵀ X (2K × 2K) ────────────────────────────────────────────────────────
        Complex sigma = 1.0 / (Complex.ImaginaryOne * omega * EmConstants.Eps0);
        Complex inv = 1.0 / sigma;
        var g = new Mat<Complex>(2 * k, 2 * k);
        for (int r = 0; r < 2 * k; r++)
            for (int c = 0; c < 2 * k; c++)
                g[r, c] = Dot(l[r], sol[p + c]);
        for (int t = 0; t < k; t++)
        {
            g[t, k + t]     -= inv;                   // M⁻¹'s −I blocks, ×σ⁻¹
            g[k + t, t]     -= inv;
            for (int u = 0; u < k; u++)
                g[k + t, k + u] -= inv * col[t][outerCells[u]];      // −σ⁻¹Q
        }
        var gLu = g.Lu();

        // ── Z′⁻¹b = y − X G⁻¹ Lᵀ y, per port ────────────────────────────────────────────────
        var currents = new Vec<Complex>[p];
        for (int j = 0; j < p; j++)
        {
            var y = sol[j];
            var ly = new Vec<Complex>(2 * k);
            for (int r = 0; r < 2 * k; r++) ly[r] = Dot(l[r], y);
            var coef = gLu.Solve(ly);
            var x = y.Copy();
            for (int c = 0; c < 2 * k; c++)
            {
                var xc = sol[p + c];
                Complex a = coef[c];
                if (a == Complex.Zero) continue;
                for (int i = 0; i < n; i++) x[i] -= a * xc[i];
            }
            currents[j] = x;
        }

        var yMat = new Mat<Complex>(p, p);
        for (int j = 0; j < p; j++)
            for (int i = 0; i < p; i++)
                yMat[i, j] = PlanarExcitation.PortCurrent(currents[j], ports[i]);
        return new PlanarPortSolution(yMat, currents);
    }

    /// <summary>The bilinear (not Hermitian) product <c>aᵀb</c> — Z is complex-symmetric, so the
    /// Woodbury identity is written in transposes, never in conjugates.</summary>
    private static Complex Dot(Vec<Complex> a, Vec<Complex> b)
    {
        Complex s = Complex.Zero;
        for (int i = 0; i < a.Count; i++) s += a[i] * b[i];
        return s;
    }
}
