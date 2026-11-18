// L8d — D7 (the reference impedance) on the planar kernel's own electrostatics.
//
// D6 (the error box), the peel and the renormalisations moved to `src/Engine/Calibration/LineDeembed.cs` at PWL-1, so the
// planar solver and the 3D solvers share one implementation; what stays here is the planar SOURCE of C_pul and the planar
// calibration's per-port record.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// D7 — THE DE-EMBEDDED S IS REFERENCED TO THE LINE'S OWN Z_c, AND THE CALIBRATION CANNOT FIND IT
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// This is a fact about the method, not a gap in the implementation: the error-box algebra (`Calibration/LineDeembed.cs`) assumed the
// section between the planes is a MATCHED line, [[0,x],[x,0]], which is only true in the line's own
// Z_c. So:
//
//   • The de-embedding's accuracy and Z_c's accuracy are SEPARABLE and are reported separately. The
//     third-line gate lives entirely in the Z_c reference and is blind to Z_c's value.
//   • Z_c = γ/(jωC_pul), with C_pul from DIFFERENCING the two standards' static capacitances, so the
//     end effects cancel EXACTLY rather than being neglected. C(ℓ) is L8c's own
//     PlanarFill.ScalarPotentialMatrix at ω → 0, which is a product surface and already gated.
//   • C_pul is QUASI-STATIC, so Z_c inherits that. R-prt-8 measures the size of it rather than
//     waving at it.
//   • Kernel A is the ORACLE for Z_c, never an input. Reading Z_c or C_pul off QuasiStaticKernel and
//     feeding it into B would make the phase table's own "A and B agree on a uniform line" gate a
//     tautology and would import A's discretisation error into B's answer.
//   • The final renormalisation is RFNetwork.SToS. R-mom-14's rule, again: no second implementation.

using System.Numerics;
using NumFlat;
using RfCore;
using CircuitRF.Engine.Calibration;

namespace CircuitRF.Engine.Mom;

/// <summary>The whole per-port calibration at one frequency: γ, the error box, and Z_c.</summary>
/// <param name="NeighbourResonanceDegrees">
/// <b>PCAL3 — how far this frequency is from the reproduced NEIGHBOUR's own half-wave resonance, in
/// degrees of electrical length, and <c>NaN</c> on every port whose standard has no neighbour in
/// it.</b>
///
/// <para>A widened standard contains a conductor that is open at both ends and driven by nothing, so
/// it is a resonator — at <c>βL = nπ</c> its own standing wave dominates the standard's 2-port and
/// the two-line cascade stops describing a single mode. <b>The DUT's neighbour does not resonate
/// there</b>: it is whatever length the user drew, and the standard's length is chosen by
/// <see cref="PlanarCalibration.SuggestDeltas"/>. So this is an artefact of the instrument and it is
/// reported rather than absorbed — measured on the series' own fixture at |ΔS| 0.22 against a floor
/// of 0.052, while every frequency more than ~25° away sits AT that floor.</para>
///
/// <para>Taken over both standards this frequency actually read (the short line and the selected long
/// one), because the error box is solved from the pair and either one resonating contaminates it.</para>
/// </param>
/// <summary>
/// <b>QSC — where this point's γ came from, and it rides on every calibration rather than on the
/// run.</b> A sweep that crosses <see cref="PlanarCalibration.QuasiStaticCrossoverHz"/> takes both
/// paths, so "which calibration produced this point" is a per-point question; RAW1 §5 is what
/// happens when a diagnostic flag cannot distinguish "measured" from "not measured".
/// </summary>
public enum PlanarCalibrationSource
{
    /// <summary>D5's two-line extraction — γ from ½·tr(M) on two full-wave standards.</summary>
    Measured,

    /// <summary><see cref="PlanarQuasiStaticLine"/> — γ = jω√(LC) from the standards' own
    /// electrostatics. The error box is still solved from two standards; only γ's SOURCE differs.</summary>
    QuasiStatic,
}

public sealed record PlanarPortCalibration(
    int                            PortNumber,
    LineCalibration.GammaResult    Gamma,
    LineErrorBox                   Box,
    Complex                        Zc,
    double                         CPerMetre,
    double                         NeighbourResonanceDegrees = double.NaN,
    PlanarCalibrationSource        Source = PlanarCalibrationSource.Measured);

