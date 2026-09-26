// ANT-5 — WHERE THE ACCEPTED POWER GOES. The loss itemisation, and the one piece of new physics in
// the metrics phase: the power a structure launches into the substrate's own guided modes.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// WHY AN ITEMISATION AT ALL, AND WHAT THE FOUR TERMS ACTUALLY ARE
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// Radiation efficiency by power balance alone is ONE number and tells a designer nothing about what
// to change. The terms tell them everything — but only if each one says honestly what it is, and two
// of the four in brief-antenna-5-metrics.md §3 are not what that brief assumed. Both corrections are
// recorded in RESOLVED.md and both are stated in the cube notes the user reads:
//
//   P_radiated     ∫U dΩ over the upper hemisphere — ANT-4's own quadrature. INDEPENDENT of
//                  everything here. This is the only term that leaves the model.
//
//   P_surfaceWave  The power launched into the substrate's guided modes, from the RESIDUES of the
//                  spectral line voltages at the surface-wave poles. Independent of the MoM matrix.
//                  In this model it is a LOSS permanently — the substrate is laterally infinite, so
//                  what goes into a surface wave never comes back.
//
//   P_dielectric   **A RESIDUAL, NOT A THIRD INDEPENDENT INTEGRAL, and the brief's §3 is wrong about
//                  that in a way worth writing down.** A volume integral of ωε₀ε″|E|² over a
//                  LATERALLY INFINITE lossy substrate converges to a number that ALREADY CONTAINS
//                  the whole surface-wave term: the guided mode decays as e^{−2αρ}/ρ and ∫ρdρ of
//                  that is finite and equals everything the mode ever carried. So "dielectric loss"
//                  and "surface-wave power" are not two disjoint channels here, and adding them
//                  would double-count. What IS disjoint, and what this reports, is
//
//                      P_dielectric = P_accepted − P_radiated − P_surfaceWave − P_conductor
//
//                  — the dielectric loss NOT carried away by a guided mode, i.e. what is absorbed
//                  under and beside the antenna. On a real finite board the surface-wave term is the
//                  part that instead reaches the edge and radiates (badly); that is exactly why the
//                  two are booked separately rather than summed.
//
//   P_conductor    **CL2 — a genuine, independent integral now: ½∫Re(Z_s)|J|²dS over the solved
//                  current, plus ½Re(Z_barrel)|I|² over each via barrel.** It ships as a hard zero
//                  only when the metal really is a perfect conductor — see PlanarConductorPower for
//                  the two distinct ways that happens and for why there is exactly ONE route to the
//                  number. A missing term reads as "not a factor"; a zero term with a note reads as
//                  "this kernel does not model it", and ConductorModelled is what separates them.
//
// CL2 — AND THE RESIDUAL HAD TO MOVE WITH IT, WHICH IS THE ONE THING IN THIS FILE THAT CANNOT BE GOT
// WRONG SAFELY. Adding P_conductor BESIDE the old residual instead of subtracting it from the
// residual leaves a budget that still sums to P_accepted by construction and therefore still LOOKS
// right, while P_dielectric silently carries a negative copy of the conductor term. The tell is a
// dielectric term that goes NEGATIVE on a low-tanδ MMIC substrate, where the conductor term is the
// larger of the two by a factor of thirty — which is what R-cl2-3 measures.
//
// R-ant-3. THE BALANCE IS AN IDENTITY BY CONSTRUCTION AND THEREFORE CANNOT GATE ITSELF. What makes
// the residual a measurement rather than a definition is the LOSSLESS case: with tanδ = 0 and PEC
// metal and a PEC floor there is no absorption anywhere, so P_dielectric AND P_conductor must BOTH
// come out ZERO, and the three quantities — ½Re(Y_jj) from the MoM factorisation, ∫U dΩ from the far
// field, and the pole residues from the spectral kernel — are three independent routes forced to
// close on one number.
// That is the gate, and it is run on two substrates of very different thickness because a balance
// that closes in one regime may be closing on a cancellation.
//
// ════════════════════════════════════════════════════════════════════════════════════════════════
// THE SURFACE-WAVE DERIVATION. Built from this repository's own conventions; nothing transcribed.
// ════════════════════════════════════════════════════════════════════════════════════════════════
//
// The complex power an impressed surface current delivers to the field is P = −½∫E·J* dS. Parseval
// in SommerfeldIntegral's own transform pair (J̃(k) = ∫J e^{+jk·r}d²r, so ∫A*B dS =
// (1/4π²)∫Ã*B̃ d²k), plus the transmission-line map SpectralGreens' L9c block comment fixes —
// Ẽ_u = −Ĵ_u V^e(z|z′), Ẽ_v = −Ĵ_v V^h(z|z′), with û = k̂ and v̂ = ẑ × k̂ — gives
//
//     P_accepted = ½ Re (1/4π²) ∫ d²k  [ Q^e(k) + Q^h(k) ]
//     Q^e(k) = Σ_{L,L′} Ĵ_u^{L*} V^e(z_L|z_{L′}) Ĵ_u^{L′},    Q^h the same with Ĵ_v and V^h.
//
// This is a second, matrix-free statement of a quantity the MoM already knows as ½Re(Y_jj), which is
// the cross-check that pins the normalisation below.
//
// A surface-wave mode is a SIMPLE POLE of V^p at k_ρ = k_p. A guided mode must decay as it
// propagates, so with e^{−jk_ρρ} outgoing the pole lies BELOW the real axis, k_p = β − jα, α ≥ 0 —
// asserted, not assumed, because a pole on the wrong sheet would change the sign of the answer.
// Writing the pole part as R/(k_ρ − β + jα) and taking α → 0⁺ (Sokhotski–Plemelj:
// 1/(x + jα) → PV(1/x) − jπδ(x)) the radial integral ∫₀^∞ k dk picks up −jπ·β·Q(β, φ) per mode, so
//
//     P_sw = ½·(1/4π²)·π · Σ_modes β ∫₀^{2π} Im[ Q_p(β, φ) ] dφ          [R_p inside Q_p]
//          = Σ_modes β · Σ_j Im[ Q_p(β, φ_j) ] / (4 N_φ)                 (uniform φ, periodic)
//
// and the φ rule is the periodic rectangle, which is spectrally exact for a periodic integrand.
//
// THREE PLACES THIS COULD BE WRITTEN WRONG AND LOOK RIGHT:
//
//   • THE RESIDUE IS TAKEN IN k_ρ, NOT IN w = k_ρ². V^p is literally a function of w
//     (LayeredSpectralGreens.Voltage's signature), so as a function of k_ρ it is EVEN with poles at
//     ±k_p and R_k = R_w/(2k_p). A contour integral in k_ρ gets that for free; an analytic
//     substitution is where the factor of 2 goes missing, invisibly.
//   • THE CONTOUR MUST NOT REACH THE BRANCH POINT AT k₀. A near-cutoff mode sits ON it — a thin
//     grounded slab's TM₀ mode is at (k_p − k₀)/k₀ ≈ ½((εᵣ−1)k₀h/εᵣ)², which is 1.7e-5 on the
//     measured 203 µm board — so the radius is a fraction of the distance to the NEAREST other
//     singularity, never a fixed number, and a mode too close to cutoff for a simple-pole picture to
//     be separable from the continuum is REFUSED by name rather than given a number.
//   • Ĵ IS EVALUATED AT Re(k_p). RooftopSpectrum takes a real wavenumber, and the correction is
//     O(α·∂Ĵ/∂k). That makes this a SMALL-LOSS expansion, so the worst pole's |Im k_p|/Re k_p is
//     reported with the answer and refused above PoleLossCeiling.

