using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>What an oscillator analysis's <c>PhaseNoise=yes</c> asks for (brief NZ-10): the offsets from the carrier, and
/// the Port or Term to read at (null: across a shunt OscProbe, or the circuit's one port).</summary>
public sealed record OscPhaseNoiseParams(double StartHz, double StopHz, int Npts, bool Log, int? OutPort)
{
    /// <summary>The offsets, Hz: evenly spaced in log-frequency when <see cref="Log"/>, linearly otherwise.</summary>
    public double[] Offsets()
    {
        var x = new double[Npts];
        for (int i = 0; i < Npts; i++)
        {
            double t = (double)i / (Npts - 1);
            x[i] = Log ? StartHz * Math.Pow(StopHz / StartHz, t) : StartHz + (StopHz - StartHz) * t;
        }
        x[0] = StartHz;
        x[^1] = StopHz;
        return x;
    }
}

/// <summary>Everything <see cref="OscPhaseNoise.Add"/> takes from the solved oscillation: the driven solve at the answer
/// (its linearisation and back-solver), the probe and its generator, and the outer Newton's Jacobian about the answer —
/// <c>R</c> the residual (the probe's immittance over its scale) against ln A and f, and <c>W</c> the circuit's own immittance
/// against the log of the probe's level and f.</summary>
internal sealed record OscPhaseNoiseInputs(
    string Analysis, OscPhaseNoiseParams P, HbEngine.SinglePointResult Point, ElaboratedComponent Probe, bool Shunt,
    double Amplitude, double FoscHz, Complex Vprobe, double GGen, double Scale,
    Complex RLnA, Complex RF, Complex WLnL, Complex WF);

/// <summary>
/// Brief NZ-10 — oscillator phase noise, on brief NZ-9's solution, from the circuit's own noise sources (brief NZ-5's: the
/// passive parts thermal at their own temperature, the devices cyclostationary over their own waveform, a Mixer's NF; every Port and Term is a thermal
/// termination at T0). Derived in <c>docs/design/hb-noise.md</c> §10.
///
/// <para><b>Far from the carrier</b> the conversion matrix about the oscillation (the probe idle, so the circuit is
/// autonomous) gives the noise at the carrier's two sidebands, <c>X₊</c> at <c>f0 + f_m</c> and <c>X₋</c> at <c>f_m − f0</c>,
/// with their correlation, through two adjoints of <see cref="HbNoise.Adjoint"/>. Referred to the carrier <c>V1</c> there,
/// the phase and amplitude parts of the envelope are</para>
/// <code>
///   φ = (X₊/V1 − X₋/V1*) / j        α = X₊/V1 + X₋/V1*        L = ½·⟨|φ|²⟩,  AN = ½·⟨|α|²⟩
/// </code>
/// <para>(the ½ because the noise densities are single-sided, and L is the single-sided phase density over two).</para>
///
/// <para><b>Close to the carrier</b> the autonomous conversion matrix is singular: nothing restores the phase. There the
/// noise moves the oscillation, and how far is the outer Newton's own sensitivity. A slow current <c>u</c> at sideband s of
/// the generator-held circuit moves the residual by <c>δR = Σ β_s·u_s</c> (one adjoint of the static driven solve), and the
/// oscillation answers with <c>[δln A; δf] = −M⁻¹·[Re δR; Im δR]</c>, M the outer Jacobian. The frequency deviation's
/// density over f_m² is L; white sources give 1/f_m², flicker at baseband 1/f_m³.</para>
///
/// <para><b>The join.</b> The sensitivity holds while f_m is well below the rate at which the amplitude settles, f_AM, read
/// off the circuit's immittance W at the probe: <c>f_AM = −Im(W_lnL·conj W_f) / |W_f|²</c>. Below <see cref="JoinFraction"/>
/// of it the close-in description is used, above it the conversion matrix, with no blend and nothing tuned per circuit. Both
/// are computed at the join offset itself and a disagreement there of more than <see cref="JoinTolDb"/> is a warning. The
/// singular point is never regularised.</para>
/// </summary>
internal static class OscPhaseNoise
{
    /// <summary>The join offset as a fraction of the amplitude-relaxation rate f_AM.</summary>
    public const double JoinFraction = 0.1;

