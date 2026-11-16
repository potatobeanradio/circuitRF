using CircuitRF.Diagnostics;

namespace CircuitRF.Core.Devices.Microstrip;

/// <summary>What a coupled-line coupler comes out to on one substrate: the asked modes, the pair that has them, and the
/// plan. With an option on, the first fields describe the coupler's first (or only) pair and the init-only fields the rest.</summary>
/// <param name="Ze">Even-mode impedance asked for, Z0·√((1 + c)/(1 − c)).</param>
/// <param name="Zo">Odd-mode impedance asked for, Z0·√((1 − c)/(1 + c)).</param>
/// <param name="Modes">MCLIN's modes at F of the pair as drawn (W, S and L on the plan grid).</param>
public sealed record CoupledLineCouplerDesign(
    double CouplingDb, double Z0, double FreqHz, double Ze, double Zo,
    double WMeters, double SMeters, double LengthMeters, KirschningJansenCoupled.Modes Modes, PassivePlan Plan)
{
    /// <summary>Sections: each one's asked Ze and Zo and the pair drawn for it, first to last (one for a single section).</summary>
    public IReadOnlyList<(double Ze, double Zo, CoupledLineGeometry Pair)> Sections { get; init; } = [];

    /// <summary>Several sections: the coupling's equal ripple about <see cref="CouplingDb"/>, ± dB, over the band.</summary>
    public double RippleDb { get; init; }

    /// <summary>Tandem: each of the two couplers' coupling, dB.</summary>
    public double SubCouplingDb { get; init; }

    /// <summary>How mode-velocity difference is compensated.</summary>
    public CouplerCompensation Compensation { get; init; }

    /// <summary>Capacitive: the capacitor to put across each coupled end, on 1-3 and on 2-4, F.</summary>
    public double CompensationFarads { get; init; }

    /// <summary>Inductive: the series reactance at F each end needs, Ω, and the line section that stands for it.</summary>
    public double CompensationOhms { get; init; }

    /// <summary>Inductive: the series sections' width and length.</summary>
    public (double W, double L) InductiveSection { get; init; }

    /// <summary>Wiggly: the odd mode's path over the straight length, √(εeff,e/εeff,o) at F.</summary>
    public double OddLengthFactor { get; init; } = 1.0;

    /// <summary>Broadside: the stack the pair is on, and the strips' sideways offset centre to centre (W is
    /// <see cref="WMeters"/>; <see cref="SMeters"/> is 0).</summary>
    public BroadsideStack? Broadside { get; init; }

    /// <summary>Broadside: the offset O; its modes (MBCLIN's or BCLIN's) as drawn.</summary>
    public double OffsetMeters { get; init; }

    /// <summary>Broadside: the pair's solved modes as drawn.</summary>
    public Coupled.BroadsideModes? BroadsideModes { get; init; }
}

/// <summary>How a coupled-line coupler equalises its two modes' phase velocities (<see cref="CoupledLineCouplerPlan"/>).</summary>
public enum CouplerCompensation { None, Capacitive, Inductive, Wiggly }

/// <summary>
/// The single-section backward-wave coupler: one quarter-wave edge-coupled pair (D. M. Pozar, Microwave Engineering,
/// the coupled-line coupler section). Pure: Coupling (dB), Z0 and F plus a substrate in,
/// <see cref="CoupledLineCouplerDesign"/> out; the pair is <see cref="CoupledLineSynthesis"/>'s inversion of MCLIN's own
/// modes at F, so the plan is one MCLIN section and the part simulates exactly as that MCLIN does.
///
/// <para><b>Pins</b>, MCLIN's own order: 1 the input (strip A's near end), 2 the through port (A's far end), 3 the
/// coupled port (strip B's near end, beside 1), 4 the isolated port (B's far end). Driven at 1, port 3 carries
/// −Coupling dB in quadrature with port 2, and the coupler is symmetric, so any port can be the input.</para>
///
/// <para><b>Limit: directivity.</b> In microstrip the odd mode is faster than the even mode, so the two cannot both
/// be a quarter wave long; the length is λg/4 at their mean εeff. Coupling and match hold at F, but the isolated
/// port sees the mode-velocity difference: on 20 mil RO4350B at 2.4 GHz the directivity is about 11 dB at 10 dB
/// coupling and under 5 dB at 20 dB, since the leak stays while the coupling falls. Tight coupling needs narrow
/// gaps: below 0.1·h the coupled-line model leaves its range and the coupling is refused
/// (<see cref="CoupledLineSynthesis"/>); on that board 10 dB needs 115 µm and 8 dB already 65 µm.</para>
/// </summary>
public static class CoupledLineCouplerPlan
{
    /// <summary>The part's pins, in terminal order: input, through, coupled, isolated.</summary>
    public static readonly IReadOnlyList<string> Pins = ["1", "2", "3", "4"];