using System.Numerics;
using NumFlat;

namespace CircuitRF.Engine.Mom;

/// <summary>
/// <b>The spectral transmission-line voltages of the problem's OWN kernel</b>, at an arbitrary
/// complex k_ρ and an arbitrary pair of conductor levels — which is what a pole residue needs and
/// what <see cref="FarFieldElementFactors"/> deliberately does not expose (it only ever looks at one
/// observation height in the top half-space).
///
/// <para><b>R-ant-2 again: <see cref="PlanarProblem.RequiresGeneralKernel"/> picks the kernel, not
/// this class.</b> The two branches are gated against each other on the one-slab case, so the choice
/// is not a numerical one — but it is still the fill's to make, because a power budget that
/// disagreed with the currents it is weighing would do so silently and only on a stratified
/// stack.</para>
/// </summary>
public sealed class PlanarSpectralVoltages
{
    private readonly SpectralGreens?        _slab;
    private readonly LayeredSpectralGreens? _general;
    private readonly double[]               _levelZ;

    public double K0          { get; }
    public double FrequencyHz { get; }
    public bool   IsGeneral   => _general is not null;
    public IReadOnlyList<double> LevelZ => _levelZ;

    /// <summary>ω, radians/second — carried because both characteristic impedances are written in it.</summary>
    public double Omega { get; }

    private PlanarSpectralVoltages(SpectralGreens? slab, LayeredSpectralGreens? general,
                                   double[] levelZ, double k0, double fHz)
    {
        _slab = slab; _general = general; _levelZ = levelZ;
        K0 = k0; FrequencyHz = fHz; Omega = 2.0 * Math.PI * fHz;
    }

    public static PlanarSpectralVoltages For(PlanarProblem problem, double fHz)
    {
        ArgumentNullException.ThrowIfNull(problem);
        if (!(fHz > 0)) throw new ArgumentOutOfRangeException(nameof(fHz), fHz, "f > 0 is required.");

        var levels = PlanarLevels.From(problem);
        var z = new double[problem.Layers.Count];
        for (int i = 0; i < z.Length; i++) z[i] = levels.Of(i);

        if (!problem.RequiresGeneralKernel)
        {
            var g = new SpectralGreens(problem.Slab, fHz);
            return new PlanarSpectralVoltages(g, null, z, g.K0, fHz);
        }

        var layered = new LayeredSpectralGreens(problem.EffectiveStack, fHz);
        return new PlanarSpectralVoltages(null, layered, z, layered.K0, fHz);
    }