    /// <summary>How far the two descriptions may differ at the join before the run says so, dB.</summary>
    public const double JoinTolDb = 0.5;

    /// <summary>One noisy part as a quadratic form: Σ_s,t h_s·C_st·h_tᴴ over the sidebands at one offset.</summary>
    private sealed class Sources
    {
        public required List<NoiseSource> Stationary;
        public required List<HbNoise.Device> Devices;
        public required List<(string Label, int Plus, int Minus, Func<double, Complex> Z)> Ports;
        public required HbNoise.Samples Smp;
        public required HbSmallSignal.ISidebands Sb;
        public string[] Labels => [.. Stationary.Select(x => x.Label), .. Devices.Select(x => x.Label), .. Ports.Select(x => x.Label)];
        public int Count => Stationary.Count + Devices.Count + Ports.Count;
    }

    public static void Add(DataSet ds, ElaboratedNetlist nl, OscPhaseNoiseInputs x)
    {
        var lin = x.Point.Linearization!;
        var back = x.Point.BackSolver!;
        var solve = lin.Solve;
        var extractor = lin.Extractor;
        int K = lin.K, nInt = extractor.InterfaceCount;
        var sb = new HbSmallSignal.ToneSidebands(solve.G!, solve.C!, solve.Buckets, K, lin.Omega0);
        int S = sb.Count, Z = sb.Zero;
        if (nInt * S > HbSmallSignal.ConversionMatrixCeiling)
            throw new InvalidOperationException(HbSmallSignal.TooLargeMessage(nInt * S, nInt, S)
                .Replace("wsprobe.hb-ss-", "osc.pn.", StringComparison.Ordinal));

        // ── Where the noise is read, and the carrier there ──
        var (plus, minus, where) = Output(nl, x.Analysis, x.Probe, x.Shunt, x.P.OutPort);
        Complex Node(int n) => n > 0 ? back.GetNodeVoltage(n, 1, 0) : Complex.Zero;
        Complex v1 = Node(plus) - Node(minus);
        if (!(v1.Magnitude > 1e-12 * Math.Max(1, x.Vprobe.Magnitude)))
            throw new InvalidOperationException(OscDiagnostics.PnNoCarrier(x.Analysis, where).Render());

        // ── The join: a fixed fraction of the amplitude's settling rate ──
        double fAm = -(x.WLnL * Complex.Conjugate(x.WF)).Imaginary / (x.WF.Magnitude * x.WF.Magnitude);
        double fJoin = 0;
        if (double.IsFinite(fAm) && fAm > 0) fJoin = JoinFraction * fAm;
        else nl.AddWarningOnce(OscDiagnostics.PnNoRelaxationKey, OscDiagnostics.PnNoRelaxation(x.Analysis).Render());

        double[] offsets = x.P.Offsets();
        double half = 0.5 * x.FoscHz;

        // ── The noise sources, once ──
        var dcX = back.GetSolution(0, 0);
        var dcNodes = new double[extractor.NonGroundCount];
        for (int i = 0; i < dcNodes.Length && i < dcX.Length; i++) dcNodes[i] = dcX[i].Real;
        var smp = HbNoise.ToneSamples(x.Point.V, lin.N, K, lin.GridN, solve.G!.GetLength(2) - 1);
        var hzSet = new SortedSet<double>();
        foreach (double f in offsets.Append(fJoin))
            if (f > 0 && f < half)
                for (int s = 0; s < S; s++) hzSet.Add(Math.Abs(2 * Math.PI * f + sb.Offset(s)) / (2 * Math.PI));
        double[] sidebandHz = [.. hzSet];
        var src = BuildSources(nl, smp, sb, extractor.InterfaceNodes, sidebandHz, dcNodes);

        // ── Close-in: the frequency's and the amplitude's sensitivity to a slow current at every sideband ──
        var (hF, hA) = Sensitivities(x, extractor, sb, nInt, lin.YDc);
        // From here on the circuit is the autonomous one at every frequency: the generator was needed only for the static
        // sensitivity above, and an offset closer to the carrier than its 1e-9 window would otherwise stamp it into a sideband.
        ((OscProbeModel)x.Probe.Model).Disarm();

        int np = src.Count, nf = offsets.Length;
        var pn = new double[nf];
        var an = new double[nf];
        var parts = new double[nf * np];
        bool warnedHalf = false;
        for (int fi = 0; fi < nf; fi++)
        {
            double fm = offsets[fi];
            if (fm >= half)
            {
                if (!warnedHalf) nl.AddWarningOnce(OscDiagnostics.PnAboveHalfKey, OscDiagnostics.PnAboveHalf(x.Analysis, x.FoscHz).Render());
                warnedHalf = true;
                pn[fi] = an[fi] = double.NaN;
                for (int k = 0; k < np; k++) parts[fi * np + k] = double.NaN;
                continue;
            }
            double[]? pnParts, anParts;
            if (fm <= fJoin)
            {
                var stat = StationaryAt(src, fm);
                pnParts = Quad(src, stat, hF, fm, whiteOnly: false);
                for (int k = 0; k < np; k++) pnParts[k] /= 2 * fm * fm;
                anParts = Quad(src, stat, hA, fm, whiteOnly: false);
                for (int k = 0; k < np; k++) anParts[k] /= 2;
            }
            else
            {
                (pnParts, anParts) = Far(x, nl, src, extractor, sb, nInt, lin.YDc, fm, plus, minus, v1);
            }
            if (pnParts is null || anParts is null)
            {
                pn[fi] = an[fi] = double.NaN;
                for (int k = 0; k < np; k++) parts[fi * np + k] = double.NaN;
                continue;
            }
            pn[fi] = pnParts.Sum();
            an[fi] = anParts.Sum();
            for (int k = 0; k < np; k++) parts[fi * np + k] = pnParts[k];
        }

        // ── Both descriptions at the join itself: they should agree there ──
        if (fJoin > 0 && fJoin < half && fJoin >= offsets[0] && fJoin <= offsets[^1])
        {
            var stat = StationaryAt(src, fJoin);
            double close = Quad(src, stat, hF, fJoin, whiteOnly: false).Sum() / (2 * fJoin * fJoin);
            var (far, _) = Far(x, nl, src, extractor, sb, nInt, lin.YDc, fJoin, plus, minus, v1);
            if (far is not null)
            {
                double diff = 10 * Math.Log10(close / far.Sum());
                if (!(Math.Abs(diff) <= JoinTolDb))
                    nl.AddWarningOnce(OscDiagnostics.PnJoinKey, OscDiagnostics.PnJoinMismatch(x.Analysis, fJoin, diff).Render());
            }
        }

        // ── The flicker corner: where the close-in 1/f³ and 1/f² asymptotes cross ──
        double corner = Corner(x, nl, src, hF, half);

        // ── Cubes ──
        Axis Off() => new("offset", (double[])offsets.Clone(), "Hz");
        static double Db(double v) => 10 * Math.Log10(Math.Max(v, 1e-300));
        ds.Add("PN", new DataCube([Off()], [.. pn.Select(v => double.IsNaN(v) ? double.NaN : Db(v))]) { Unit = "dBc/Hz" });
        ds.Add("AN", new DataCube([Off()], [.. an.Select(v => double.IsNaN(v) ? double.NaN : Db(v))]) { Unit = "dBc/Hz" });
        var labels = src.Labels;
        if (np > 0)
        {
            Axis Part() => new("part", [.. Enumerable.Range(1, np).Select(k => (double)k)], "", labels);
            var pnc = new double[nf * np];
            var pct = new double[nf * np];
            for (int fi = 0; fi < nf; fi++)
                for (int k = 0; k < np; k++)
                {
                    double c = parts[fi * np + k];
                    pnc[fi * np + k] = double.IsNaN(c) ? double.NaN : Db(c);
                    pct[fi * np + k] = 100 * c / pn[fi];
                }
            ds.Add("PNc",   new DataCube([Off(), Part()], pnc) { Unit = "dBc/Hz" });
            ds.Add("PNpct", new DataCube([Off(), Part()], pct) { Unit = "%" });
        }
        ds.Add("PNcorner", new DataCube(Array.Empty<Axis>(), [corner]) { Unit = "Hz" });
    }