    /// <summary>The pins of a tandem: the four ports, then J1/J2 and J3/J4, the landings of the two crossover jumpers.</summary>
    public static readonly IReadOnlyList<string> TandemPins = ["1", "2", "3", "4", "J1", "J2", "J3", "J4"];

    /// <summary>The band a multi-section coupler opens with, f2/f1.</summary>
    public const double DefaultBand = 2.0;

    /// <summary>The most sections <see cref="EqualRipple"/> designs.</summary>
    public const int MaxSections = 9;

    public static CoupledLineCouplerDesign Build(double couplingDb, double z0, double fHz, MicrostripSubstrate s,
        double sections = 1, double band = DefaultBand, double tandem = 0, CouplerCompensation compensation = CouplerCompensation.None,
        double broadside = 0, BroadsideStack? stack = null)
    {
        foreach (var (name, value) in new[] { ("Coupling", couplingDb), ("Z0", z0), ("F", fHz) })
            if (!(value > 0))
                throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line-coupler.spec-not-positive",
                    DiagnosticSeverity.Error, "Coupled-line coupler: {name} = {value} must be positive.",
                    ("name", name), ("value", value)));
        if (!(sections >= 1 && sections <= MaxSections && sections % 2 == 1))
            throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line-coupler.sections-invalid",
                DiagnosticSeverity.Error, "Coupled-line coupler: Sections = {n}; it is an odd number from 1 to {max}.",
                ("n", sections), ("max", MaxSections)));
        int options = (sections > 1 ? 1 : 0) + (tandem != 0 ? 1 : 0) + (broadside != 0 ? 1 : 0)
                    + (compensation != CouplerCompensation.None ? 1 : 0);
        if (options > 1)
            throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line-coupler.options-combined",
                DiagnosticSeverity.Error, "Coupled-line coupler: Sections, Broadside, Tandem and Compensation are each "
              + "an option of their own; set one at a time."));

        if (sections > 1) return MultiSection(couplingDb, z0, fHz, s, (int)sections, band);
        if (tandem != 0) return Tandem(couplingDb, z0, fHz, s);
        if (broadside != 0) return Broadside(couplingDb, z0, fHz, stack);
        if (compensation != CouplerCompensation.None) return Compensated(couplingDb, z0, fHz, s, compensation);

        var (ze, zo) = CoupledLineSynthesis.ModeImpedances(couplingDb, z0);
        var g = CoupledLineSynthesis.Synthesize(ze, zo, fHz, s, "Coupled-line coupler");
        double w = PassivePlan.OnGrid(g.WMeters), gap = PassivePlan.OnGrid(g.SMeters), l = PassivePlan.OnGrid(g.QuarterWaveMeters);
        var sectionsOut = new PlanSection[] { new PlanCoupledLine([1, 2, 3, 4], 0.0, 0.0, 0.0, w, gap, l) };
        return new CoupledLineCouplerDesign(couplingDb, z0, fHz, ze, zo, w, gap, l, Modes(w, gap, l, fHz, s),
            new PassivePlan(Pins, sectionsOut, NodeCount: 4)) { Sections = [(ze, zo, g)] };
    }

    // ── Sections: the symmetric equal-ripple coupler ──────────────────────────────────────────────────────────

    /// <summary>
    /// The normalised even-mode impedances z_k = Ze_k/Z0 of an <paramref name="n"/>-section (odd) symmetric coupler whose
    /// coupling ripples equally about <paramref name="couplingDb"/> over f2/f1 = <paramref name="band"/> centred on F, and
    /// that ripple (± dB). The synthesis behind Cristal and Young's optimum tables, worked here rather than copied:
    /// with Ze_k·Zo_k = Z0² in every section a TEM coupler is matched and isolated at every frequency, and its coupled
    /// voltage is the reflection Γ of the even-mode chain z_1 … z_n between unit loads. For a symmetric chain of odd n,
    /// Γ/T = jQ(sin θ) with Q an odd polynomial of degree n (θ the sections' electrical length), and |Γ|² = Q²/(1 + Q²).
    /// So an equal-ripple coupling is an odd polynomial that ripples equally about a constant on sin θ1 ≤ sin θ ≤ 1,
    /// θ1 = π/(1 + band): found by Remez exchange in u = sin²θ, scaled until the coupling's mean in dB is the spec, and
    /// the chain's z_k then fitted to it by Newton's method at (n + 1)/2 points, which fixes them.
    /// </summary>
    public static (double[] Z, double RippleDb) EqualRipple(double couplingDb, int n, double band)
    {
        if (!(band > 1.0) || band > 20.0)
            throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line-coupler.band-invalid",
                DiagnosticSeverity.Error, "Coupled-line coupler: Band = {b}; it is f2/f1, above 1 and at most 20.", ("b", band)));
        int m = (n - 1) / 2;
        double um = Math.Pow(Math.Sin(Math.PI / (1.0 + band)), 2);
        double[] r = Remez(m, um, out double h);
        double Qhat(double sn) => sn * Poly(r, sn * sn);

        // The scale q0 whose coupling ripples between C(q0(1 - h)) and C(q0(1 + h)) with that mean in dB.
        static double Cdb(double q) => 10.0 * Math.Log10(1.0 + 1.0 / (q * q));
        double lo = -20.0, hi = 20.0;
        for (int i = 0; i < 200; i++)
        {
            double mid = 0.5 * (lo + hi), q0 = Math.Exp(mid);
            double mean = 0.5 * (Cdb(q0 * (1 - h)) + Cdb(q0 * (1 + h)));
            if (mean > couplingDb) lo = mid; else hi = mid;
        }
        double scale = Math.Exp(0.5 * (lo + hi));
        double ripple = 0.5 * (Cdb(scale * (1 - h)) - Cdb(scale * (1 + h)));

        // The chain z_1..z_(m+1) (the rest mirrored) whose Q matches scale·Qhat at m + 1 points in (0, 1].
        double[] at = [.. Enumerable.Range(1, m + 1).Select(j => (double)j / (m + 1))];
        double[] target = [.. at.Select(x => scale * Qhat(x))];
        double single = Math.Sqrt((1 + Math.Pow(10, -couplingDb / 20)) / (1 - Math.Pow(10, -couplingDb / 20)));
        double[] lnz = [.. Enumerable.Range(0, m + 1).Select(_ => Math.Log(single))];
        double[] Residual(double[] x) => [.. at.Select((sn, j) => ChainQ(Mirror([.. x.Select(Math.Exp)], n), sn) - target[j])];
        static double Norm(double[] v) => Math.Sqrt(v.Sum(e => e * e));
        var res = Residual(lnz);
        for (int it = 0; it < 100 && Norm(res) > 1e-14; it++)
        {
            var jac = new double[m + 1, m + 1];
            for (int k = 0; k <= m; k++)
            {
                double[] x = [.. lnz];
                x[k] += 1e-7;
                var rk = Residual(x);
                for (int j = 0; j <= m; j++) jac[j, k] = (rk[j] - res[j]) / 1e-7;
            }
            var step = Solve(jac, [.. res.Select(e => -e)]);
            double t = 1.0;
            for (int back = 0; back < 30; back++, t /= 2)
            {
                double[] x = [.. lnz.Select((v, k) => v + t * step[k])];
                var rx = Residual(x);
                if (Norm(rx) < Norm(res)) { lnz = x; res = rx; break; }
            }
        }
        if (!(Norm(res) < 1e-9))
            throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line-coupler.sections-unsolved",
                DiagnosticSeverity.Error, "Coupled-line coupler: no {n}-section chain was found for {c} dB over a band of "
              + "{b}; ask for a narrower band or fewer sections.", ("n", n), ("c", couplingDb), ("b", band)));
        return (Mirror([.. lnz.Select(Math.Exp)], n), ripple);
    }

    /// <summary>Q(sin θ) = Im(B − C)/2 of the chain's ABCD between unit loads, so Γ/T = jQ.</summary>
    public static double ChainQ(double[] z, double sn)
    {
        double cs = Math.Sqrt(Math.Max(0.0, 1.0 - sn * sn));
        // [[a, j b], [j c, d]] with a, b, c, d real.
        double a = 1, b = 0, c = 0, d = 1;
        foreach (double zk in z)
            (a, b, c, d) = (a * cs - b * sn / zk, a * zk * sn + b * cs, c * cs + d * sn / zk, -c * zk * sn + d * cs);
        return (b - c) / 2.0;
    }

    private static double[] Mirror(double[] half, int n)
        => half.Length == n ? half : [.. Enumerable.Range(0, n).Select(k => half[Math.Min(k, n - 1 - k)])];

    private static double Poly(double[] r, double u)
    {
        double v = 0;
        for (int i = r.Length - 1; i >= 0; i--) v = v * u + r[i];
        return v;
    }

    /// <summary>The R of degree m with √u·R(u) the minimax approximation of 1 on [um, 1]; h its signed level error.</summary>
    private static double[] Remez(int m, double um, out double h)
    {
        int k = m + 2;
        double[] refs = [.. Enumerable.Range(0, k).Select(j => um + (1 - um) * (1 - Math.Cos(Math.PI * j / (k - 1))) / 2)];
        double[] r = new double[m + 1];
        h = 0;
        const int grid = 4000;
        for (int it = 0; it < 60; it++)
        {
            var a = new double[k, k];
            var rhs = new double[k];
            for (int j = 0; j < k; j++)
            {
                for (int i = 0; i <= m; i++) a[j, i] = Math.Pow(refs[j], i) * Math.Sqrt(refs[j]);
                a[j, m + 1] = j % 2 == 0 ? 1 : -1;
                rhs[j] = 1;
            }
            var x = Solve(a, rhs);
            Array.Copy(x, r, m + 1);
            h = x[m + 1];
            // The error's extremum on each run of one sign, ends included; keep k alternating ones.
            var rr = r;
            double E(double u) => Math.Sqrt(u) * Poly(rr, u) - 1;
            var ext = new List<(double U, double E)>();
            for (int g = 0; g <= grid; g++)
            {
                double u = um + (1 - um) * g / grid, e = E(u);
                if (ext.Count > 0 && Math.Sign(ext[^1].E) == Math.Sign(e)) { if (Math.Abs(e) > Math.Abs(ext[^1].E)) ext[^1] = (u, e); }
                else ext.Add((u, e));
            }
            while (ext.Count > k) ext.RemoveAt(Math.Abs(ext[0].E) < Math.Abs(ext[^1].E) ? 0 : ext.Count - 1);
            if (ext.Count < k) break;
            // Each interior extremum to the root of d(√u·R)/du ∝ R(u) + 2u·R'(u), by Newton's method from the grid.
            double[] dr = [.. Enumerable.Range(1, m).Select(i => i * rr[i])];
            double[] ddr = [.. Enumerable.Range(2, Math.Max(0, m - 1)).Select(i => i * (i - 1) * rr[i])];
            double[] next = [.. ext.Select(p => p.U)];
            for (int j = 0; j < k; j++)
            {
                if (next[j] <= um || next[j] >= 1.0) continue;
                double u = next[j];
                for (int n = 0; n < 20; n++)
                {
                    double g = Poly(rr, u) + 2 * u * Poly(dr, u), dg = 3 * Poly(dr, u) + 2 * u * Poly(ddr, u);
                    if (dg == 0) break;
                    u = Math.Clamp(u - g / dg, um, 1.0);
                }
                next[j] = u;
            }
            if (next.Zip(refs).All(p => Math.Abs(p.First - p.Second) < 1e-12)) break;
            refs = next;
        }
        h = Math.Abs(h);
        return r;
    }

    /// <summary>Gaussian elimination with partial pivoting.</summary>
    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = (double[,])a.Clone();
        var x = (double[])b.Clone();
        for (int c = 0; c < n; c++)
        {
            int p = Enumerable.Range(c, n - c).MaxBy(i => Math.Abs(m[i, c]));
            for (int j = 0; j < n; j++) (m[c, j], m[p, j]) = (m[p, j], m[c, j]);
            (x[c], x[p]) = (x[p], x[c]);
            for (int i = c + 1; i < n; i++)
            {
                double f = m[i, c] / m[c, c];
                for (int j = c; j < n; j++) m[i, j] -= f * m[c, j];
                x[i] -= f * x[c];
            }
        }
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = i + 1; j < n; j++) x[i] -= m[i, j] * x[j];
            x[i] /= m[i, i];
        }
        return x;
    }

    /// <summary>n pairs in a row along +x, each λg/4 at its own mean εeff, centred on one gap centre line, so a section's
    /// strips step in width and position where it meets the next (the step itself is not modelled).</summary>
    private static CoupledLineCouplerDesign MultiSection(double couplingDb, double z0, double fHz, MicrostripSubstrate s,
        int n, double band)
    {
        var (z, ripple) = EqualRipple(couplingDb, n, band);
        var pairs = z.Select(zk => (Ze: zk * z0, Zo: z0 / zk,
            Pair: CoupledLineSynthesis.Synthesize(zk * z0, z0 / zk, fHz, s, "Coupled-line coupler"))).ToList();
        // Nodes: 1-4 the pins; 5.. strip A's joints, then strip B's.
        int A(int k) => k == 0 ? 1 : k == n ? 2 : 4 + k;
        int B(int k) => k == 0 ? 3 : k == n ? 4 : 4 + (n - 1) + k;
        var sections = new List<PlanSection>();
        double x = 0.0, centre = 0.0;
        for (int k = 0; k < n; k++)
        {
            var g = pairs[k].Pair;
            double w = PassivePlan.OnGrid(g.WMeters), gap = PassivePlan.OnGrid(g.SMeters), l = PassivePlan.OnGrid(g.QuarterWaveMeters);
            if (k == 0) centre = -(w + gap) / 2.0;
            sections.Add(new PlanCoupledLine([A(k), A(k + 1), B(k), B(k + 1)], x, PassivePlan.OnGrid(centre + (w + gap) / 2.0),
                0.0, w, gap, l));
            x += l;
        }
        var first = (PlanCoupledLine)sections[0];
        return new CoupledLineCouplerDesign(couplingDb, z0, fHz, pairs[0].Ze, pairs[0].Zo, first.W, first.S, first.L,
            Modes(first.W, first.S, first.L, fHz, s), new PassivePlan(Pins, sections, NodeCount: 4 + 2 * (n - 1)))
        {
            Sections = pairs, RippleDb = ripple,
        };
    }

    // ── Compensation ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The single section with its two modes' phase velocities equalised at F. Each form changes what the odd mode (or
    /// both modes) sees at the ends so that the two half-circuits are equal electrical lengths again, with impedances
    /// whose product is Z0² and whose ratio gives the coupling; the pair is re-synthesised until it is so.
    /// <list type="bullet">
    /// <item><b>Capacitive</b> (M. Dydyk, 1990 and 1999): a capacitor C across the gap at each end loads only the odd
    /// mode, by 2C to its midplane. The pair is λg/4 in the even mode (θe = π/2); then [2C][Zo, θo][2C] is the line
    /// (Zo·sin θo/sin θe, θe) when 2ωC = (cos θo − cos θe)/(Zo·sin θo), so the odd target is asked as Zo/sin θo.</item>
    /// <item><b>Inductive</b> (S. Phromloungsri et al., 2006): a series reactance X at each of the four ends, a short
    /// narrow line here. [X][Z, θ][X] is the line (Z·sin θ'/sin θ, θ') with cos θ' = cos θ − (X/Z)·sin θ, so θ'e = θ'o =
    /// π/2 needs X = Ze·cot θe = Zo·cot θo, which fixes a length under λg/4, and the targets are asked as Ze·sin θe and
    /// Zo·sin θo. Each line section's series term Z_L·sin(β_L·ℓ) is X; its shunt term is the realisation's error.</item>
    /// <item><b>Wiggly</b> (A. Podell, 1970): the facing edges a saw-tooth whose path is √(εe/εo) times the length, so
    /// the odd mode, carried along them, is λg/4 when the even mode is (<see cref="PlanCoupledLine.OddLengthFactor"/>).
    /// The circuit model is that length correction alone.</item>
    /// </list>
    /// </summary>
    private static CoupledLineCouplerDesign Compensated(double couplingDb, double z0, double fHz, MicrostripSubstrate s,
        CouplerCompensation form)
    {
        const string part = "Coupled-line coupler";
        var (ze, zo) = CoupledLineSynthesis.ModeImpedances(couplingDb, z0);
        double k0 = 2.0 * Math.PI * fHz / MicrostripLoss.SpeedOfLight;
        double askE = ze, askO = zo, w = 0, gap = 0, l = 0, x = 0;
        CoupledLineGeometry g = null!;
        KirschningJansenCoupled.Modes m = default!;
        for (int it = 0; it < 8; it++)
        {
            g = CoupledLineSynthesis.Synthesize(askE, askO, fHz, s, part);
            w = PassivePlan.OnGrid(g.WMeters);
            gap = PassivePlan.OnGrid(g.SMeters);
            m = Modes(w, gap, 1.0, fHz, s);
            double be = k0 * Math.Sqrt(m.EeffE), bo = k0 * Math.Sqrt(m.EeffO);
            if (form == CouplerCompensation.Inductive)
            {
                // Ze·cot(βe·L) = Zo·cot(βo·L) on (0, λe/4): the left side falls to 0 at λe/4, the right stays above it.
                double lo = 1e-9, hi = Math.PI / (2.0 * be);
                for (int i = 0; i < 100; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    if (m.Ze / Math.Tan(be * mid) > m.Zo / Math.Tan(bo * mid)) lo = mid; else hi = mid;
                }
                l = PassivePlan.OnGrid(0.5 * (lo + hi));
                x = m.Ze / Math.Tan(be * l);
                askE = ze * Math.Sin(be * l);
                askO = zo * Math.Sin(bo * l);
            }
            else
            {
                l = PassivePlan.OnGrid(Math.PI / (2.0 * be));
                if (form == CouplerCompensation.Capacitive) askO = zo / Math.Sin(bo * l);
                else break;                                   // Wiggly: the uncompensated pair, longer.
            }
        }
        double thetaE = k0 * Math.Sqrt(m.EeffE) * l, thetaO = k0 * Math.Sqrt(m.EeffO) * l;
        var design = new CoupledLineCouplerDesign(couplingDb, z0, fHz, ze, zo, w, gap, l, Modes(w, gap, l, fHz, s),
            new PassivePlan(Pins, [new PlanCoupledLine([1, 2, 3, 4], 0.0, 0.0, 0.0, w, gap, l)], NodeCount: 4))
        {
            Sections = [(askE, askO, g)], Compensation = form,
        };
        switch (form)
        {
            case CouplerCompensation.Capacitive:
                return design with
                {
                    CompensationFarads = (Math.Cos(thetaO) - Math.Cos(thetaE)) / (2.0 * 2.0 * Math.PI * fHz * m.Zo * Math.Sin(thetaO)),
                };
            case CouplerCompensation.Wiggly:
            {
                double k = Math.Sqrt(m.EeffE / m.EeffO);
                return design with
                {
                    OddLengthFactor = k,
                    Plan = new PassivePlan(Pins, [new PlanCoupledLine([1, 2, 3, 4], 0.0, 0.0, 0.0, w, gap, l, 2, k)], 4),
                };
            }
            default:
            {
                // Each end's series line: 2·Z0 wide (narrow, so mostly inductance), as long as its series term needs.
                double wl = PassivePlan.OnGrid(BranchLinePlan.Width(2.0 * z0, fHz, s, part, "device.coupled-line-coupler.width-unreachable"));
                var line = BranchLinePlan.Line(wl, s).LineParameters(fHz);
                if (!(x > 0 && x < line.Z0))
                    throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line-coupler.inductive-unreachable",
                        DiagnosticSeverity.Error, "Coupled-line coupler: the inductive compensation needs {x} ohm at each end, "
                      + "which a {z} ohm line section cannot give.", ("x", x), ("z", line.Z0)));
                double li = PassivePlan.OnGrid(PlanarLineAngle.LengthMeters(line, Math.Asin(x / line.Z0)));
                double p = w + gap;
                // Nodes: 1-4 the pins at the sections' outer ends; 5-8 the pair's own ends.
                var sections = new PlanSection[]
                {
                    new PlanLine([1, 5], 0.0, 0.0, 0.0, wl, li),
                    new PlanLine([3, 7], 0.0, -p, 0.0, wl, li),
                    new PlanCoupledLine([5, 6, 7, 8], li, 0.0, 0.0, w, gap, l),
                    new PlanLine([6, 2], li + l, 0.0, 0.0, wl, li),
                    new PlanLine([8, 4], li + l, -p, 0.0, wl, li),
                };
                return design with
                {
                    CompensationOhms = x, InductiveSection = (wl, li), Plan = new PassivePlan(Pins, sections, NodeCount: 8),
                };
            }
        }
    }

    // ── Tandem ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The coupling (dB) of each of two identical couplers whose tandem couples <paramref name="couplingDb"/>:
    /// voltage couplings c1 = sin θ give sin 2θ in tandem, so c1 = sin(asin(c)/2); 3 dB from two of 8.34 dB.</summary>
    public static double TandemSubCoupling(double couplingDb)
        => -20.0 * Math.Log10(Math.Sin(Math.Asin(Math.Pow(10.0, -couplingDb / 20.0)) / 2.0));

    /// <summary>
    /// Two identical couplers in tandem (J. P. Shelton, J. Wolfe and R. C. Van Wagoner, "Tandem couplers and phase
    /// shifters for multi-octave bandwidth," Microwaves, April 1965). A TEM backward-wave coupler takes its inputs on
    /// two diagonal corners (1 and 4) to outputs on the other two (2 and 3) as [[t, c], [c, t]]; feeding the second
    /// coupler's diagonal pair from the first's gives [[t² + c², 2tc], [2tc, t² + c²]], so two couplings c1 make
    /// 2·c1·√(1 − c1²) — a tighter coupling, broadband, from two weak and buildable ones.
    ///
    /// <para><b>The layout.</b> Coupler I on top, coupler II below it, both along +x; I.2 (A far) joins II.2' and I.3 (B
    /// near) joins II.3' by two lines around the right and left ends, equal by the layout's 180° symmetry, so both
    /// arrive in phase. II.1' (t² + c²) is the through port 2 and II.4' (2tc) the coupled port 3, in quadrature; I.4 is
    /// the isolated port 4. Each joining line encloses one port's lead, I.4's on the right and II.1''s on the left,
    /// which no planar layout avoids, so each line is broken where that lead crosses: J1/J2 on the right and J3/J4 on
    /// the left are the ends of the break, for a jumper each (a bond wire, an airbridge or an underpass), as MIDC's
    /// fingers are joined. Every line is the coupled strips' own width; pins 1 and 3 have straight leads as long, at F,
    /// as the bent leads of 2 and 4, so the ports keep the couplers' phases.</para>
    /// </summary>
    private static CoupledLineCouplerDesign Tandem(double couplingDb, double z0, double fHz, MicrostripSubstrate s)
    {
        double c1Db = TandemSubCoupling(couplingDb);
        var (ze, zo) = CoupledLineSynthesis.ModeImpedances(c1Db, z0);
        var g = CoupledLineSynthesis.Synthesize(ze, zo, fHz, s, "Coupled-line coupler (tandem)");
        double w = PassivePlan.OnGrid(g.WMeters), gap = PassivePlan.OnGrid(g.SMeters), l = PassivePlan.OnGrid(g.QuarterWaveMeters);
        double p = w + gap, clr = w;
        var bend = new PlanBend([], 0.0, 0.0, 0.0, w, -90.0, PlanBend.MiterFor(PlanCorner.Miter, w, s));
        double r = bend.Arm;
        var lead = BranchLinePlan.Line(w, s).LineParameters(fHz);
        double q = PlanarLineAngle.LengthMeters(lead, Math.PI / 2.0);
        double corner = bend.LineLength(s, fHz, lead.Z0, q);

        double a = 2.0 * w;                                              // I.2's line out to the right-hand run
        // I's B to II's A, a multiple of 4 nm so the mid-gap is on the grid: room for the lead's drop and the runs either
        // side of the break, each at least one w, so no piece is so short that its admittance swamps the rest.
        double d = Math.Ceiling(Math.Max(2.0 * w + 4.0 * r, 5.0 * w + 2.0 * r) / 4e-9) * 4e-9;
        double y0 = -p - d, ys = (y0 - p) / 2.0;                         // II's A; the leads' height, mid-gap
        double d1 = PassivePlan.OnGrid(-p - 2.0 * r - ys);               // the right lead's drop
        double yl = -p - 2.0 * r - d1;                                   // where it runs out, as drawn
        double f = PassivePlan.OnGrid(a - r + 1.5 * w + clr);             // out past the run, one w beyond its clearance
        double lf = PassivePlan.OnGrid(2.0 * corner + d1 + f);            // pins 1 and 3's straight leads, as long at F
        double xi = lf + l, xr = xi + a + r;
        double h1 = PassivePlan.OnGrid(-r - (yl + w / 2.0 + clr));       // the run down to J1, above the lead
        double h2 = PassivePlan.OnGrid(yl - w / 2.0 - clr - (y0 + r));   // and from J2, below it, to the lower corner
        if (!(d1 >= 0 && h1 >= 0 && h2 >= 0))
            throw new InvalidOperationException("tandem layout: a negative run");

        // Nodes: 1-4 the ports, 5-8 J1-J4; I's ends 9-12, II's 13-16; right-hand pieces 17-22, left-hand 23-28.
        const int i1 = 9, i2 = 10, i3 = 11, i4 = 12, ii1 = 13, ii2 = 14, ii3 = 15, ii4 = 16;
        var right = new PlanSection[]
        {
            new PlanLine([i2, 17], xi, 0.0, 0.0, w, a),
            new PlanBend([17, 18], xi + a, 0.0, 0.0, w, -90.0, bend.M),
            new PlanLine([18, 5], xr, -r, 270.0, w, h1),
            new PlanLine([6, 19], xr, y0 + r + h2, 270.0, w, h2),
            new PlanBend([19, 20], xr, y0 + r, 270.0, w, -90.0, bend.M),
            new PlanLine([20, ii2], xr - r, y0, 180.0, w, a),
            new PlanBend([i4, 21], xi, -p, 0.0, w, -90.0, bend.M),
            new PlanLine([21, 22], xi + r, -p - r, 270.0, w, d1),
            new PlanBend([22, 23], xi + r, yl + r, 270.0, w, 90.0, bend.M),
            new PlanLine([23, 4], xi + 2.0 * r, yl, 0.0, w, f),
        };
        // The left-hand pieces: the right-hand ones turned 180° about the layout's centre.
        double xc = lf + l / 2.0, yc = (y0 - p) / 2.0;
        var image = new Dictionary<int, int> { [i2] = ii3, [ii2] = i3, [i4] = ii1, [4] = 2, [5] = 7, [6] = 8 };
        int Image(int node) => image.TryGetValue(node, out int to) ? to : node + 7;   // 17-23 to 24-30
        var sections = new List<PlanSection>
        {
            new PlanLine([1, i1], 0.0, 0.0, 0.0, w, lf),
            new PlanCoupledLine([i1, i2, i3, i4], lf, 0.0, 0.0, w, gap, l),
            new PlanCoupledLine([ii1, ii2, ii3, ii4], lf, y0, 0.0, w, gap, l),
            new PlanLine([ii4, 3], xi, y0 - p, 0.0, w, lf),
        };
        sections.AddRange(right);
        sections.AddRange(right.Select(x => x with
        {
            Nodes = [.. x.Nodes.Select(Image)], X = 2.0 * xc - x.X, Y = 2.0 * yc - x.Y, RotationDeg = (x.RotationDeg + 180.0) % 360.0,
        }));
        return new CoupledLineCouplerDesign(couplingDb, z0, fHz, ze, zo, w, gap, l, Modes(w, gap, l, fHz, s),
            new PassivePlan(TandemPins, sections, NodeCount: 30))
        {
            Sections = [(ze, zo, g)], SubCouplingDb = c1Db,
        };
    }

    // ── Broadside ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One broadside pair (BCLIN on a stripline stack, MBCLIN otherwise), its W and offset found for the modes;
    /// refused when the stack has no second signal conductor next to the first.</summary>
    private static CoupledLineCouplerDesign Broadside(double couplingDb, double z0, double fHz, BroadsideStack? stack)
    {
        if (stack is null)
            throw new ParameterRefusalException(Diagnostic.Create("device.coupled-line-coupler.no-second-layer",
                DiagnosticSeverity.Error, "Coupled-line coupler: Broadside needs two signal conductors one above the other "
              + "over a ground, and this technology has none next to the signal layer. Turn Broadside off, or use a stack "
              + "with a second signal layer."));
        var (ze, zo) = CoupledLineSynthesis.ModeImpedances(couplingDb, z0);
        var (w, o, l, modes) = BroadsideSynthesis.Synthesize(ze, zo, fHz, stack, "Coupled-line coupler");
        var sections = new PlanSection[] { new PlanBroadsideLine([1, 2, 3, 4], 0.0, 0.0, 0.0, w, o, l, stack) };
        return new CoupledLineCouplerDesign(couplingDb, z0, fHz, ze, zo, w, 0.0, l, default!,
            new PassivePlan(Pins, sections, NodeCount: 4))
        {
            Broadside = stack, OffsetMeters = o, BroadsideModes = modes,
        };
    }

    /// <summary>MCLIN's modes at F for a pair as drawn: what the model will stamp.</summary>
    internal static KirschningJansenCoupled.Modes Modes(double w, double gap, double l, double fHz, MicrostripSubstrate s)
        => new MicrostripCoupledLineModel(w, gap, l, s.HMeters, s.TMeters, s.EpsR, s.SigmaSPerM, s.TanD,
            "(coupled-line plan)", s.RoughnessMeters).ModesAt(fHz);
}