    /// <summary>
    /// <c>V_i^p(z_a | z_b)</c> — the line voltage at level <paramref name="levelA"/> for a 1 A shunt
    /// current source at level <paramref name="levelB"/>, in ohms.
    ///
    /// <para><b>The one-slab branch is written as <c>(Z₀^p/2)(1 + Γ^p)</c> and that is an identity,
    /// not an approximation</b>: the voltage a shunt source sees at the interface is
    /// <c>Z_up ∥ Z_down</c>, and with <c>Z_up = Z₀</c> and
    /// <c>Γ = (Z_down − Z₀)/(Z_down + Z₀)</c> — which is exactly what
    /// <see cref="SpectralGreens.ReflectionTm"/> and <see cref="SpectralGreens.ReflectionTe"/> are,
    /// cross-multiplied — the two expressions are the same number. It is gated against the general
    /// kernel rather than asserted.</para>
    /// </summary>
    public Complex Voltage(SurfaceWavePolarization pol, Complex kRho, int levelA, int levelB)
    {
        if (_slab is { } s)
        {
            if (levelA != 0 || levelB != 0)
                throw new ArgumentOutOfRangeException(nameof(levelA),
                    "The one-slab kernel carries exactly one conductor level, on the slab's top " +
                    "surface (L8's D2), so there is no second level for a voltage to be taken " +
                    "between. A multi-level problem takes the general kernel — which is " +
                    "PlanarProblem.RequiresGeneralKernel's decision, not this class's.");

            Complex kz0 = s.Kz0(kRho);
            return pol == SurfaceWavePolarization.Tm
                ? 0.5 * (kz0 / (Omega * EmConstants.Eps0)) * (1.0 + s.ReflectionTm(kRho))
                : 0.5 * (Omega * EmConstants.Mu0 / kz0)    * (1.0 + s.ReflectionTe(kRho));
        }

        return _general!.Voltage(pol, kRho * kRho, _levelZ[levelA], _levelZ[levelB]);
    }
}

/// <summary>One guided mode's share of the accepted power.</summary>
/// <param name="ModeName">TM0, TE1, … — <see cref="LayeredSurfaceWaveMode.Name"/>.</param>
/// <param name="KRhoOverK0">Re(k_p)/k₀ — how tightly the mode is bound; 1 is cutoff.</param>
/// <param name="PoleLoss">|Im k_p| / Re k_p — how far the pole sits off the real axis, which is what
/// decides whether a simple-pole residue describes it at all.</param>
/// <param name="ResidueRadiusOverK0">The contour radius actually used, relative to k₀ — reported
/// because it is set by the distance to the nearest other singularity and is the number that says
/// whether the residue was well conditioned.</param>
/// <param name="PowerW">Watts launched into this mode.</param>
public sealed record PlanarGuidedModePower(
    string ModeName, double KRhoOverK0, double PoleLoss, double ResidueRadiusOverK0, double PowerW);

/// <summary>Every guided mode's launched power at one frequency, for one driven port.</summary>
public sealed record PlanarSurfaceWavePower(
    double                                    TotalW,
    IReadOnlyList<PlanarGuidedModePower>      Modes,
    int                                       AzimuthSamples,
    string                                    SearchDomain)
{
    public double WorstPoleLoss => Modes.Count == 0 ? 0 : Modes.Max(m => m.PoleLoss);

    /// <summary>R-res-8 — the scale and its provenance as one line a run prints verbatim.</summary>
    public string Caption =>
        Modes.Count == 0
            ? "No surface-wave mode is guided by this stack at this frequency, so the launched " +
              "guided-mode power is exactly zero — not small, absent."
            : $"Surface-wave power {SurfaceMesher.Eng(TotalW)}W into {Modes.Count} guided mode(s): " +
              string.Join(", ", Modes.Select(m =>
                  $"{m.ModeName} at k_ρ/k₀ = {m.KRhoOverK0:F6} carries {SurfaceMesher.Eng(m.PowerW)}W")) +
              $". From the pole RESIDUES of the spectral line voltages, {AzimuthSamples} azimuth " +
              $"samples, worst |Im k_ρ|/Re k_ρ = {WorstPoleLoss:E2}.";
}

/// <summary>
/// <b>ANT-5 — the power a structure launches into the substrate's guided modes.</b> See this file's
/// header for the derivation, the three places it could be written wrong, and why this term is a
/// permanent loss in a laterally infinite model and is NOT one on a real board.
/// </summary>
public static class PlanarSurfaceWaveLaunch
{
    /// <summary>
    /// <b>The small-loss ceiling.</b> The residue picture replaces a Lorentzian of fractional width
    /// |Im k_p|/Re k_p by a delta function, so it is an expansion in exactly that quantity — and Ĵ is
    /// evaluated at Re(k_p) for the same reason. FR-4's tanδ = 0.02 puts the TM₀ pole at ~1e-2 here,
    /// so this ceiling is loose enough for every ordinary board and tight enough that a deliberately
    /// absorptive substrate is refused rather than given a number.
    /// </summary>
    public const double PoleLossCeiling = 0.05;