    // ── Where to read ────────────────────────────────────────────────────────

    /// <summary>The two nodes the noise is read across and their owner's name: the Port or Term <paramref name="outPort"/>
    /// numbers, else a shunt probe, else the circuit's one Port or Term. Anything else is the refusal the run gives.</summary>
    internal static (int Plus, int Minus, string Where) Output(ElaboratedNetlist nl, string analysis, ElaboratedComponent probe,
                                                              bool shunt, int? outPort)
    {
        var ports = nl.Components.Where(c => !c.InstancePath.Contains('.') && c.Model is PortModel or TermModel).ToList();
        if (outPort is int num)
        {
            var ec = ports.FirstOrDefault(c => c.Parameters.TryGetValue("Num", out var nv) && (int)nv.AsReal() == num)
                     ?? throw new InvalidOperationException(OscDiagnostics.PnNoPort(analysis, num).Render());
            return (ec.Nodes[0], ec.Nodes.Length > 1 ? ec.Nodes[1] : 0, ec.InstancePath);
        }
        if (shunt) return (probe.Nodes[0], probe.Nodes[1], probe.InstancePath);
        if (ports.Count == 1) return (ports[0].Nodes[0], ports[0].Nodes.Length > 1 ? ports[0].Nodes[1] : 0, ports[0].InstancePath);
        throw new InvalidOperationException(OscDiagnostics.PnNoOutput(analysis, probe.InstancePath).Render());
    }