public static class PlanarDeembed
{
    // ══════════════════════════════════════════════════════════════════════════════════════════
    // D7 — the reference impedance
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The static capacitance of a whole meshed sheet to ground, at ω → 0. This is L8c's Tier 5
    /// harness promoted from the test project, because D7 needs it in production — it is assembled
    /// from <see cref="PlanarFill.ScalarPotentialMatrix"/>, which was already a product surface.
    /// </summary>
    /// <param name="cores">
    /// <b>P2/M3 — this mesh's already-built cores, when the caller has them.</b> A calibration
    /// standard's <c>PlanarSolveContext</c> holds cores for exactly this mesh and exactly these fill
    /// settings, and rebuilding them here is a second O(m²) core build of a mesh already cored. Null
    /// (or a geometry-only core, which the accelerator's contexts carry) falls back to building them,
    /// which is what this method always did.
    /// </param>
    /// <param name="slabHeightM">
    /// <b>P11 — required whenever <paramref name="settings"/> asks for the accelerator</b>, and
    /// ignored on the dense path (which is why it is optional rather than positional): the
    /// accelerated static solve's near radius has a floor of 2h under it and h is not derivable from
    /// a mesh. Omitting it on an accelerated call throws rather than quietly solving densely, because
    /// a silent dense fallback is exactly the ceiling this phase exists to remove.
    /// </param>
    /// <param name="potential">
    /// <b>RP-2c — the per-cell potential the system is driven with, or null for the whole sheet at
    /// 1 V.</b> A coplanar standard hands its <see cref="PlanarStandard.ModePotential"/> here, so the
    /// capacitance measured is the one the PORT's mode sees rather than the whole pair's capacitance
    /// to the plane. Null is L8d's own arithmetic, bit for bit.
    /// </param>
    /// <param name="weight">
    /// What the returned charge sums with, per cell; null totals every cell. See
    /// <see cref="PlanarStandard.ModeWeight"/>.
    /// </param>
    /// <param name="floating">
    /// <b>PCAL3 — the cells of a conductor that is present but connected to NOTHING, which is a
    /// CONSTRAINT rather than a potential and is why it cannot ride on <paramref name="potential"/>.</b>
    /// An unconnected passive trace beside a calibrated feed carries zero NET charge and floats to
    /// whatever potential that implies; it is not at the line's potential (which would measure the
    /// two of them bonded together) and it is not at ground (which is a ground pour, a different
    /// structure). Both readings are complete and plausible and they are <b>12.3 % apart</b> at
    /// s/h = 0.27 on the series' own fixture, which lands directly on Z_c and therefore on every
    /// published s-parameter.
    ///
    /// <para>It costs ONE extra right-hand side and no extra factorisation: solve for the unit
    /// potential and for the mask, then take the combination whose net charge on the mask is zero.
    /// Null is RP-2c's arithmetic, bit for bit.</para>
    /// </param>
    public static double StaticCapacitance(PlanarMesh mesh, PlanarKernelTerms staticScalar,
                                           PlanarFillSettings? settings = null,
                                           PlanarFillCores? cores = null,
                                           double slabHeightM = 0,
                                           IReadOnlyList<double>? potential = null,
                                           IReadOnlyList<double>? weight = null,
                                           IReadOnlyList<double>? floating = null)
        => StaticCapacitanceComplex(mesh, staticScalar, settings, cores, slabHeightM,
                                    potential, weight, floating).Real;