    /// <summary>
    /// The smallest usable contour radius, relative to k₀. Below this the circle's sample points are
    /// not numerically distinguishable from the pole itself and the residue is noise.
    /// </summary>
    public const double MinResidueRadiusOverK0 = 1e-11;

    /// <summary>Default azimuth samples for the periodic-rectangle φ rule.</summary>
    public const int DefaultAzimuthSamples = 360;

    /// <summary>
    /// R-mom-17: what this can and cannot answer, in the medium alone. The MESH half is ANT-4's —
    /// the same cut-cell and vertical-basis refusals apply, for the same reason (the transform of a
    /// cut cell is not its rectangle's, and a z-directed current enters the TM line through a series
    /// voltage source rather than a shunt current one), so a caller with a mesh should ask
    /// <see cref="PlanarFarField.CanCompute"/> as well.
    /// </summary>
    public static EmSuitability CanCompute(PlanarProblem problem, double fHz)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var medium = PlanarFarField.CanComputeMedium(problem, fHz);
        if (!medium.Ok) return medium;

        var report = SurfaceWavePoles.Find(problem.EffectiveStack, fHz);
        double k0 = 2.0 * Math.PI * fHz / EmConstants.C0;

        foreach (var m in report.Modes)
        {
            if (m.KRho.Imaginary > 0)
                return EmSuitability.No(
                    $"Surface-wave mode {m.Name} was located at k_ρ = {m.KRho} — ABOVE the real " +
                    $"axis. A guided mode must decay as it propagates, so its pole lies below the " +
                    $"axis (k_ρ = β − jα, α ≥ 0) and the residue's sign depends on that. A pole on " +
                    $"the other sheet means the mode search landed on an improper root and the " +
                    $"launched power cannot be signed; it is refused rather than reported with a " +
                    $"sign nobody can check.");

            double loss = Math.Abs(m.KRho.Imaginary) / Math.Abs(m.KRho.Real);
            if (loss > PoleLossCeiling)
                return EmSuitability.No(
                    $"Surface-wave mode {m.Name}'s pole sits at |Im k_ρ|/Re k_ρ = {loss:E2}, past " +
                    $"the {PoleLossCeiling:G3} ceiling this residue is good to. The launched power " +
                    $"is read from a SIMPLE POLE replacing a Lorentzian of that fractional width by " +
                    $"a delta function, and the current transform is evaluated at Re(k_ρ) for the " +
                    $"same reason — both are expansions in this number. A substrate this absorptive " +
                    $"has no separable guided mode to launch power into: the power is reported as " +
                    $"dielectric loss, which is where it goes.");

            double radius = ContourRadius(report, m, k0);
            if (!(radius / k0 > MinResidueRadiusOverK0))
                return EmSuitability.No(
                    $"Surface-wave mode {m.Name} sits at (k_ρ − k₀)/k₀ = " +
                    $"{Math.Abs(m.KRho.Real) / k0 - 1.0:E2}, so the largest residue contour that " +
                    $"stays clear of the branch point at k₀ has radius {radius / k0:E2}·k₀ — below " +
                    $"the {MinResidueRadiusOverK0:E0}·k₀ floor at which the circle's samples stop " +
                    $"being numerically distinguishable from the pole. A mode this close to cutoff " +
                    $"is not separable from the radiating continuum by a simple-pole residue at all: " +
                    $"it is barely bound, it carries almost nothing, and a number for it would be " +
                    $"noise. A thicker or slower substrate binds it properly.");
        }

