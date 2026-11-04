// CL2 — P_conductor STOPS BEING AN IDENTICAL ZERO.
//
// ANT-5 itemised where a driven port's accepted power goes and had to book one of its four terms as a
// hard zero, because kernel B's metal was a perfect conductor. CL1 put the surface impedance INTO the
// EFIE; this file reads the answer back out as a power, and PlanarPowerBudget subtracts it from the
// dielectric residual.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// THERE IS EXACTLY ONE ROUTE TO THIS NUMBER, AND THAT IS THE POINT OF THE FILE
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// The quantity is the ordinary dissipation in a surface impedance,
//
//     P_conductor = ½ ∫ Re(Z_s) |J|² dS   +   Σ_barrels ½ Re(Z_barrel) |I|²
//
// and with J = Σ_n x_n f_n it is NOT a new integral at all. Expanding,
//
//     ∫ |J|² dS = Σ_{m,n} x_m* x_n ∫ f_m·f_n dS = xᴴ G x ,     G = PlanarGram
//
// so the sheet term is a QUADRATIC FORM in the solved coefficient vector against the SAME Gram matrix
// CL1 built for the fill, one Re(Z_s) per conductor level:
//
//     P_sheet = ½ Σ_{m,n on level L} Re(Z_s^L) · Re(x_m* x_n) · G[m,n] .
//
// It costs one sparse product. It needs no new geometry, no new quadrature and no second
// discretisation of the metal — and that is deliberate rather than merely convenient: **a second
// route to the same number is how a factor of two gets in.** Every scalar here comes from the two
// objects the FILL used (PlanarGram and PlanarConductorLoss), so a loss term in the matrix and a loss
// term in the budget cannot mean different things. The test project builds the direct
// ∫Re(Z_s)|J|² over PlanarCurrentDensity's map as an independent reference; that route is a GATE and
// it does not ship.
//
// THE TRIANGLE IS STORED ONCE AND COUNTED TWICE. G is real symmetric and PlanarGram holds only
// j ≥ i, so the diagonal contributes |x_i|²·G[i,i] and each off-diagonal slot contributes
// 2·Re(x_i* x_j)·G[i,j]. Dropping the 2 is precisely the factor-of-two failure the gate exists for,
// and it would be invisible: the budget would still sum to P_accepted, because the dielectric
// residual absorbs whatever this term gets wrong.
//
// THE IMAGINARY PART IS DISCARDED, AND IT IS EXACTLY ZERO BEFORE IT IS. xᴴGx is a real number for
// Hermitian G (and G here is real symmetric), so Im(x_i* x_j)·G[i,j] cancels against its transpose
// slot. Only Re(Z_s) is read: the reactive part of the surface impedance is internal INDUCTANCE, it
// stores energy rather than absorbing it, and folding it in here would report a stored energy as a
// loss.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// A VIA BARREL IS NOT A SHEET, HERE EITHER
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// PlanarSurfaceImpedance's header says why Z_barrel is a SERIES impedance in ohms rather than an
// ohms-per-square: a barrel's current crosses a footprint AREA and returns through the wall. The fill
// adds it straight to the Z_zz diagonal with no Gram weight, so a vertical basis's coefficient is the
// whole barrel current in amperes and its dissipation is the elementary ½Re(Z)|I|². A vertical basis
// has no row in the Gram at all (PlanarGram's header), so the two arms cannot double-count each other.
//
// ══════════════════════════════════════════════════════════════════════════════════════════════
// WHEN THE METAL IS A PERFECT CONDUCTOR THIS IS A HARD ZERO, AND THAT IS NOT A SPECIAL CASE
// ══════════════════════════════════════════════════════════════════════════════════════════════
//
// Two different runs report zero here and they mean different things, which is why
// PlanarPowerBudget.ConductorModelled is carried beside the number:
//
//   • The FILL had no conductor-loss term (PlanarFillSettings.ConductorLoss is null — CL1's PEC
//     oracle, and still the default). The solved current belongs to a PEC structure and P_accepted
//     contains no metal loss, so computing a term from it would be a fiction the dielectric residual
//     would then have to pay for with a NEGATIVE number. Nothing here is even constructed.
//   • The fill HAD the term and the stackup declares PEC metal (σ ≤ 0, t ≤ 0, or σ = +∞). Then
//     PlanarSurfaceImpedance.Sheet is exactly Complex.Zero and this arithmetic returns exactly 0.0
//     — the same zero, by the same route the matrix took.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>CL2 — the three things the conductor integral needs, carried as ONE object because a caller
/// holding two of them would compute a plausible wrong number.</b>
///
/// <para>All three come from the objects the FILL used: the <see cref="PlanarConductorLoss"/> that
/// resolved Z_s per level, the <see cref="PlanarGram"/> that weighted it, and the
/// <see cref="PlanarLevels"/> a via barrel's length is measured on. <b>Its very existence is the
/// switch</b> — a null on <see cref="PlanarPowerBudget.For"/> means the fill modelled no conductor
/// loss and the term is the hard zero ANT-5 shipped, not a number nobody computed.</para>
/// </summary>
/// <param name="Loss">The same loss model the fill read Z_s out of.</param>
/// <param name="Gram">The same ⟨f_m, f_n⟩ the fill multiplied it by — <c>PlanarFillCores.Gram</c>,
/// not a rebuild, so the budget cannot integrate a different basis from the matrix.</param>
/// <param name="Levels">Null on the single-level path, which has no vertical basis to give a barrel
/// impedance to. Exactly the argument <c>PlanarFill.AddSurfaceImpedance</c> takes, for the same
/// reason.</param>
/// <remarks><b>EM-TFR — a null <paramref name="Loss"/> is a run whose METAL is perfect and whose film is
/// not.</b> The fill then carried the film's term and nothing else, so the film's dissipation is real
/// and the metal's is the hard zero it always was.</remarks>
public sealed record PlanarConductorLossInputs(
    PlanarConductorLoss? Loss,
    PlanarGram          Gram,
    PlanarLevels?       Levels)
{
    /// <summary>
    /// <b>½∫Re(Z_s)|J|²dS + Σ½Re(Z_barrel)|I|², watts</b>, over one solved column — the quadratic
    /// form described in this file's header.
    /// </summary>
    /// <param name="basisCurrents">One column of <c>PlanarPortSolution.Currents</c>: the basis
    /// currents with a single port driven at 1 V, which is the same excitation
    /// <see cref="PlanarPowerBudget.AcceptedW"/> is ½Re(Y_jj) of.</param>
    public PlanarConductorPower PowerOf(PlanarMesh mesh, Vec<Complex> basisCurrents, double fHz)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (basisCurrents.Count != mesh.Bases.Count)
            throw new ArgumentException(
                $"The solution has {basisCurrents.Count} unknowns and the mesh has " +
                $"{mesh.Bases.Count} basis functions — these are not the same solve.",
                nameof(basisCurrents));
        if (Gram.UnknownCount != mesh.Bases.Count)
            throw new ArgumentException(
                $"The Gram matrix is of a {Gram.UnknownCount}-unknown mesh and this one has " +
                $"{mesh.Bases.Count} — these are not the same mesh, so ⟨f_m, f_n⟩ would be taken " +
                "over a different basis from the one the currents belong to.", nameof(mesh));

        double omega = 2.0 * Math.PI * fHz;
        var    zs    = PlanarConductorLoss.TableFor(Loss, mesh, omega) ?? [];

        // EM-TFR — the same quadratic form restricted by sheet class: a film's dissipation is a
        // circuit element's and is reported on its own, never folded into the metal's.
        double sheet = 0, film = 0;
        for (int i = 0; i < Gram.UnknownCount && zs.Length > 0; i++)
        {
            var xi = basisCurrents[i];
            for (int k = Gram.RowPtr[i]; k < Gram.RowPtr[i + 1]; k++)
            {
                int j = Gram.ColIdx[k];
                var xj = basisCurrents[j];
                // Re(x_i* x_j), written out rather than through Complex.Conjugate so the diagonal is
                // literally |x_i|² and cannot pick up a sign from a conjugation the other way round.
                double re = xi.Real * xj.Real + xi.Imaginary * xj.Imaginary;
                // Stored triangle: the diagonal once, an off-diagonal PAIR twice. See the header.
                double pair = i == j ? re : 2.0 * re;

                int first = Gram.Layer[k];
                double rs = zs[first].Real;
                if (rs != 0)
                {
                    if (mesh.IsFilmClass(first)) film += rs * Gram.Value[k] * pair;
                    else                         sheet += rs * Gram.Value[k] * pair;
                }
                if (Gram.Value2 is { } second && second[k] != 0)
                {
                    int other = Gram.Layer2![k];
                    double rs2 = zs[other].Real;
                    if (mesh.IsFilmClass(other)) film += rs2 * second[k] * pair;
                    else                         sheet += rs2 * second[k] * pair;
                }
            }
        }
        sheet *= 0.5;
        film  *= 0.5;

        double barrel = 0;
        if (Levels is { } levels && Loss is { } loss)
            for (int i = 0; i < mesh.Bases.Count; i++)
            {
                var b = mesh.Bases[i];
                if (b.Direction != PlanarBasisDirection.Z) continue;
                double rz = loss.BarrelAt(mesh, levels, b, omega).Real;
                if (rz == 0) continue;
                var x = basisCurrents[i];
                barrel += 0.5 * rz * (x.Real * x.Real + x.Imaginary * x.Imaginary);
            }

        return new PlanarConductorPower(sheet + barrel, sheet, barrel, film);
    }
}

/// <summary>
/// <b>CL2 — the conductor term, split by the two mechanisms that produce it.</b> The split is
/// carried because they are different physics with different limits — a sheet is thickness-blind in
/// the regime real stackups use and a barrel is not — and because a run where the barrels dominate
/// is a run whose answer rests on the via model rather than on the strip model.
/// </summary>
/// <param name="TotalW">Sheet + barrel, watts. This is <c>PlanarPowerBudget.ConductorW</c>.</param>
/// <param name="SheetW">½∫Re(Z_s)|J|²dS over every horizontal basis.</param>
/// <param name="BarrelW">Σ½Re(Z_barrel)|I|² over every vertical (via) basis; 0 on a single-level
/// problem, which has none.</param>
/// <param name="FilmW">EM-TFR — ½∫R_s|J|²dS over the cells that are resistive film. <b>Not part of
/// <paramref name="TotalW"/></b>: a terminated antenna's "conductor loss" must not read as most of its
/// budget because of the resistor it was built with.</param>
public sealed record PlanarConductorPower(double TotalW, double SheetW, double BarrelW, double FilmW = 0);