    /// <summary>
    /// <b>QSC — <see cref="StaticCapacitance"/> with the imaginary part KEPT</b>, which is the whole
    /// body and the one the double-valued reading above is <c>.Real</c> of.
    ///
    /// <para><c>Y = jωC</c> is exactly <c>G + jωC</c> when the loss rides in the imaginary part
    /// (R-mom-6), and <see cref="PlanarKernelTerms.StaticScalar"/> is built on
    /// <see cref="GroundedSlab.EpsComplex"/> — so the charge this solve returns already carries the
    /// dielectric's tanδ and <c>.Real</c> is where it was being dropped. A quasi-static
    /// <c>γ = jω√(LC)</c> needs it: without it the reported α would be exactly zero on a lossy
    /// board, which is a plausible, wrong number of the kind this file's headers keep warning
    /// about. D7's Z_c still takes a double and is untouched.</para>
    /// </summary>
    public static Complex StaticCapacitanceComplex(
        PlanarMesh mesh, PlanarKernelTerms staticScalar,
        PlanarFillSettings? settings = null,
        PlanarFillCores? cores = null,
        double slabHeightM = 0,
        IReadOnlyList<double>? potential = null,
        IReadOnlyList<double>? weight = null,
        IReadOnlyList<double>? floating = null)
    {
        var q = StaticChargeComplex(mesh, staticScalar, settings, cores, slabHeightM,
                                    potential, floating);

        Complex total = Complex.Zero;
        if (weight is null) for (int i = 0; i < q.Length; i++) total += q[i];
        else                for (int i = 0; i < q.Length; i++) total += weight[i] * q[i];
        return total;
    }

    /// <summary>
    /// <b>CL3 — the solved charge vector itself, which is the body
    /// <see cref="StaticCapacitanceComplex"/> is the weighted SUM of.</b>
    ///
    /// <para>A quasi-static conductor term needs the charge's SECOND moment (<c>Σ|q|²/A</c>) beside
    /// its first, and the two have to come off one solve: a second electrostatic solve for the
    /// second moment would be a second answer to the same question, and the whole reason the
    /// quasi-static path can afford a conductor term at all is that this solve is one the run
    /// already owes. See <see cref="PlanarQuasiStaticLine"/>.</para>
    ///
    /// <para>Both routes are the ones that shipped, unchanged: the dense branch is P2/M2's
    /// <c>P q = ε₀·1</c>, and the accelerated one is <see cref="PlanarStaticAim.ChargeComplex"/>,
    /// whose all-1 V drive is <c>ε₀·1.0 == ε₀</c> exactly.</para>
    /// </summary>
    public static Complex[] StaticChargeComplex(
        PlanarMesh mesh, PlanarKernelTerms staticScalar,
        PlanarFillSettings? settings = null,
        PlanarFillCores? cores = null,
        double slabHeightM = 0,
        IReadOnlyList<double>? potential = null,
        IReadOnlyList<double>? floating = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        var st = settings ?? PlanarFillSettings.Default;

        if (potential is not null && potential.Count != mesh.Cells.Count)
            throw new ArgumentException(
                $"The mode potential has {potential.Count} entries for a mesh of {mesh.Cells.Count} " +
                "cells.", nameof(potential));

        // ── P11 — the accelerated route ───────────────────────────────────────────────────────
        //
        // P IS the operator M5 already projects (the scalar block, with the static kernel), so an
        // accelerated run's reference impedance no longer needs a dense m×m LU. The dense branch
        // below is untouched and is what a null Aim still runs, bit for bit.
        if (st.Aim is { } aim)
        {
            if (!(slabHeightM > 0))
                throw new ArgumentOutOfRangeException(nameof(slabHeightM), slabHeightM,
                    "An ACCELERATED static capacitance solve needs the slab height: P8's near-radius " +
                    "floor is 2h and h cannot be read off a mesh. Pass the problem's own Slab.HeightM.");

            GuardCapacitanceCeiling(mesh, accelerated: true, st.MemoryLimitBytes);

            // Geometry-only cores are the right shape here and are what an accelerated context holds
            // — the O(m²) pair cores are exactly the build this route exists to skip.
            var gc = cores is not null && ReferenceEquals(cores.Mesh, mesh)
                   ? cores
                   : PlanarFill.BuildGeometryOnlyCores(mesh, st);

            var acc = PlanarStaticAim.Build(gc, staticScalar, slabHeightM, aim);
            return acc.ChargeComplex(potential, floating);
        }

        GuardCapacitanceCeiling(mesh, accelerated: false, st.MemoryLimitBytes);

        // The mesh identity is checked rather than assumed: the cores carry their own Mesh, and a
        // core built for a DIFFERENT mesh would index a packed triangle of the wrong length and give
        // a plausible wrong capacitance rather than an exception.
        var c = cores is { HasPairCores: true } && ReferenceEquals(cores.Mesh, mesh)
              ? cores
              : PlanarFill.BuildCores(mesh, st);

        var p = PlanarFill.ScalarPotentialMatrix(c, staticScalar.With(st.Order, c.RhoFloorM));

        // ── P2/M2 — SOLVE P q = ε₀·1, rather than copying P into an m×m scaled by 1/ε₀ ──────────
        //
        // The system being solved is (P/ε₀) q = 1. Dividing every entry of P allocated a SECOND m×m
        // complex matrix — at the de-embedding ceiling that is the same size as the one the fill just
        // built — to express a scaling that the right-hand side carries for free. It is also one
        // rounding per entry that this form does not do at all: ε₀·1 is exact, so the factored matrix
        // is now the fill's own P bit for bit and only the SOLVE's own arithmetic differs.
        int m = mesh.Cells.Count;
        var rhs = new Vec<Complex>(m);
        if (potential is null) for (int i = 0; i < m; i++) rhs[i] = EmConstants.Eps0;
        else                   for (int i = 0; i < m; i++) rhs[i] = EmConstants.Eps0 * potential[i];

        var lu = p.Lu();
        var q  = lu.Solve(rhs);

        // PCAL3 — the floating conductor's own potential is the unknown, so a SECOND right-hand side
        // (the mask at 1 V) spans it and the combination that leaves its net charge at zero is the
        // one the structure is actually in. Same factorisation; one more back-substitution.
        if (floating is not null)
        {
            var rhsF = new Vec<Complex>(m);
            for (int i = 0; i < m; i++) rhsF[i] = EmConstants.Eps0 * floating[i];
            var qf = lu.Solve(rhsF);

            Complex qa = Complex.Zero, qb = Complex.Zero;
            for (int i = 0; i < m; i++) { qa += floating[i] * q[i]; qb += floating[i] * qf[i]; }
            if (qb != Complex.Zero)
            {
                Complex alpha = -qa / qb;
                for (int i = 0; i < m; i++) q[i] += alpha * qf[i];
            }
        }

        var outq = new Complex[m];
        for (int i = 0; i < m; i++) outq[i] = q[i];
        return outq;
    }

