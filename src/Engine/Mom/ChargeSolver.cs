using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// Assembles and solves the boundary-charge system (§3.6) and integrates the result into the
/// <b>Maxwell (short-circuit) capacitance matrix</b> — positive diagonal, negative off-diagonal —
/// which is the [C] multiconductor transmission-line theory wants.
///
/// <para><b>Unknowns.</b> One uniform equivalent-free-space charge density σ_j per segment: free
/// charge on conductor perimeters, bound (polarisation) charge on dielectric interfaces.</para>
///
/// <para><b>Rows.</b>
/// <list type="bullet">
///   <item>conductor segment i on conductor c: <c>Σ_j P_ij σ_j = V_c</c></item>
///   <item>dielectric segment i: <c>σ_i − 2ε₀·K_i·Σ_j F_ij σ_j = 0</c></item>
/// </list>
/// where P_ij is <see cref="Kernel2D.Potential"/> (self term <see cref="Kernel2D.SelfPotential"/>)
/// and F_ij is <see cref="Kernel2D.Field"/> projected onto the observation segment's normal, both
/// including the exact ground image. <c>F_ii = 0</c> — R-mom-5.</para>
///
/// <para><b>Sign check for the dielectric row</b>, which is where this kind of solver silently goes
/// wrong. With n̂ pointing from region 1 into region 2, the field just either side of an interface
/// carrying bound charge σ_b is E_n⁽²⁾ = E_n^avg + σ_b/(2ε₀) and E_n⁽¹⁾ = E_n^avg − σ_b/(2ε₀),
/// with E_n^avg the principal-value field from every <i>other</i> charge. Normal-D continuity
/// ε₁E_n⁽¹⁾ = ε₂E_n⁽²⁾ gives σ_b = 2ε₀·K·E_n^avg, K = (ε₁−ε₂)/(ε₁+ε₂).
/// <i>Concretely:</i> a positive line charge above a dielectric half-space gives K &gt; 0 and a
/// downward E_n^avg, hence <b>negative</b> bound charge — the dielectric is attracted. That matches
/// the textbook image q′ = −q(ε_r−1)/(ε_r+1).</para>
///
/// <para><b>Loss costs one solve</b> (R-mom-6). ε* = ε_r(1 − j·tanδ) throughout makes K complex,
/// so [C] comes out complex, C = C′ − jC″, and the per-unit-length shunt admittance is exactly
/// Y = jω·C_complex = ωC″ + jωC′ — i.e. G = −ω·Im(C) and C = Re(C). G ∝ ω for constant tanδ falls
/// out rather than being asserted, any number of independently lossy dielectrics is handled, and
/// there is no separate partial-capacitance accumulation.</para>
/// </summary>
public static class ChargeSolver
{
    /// <summary>
    /// Excite each conductor in turn with V = 1 (all others at 0), solve, and integrate the charge
    /// per conductor. The dense complex LU is <b>factored once and reused for all M excitations</b>
    /// — they differ only in the right-hand side.
    /// </summary>
    /// <returns>The M×M Maxwell capacitance matrix in F/m (complex, per R-mom-6).</returns>
    public static Mat<Complex> MaxwellCapacitance(EmMesh mesh)
    {
        var segs = mesh.Segments;
        int n = segs.Count;
        int m = mesh.ConductorCount;

        if (n == 0) throw new InvalidOperationException("MoM: cannot solve an empty mesh.");
        if (m == 0) throw new InvalidOperationException("MoM: mesh has no conductors.");

        var a = new Mat<Complex>(n, n);
        var ground = mesh.Ground;

        for (int i = 0; i < n; i++)
        {
            var si  = segs[i];
            var mid = si.Mid;

            if (si.Kind == EmSegmentKind.Conductor)
            {
                for (int j = 0; j < n; j++)
                {
                    var sj = segs[j];
                    double p = i == j
                        ? Kernel2D.SelfPotential(sj.Length)
                        : Kernel2D.Potential(sj.A, sj.B, mid);
                    if (ground is not null)
                        p -= Kernel2D.Potential(Kernel2D.Mirror(sj.A, ground.Y),
                                                Kernel2D.Mirror(sj.B, ground.Y), mid);
                    a[i, j] = p;
                }
            }
            else
            {
                Complex k2 = -2.0 * EmConstants.Eps0 * si.K;
                for (int j = 0; j < n; j++)
                {
                    var sj = segs[j];
                    // R-mom-5: the principal-value self field is zero. The IMAGE of segment i seen
                    // at its own midpoint is a perfectly regular integral and is NOT excluded.
                    var e = i == j ? default : Kernel2D.Field(sj.A, sj.B, mid);
                    if (ground is not null)
                        e -= Kernel2D.Field(Kernel2D.Mirror(sj.A, ground.Y),
                                            Kernel2D.Mirror(sj.B, ground.Y), mid);
                    double fij = e.Dot(si.Normal);
                    a[i, j] = k2 * fij;
                    if (i == j) a[i, j] += Complex.One;
                }
            }
        }

        var lu = a.Lu();
        var c  = new Mat<Complex>(m, m);

        for (int excited = 0; excited < m; excited++)
        {
            var rhs = new Vec<Complex>(n);
            for (int i = 0; i < n; i++)
                rhs[i] = segs[i].Kind == EmSegmentKind.Conductor && segs[i].ConductorIndex == excited
                    ? Complex.One
                    : Complex.Zero;

            var sigma = lu.Solve(rhs);

            for (int i = 0; i < n; i++)
            {
                var si = segs[i];
                if (si.Kind != EmSegmentKind.Conductor) continue;
                // σ_free = ε_r·σ — the dielectric's own bound charge sitting immediately against
                // the metal is folded into the equivalent free-space σ that was solved for.
                c[si.ConductorIndex, excited] += si.EpsOutside * sigma[i] * si.Length;
            }
        }

        return c;
    }

    /// <summary>
    /// The same mesh with every material replaced by air: every K is then zero, so the dielectric
    /// rows drop out entirely and only the conductor block is solved. Cheap — and it is what
    /// [C₀] (hence [L] = µ₀ε₀[C₀]⁻¹ and ε_eff = C/C₀) is built from.
    /// </summary>
    public static EmMesh AirFilled(EmMesh mesh)
    {
        var kept = new List<EmSegment>(mesh.Segments.Count);
        foreach (var s in mesh.Segments)
            if (s.Kind == EmSegmentKind.Conductor)
                kept.Add(s with { EpsOutside = Complex.One });
        return mesh with { Segments = kept };
    }
}