    // ── The sources ──────────────────────────────────────────────────────────

    private static Sources BuildSources(ElaboratedNetlist nl, HbNoise.Samples smp, HbSmallSignal.ISidebands sb, int[] ifNodes,
                                        double[] sidebandHz, double[] dcNodes)
    {
        var deviceRows = new List<(NoiseModelRow, ElaboratedComponent)>();
        var mixers = new List<NoiseSource>();
        bool Claim(NoiseModelRow row, List<ElaboratedComponent> parts)
        {
            // The probe is the analysis's own instrument: idle (a short or an open) at every frequency the noise is read at.
            if (parts.Any(ec => ec.Model is OscProbeModel)) return true;
            if (row.Kind == NoiseModelKind.DeviceSources) { deviceRows.Add((row, parts[0])); return true; }
            if (row.Kind == NoiseModelKind.Noiseless && parts is [{ Model: MixerModel { NoiseFigureDb: double nfDb } mx } m])
            {
                mixers.Add(new NoiseSource(row.Label, HbNoise.MixerSource(nl, m, mx, nfDb)));
                return true;
            }
            return false;
        }
        var stationary = SParameterNoise.Sources(nl, sidebandHz, dcNodes, Claim);
        stationary.AddRange(mixers);
        var devices = HbNoise.Devices(nl, deviceRows, smp, ifNodes, sidebandHz);

        // Every top-level Port and Term: the oscillator's load, thermal at T0 through its resistance.
        var ports = new List<(string, int, int, Func<double, Complex>)>();
        foreach (var ec in nl.Components)
        {
            if (ec.InstancePath.Contains('.') || ec.Model is not (PortModel or TermModel)) continue;
            Complex z = !ec.Parameters.TryGetValue("Z", out var zv) ? new Complex(50, 0)
                      : zv.Kind == Core.Expressions.ValueKind.Complex ? zv.AsComplex() : new Complex(zv.AsReal(), 0);
            ports.Add((ec.InstancePath, ec.Nodes[0], ec.Nodes.Length > 1 ? ec.Nodes[1] : 0, _ => z));
        }
        return new Sources { Stationary = stationary, Devices = devices, Ports = ports, Smp = smp, Sb = sb };
    }