    /// <summary>
    /// <b>PCAL4/D7' — the N×N static capacitance MATRIX of a calibration group's standard.</b>
    /// Conductor k is driven at 1 V with every other conductor at 0 V, and
    /// <c>C[j,k] = Σ_{cells of j} q</c> — the Maxwell capacitance matrix, negative off the diagonal.
    ///
    /// <para>It is <see cref="StaticCapacitance"/>'s arithmetic with N right-hand sides instead of
    /// one, and on the dense route it is <b>one factorisation and N back-substitutions</b>: a group
    /// of N conductors costs N times the summation, not N times the solve. The accelerated route has
    /// no factorisation to share and pays N GMRES solves.</para>
    ///
    /// <para><b>The result is COMPLEX and that is R-mom-6, not sloppiness</b> — <c>Y = jω·C</c> is
    /// exactly <c>G + jωC</c> when the loss rides in the imaginary part, so a mode's shunt
    /// admittance stays one complex number and there is no separate G matrix to get wrong. The
    /// scalar path above returns <c>.Real</c> because D7's Z_c takes a double; nothing there
    /// changes.</para>
    /// </summary>
    /// <param name="conductorOfCell">Which conductor each cell belongs to, or −1 for none.</param>
    /// <param name="conductorCount">How many conductors — the matrix order.</param>
    public static Mat<Complex> StaticCapacitanceMatrix(
        PlanarMesh mesh, PlanarKernelTerms staticScalar,
        IReadOnlyList<int> conductorOfCell, int conductorCount,
        PlanarFillSettings? settings = null, PlanarFillCores? cores = null,
        double slabHeightM = 0)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(conductorOfCell);
        if (conductorOfCell.Count != mesh.Cells.Count)
            throw new ArgumentException(
                $"The conductor map has {conductorOfCell.Count} entries for a mesh of " +
                $"{mesh.Cells.Count} cells.", nameof(conductorOfCell));