        return EmSuitability.Yes;
    }

    /// <summary>
    /// The contour radius for one pole: a tenth of the distance to the nearest OTHER singularity —
    /// the branch point at k₀, the slowest medium's k, and every other pole of the same polarisation
    /// — capped at 1e-3·k₀ so a well-isolated pole does not get a contour large enough for the
    /// quadratic term to matter.
    /// </summary>
    private static double ContourRadius(SurfaceWaveSearchReport report, LayeredSurfaceWaveMode mode,
                                        double k0)
    {
        double b = Math.Abs(mode.KRho.Real);
        double gap = Math.Min(Math.Abs(b - report.KLo), Math.Abs(report.KHi - b));
        foreach (var other in report.Modes)
        {
            if (ReferenceEquals(other, mode) || other.Polarization != mode.Polarization) continue;
            gap = Math.Min(gap, Math.Abs(b - Math.Abs(other.KRho.Real)));
        }
        return Math.Min(0.1 * gap, 1e-3 * k0);
    }

    /// <summary>
    /// <b>The launched guided-mode power for ONE driven port at ONE frequency.</b> The signature
    /// enforces that, exactly as <see cref="PlanarFarField.Compute"/>'s and
    /// <see cref="PlanarCurrentDensity.Compute"/>'s do: a budget that superposed every excitation
    /// would be a budget of nothing.
    /// </summary>
    public static PlanarSurfaceWavePower Compute(
        PlanarProblem problem, PlanarMesh mesh, Vec<Complex> basisCurrents, double fHz,
        int azimuthSamples = DefaultAzimuthSamples, int? maxDegreeOfParallelism = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(problem);
        ArgumentNullException.ThrowIfNull(mesh);
        if (azimuthSamples < 8)
            throw new ArgumentOutOfRangeException(nameof(azimuthSamples), azimuthSamples,
                "The azimuth rule needs at least 8 samples.");

        var ok = CanCompute(problem, fHz);
        if (!ok.Ok) throw new InvalidOperationException(ok.Reason);
        if (basisCurrents.Count != mesh.Bases.Count)
            throw new ArgumentException(
                $"The solution has {basisCurrents.Count} unknowns and the mesh has " +
                $"{mesh.Bases.Count} basis functions — these are not the same solve.",
                nameof(basisCurrents));

        var v      = PlanarSpectralVoltages.For(problem, fHz);
        var report = SurfaceWavePoles.Find(problem.EffectiveStack, fHz);
        double k0  = v.K0;
        int nL     = problem.Layers.Count;

        var terms = new List<PlanarGuidedModePower>(report.Modes.Count);
        double total = 0;

        foreach (var mode in report.Modes)
        {
            ct.ThrowIfCancellationRequested();

            double beta   = Math.Abs(mode.KRho.Real);
            double radius = ContourRadius(report, mode, k0);

            // R^{LL′} — the residue of V^p(z_L|z_{L′}) in k_ρ, by a circular contour around the pole
            // at its own complex location. Taken in k_ρ rather than in w: V is a function of w, so
            // it is EVEN in k_ρ with poles at ±k_p, and R_k = R_w/(2k_p) — a factor a substitution
            // loses silently and a contour gets for free.
            var residue = new Complex[nL * nL];
            for (int a = 0; a < nL; a++)
                for (int b = 0; b < nL; b++)
                    residue[a * nL + b] = Residue(
                        k => v.Voltage(mode.Polarization, k, a, b), mode.KRho, radius);

            bool tm = mode.Polarization == SurfaceWavePolarization.Tm;
            var  partial = new double[azimuthSamples];

            var work = new List<Action>(azimuthSamples);
            for (int jOuter = 0; jOuter < azimuthSamples; jOuter++)
            {
                int j = jOuter;
                work.Add(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    double phi = 2.0 * Math.PI * j / azimuthSamples;
                    var (sin, cos) = Math.SinCos(phi);
                    double kx = beta * cos, ky = beta * sin;

                    var jx = new Complex[nL];
                    var jy = new Complex[nL];
                    for (int bi = 0; bi < mesh.Bases.Count; bi++)
                    {
                        var basis = mesh.Bases[bi];
                        Complex t = basisCurrents[bi] * RooftopSpectrum.Of(mesh, basis, kx, ky);
                        if (basis.Direction == PlanarBasisDirection.X) jx[basis.LayerIndex] += t;
                        else                                          jy[basis.LayerIndex] += t;
                    }

                    // û = k̂ carries the TM line, v̂ = ẑ × k̂ the TE one.
                    var comp = new Complex[nL];
                    for (int L = 0; L < nL; L++)
                        comp[L] = tm ? jx[L] * cos + jy[L] * sin
                                     : -jx[L] * sin + jy[L] * cos;

                    Complex q = Complex.Zero;
                    for (int a = 0; a < nL; a++)
                        for (int b = 0; b < nL; b++)
                            q += Complex.Conjugate(comp[a]) * residue[a * nL + b] * comp[b];

                    partial[j] = q.Imaginary;
                });
            }
            PlanarFanOut.Run(maxDegreeOfParallelism, work);

            double sum = 0;
            foreach (double p in partial) sum += p;

            // P_mode = ½·(1/4π²)·π·β·∫Im Q dφ, with ∫dφ ≈ (2π/N)Σ — see the file header.
            double power = beta * sum / (4.0 * azimuthSamples);
            total += power;
            terms.Add(new PlanarGuidedModePower(
                mode.Name, beta / k0, Math.Abs(mode.KRho.Imaginary) / beta, radius / k0, power));
        }

        return new PlanarSurfaceWavePower(total, terms, azimuthSamples, report.Domain);
    }

    /// <summary>
    /// <c>(1/2πj)∮f dk</c> on a circle of the stated radius about <paramref name="centre"/>, by the
    /// periodic rectangle rule — which for an analytic integrand on a circle converges
    /// geometrically, so 32 points is far past machine precision for a simple pole.
    /// </summary>
    internal static Complex Residue(Func<Complex, Complex> f, Complex centre, double radius,
                                    int samples = 32)
    {
        Complex sum = Complex.Zero;
        for (int j = 0; j < samples; j++)
        {
            Complex e = RooftopSpectrum.Cis(2.0 * Math.PI * j / samples);
            sum += radius * e * f(centre + radius * e);
        }
        return sum / samples;
    }
}