    /// <summary>Every stationary source at every sideband of offset <paramref name="fm"/>, before any partition is stamped there
    /// (a recorded stamp writes the model's branch index; the partition's own stamp writes the real one back).</summary>
    private static NoiseCurrents[,] StationaryAt(Sources src, double fm)
    {
        int S = src.Sb.Count;
        var stat = new NoiseCurrents[src.Stationary.Count, S];
        for (int k = 0; k < src.Stationary.Count; k++)
            for (int s = 0; s < S; s++)
            {
                double om = 2 * Math.PI * fm + src.Sb.Offset(s);
                var c = src.Stationary[k].At(Math.Abs(om));
                stat[k, s] = om < 0 && c.Nodes is not null ? new NoiseCurrents(c.Nodes, HbNoise.Conj(c.C)) : c;
            }
        return stat;
    }

    /// <summary>Each part's <c>Σ_s,t h_s·⟨u_s·u_tᴴ⟩·h_tᴴ</c> at offset <paramref name="fm"/>, in the noise currents' single-sided
    /// scaling. <paramref name="whiteOnly"/> drops the devices' flicker terms.</summary>
    private static double[] Quad(Sources src, NoiseCurrents[,] stat, Complex[][] h, double fm, bool whiteOnly)
    {
        int S = src.Sb.Count;
        var om = new double[S];
        for (int s = 0; s < S; s++) om[s] = 2 * Math.PI * fm + src.Sb.Offset(s);
        Complex H(int s, int node) => node > 0 ? h[s][node - 1] : Complex.Zero;
        Complex Form(int s, int t, int[] nodes, Mat<Complex> c)
        {
            Complex acc = Complex.Zero;
            for (int i = 0; i < nodes.Length; i++)
            {
                Complex hi = H(s, nodes[i]);
                if (hi == Complex.Zero) continue;
                for (int j = 0; j < nodes.Length; j++) acc += hi * c[i, j] * Complex.Conjugate(H(t, nodes[j]));
            }
            return acc;
        }

        var outp = new double[src.Count];
        int k0 = 0;
        for (int k = 0; k < src.Stationary.Count; k++)
            for (int s = 0; s < S; s++)
            {
                var c = stat[k, s];
                if (c.Nodes is null) continue;
                outp[k0 + k] += Form(s, s, c.Nodes, c.C).Real;
            }
        k0 += src.Stationary.Count;
        for (int k = 0; k < src.Devices.Count; k++)
        {
            var d = src.Devices[k];
            for (int s = 0; s < S; s++)
                for (int t = 0; t <= s; t++)
                {
                    double fs = Math.Abs(om[s]) / (2 * Math.PI), ft = Math.Abs(om[t]) / (2 * Math.PI);
                    Complex term = Form(s, t, d.Nodes, HbNoise.DeviceAt(d, src.Smp, src.Sb, s, t, fs, ft, whiteOnly));
                    outp[k0 + k] += s == t ? term.Real : 2 * term.Real;
                }
        }
        k0 += src.Devices.Count;
        for (int k = 0; k < src.Ports.Count; k++)
        {
            var (_, p, m, z) = src.Ports[k];
            for (int s = 0; s < S; s++)
            {
                Complex zp = z(Math.Abs(om[s]));
                double g = zp == Complex.Zero ? 0 : Math.Max(0, (Complex.One / zp).Real);
                Complex hh = H(s, p) - H(s, m);
                outp[k0 + k] += 4 * NoiseMetrics.KT0 * g * (hh.Real * hh.Real + hh.Imaginary * hh.Imaginary);
            }
        }
        return outp;
    }