        var st = settings ?? PlanarFillSettings.Default;
        int m  = mesh.Cells.Count;
        var result = new Mat<Complex>(conductorCount, conductorCount);

        var drives = new double[conductorCount][];
        for (int k = 0; k < conductorCount; k++)
        {
            drives[k] = new double[m];
            for (int i = 0; i < m; i++) drives[k][i] = conductorOfCell[i] == k ? 1.0 : 0.0;
        }

        void Accumulate(int k, IReadOnlyList<Complex> q)
        {
            for (int i = 0; i < m; i++)
            {
                int j = conductorOfCell[i];
                if (j >= 0) result[j, k] += q[i];
            }
        }

        if (st.Aim is { } aim)
        {
            if (!(slabHeightM > 0))
                throw new ArgumentOutOfRangeException(nameof(slabHeightM), slabHeightM,
                    "An ACCELERATED static capacitance solve needs the slab height: P8's near-radius " +
                    "floor is 2h and h cannot be read off a mesh. Pass the problem's own Slab.HeightM.");

            GuardCapacitanceCeiling(mesh, accelerated: true, st.MemoryLimitBytes);

            var gc = cores is not null && ReferenceEquals(cores.Mesh, mesh)
                   ? cores
                   : PlanarFill.BuildGeometryOnlyCores(mesh, st);
            var acc = PlanarStaticAim.Build(gc, staticScalar, slabHeightM, aim);

            for (int k = 0; k < conductorCount; k++) Accumulate(k, acc.ChargeFor(drives[k]));
            return result;
        }

        GuardCapacitanceCeiling(mesh, accelerated: false, st.MemoryLimitBytes);

        var c = cores is { HasPairCores: true } && ReferenceEquals(cores.Mesh, mesh)
              ? cores
              : PlanarFill.BuildCores(mesh, st);
        var p = PlanarFill.ScalarPotentialMatrix(c, staticScalar.With(st.Order, c.RhoFloorM));
        var lu = p.Lu();