/// <summary>
/// <b>Where one driven port's accepted power went, at one frequency.</b> See this file's header for
/// what each term is and for why <see cref="DielectricAndGroundW"/> is a residual rather than a third
/// integral.
/// </summary>
/// <param name="AcceptedW">½·Re(Y_jj) for the 1 V delta gap — <b>the RAW self-admittance</b> of the
/// structure as meshed, which is what the currents belong to. The de-embedded matrix describes a
/// different structure with the feed leads removed.</param>
/// <param name="RadiatedW">∫U dΩ over the upper hemisphere.</param>
/// <param name="SurfaceWaveW">Launched into the substrate's guided modes; 0 when refused.</param>
/// <param name="DielectricAndGroundW">The residual: accepted − radiated − surface-wave −
/// <b>conductor</b>. CL2 moved the conductor term out of it; see the file header for why adding the
/// term beside the residual instead is the failure that still sums to <paramref name="AcceptedW"/>.
/// <para><b>CL7 — IT IS NAMED FOR TWO MECHANISMS BECAUSE IT CARRIES TWO, and until CL7 it carried
/// one.</b> <paramref name="ConductorW"/> is an integral over the fill's own basis functions; the
/// laterally infinite GROUND PLANE is not meshed and has no basis, so it enters as a termination of
/// the Green's function and what it absorbs arrives as everything-else-minus and is booked here.
/// CL4 §8 found that and left it alone because no run could build such a termination; CL7 makes
/// every run with a σ on its ground layer build one, and a budget line whose label named only the
/// dielectric would then be false. <b>Measured</b> (<c>GroundReachesAUserTests</c> R-cl7-4): with
/// tanδ = 0 and a perfect strip, where the plane is the only absorber in the model, it is the WHOLE
/// of this line.
/// <para><b>It is renamed rather than SPLIT, and that is a recorded decision.</b> Splitting needs a
/// second quadratic form in the spectral domain — a 2D spectral integral per basis PAIR for
/// ½∫Re(Z_s)|H_tan|² over the plane — which is a brief rather than a relabelling, and CL4's own
/// "Must NOT" reserved the residual's arithmetic. `RESOLVED.md` §CL7.</para>
/// <para>A run whose ground layer has no σ has a perfect plane, which absorbs nothing, and this
/// line is then dielectric loss alone — exactly as it was. The two states are distinguishable from
/// the extraction's own return-plane note, not from this number.</para></param>
/// <param name="ConductorW">½∫Re(Z_s)|J|²dS + Σ½Re(Z_barrel)|I|² — see
/// <see cref="PlanarConductorLossInputs"/>. Exactly zero when the metal is a perfect conductor, and
/// <paramref name="ConductorModelled"/> is what says which kind of zero that is.</param>
/// <param name="ConductorModelled">
/// <b>Whether the FILL carried a surface-impedance term at all.</b> False means kernel B's metal was
/// a perfect conductor for this run — CL1's PEC oracle, and still the default — so the zero in
/// <paramref name="ConductorW"/> is "not modelled" rather than "modelled and negligible", and the
/// notes say the two different things. It is not derivable from the number: an all-PEC stackup
/// reports zero with the term switched fully on.</param>
/// <param name="Conductor">The conductor term split into its sheet and barrel halves, or null when
/// nothing was modelled. Carried because a run whose metal loss is mostly BARRELS rests on the via
/// model rather than on the strip model, and nothing else would say so.</param>
/// <param name="ConductorVerdict">brief-em3d-31 — null for kernel B, whose conductor term is always
/// published; a 3D solver's budget, which does not itemise its loss, refuses the term here by name.</param>
public sealed record PlanarPowerBudget(
    double                   AcceptedW,
    double                   RadiatedW,
    double                   SurfaceWaveW,
    double                   DielectricAndGroundW,
    double                   ConductorW,
    EmSuitability            SurfaceWaveVerdict,
    PlanarSurfaceWavePower?  SurfaceWave,
    int                      DrivenPort,
    double                   FrequencyHz,
    bool                     ConductorModelled = false,
    PlanarConductorPower?    Conductor         = null,
    EmSuitability?           ConductorVerdict  = null)
{
    /// <summary>P_radiated / P_accepted — a FRACTION, not dB, and deliberately not clamped.</summary>
    public double RadiationEfficiency => AcceptedW > 0 ? RadiatedW / AcceptedW : double.NaN;

    /// <summary>
    /// <b>CL2 — the conductor term's note. It covers BOTH states a run can be in</b>, because the
    /// term is computed when the fill carried a surface impedance and is a hard zero when it did
    /// not, and the fill's PEC oracle is still the default. <see cref="Caption"/> is where a
    /// particular run says which; a note that named only one state would be describing a state the
    /// code is not in half the time.
    ///
    /// <para>It carries the model's OWN limits deliberately, not as a hedge: a σ field the user can
    /// edit and a solver that reads it into a term that under-reads by a known factor are different
    /// kinds of trap, and the second one is only avoided by saying the factor.</para>
    /// </summary>
    public const string ConductorNote =
        "Conductor loss is the ohmic dissipation in the metal itself: ½∫Re(Z_s)|J|² dS over the " +
        "solved surface current, plus ½Re(Z_barrel)|I|² over each via barrel. It is a genuine " +
        "integral against the SAME surface impedance the matrix fill was loaded with, evaluated as " +
        "a quadratic form in the solved current, so there is exactly one route to the number. IT IS " +
        "EXACTLY ZERO WHEN THE METAL IS A PERFECT CONDUCTOR — either because the stackup says so " +
        "(σ ≤ 0 or thickness ≤ 0) or because this run's fill modelled no conductor loss at all, " +
        "which is this kernel's PEC reference and is still its default; the power-budget line says " +
        "which of the two a given run is. WHAT THE TERM CANNOT CARRY, where it is live: the metal " +
        "is modelled as a ZERO-THICKNESS sheet with one unknown per location, which holds neither " +
        "the strip's two independently loaded faces nor its sidewall current, and Re(Z_s) is flat " +
        "over the thickness range real stackups use while the true loss is not. Measured against an " +
        "independent quasi-static kernel on a re-bisected 50 Ω line at 10 GHz, the sheet converges " +
        "in edge refinement to 0.63 of that kernel's conductor loss on 1.6 mm FR-4 with 35 µm " +
        "copper and 0.73 on 100 µm GaAs with 3 µm gold — so where this term is live it is a real " +
        "measurement that still reads roughly a third LOW, and surface roughness is absent from the " +
        "model entirely.";

    /// <summary>
    /// <b>The bound on the efficiency that is true of EVERY run</b>, and the half of it that is not
    /// — the conductor correction — is <see cref="ConductorBoundClause"/>, because it depends on
    /// whether this run modelled conductor loss. <see cref="BoundNoteForRun"/> is the two together
    /// and is what a run prints; this constant alone is what the metric REGISTRY carries, where
    /// there is no run to ask.
    /// </summary>
    public const string BoundNote =
        "Two corrections to read this efficiency by, and they do not cancel — say both. (1) " +
        "Surface-wave power is a LOSS here permanently: the substrate is laterally infinite, so " +
        "power launched into a guided mode never comes back. On a real board it reaches the edge and " +
        "radiates, usually badly, so the efficiency reported here is a LOWER BOUND on what a finite " +
        "board does and the pattern is missing the edge-diffracted contribution entirely. (2) The " +
        "conductor term pushes the other way, and by how much depends on whether this run modelled " +
        "conductor loss at all — the power budget's own line says which, and PowerConductor's note " +
        "says what the model does and does not carry.";

    /// <summary>
    /// <b>CL2 — clause (2) of <see cref="BoundNote"/>, per run.</b> Where the conductor term is a
    /// hard zero it is ANT-5's own sentence, unchanged. Where it is real that sentence is RETIRED
    /// rather than left standing over a state the code is no longer in — and what replaces it is
    /// CL1's measured residual under-read, which is smaller and in the same direction, not nothing.
    /// </summary>
    public string ConductorBoundClause =>
        ConductorModelled
            ? "On clause (2): this run DID model conductor loss, so the old correction — that the " +
              "missing metal loss makes the efficiency read high by the metal's whole share — does " +
              "not apply. What is left of it is smaller and in the same direction: a " +
              "zero-thickness surface impedance converges to 0.63 (1.6 mm FR-4, 35 µm copper) and " +
              "0.73 (100 µm GaAs, 3 µm gold) of an independent quasi-static kernel's conductor " +
              "loss on a uniform 50 Ω line, so the efficiency still reads high, by roughly a third " +
              "of the metal's share rather than by all of it."
            : "On clause (2): this run's metal is a PERFECT CONDUCTOR, so the conductor term is a " +
              "hard zero and the accepted power has one fewer place to go — the efficiency reads " +
              "HIGH by roughly the metal's own share. On 1.6 mm FR-4 that share is 6.4 % / 3.0 % / " +
              "2.1 % of the total conducted loss at 2 / 10 / 20 GHz, but FR-4 is the substrate " +
              "class where it matters LEAST: on a 100 µm GaAs MMIC stackup the metal carries " +
              "92-99 % of the conducted loss.";

    /// <summary>The bound and this run's own conductor clause, as one note.</summary>
    public string BoundNoteForRun => BoundNote + " " + ConductorBoundClause;

    /// <summary>R-res-8 — the whole budget as lines a run or a panel prints verbatim.</summary>
    public string Caption
    {
        get
        {
            string pc(double w) => AcceptedW > 0 ? $" ({100.0 * w / AcceptedW:F2} %)" : "";
            string sw = SurfaceWaveVerdict.Ok
                ? $"{SurfaceMesher.Eng(SurfaceWaveW)}W surface wave{pc(SurfaceWaveW)}"
                : "surface wave REFUSED";
            // CL2 — the conductor line says WHICH ZERO a zero is. Nothing else in the budget can:
            // an all-PEC stackup reports 0 W with the term switched fully on.
            string cond = ConductorModelled
                ? $"{SurfaceMesher.Eng(ConductorW)}W conductor{pc(ConductorW)}" +
                  (Conductor is { BarrelW: > 0 } c
                      ? $" ({SurfaceMesher.Eng(c.SheetW)}W sheet + " +
                        $"{SurfaceMesher.Eng(c.BarrelW)}W via barrels)"
                      : "")
                : $"{SurfaceMesher.Eng(ConductorW)}W conductor{pc(ConductorW)} — NOT MODELLED, the " +
                  $"metal is a perfect conductor in this run";

            return
                $"Power budget at {SurfaceMesher.Eng(FrequencyHz)}Hz, port {DrivenPort} driven at " +
                $"1 V: {SurfaceMesher.Eng(AcceptedW)}W accepted, " +
                $"{SurfaceMesher.Eng(RadiatedW)}W radiated{pc(RadiatedW)}, {sw}, " +
                $"{SurfaceMesher.Eng(DielectricAndGroundW)}W dielectric + ground plane" +
                $"{pc(DielectricAndGroundW)}, {cond}. " +
                $"Radiation efficiency {RadiationEfficiency:P2}. " +
                $"The dielectric + ground-plane term is a RESIDUAL (accepted − radiated − " +
                $"surface wave − conductor), not a third independent integral: over a laterally " +
                $"infinite lossy substrate a volume loss integral already contains the whole " +
                $"surface-wave term, so adding the two would double-count. What is reported is " +
                $"therefore the dielectric loss NOT carried away by a guided mode, PLUS whatever " +
                $"the ground plane absorbed — the plane is not meshed and has no basis function, so " +
                $"it cannot be integrated the way the drawn metal is, and on a low-tanδ substrate " +
                $"it can be most of what this line reads. The conductor term, by contrast, IS an " +
                $"independent integral over the drawn metal and is taken OUT of that residual " +
                $"rather than reported beside it.";
        }
    }

    /// <summary>
    /// The whole budget for one driven port at one frequency. <paramref name="rawSelfAdmittance"/> is
    /// <c>Y[j, j]</c> of the RAW (not de-embedded) port admittance — the matrix the
    /// <paramref name="pattern"/>'s own currents came out of.
    /// </summary>
    /// <param name="conductor">
    /// <b>CL2 — the fill's own conductor-loss model, or null when the fill modelled none.</b> Null
    /// is the pre-CL2 behaviour exactly: <c>ConductorW</c> is a hard zero and the residual is
    /// <c>accepted − radiated − surface wave</c>. It is not defaulted to
    /// <c>PlanarConductorLoss.For(problem)</c> and must not be — the solved current belongs to a PEC
    /// structure when the fill had no such term, and the power it did not dissipate is not in
    /// <paramref name="rawSelfAdmittance"/> either, so a term computed from it would be paid for by
    /// a NEGATIVE dielectric residual.
    /// </param>
    public static PlanarPowerBudget For(
        PlanarProblem problem, PlanarMesh mesh, Vec<Complex> basisCurrents,
        PlanarFarFieldPattern pattern, Complex rawSelfAdmittance,
        PlanarConductorLossInputs? conductor = null,
        int azimuthSamples = PlanarSurfaceWaveLaunch.DefaultAzimuthSamples,
        int? maxDegreeOfParallelism = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        double accepted = 0.5 * rawSelfAdmittance.Real;
        double radiated = pattern.RadiatedPowerW;

        var verdict = PlanarSurfaceWaveLaunch.CanCompute(problem, pattern.FrequencyHz);
        PlanarSurfaceWavePower? sw = null;
        if (verdict.Ok)
            sw = PlanarSurfaceWaveLaunch.Compute(problem, mesh, basisCurrents, pattern.FrequencyHz,
                                                 azimuthSamples, maxDegreeOfParallelism, ct);

        double swW = sw?.TotalW ?? 0.0;

        // CL2 milestone 2 — the conductor term is an INDEPENDENT integral, so it comes OUT of the
        // residual. Adding it beside the residual instead leaves a budget that still sums to
        // `accepted` and is silently wrong; the file header names the tell.
        var cond = conductor?.PowerOf(mesh, basisCurrents, pattern.FrequencyHz);
        double condW = cond?.TotalW ?? 0.0;

        return new PlanarPowerBudget(
            accepted, radiated, swW, accepted - radiated - swW - condW, condW,
            verdict, sw, pattern.DrivenPort, pattern.FrequencyHz,
            ConductorModelled: conductor is not null, Conductor: cond);
    }
}
