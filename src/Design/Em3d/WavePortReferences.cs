// brief-emw-2 R-emw2-4 — what a wave port's S is referred to, and how circuitRF moves it to what the .sNp states. Shared by
// every solver that refers a wave port's S to the port's own mode at unit power (Palace, and EMerge from EMW-2): the
// reference-plane shift in the mode's own normalisation, then the renormalisation from the mode's impedance Z_PV to the
// port's Z0, and the run's note saying both. Moved here out of Palace's partial (R-em3d23-2d) so the two solvers share one.

using System.Globalization;
using System.Numerics;
using CircuitRF.Engine.Em3d;
using NumFlat;
using RfCore;

namespace CircuitRF.Design.Em3d;

internal static class WavePortReferences
{
    private static string Fmt(double v) => v.ToString("G4", CultureInfo.InvariantCulture);

    /// <summary>
    /// R-em3d23-2d — <paramref name="s"/> with every port moved from <paramref name="from"/>[k][i] (per frequency, per port)
    /// to its stated Z0: the whole matrix per frequency (RfCore's <see cref="RFNetwork.SToS"/>), never an element at a time.
    /// </summary>
    public static Complex[][,] Renormalise(Complex[][,] s, IReadOnlyList<Em3dPort> ports, IReadOnlyList<Complex[]> from)
    {
        int n = ports.Count;
        var target = ports.Select(p => p.Z0).ToArray();
        var mats = new Complex[s.Length][,];
        for (int k = 0; k < s.Length; k++)
        {
            var m = new Mat<Complex>(n, n);
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) m[i, j] = s[k][i, j];
            var r = RFNetwork.SToS(m, from[k], target);
            mats[k] = new Complex[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) mats[k][i, j] = r[i, j];
        }
        return mats;
    }

    /// <summary>
    /// Palace's S with every wave port moved from its mode's impedance (<paramref name="modeZ"/>[k][w], <paramref name="wave"/>'s
    /// order) to its stated Z0. A lumped port is already at its Z0 (its resistance), so its reference is unchanged.
    /// </summary>
    public static PalacePortS Renormalise(PalacePortS s, IReadOnlyList<Em3dPort> ports, IReadOnlyList<Em3dPort> wave, Complex[][] modeZ)
    {
        var at = wave.Select(w => ports.ToList().IndexOf(w)).ToArray();
        var from = new Complex[s.S.Length][];
        for (int k = 0; k < s.S.Length; k++)
        {
            from[k] = [.. ports.Select(p => p.Z0)];
            for (int w = 0; w < wave.Count; w++) from[k][at[w]] = modeZ[k][w];
        }
        return s with { S = Renormalise(s.S, ports, from) };
    }

    /// <summary>
    /// The reference-plane shift, in the mode's own normalisation: S′ᵢⱼ = Sᵢⱼ·e^(γᵢdᵢ)·e^(γⱼdⱼ), dᵢ port i's
    /// <see cref="Em3dReferencePlane.ShiftM"/> (positive into the structure) and γᵢ = αᵢ + jβᵢ its mode's propagation constant
    /// at that frequency (<paramref name="gamma"/>[k][i]; 0 for a lumped port, which moves nothing). It must come BEFORE the
    /// renormalisation: renormalising mixes incident and reflected waves, and the shift is exact only on a matched line.
    /// </summary>
    public static Complex[][,] Shift(Complex[][,] s, IReadOnlyList<Em3dPort> ports, IReadOnlyList<Complex[]> gamma)
    {
        int n = ports.Count;
        var mats = new Complex[s.Length][,];
        for (int k = 0; k < s.Length; k++)
        {
            var e = new Complex[n];
            for (int i = 0; i < n; i++) e[i] = Complex.Exp(gamma[k][i] * ports[i].ReferencePlane.ShiftM);
            mats[k] = new Complex[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) mats[k][i, j] = s[k][i, j] * e[i] * e[j];
        }
        return mats;
    }

    /// <summary>
    /// brief-em3d-141 R-em3d141-2 — each wave port's mode voltage per frequency, and where its fit leaned. Unit-power
    /// normalisation fixes a mode's magnitude (|v|² = Z_PV) and not its phase, which is an eigenvector's free phase; the S a
    /// solver writes against such modes carries e^(j(θᵢ − θⱼ)) and is non-reciprocal whenever two ports' θ differ.
    /// <para><see cref="Voltage"/>[k][w] is wave port w's mode voltage at frequency row k (zero when its path reads nothing).
    /// <see cref="LeanedOn"/>[k][w] is −1 unless the port looks into a near short there (|1 + Sᵢᵢ| below <see cref="NearShort"/>),
    /// when its own excitation says little about its mode: then the wave index of the excitation weighted most in its fit.</para>
    /// </summary>
    public sealed record ModeVoltages(Complex[][] Voltage, int[][] LeanedOn);

    /// <summary>|1 + Sᵢᵢ| below which a port's own excitation is said not to have carried its mode's phase.</summary>
    public const double NearShort = 0.1;

    /// <summary>
    /// brief-em3d-141 R-em3d141-2 — each wave port's mode voltage, fitted from every excitation at once. With port u driven,
    /// the total field's line integral along port w's path is V_wu = v_w·P_wu, P = 1 + S at the port faces, so v_w is the
    /// least-squares solution of that row: v_w = Σᵤ P̄_wu V_wu / Σᵤ |P_wu|². A port looking into a short (small |1 + S_ww|)
    /// is weighted toward the excitations that do reach it, with no threshold to choose. It is the one-entry case of
    /// <see cref="ModalTerminalTransform"/>'s per-face fit of M, and on brief 141's fixture it leaves |S₂₁ − S₁₂| at 0.001
    /// where the port's own excitation alone leaves 0.016 (the voltage path also sees evanescent fields the projection does not).
    /// <para><paramref name="s"/> is the S the solver wrote, in port order; <paramref name="at"/> each wave port's index in that
    /// order; <paramref name="v"/>[k][w, u] V_wp for wave port w with wave port u driven. <paramref name="deembed"/>[k][i], when
    /// given, is the factor the solver multiplied each side of S by for port i's reference-plane offset (Palace: e^(j kₙ d));
    /// S is divided by it on both sides to read P at the faces, where V_wp is taken.</para>
    /// </summary>
    public static ModeVoltages FitModeVoltages(Complex[][,] s, IReadOnlyList<int> at, IReadOnlyList<Complex[,]> v,
                                               IReadOnlyList<Complex[]>? deembed = null)
    {
        int nw = at.Count;
        var volts = new Complex[s.Length][];
        var leaned = new int[s.Length][];
        for (int k = 0; k < s.Length; k++)
        {
            volts[k] = new Complex[nw];
            leaned[k] = new int[nw];
            for (int w = 0; w < nw; w++)
            {
                Complex num = Complex.Zero;
                double den = 0, own = 0, otherMax = 0;
                int other = -1;
                for (int u = 0; u < nw; u++)
                {
                    Complex face = s[k][at[w], at[u]];
                    if (deembed is not null) face /= deembed[k][at[w]] * deembed[k][at[u]];
                    Complex p = (w == u ? Complex.One : Complex.Zero) + face;
                    double weight = p.Real * p.Real + p.Imaginary * p.Imaginary;
                    num += Complex.Conjugate(p) * v[k][w, u];
                    den += weight;
                    if (u == w) own = weight;
                    else if (weight > otherMax) (otherMax, other) = (weight, u);
                }
                volts[k][w] = den > 0 ? num / den : Complex.Zero;
                leaned[k][w] = own < NearShort * NearShort && otherMax > own ? other : -1;
            }
        }
        return new ModeVoltages(volts, leaned);
    }

    /// <summary>
    /// brief-em3d-141 R-em3d141-2 — S referred to modes whose voltage along each port's path is real: S′ = D S D⁻¹,
    /// D = diag(e^(jθᵢ)), θ given per frequency for each wave port (<paramref name="at"/>'s order); a lumped port has θ = 0.
    /// Each off-diagonal entry is multiplied by one factor e^(j(θᵢ − θⱼ)), so a row whose θ are equal comes back bit-identical
    /// and the diagonal is never touched. Diagonal, so it commutes with <see cref="Shift"/>; it must precede
    /// <see cref="Renormalise(Complex[][,], IReadOnlyList{Em3dPort}, IReadOnlyList{Complex[]})"/>, which mixes entries.
    /// </summary>
    public static Complex[][,] Rotate(Complex[][,] s, IReadOnlyList<int> at, IReadOnlyList<double[]> theta)
    {
        var mats = new Complex[s.Length][,];
        for (int k = 0; k < s.Length; k++)
        {
            int n = s[k].GetLength(0);
            var th = new double[n];
            for (int w = 0; w < at.Count; w++) th[at[w]] = theta[k][w];
            mats[k] = (Complex[,])s[k].Clone();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    if (i != j && th[i] != th[j]) mats[k][i, j] = s[k][i, j] * Complex.FromPolarCoordinates(1, th[i] - th[j]);
        }
        return mats;
    }

    /// <summary>The largest |S_ij − S_ji| over every frequency and pair: 0 for a reciprocal network.</summary>
    public static double Reciprocity(Complex[][,] s)
    {
        double worst = 0;
        foreach (var m in s)
            for (int i = 0; i < m.GetLength(0); i++)
                for (int j = i + 1; j < m.GetLength(0); j++) worst = Math.Max(worst, Complex.Abs(m[i, j] - m[j, i]));
        return worst;
    }

    /// <summary>
    /// brief-em3d-141 R-em3d141-4 — the run's one line on the modes' phase: each wave port's range over the sweep, that S was
    /// referred to it, and the largest |S_ij − S_ji| of the solver's own S and of the published one.
    /// </summary>
    public static string PhaseNote(string solver, IReadOnlyList<Em3dPort> wave, IReadOnlyList<double[]> theta, double before, double after)
    {
        var parts = wave.Select((p, w) =>
        {
            var deg = theta.Select(t => t[w] * 180 / Math.PI).ToList();
            string range = Fmt(deg.Min()) == Fmt(deg.Max()) ? $"{Fmt(deg[0])}°" : $"{Fmt(deg.Min())}° to {Fmt(deg.Max())}°";
            return $"port {p.Number}'s {range}";
        });
        return $"{solver} leaves each wave port's mode in an arbitrary phase, which its S carries between ports: the mode voltage " +
               $"along each port's path is {string.Join("; ", parts)} over the sweep, and S was referred to modes whose voltage is " +
               $"real before it was renormalised. Reciprocity, the largest |S_ij − S_ji|: {Sci(before)} as {solver} wrote it, " +
               $"{Sci(after)} published.";
    }

    private static string Sci(double v) => v.ToString(v != 0 && Math.Abs(v) < 1e-3 ? "0.0E+0" : "0.####", CultureInfo.InvariantCulture);

    /// <summary>What each wave port's S was referred to, and what it is now. <paramref name="source"/> says where the mode's
    /// impedance came from, in parentheses after Z_PV.</summary>
    public static string Note(string solver, string source, IReadOnlyList<Em3dPort> wave, IReadOnlyList<Complex[]> modeZ)
    {
        var parts = wave.Select((p, w) =>
        {
            var re = modeZ.Select(z => z[w].Real).ToList();
            string range = Fmt(re.Min()) == Fmt(re.Max()) ? $"{Fmt(re[0])} Ω" : $"{Fmt(re.Min())} to {Fmt(re.Max())} Ω";
            return $"port {p.Number}'s mode impedance is {range} over the sweep, renormalised to {Fmt(p.Z0.Real)} Ω";
        });
        return $"{solver} refers a wave port's S-parameters to the port's own mode, normalised to unit power — that is, to the " +
               $"mode's impedance Z_PV ({source}), which varies with frequency. The .sNp states one real reference per port, " +
               $"so the wave ports were renormalised: {string.Join("; ", parts)}.";
    }

    /// <summary>One line per wave port whose reference plane moved: how far, and that the mode's β de-embedded it.</summary>
    public static IEnumerable<string> ShiftNotes(IEnumerable<Em3dPort> wave)
        => wave.Where(p => p.ReferencePlane.ShiftM != 0).Select(p =>
               $"Port {p.Number}'s reference plane is {Fmt(Math.Abs(p.ReferencePlane.ShiftM) * 1e3)} mm " +
               $"{(p.ReferencePlane.ShiftM > 0 ? "into" : "out of")} the structure: its line was de-embedded with the mode's β.");
}