        for (int k = 0; k < conductorCount; k++)
        {
            var rhs = new Vec<Complex>(m);
            for (int i = 0; i < m; i++) rhs[i] = EmConstants.Eps0 * drives[k][i];
            Accumulate(k, lu.Solve(rhs));
        }
        return result;
    }

    /// <summary>
    /// <b>PCAL4/D7' — [C] per unit length for a group, by DIFFERENCING the two standards'
    /// capacitance matrices.</b> D7's own construction, one conductor wider: the two standards are
    /// identical except for the bulk cells in the middle, so every end effect cancels EXACTLY rather
    /// than being neglected.
    /// </summary>
    /// <param name="airFilled">
    /// <b>Solve the same geometry with the dielectric removed.</b> The modal voltage matrix needs
    /// [L], and the quasi-TEM relation <c>[L] = μ₀ε₀[C₀]⁻¹</c> is the only route to it that does not
    /// import a second kernel's answer. It doubles the electrostatic solves and is reported as such
    /// (R-pcal4-7).
    /// </param>
    public static Mat<Complex> CapacitanceMatrixPerMetre(
        PlanarStandard shortStd, PlanarStandard longStd, GroundedSlab slab,
        IReadOnlyList<int> shortConductorOfCell, IReadOnlyList<int> longConductorOfCell,
        int conductorCount, bool airFilled,
        PlanarFillSettings? settings = null,
        PlanarFillCores? shortCores = null, PlanarFillCores? longCores = null)
    {
        ArgumentNullException.ThrowIfNull(shortStd);
        ArgumentNullException.ThrowIfNull(longStd);

        var medium = airFilled ? slab with { Material = new EmMaterial(1.0, 0.0) } : slab;
        var terms  = PlanarKernelTerms.StaticScalar(medium);

        var c1 = StaticCapacitanceMatrix(shortStd.Mesh, terms, shortConductorOfCell, conductorCount,
                                         settings, shortCores, slab.HeightM);
        var c2 = StaticCapacitanceMatrix(longStd.Mesh, terms, longConductorOfCell, conductorCount,
                                         settings, longCores, slab.HeightM);

        double dl = longStd.LengthM - shortStd.LengthM;
        if (!(dl > 0))
            throw new InvalidOperationException("The two calibration standards have the same length.");

        var c = new Mat<Complex>(conductorCount, conductorCount);
        for (int i = 0; i < conductorCount; i++)
            for (int j = 0; j < conductorCount; j++)
                c[i, j] = (c2[i, j] - c1[i, j]) / dl;
        return c;
    }

    /// <summary>
    /// C2 (brief-em-deembed-ceiling-closeout.md) — <see cref="PlanarFill.BuildCores"/>'s own shared
    /// guard asks about <c>mesh.Bases.Count</c> and quotes an n×n DENSE COMPLEX MATRIX, because that
    /// is what its OTHER callers (<see cref="PlanarFill.Fill"/> / <c>PlanarSystem.Build</c>) go on to
    /// allocate. <see cref="StaticCapacitance"/> never does: its own working set is THREE m×m complex
    /// matrices over CELLS (<see cref="PlanarFill.ScalarPotentialMatrix"/>'s own P, plus the L and U
    /// the general LU builds beside it — P1's own <c>PlanarSystem.FactorBytes</c> measurement) — a
    /// different, and materially smaller, number, because a mesh's
    /// basis count runs roughly 2× its cell count (an ordinary tensor grid, same ratio §L8b's own N
    /// report states generally). Measured, not estimated, on a real standard —
    /// <c>EmDeembedCeilingTests</c> carries the ratio — exactly like §7's own "381 MB vs 607 MB"
    /// defect this is the same class of: quote what a machine will actually see.
    ///
    /// <para>The THRESHOLD stays <see cref="SurfaceMesher.UnknownCeiling"/> asked of <c>n</c> —
    /// unchanged, and deliberately so: <see cref="PlanarFill.BuildCores"/> is shared by callers for
    /// whom <c>n</c> genuinely is the right question (its own guard's comment), and this does not
    /// tighten or loosen that. It only replaces the MESSAGE a caller reaching the ceiling through
    /// <see cref="StaticCapacitance"/> would otherwise see with one describing what this call site
    /// actually allocates.</para>
    /// </summary>
    /// <param name="accelerated">
    /// <b>P11 — which ceiling this call site is judged against.</b> Public, and public for the reason
    /// this area already states about its instruments: the decision it makes is the whole of what
    /// changed here, and a test that had to run the SOLVE to observe it would be paying minutes to
    /// read one comparison. <see cref="StaticCapacitance"/> calls it with its own route's flag.
    /// </param>
    public static void GuardCapacitanceCeiling(PlanarMesh mesh, bool accelerated, long? limit = null)
    {
        int n = mesh.Bases.Count;

        // A memory limit is advisory: the run priced this step with its standards and asked about it
        // before it started (PlanarRunMemory), so a run that carries one is let through.
        if (limit is not null) return;

        // P11 — the accelerated route holds no m×m anything, so the DENSE ceiling is the wrong
        // question to ask of it, exactly as PlanarSolveContext's constructor already reasons about
        // the DUT's own system. The threshold is the same one the accelerated DUT is judged against.
        if (accelerated)
        {
            if (n <= SurfaceMesher.AcceleratedUnknownCeiling) return;
            throw new InvalidOperationException(
                $"This calibration standard's static capacitance solve (D7's reference impedance) " +
                $"needs {mesh.Cells.Count:N0} cells ({n:N0} basis functions), past the " +
                $"{SurfaceMesher.AcceleratedUnknownCeiling:N0}-unknown ACCELERATED ceiling this " +
                "kernel is built for (brief-em-aim-ceiling.md). The static solve is accelerated too " +
                "(P11), so it is not what bounds this run — the DUT's own mesh is judged against the " +
                "same number.");
        }

        if (n <= SurfaceMesher.UnknownCeiling) return;

        int m = mesh.Cells.Count;
        // P2/M2 removed the scaled COPY of P this used to count as its second matrix; what is left
        // at the peak is P and the general LU's own L and U, which P1 measured as two further full
        // matrices rather than a packed in-place factorisation.
        double mb = 3.0 * m * (double)m * 16.0 / (1024.0 * 1024.0);
        throw new InvalidOperationException(
            $"This calibration standard's static capacitance solve (D7's reference impedance) needs " +
            $"{m:N0} cells ({n:N0} basis functions), past the {SurfaceMesher.UnknownCeiling:N0}-" +
            $"unknown ceiling — {mb:N0} MB for the three m×m complex matrices this solve holds at " +
            "once (the potential-coefficient matrix, and the L and U a general LU builds beside it), " +
            "not the n×n matrix PlanarFill's shared fill guard describes, because this solve never " +
            "builds one.");
    }

    /// <summary>
    /// C per unit length, by DIFFERENCING the two standards' total static capacitances. The two
    /// lines are identical except for the bulk cells in the middle, so both end effects — the open
    /// ends and the port neighbourhoods — cancel EXACTLY rather than being neglected.
    /// </summary>
    /// <param name="shortCores">The short standard's already-built cores — see
    /// <see cref="StaticCapacitance"/>'s own parameter. <b>P2/M3.</b></param>
    /// <param name="longCores">The long standard's, likewise.</param>
    public static double CapacitancePerMetre(PlanarStandard shortStd, PlanarStandard longStd,
                                             GroundedSlab slab, PlanarFillSettings? settings = null,
                                             PlanarFillCores? shortCores = null,
                                             PlanarFillCores? longCores = null)
        => CapacitancePerMetreComplex(shortStd, longStd, slab, settings, shortCores, longCores).Real;

    /// <summary>
    /// <b>QSC — the same differencing with the imaginary part KEPT</b>; see
    /// <see cref="StaticCapacitanceComplex"/>. The scalar reading above is this, <c>.Real</c>.
    /// </summary>
    /// <param name="airFilled">
    /// <b>Solve the same geometry with the dielectric removed</b>, which is the only route to [L]
    /// that does not import a second kernel's answer: <c>L = μ₀ε₀/C₀</c>.
    /// <see cref="CapacitanceMatrixPerMetre"/>'s own parameter, one conductor down.
    /// </param>
    public static Complex CapacitancePerMetreComplex(
        PlanarStandard shortStd, PlanarStandard longStd, GroundedSlab slab,
        PlanarFillSettings? settings = null,
        PlanarFillCores? shortCores = null, PlanarFillCores? longCores = null,
        bool airFilled = false)
    {
        var medium = airFilled ? slab with { Material = new EmMaterial(1.0, 0.0) } : slab;
        var terms  = PlanarKernelTerms.StaticScalar(medium);
        Complex c1 = StaticCapacitanceComplex(shortStd.Mesh, terms, settings, shortCores, slab.HeightM,
                                              shortStd.ModePotential, shortStd.ModeWeight,
                                              shortStd.FloatingPotential);
        Complex c2 = StaticCapacitanceComplex(longStd.Mesh,  terms, settings, longCores, slab.HeightM,
                                              longStd.ModePotential, longStd.ModeWeight,
                                              longStd.FloatingPotential);
        double dl = longStd.LengthM - shortStd.LengthM;

        if (!(dl > 0))
            throw new InvalidOperationException("The two calibration standards have the same length.");
        return (c2 - c1) / dl;
    }

    /// <summary>
    /// <b>MIM-4 — the same differencing, for a standard on a level at ANY height in a general
    /// stack.</b> Only the electrostatic kernel changes: <see cref="PlanarKernelTerms.StaticScalarAt"/>
    /// in place of the one-slab image series, at the level's own z. The two lines, the differencing,
    /// the end-effect cancellation and every guard are the ones above.
    ///
    /// <para><b>The overload above is untouched and is what the on-slab-top path still calls</b> —
    /// R-mlp-1. This one is reached only where <c>PlanarSolve</c> used to throw.</para>
    /// </summary>
    /// <param name="stack">The problem's own medium.</param>
    /// <param name="levelZ">The z of the conductor level the standards sit on (D3: one level).</param>
    /// <param name="referenceHeightM">
    /// The height the ACCELERATED route's near-radius floor is sized from — <c>2h</c> in
    /// <see cref="PlanarStaticAim"/>. For a level over a ground plane that is its own distance to the
    /// plane, which is exactly <c>slab.HeightM</c> on the case the overload above serves; it is a
    /// mesh-radius heuristic and not a physical claim about the medium, which is why it is a separate
    /// argument rather than derived from <paramref name="stack"/>.
    /// </param>
    /// <param name="model">
    /// The fitted interior model, when the caller has already built one to read its residual. Null
    /// fits one here.
    /// </param>
    public static double CapacitancePerMetre(PlanarStandard shortStd, PlanarStandard longStd,
                                             LayerStack stack, double levelZ, double referenceHeightM,
                                             PlanarFillSettings? settings = null,
                                             PlanarFillCores? shortCores = null,
                                             PlanarFillCores? longCores = null,
                                             InteriorStaticModel? model = null)
        => CapacitancePerMetreComplex(shortStd, longStd, stack, levelZ, referenceHeightM,
                                      settings, shortCores, longCores, model).Real;

    /// <inheritdoc cref="CapacitancePerMetreComplex(PlanarStandard, PlanarStandard, GroundedSlab, PlanarFillSettings, PlanarFillCores, PlanarFillCores, bool)"/>
    /// <param name="airFilled">
    /// <b>QSC — every layer's material replaced by air, the TERMINATIONS kept.</b> A PEC floor is
    /// still a PEC floor with the dielectric gone: what <c>L = μ₀ε₀/C₀</c> asks for is the same
    /// conductor geometry over the same ground in a vacuum, not an unbounded one. The fit is redone
    /// for that medium, so <paramref name="model"/> is ignored when this is set — a model fitted to
    /// the real stack is the wrong Green's function for an air-filled one, and reusing it would be a
    /// plausible, wrong [L].
    /// </param>
    public static Complex CapacitancePerMetreComplex(
        PlanarStandard shortStd, PlanarStandard longStd,
        LayerStack stack, double levelZ, double referenceHeightM,
        PlanarFillSettings? settings = null,
        PlanarFillCores? shortCores = null, PlanarFillCores? longCores = null,
        InteriorStaticModel? model = null, bool airFilled = false)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var medium = airFilled ? AirFilled(stack) : stack;
        var terms  = PlanarKernelTerms.StaticScalarAt(
            airFilled ? InteriorStaticImages.FitScalar(medium, levelZ, levelZ)
                      : model ?? InteriorStaticImages.FitScalar(medium, levelZ, levelZ));

        Complex c1 = StaticCapacitanceComplex(shortStd.Mesh, terms, settings, shortCores, referenceHeightM,
                                              shortStd.ModePotential, shortStd.ModeWeight,
                                              shortStd.FloatingPotential);
        Complex c2 = StaticCapacitanceComplex(longStd.Mesh,  terms, settings, longCores, referenceHeightM,
                                              longStd.ModePotential, longStd.ModeWeight,
                                              longStd.FloatingPotential);
        double dl = longStd.LengthM - shortStd.LengthM;

        if (!(dl > 0))
            throw new InvalidOperationException("The two calibration standards have the same length.");
        return (c2 - c1) / dl;
    }

    /// <summary>The same stack with every dielectric replaced by vacuum and every termination KIND
    /// left alone — see <see cref="CapacitancePerMetreComplex(PlanarStandard, PlanarStandard, LayerStack, double, double, PlanarFillSettings, PlanarFillCores, PlanarFillCores, InteriorStaticModel, bool)"/>.</summary>
    private static LayerStack AirFilled(LayerStack stack)
    {
        var layers = new MediumLayer[stack.LayerCount];
        for (int i = 0; i < layers.Length; i++)
            layers[i] = stack.Layers[i] with { Material = EmMaterial.Air };

        // A PEC or PMC termination already carries EmMaterial.Air and its KIND is what matters — a
        // PEC floor is still a PEC floor with the dielectric gone. Only a half-space needs emptying.
        static Termination Vacuum(Termination t) => t.IsOpen ? Termination.Air : t;

        return new LayerStack(Vacuum(stack.Bottom), layers, Vacuum(stack.Top));
    }

}