    // ── Close-in ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The frequency's (Hz) and the log-amplitude's sensitivity to a slow current at every sideband, as rows over the
    /// circuit's unknowns: <c>δf = Σ_s h^f_s·u_s</c> with <c>u_s</c> the two-sided phasors of a current at <c>k·f0</c>.
    /// One adjoint of the generator-held circuit's static conversion matrix (the HB Jacobian, at <c>ω_ss = 0</c>) reads the
    /// probe's voltage at the fundamental; the residual's derivative turns that into <c>δR</c>; the outer Jacobian turns
    /// <c>δR</c> into <c>(δln A, δf)</c>. A static current is real, so its <c>−k</c> phasor is the conjugate of its <c>k</c>
    /// one, which is how a real-linear map is written over complex phasors.
    /// </summary>
    private static (Complex[][] HF, Complex[][] HA) Sensitivities(OscPhaseNoiseInputs x, HbLinearExtractor extractor,
        HbSmallSignal.ISidebands sb, int nInt, Complex[,] yDc)
    {
        int S = sb.Count, Z = sb.Zero, nc = nInt * S;
        var om = new double[S];
        for (int s = 0; s < S; s++) om[s] = sb.Offset(s);
        var ys = new Dictionary<double, Complex[,]>();
        for (int s = 0; s < S; s++) ys[om[s]] = HbSmallSignal.YNNAt(extractor, om[s], yDc);
        var jss = HbSmallSignal.BuildJss(sb, nInt, 0.0, w => ys[w]);
        var lu = new HbSmallSignal.DenseLu(Transpose(jss, nc), nc);
        if (lu.Singular) throw new InvalidOperationException(OscDiagnostics.PnSingular(x.Analysis, 0).Render());

        int pa = x.Probe.Nodes[0], pb = x.Probe.Nodes[1];
        var a = HbNoise.Adjoint(extractor, nInt, S, om, ys, jss, lu, Z + 1, r =>
        {
            if (pa > 0) r[pa - 1] += Complex.One;
            if (pb > 0) r[pb - 1] -= Complex.One;
        });

        // dR/dV at the fundamental (V the full-amplitude phasor, twice the two-sided one the adjoint reads).
        double g = x.GGen, amp = x.Amplitude;
        Complex v = x.Vprobe;
        Complex rho = x.Shunt ? -g * amp / (v * v * x.Scale) : -amp / ((amp + g * v) * (amp + g * v) * x.Scale);

        // N = −M⁻¹, M = [[Re R_lnA, Re R_f], [Im R_lnA, Im R_f]].
        double m00 = x.RLnA.Real, m01 = x.RF.Real, m10 = x.RLnA.Imaginary, m11 = x.RF.Imaginary;
        double det = m00 * m11 - m01 * m10;
        double n00 = -m11 / det, n01 = m01 / det, n10 = m10 / det, n11 = -m00 / det;
        var muA = new Complex(n00, n01);
        var muF = new Complex(n10, n11);

        int size = a[Z].Length;
        var hF = new Complex[S][];
        var hA = new Complex[S][];
        for (int s = 0; s < S; s++)
        {
            int mirror = 2 * Z - s;
            hF[s] = new Complex[size];
            hA[s] = new Complex[size];
            for (int i = 0; i < size; i++)
            {
                Complex beta = 2 * rho * a[s][i], betaM = 2 * rho * a[mirror][i];
                hF[s][i] = 0.5 * (Complex.Conjugate(muF) * beta + muF * Complex.Conjugate(betaM));
                hA[s][i] = 0.5 * (Complex.Conjugate(muA) * beta + muA * Complex.Conjugate(betaM));
            }
        }
        return (hF, hA);
    }

    /// <summary>
    /// The flicker corner: where the close-in phase noise's flicker part (the total less its white part) equals its white
    /// part, found by bisection in log-offset between a millihertz and half the carrier. NaN with no flicker, or when a model
    /// reports its noise as one density.
    /// </summary>
    private static double Corner(OscPhaseNoiseInputs x, ElaboratedNetlist nl, Sources src, Complex[][] hF, double half)
    {
        if (src.Devices.Count == 0) return double.NaN;
        var unsplit = src.Devices.Where(d => d.Table is not null).Select(d => d.Label).ToList();
        if (unsplit.Count > 0)
        {
            nl.AddWarningOnce(OscDiagnostics.PnCornerKey, OscDiagnostics.PnCornerUnsplit(x.Analysis, string.Join(", ", unsplit)).Render());
            return double.NaN;
        }
        double G(double f)
        {
            var stat = StationaryAt(src, f);
            double total = Quad(src, stat, hF, f, whiteOnly: false).Sum();
            double white = Quad(src, stat, hF, f, whiteOnly: true).Sum();
            return Math.Log((total - white) / white);      // > 0 below the corner, < 0 above, NaN with no flicker
        }
        double lo = Math.Log(1e-3), hi = Math.Log(0.5 * half);
        double gLo = G(Math.Exp(lo)), gHi = G(Math.Exp(hi));
        if (!(gLo > 0) || !(gHi < 0)) return double.NaN;
        for (int it = 0; it < 60 && hi - lo > 1e-7; it++)
        {
            double mid = 0.5 * (lo + hi), gm = G(Math.Exp(mid));
            if (gm > 0) lo = mid; else hi = mid;
        }
        return Math.Exp(0.5 * (lo + hi));
    }

    // ── Far from the carrier ─────────────────────────────────────────────────

    /// <summary>The phase and amplitude noise at offset <paramref name="fm"/>, part by part, from the autonomous circuit's
    /// conversion matrix read at the carrier's two sidebands. Null (with one warning) when the matrix is singular.</summary>
    private static (double[]? Pn, double[]? An) Far(OscPhaseNoiseInputs x, ElaboratedNetlist nl, Sources src,
        HbLinearExtractor extractor, HbSmallSignal.ISidebands sb, int nInt, Complex[,] yDc, double fm, int plus, int minus, Complex v1)
    {
        int S = sb.Count, Z = sb.Zero, nc = nInt * S;
        double wm = 2 * Math.PI * fm;
        var om = new double[S];
        for (int s = 0; s < S; s++) om[s] = wm + sb.Offset(s);
        var stat = StationaryAt(src, fm);

        var ys = new Dictionary<double, Complex[,]>();
        for (int s = 0; s < S; s++) ys[om[s]] = HbSmallSignal.YNNAt(extractor, om[s], yDc);
        var jss = HbSmallSignal.BuildJss(sb, nInt, wm, w => ys[w]);
        var lu = new HbSmallSignal.DenseLu(Transpose(jss, nc), nc);
        if (lu.Singular)
        {
            nl.AddWarningOnce(OscDiagnostics.PnSingularKey, OscDiagnostics.PnSingular(x.Analysis, fm).Render());
            HbNoise.Release(extractor, om, wm);
            return (null, null);
        }
        void Row(Complex[] r)
        {
            if (plus > 0)  r[plus - 1]  += Complex.One;
            if (minus > 0) r[minus - 1] -= Complex.One;
        }
        var up = HbNoise.Adjoint(extractor, nInt, S, om, ys, jss, lu, Z + 1, Row);
        var dn = HbNoise.Adjoint(extractor, nInt, S, om, ys, jss, lu, Z - 1, Row);
        HbNoise.Release(extractor, om, wm);

        Complex inv = Complex.One / v1, invC = Complex.One / Complex.Conjugate(v1);
        int size = up[Z].Length;
        var gPhi = new Complex[S][];
        var gAm = new Complex[S][];
        for (int s = 0; s < S; s++)
        {
            gPhi[s] = new Complex[size];
            gAm[s] = new Complex[size];
            for (int i = 0; i < size; i++)
            {
                Complex u = up[s][i] * inv, l = dn[s][i] * invC;
                gPhi[s][i] = (u - l) / Complex.ImaginaryOne;
                gAm[s][i] = u + l;
            }
        }
        var pn = Quad(src, stat, gPhi, fm, whiteOnly: false);
        var an = Quad(src, stat, gAm, fm, whiteOnly: false);
        for (int k = 0; k < pn.Length; k++) { pn[k] *= 0.5; an[k] *= 0.5; }
        return (pn, an);
    }

    private static Complex[] Transpose(Complex[] m, int n)
    {
        var t = new Complex[n * n];
        for (int r = 0; r < n; r++)
            for (int c = 0; c < n; c++) t[c * n + r] = m[r * n + c];
        return t;
    }
}
