using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine.HarmonicBalance;

/// <summary>
/// What an HB analysis' <c>Noise=yes</c> asks for (brief NZ-5): the input and output ports by number, the input sideband that
/// carries the signal — one integer per tone, so the input frequency is <c>f_out + Σ kᵢ·fᵢ</c> — and whether each part's share
/// is written.
/// </summary>
public sealed record HbNoiseRequest(int InPort, int OutPort, int[] Sideband, bool Contrib)
{
    /// <summary>Brief NZ-6: a Tuner's instance name to take as the input termination in place of port <see cref="InPort"/> — a
    /// loadpull's source tuner. Its RF termination is the port; its drive and bias tee are not.</summary>
    public string? InTuner { get; init; }
    /// <summary>Brief NZ-6: a Tuner's instance name to read the output noise at in place of port <see cref="OutPort"/>.</summary>
    public string? OutTuner { get; init; }

    /// <summary>The sideband as the run summary and the cubes spell it: <c>-1</c>, or <c>1,-1</c> on a lattice.</summary>
    public string SidebandText => string.Join(",", Sideband);
}

/// <summary>
/// Noise in harmonic balance (brief NZ-5): the circuit's own noise sources driving the conversion matrix the small-signal
/// sweep already builds (<see cref="HbSmallSignal"/>), read at one output port over the <c>ssfreq</c> grid.
///
/// <para><b>One adjoint per output frequency.</b> At output frequency <c>ω</c> the sidebands are <c>ω_s = ω + Ω_s</c>. With the
/// full linear partition <c>A_s</c> at each, the interface selection <c>P</c>, the device part of the conversion matrix
/// <c>D = J_ss − diag(Y_s)</c>, and the output wave read by the row <c>r</c> at the zero sideband, a node-current injection
/// <c>u_s</c> at sideband s reaches the output as <c>y = Σ_s a_sᵀ·u_s</c> with</para>
/// <code>
///   a_s = δ_s0·w − z_s,   A_0ᵀ·w = r,   J_ssᵀ·p = Dᵀ·(e_0 ⊗ P·w),   A_sᵀ·z_s = Pᵀ·(Y_sᵀ·p_s)
/// </code>
/// <para>— one transposed sparse solve at the output, one transposed dense solve of <c>J_ss</c>, and one transposed sparse
/// solve per sideband, whatever the number of noise sources. Derived in <c>docs/design/hb-noise.md</c>.</para>
///
/// <para><b>Sources.</b> A passive part is stationary: its Bosma currents at <c>|ω_s|</c> (conjugated below zero) on the
/// sideband diagonal only. A device that declares noise sources is cyclostationary: its densities are read at every sample of
/// the large-signal waveform, <c>p(t)</c>, and the correlation between sidebands s and t is the two-sided Fourier coefficient
/// <c>P[s − t]</c> — through <see cref="HbSmallSignal.ISidebands.DifferenceIndex"/>, the conversion matrix's own bookkeeping.
/// A flicker term is taken at the geometric mean of the two sidebands' frequencies. A Mixer's NF is a stationary source at its
/// RF port. The input port's termination is at T0 at every sideband; every other port is a noiseless termination, as in the
/// S-parameter noise analysis.</para>
///
/// <para><b>NFsb</b> is the share of the output noise each input sideband carries from the sources that are stationary — the
/// input termination, the passive parts, a Mixer's NF — which is where noise folding lives. A cyclostationary device is not
/// split by sideband: its cross-sideband terms are large and cancel, so its share is on <c>NFc</c> only, and NFsb sums to
/// 100 % less the devices' share.</para>
/// </summary>
internal static class HbNoise
{
    /// <summary>The time base of one operating point: the waveform at each interface node, and how to read a coefficient.</summary>
    internal sealed class Samples
    {
        /// <summary>Number of time samples.</summary>
        public required int Count { get; init; }
        /// <summary>Interface node <c>n</c>'s voltage at sample <c>t</c>.</summary>
        public required Func<int, int, double> Node { get; init; }
        /// <summary>A real series → its one-sided full-amplitude spectrum, indexed as <see cref="HbSmallSignal.ISidebands.DifferenceIndex"/> reads.</summary>
        public required Func<double[], Complex[]> Spectrum { get; init; }
        /// <summary>One full-amplitude coefficient of a real series at one index of that spectrum.</summary>
        public required Func<double[], int, Complex> Bin { get; init; }
    }

    /// <summary>One tone: the solve's own time grid and FFT, up to harmonic <paramref name="kj"/> (the reach of the conversion
    /// matrix's G and C).</summary>
    public static Samples ToneSamples(Complex[,] v, int nInt, int k, int gridN, int kj)
    {
        var vTime = new double[nInt][];
        for (int n = 0; n < nInt; n++)
        {
            vTime[n] = new double[gridN];
            var xn = new Complex[k + 1];
            for (int h = 0; h <= k; h++) xn[h] = v[n, h];
            HbFft.Inverse(xn, k, vTime[n]);
        }
        return new Samples
        {
            Count = gridN,
            Node = (n, t) => vTime[n][t],
            Spectrum = series => { HbFft.Forward(series, kj, out var x, out _); return x; },
            Bin = (series, h) =>
            {
                if (h > gridN / 2) return Complex.Zero;
                Complex acc = Complex.Zero;
                for (int t = 0; t < gridN; t++)
                    acc += series[t] * Complex.FromPolarCoordinates(1.0, -2 * Math.PI * h * t / gridN);
                return h == 0 ? new Complex(acc.Real / gridN, 0) : acc / (gridN / 2.0);
            },
        };
    }

    /// <summary>Several tones: the operating point re-synthesised on the order-2·MaxMixOrder torus
    /// <see cref="HbSmallSignal.LatticeSpectra"/> evaluates the devices on, so a sideband difference resolves.</summary>
    public static Samples LatticeSamples(Complex[,] v, HbApft apft, double oversample, int nInt)
    {
        var lattice = apft.Lattice;
        var apft2 = HbApft.Get(lattice.ToneCount, 2 * lattice.MaxMixOrder, oversample);
        var l2 = apft2.Lattice;
        int m2 = l2.MixCount, count = apft2.SampleCount;
        var vTime = new double[nInt][];
        var wide = new Complex[m2];
        for (int n = 0; n < nInt; n++)
        {
            Array.Clear(wide);
            for (int m = 0; m < lattice.MixCount; m++)
            {
                int j = l2.IndexOf(lattice.ToneOf(m));
                if (j >= 0) wide[j] = m == 0 ? new Complex(v[n, 0].Real, 0) : v[n, m];
            }
            vTime[n] = new double[count];
            apft2.Synthesize(wide, vTime[n]);
        }
        return new Samples
        {
            Count = count,
            Node = (n, t) => vTime[n][t],
            Spectrum = series => { var x = new Complex[m2]; apft2.Analyze(series, x); return x; },
            Bin = (series, idx) =>
            {
                double re = 0, im = 0;
                for (int t = 0; t < count; t++)
                {
                    re += apft2.AnalysisElement(2 * idx, t) * series[t];
                    im += apft2.AnalysisElement(2 * idx + 1, t) * series[t];
                }
                return new Complex(re, idx == 0 ? 0 : im);
            },
        };
    }

    /// <summary>The two-sided coefficient at sideband difference (s, t) of a one-sided full-amplitude value at its index:
    /// conjugated when reached through a negation, halved off DC — R-wsp5-2's rule.</summary>
    private static Complex TwoSided(Complex full, int index, bool conj)
    {
        if (conj) full = Complex.Conjugate(full);
        return index == 0 ? full : full * 0.5;
    }

    private static Complex TwoSided(Complex[] spectrum, (int Index, bool Conj) d) =>
        d.Index < 0 || d.Index >= spectrum.Length ? Complex.Zero : TwoSided(spectrum[d.Index], d.Index, d.Conj);

    // ── cyclostationary devices ──────────────────────────────────────────────

    /// <summary>One device's noise over the waveform: its sources on its circuit nodes, and the density per sample.</summary>
    private sealed class Device
    {
        public required string Label;
        public required int[] Nodes;                    // circuit nodes > 0
        public required (int A, int B)[] Inc;           // per source: index into Nodes (−1 = ground)
        public required double Mult;
        public required (int I, int J, Complex Rho)[] Corr;
        // Built-in: the white and flicker parts per sample, their spectra, and each source's exponent.
        public NoiseDensity[][]? Raw;                   // [t][source]
        public Complex[][]? White, Flicker;             // [source][index]
        public double[]? Exponent;
        // A batched model: densities [t, k, source] at the sideband frequencies in FreqIndex.
        public double[,,]? Table;
        public Dictionary<double, int>? FreqIndex;
    }

    /// <summary>The density series of source <paramref name="i"/> at frequency <paramref name="hz"/> (geometric-mean rule
    /// for a pair, through <paramref name="other"/>).</summary>
    private static double[] Series(Device d, int i, int j, double fs, double ft, int count)
    {
        var x = new double[count];
        if (d.Table is { } table)
        {
            int ks = d.FreqIndex![fs], kt = d.FreqIndex[ft];
            for (int t = 0; t < count; t++)
                x[t] = Math.Sqrt(Math.Max(0, table[t, ks, i]) * Math.Max(0, table[t, kt, j]));
        }
        else
        {
            double fg = Math.Sqrt(fs * ft);
            for (int t = 0; t < count; t++)
                x[t] = Math.Sqrt(Math.Max(0, d.Raw![t][i].At(fg)) * Math.Max(0, d.Raw[t][j].At(fg)));
        }
        return x;
    }

    /// <summary>⟨u_s·u_tᴴ⟩ over the device's nodes, A²/Hz, for sidebands s and t at |f| = <paramref name="fs"/>, <paramref name="ft"/>.</summary>
    private static Mat<Complex> DeviceAt(Device d, Samples smp, HbSmallSignal.ISidebands sb, int s, int t, double fs, double ft)
    {
        int n = d.Nodes.Length;
        var c = new Mat<Complex>(n, n);
        var diff = sb.DifferenceIndex(s, t);
        if (diff.Index < 0) return c;
        for (int i = 0; i < d.Inc.Length; i++)
        {
            Complex p;
            if (d.Table is not null)
                p = TwoSided(smp.Bin(Series(d, i, i, fs, ft, smp.Count), diff.Index), diff.Index, diff.Conj);
            else
            {
                p = TwoSided(d.White![i], diff);
                if (d.Exponent![i] is double a && !double.IsNaN(a))
                    p += TwoSided(d.Flicker![i], diff) * Math.Pow(fs * ft, -a / 2);
            }
            p *= d.Mult;
            var (ia, ib) = d.Inc[i];
            if (ia >= 0) c[ia, ia] += p;
            if (ib >= 0) c[ib, ib] += p;
            if (ia >= 0 && ib >= 0) { c[ia, ib] -= p; c[ib, ia] -= p; }
        }
        foreach (var (i, j, rho) in d.Corr)
        {
            Complex f = d.Mult * TwoSided(smp.Bin(Series(d, i, j, fs, ft, smp.Count), diff.Index), diff.Index, diff.Conj);
            Complex x = rho * f, xc = Complex.Conjugate(rho) * f;
            foreach (var (u, su) in new[] { (d.Inc[i].A, 1.0), (d.Inc[i].B, -1.0) })
                foreach (var (v, sv) in new[] { (d.Inc[j].A, 1.0), (d.Inc[j].B, -1.0) })
                {
                    if (u < 0 || v < 0) continue;
                    c[u, v] += su * sv * x;
                    c[v, u] += su * sv * xc;
                }
        }
        return c;
    }

    /// <summary>
    /// Every device of <paramref name="nl"/> that declares noise sources, read over the waveform once: a built-in model per
    /// sample (<see cref="ComponentModel.EvaluateNoise"/>), a batched one in ONE call per operating point at every sideband
    /// frequency the sweep visits (<see cref="ComponentModel.NoiseDensitiesOver"/>).
    /// </summary>
    private static List<Device> Devices(ElaboratedNetlist nl, IReadOnlyList<(NoiseModelRow Row, ElaboratedComponent Ec)> rows,
        Samples smp, int[] ifNodes, double[] sidebandHz)
    {
        var list = new List<Device>();
        foreach (var (row, ec) in rows)
        {
            var model = ec.Model;
            var decl = model.NoiseSources;
            int Circuit(int t) => t < 0 || t >= ec.Nodes.Length ? 0 : ec.Nodes[t];
            int[] nodes = [.. decl.SelectMany(x => new[] { Circuit(x.TerminalA), Circuit(x.TerminalB) }).Where(x => x > 0).Distinct()];
            if (nodes.Length == 0) continue;
            var inc = decl.Select(x => (Array.IndexOf(nodes, Circuit(x.TerminalA)), Array.IndexOf(nodes, Circuit(x.TerminalB)))).ToArray();
            var corr = decl.Select((x, i) => (x, i)).Where(p => p.x.CorrelatedWith is int t && t >= 0 && t < p.i)
                           .Select(p => (p.i, p.x.CorrelatedWith, p.x.Correlation)).ToArray();

            int pc = model.PortCount;
            var plus = new int[pc];
            var minus = new int[pc];
            for (int q = 0; q < pc; q++)
            {
                plus[q]  = Array.IndexOf(ifNodes, ec.Nodes.Length > 2 * q ? ec.Nodes[2 * q] : 0);
                minus[q] = Array.IndexOf(ifNodes, ec.Nodes.Length > 2 * q + 1 ? ec.Nodes[2 * q + 1] : 0);
            }
            var states = new PortVoltages[smp.Count];
            for (int t = 0; t < smp.Count; t++)
            {
                var v = new double[pc];
                for (int q = 0; q < pc; q++)
                    v[q] = (plus[q] >= 0 ? smp.Node(plus[q], t) : 0) - (minus[q] >= 0 ? smp.Node(minus[q], t) : 0);
                states[t] = new PortVoltages(v);
            }

            var d = new Device { Label = row.Label, Nodes = nodes, Inc = inc, Mult = ec.Multiplicity, Corr = corr };
            if (model.PrefersBatchNoise)
            {
                d.Table = model.NoiseDensitiesOver(ec, states, row.Kelvin, sidebandHz);
                if (d.Table is null) continue;
                d.FreqIndex = [];
                for (int k = 0; k < sidebandHz.Length; k++) d.FreqIndex[sidebandHz[k]] = k;
            }
            else
            {
                int ns = decl.Count;
                d.Raw = new NoiseDensity[smp.Count][];
                for (int t = 0; t < smp.Count; t++)
                {
                    d.Raw[t] = new NoiseDensity[ns];
                    model.EvaluateNoise(states[t], row.Kelvin, d.Raw[t]);
                }
                d.White = new Complex[ns][];
                d.Flicker = new Complex[ns][];
                d.Exponent = new double[ns];
                var w = new double[smp.Count];
                var f = new double[smp.Count];
                for (int i = 0; i < ns; i++)
                {
                    double a = double.NaN;
                    for (int t = 0; t < smp.Count; t++)
                    {
                        w[t] = d.Raw[t][i].White;
                        f[t] = d.Raw[t][i].FlickerCoeff;
                        if (f[t] != 0 && double.IsNaN(a)) a = d.Raw[t][i].FlickerExp;
                    }
                    d.White[i] = smp.Spectrum(w);
                    d.Flicker[i] = smp.Spectrum(f);
                    d.Exponent[i] = a;
                }
            }
            list.Add(d);
        }
        return list;
    }

    // ── ports ────────────────────────────────────────────────────────────────

    /// <summary>A port of the run: its two circuit nodes and the impedance it presents at a frequency.</summary>
    private sealed record Port(string Label, int Plus, int Minus, Func<double, Complex> Z);

    private static Port FindPort(ElaboratedNetlist nl, int num, string role, string key)
    {
        foreach (var ec in nl.Components)
        {
            if (ec.InstancePath.Contains('.')) continue;
            if (ec.Model is not (PortModel or TermModel or P1ToneModel)) continue;
            if (!ec.Parameters.TryGetValue("Num", out var nv) || (int)nv.AsReal() != num) continue;
            int plus = ec.Nodes[0], minus = ec.Nodes.Length > 1 ? ec.Nodes[1] : 0;
            if (ec.Model is P1ToneModel p1) return new Port(ec.InstancePath, plus, minus, w => p1.ImpedanceAt(w));
            Complex z = !ec.Parameters.TryGetValue("Z", out var zv) ? new Complex(50, 0)
                      : zv.Kind == Core.Expressions.ValueKind.Complex ? zv.AsComplex() : new Complex(zv.AsReal(), 0);
            return new Port(ec.InstancePath, plus, minus, _ => z);
        }
        throw new InvalidOperationException(HbNoiseDiagnostics.NoPort(key, num, role).Render());
    }

    /// <summary>
    /// A Tuner as a noise port (brief NZ-6): its RF termination — the Z_Port a loadpull overrides per grid point — between
    /// the two nodes it sits on. A source tuner's is in series between its drive node (held by the drive, a short to every
    /// small-signal current) and its DC block; a load tuner's runs from its DC block to its reference. The impedance is the
    /// band's at each sideband, as the tuner stamps it. The drive and the bias tee are ideal, so the port is the whole of
    /// the tuner's noise.
    /// </summary>
    private static Port FindTuner(ElaboratedNetlist nl, string name, string key)
    {
        var ec = nl.Components.FirstOrDefault(c => c.Model is TunerModel && c.InstancePath == name)
                 ?? throw new InvalidOperationException(HbNoiseDiagnostics.NoTuner(key, name).Render());
        var t = (TunerModel)ec.Model;
        int Node(int i) => ec.Nodes.Length > i ? ec.Nodes[i] : 0;
        return t.Role == TunerRole.Source
            ? new Port(ec.InstancePath, Node(2), Node(4), t.ImpedanceAt)
            : new Port(ec.InstancePath, Node(2), Node(1), t.ImpedanceAt);
    }

    // ── the analysis ─────────────────────────────────────────────────────────

    /// <summary>One output frequency's answer.</summary>
    private sealed class Point
    {
        public double F = double.NaN, Fdsb = double.NaN, GcLin = double.NaN, Nout = double.NaN;
        public double[] Share = [];
        public double[] Parts = [];
    }

    /// <summary>
    /// The whole HB noise answer at one operating point, added to <paramref name="ds"/> over <paramref name="grid"/>.
    /// <paramref name="sideLabels"/> spells each sideband on the <c>sideband</c> axis; <paramref name="sideValues"/> is its
    /// value there. <paramref name="dcNodeVoltages"/> is the operating point's DC node vector (1-based node n at n − 1),
    /// which a composed FET's parasitics take their temperature from.
    /// </summary>
    public static void Add(
        DataSet ds, ElaboratedNetlist nl, HbNoiseRequest req, double[] grid,
        HbLinearExtractor extractor, HbSmallSignal.ISidebands sb, Complex[,] yDc, Samples smp,
        double[] toneOmegas, double[] sideValues, string[] sideLabels, double[]? dcNodeVoltages, string commensurateWith)
    {
        int nInt = extractor.InterfaceCount, S = sb.Count, Z = sb.Zero;
        int[] ifNodes = extractor.InterfaceNodes;
        if (nInt * S > HbSmallSignal.ConversionMatrixCeiling)
            throw new InvalidOperationException(HbSmallSignal.TooLargeMessage(nInt * S, nInt, S)
                .Replace("wsprobe.hb-ss-", "hb.noise.", StringComparison.Ordinal));

        var pin  = req.InTuner  is { } ti ? FindTuner(nl, ti, "NoiseIn")  : FindPort(nl, req.InPort,  "input",  "NoiseIn");
        var pout = req.OutTuner is { } to ? FindTuner(nl, to, "NoiseOut") : FindPort(nl, req.OutPort, "output", "NoiseOut");

        // The signal sideband and its image, by their mixing offsets.
        double target = 0;
        for (int i = 0; i < req.Sideband.Length && i < toneOmegas.Length; i++) target += req.Sideband[i] * toneOmegas[i];
        int Find(double off)
        {
            double tol = 1e-9 * Math.Max(1.0, toneOmegas.Select(Math.Abs).DefaultIfEmpty(1).Max());
            for (int s = 0; s < S; s++) if (Math.Abs(sb.Offset(s) - off) <= tol) return s;
            return -1;
        }
        int sig = Find(target), img = Find(-target);
        if (sig < 0)
            throw new InvalidOperationException(HbNoiseDiagnostics.SidebandNotRetained(req.SidebandText).Render());
        bool dsb = sig != img;

        // Every sideband frequency the sweep visits, so a batched device is asked once.
        var hzSet = new SortedSet<double>();
        foreach (double f in grid)
            for (int s = 0; s < S; s++)
                hzSet.Add(Math.Abs(2 * Math.PI * f + sb.Offset(s)) / (2 * Math.PI));
        double[] sidebandHz = [.. hzSet];

        // Rows: devices on the waveform, a Mixer's NF on its RF port, everything else the S-parameter seam's own entries.
        var deviceRows = new List<(NoiseModelRow, ElaboratedComponent)>();
        var mixers = new List<NoiseSource>();
        bool Claim(NoiseModelRow row, List<ElaboratedComponent> parts)
        {
            if (row.Kind == NoiseModelKind.DeviceSources) { deviceRows.Add((row, parts[0])); return true; }
            if (row.Kind == NoiseModelKind.Noiseless && parts is [{ Model: MixerModel { NoiseFigureDb: double nfDb } mx } m])
            {
                mixers.Add(new NoiseSource(row.Label, MixerSource(nl, m, mx, nfDb)));
                return true;
            }
            return false;
        }
        var stationary = SParameterNoise.Sources(nl, sidebandHz, dcNodeVoltages, Claim);
        stationary.AddRange(mixers);
        var devices = Devices(nl, deviceRows, smp, ifNodes, sidebandHz);

        string[] labels = [.. stationary.Select(x => x.Label), .. devices.Select(x => x.Label), pin.Label];
        var points = new Point[grid.Length];
        var nan = new Complex(double.NaN, double.NaN);

        for (int fi = 0; fi < grid.Length; fi++)
        {
            double wOut = 2 * Math.PI * grid[fi];
            var pt = points[fi] = new Point { Share = new double[S], Parts = new double[labels.Length] };
            if (sb.IsDegenerate(wOut))
            {
                nl.AddWarningOnce(HbNoiseDiagnostics.DegenerateKey, HbNoiseDiagnostics.Degenerate(grid[fi], commensurateWith).Render());
                pt.Share.AsSpan().Fill(double.NaN); pt.Parts.AsSpan().Fill(double.NaN);
                continue;
            }

            var om = new double[S];
            for (int s = 0; s < S; s++) om[s] = wOut + sb.Offset(s);

            // 1. Every stationary source at every sideband, BEFORE any partition is stamped: a recorded stamp writes the
            //    model's branch index, and the partition's own stamp writes the real one back.
            var stat = new NoiseCurrents[stationary.Count, S];
            for (int k = 0; k < stationary.Count; k++)
                for (int s = 0; s < S; s++)
                {
                    var c = stationary[k].At(Math.Abs(om[s]));
                    stat[k, s] = om[s] < 0 && c.Nodes is not null ? new NoiseCurrents(c.Nodes, Conj(c.C)) : c;
                }

            // 2. The conversion matrix and its interface admittances.
            var ys = new Dictionary<double, Complex[,]>();
            for (int s = 0; s < S; s++) ys[om[s]] = HbSmallSignal.YNNAt(extractor, om[s], yDc);
            var jss = HbSmallSignal.BuildJss(sb, nInt, wOut, w => ys[w]);
            int nc = nInt * S;
            var jt = new Complex[nc * nc];
            for (int r = 0; r < nc; r++)
                for (int c = 0; c < nc; c++) jt[c * nc + r] = jss[r * nc + c];
            var lu = new HbSmallSignal.DenseLu(jt, nc);
            if (lu.Singular)
            {
                nl.AddWarningOnce(HbNoiseDiagnostics.SingularKey, HbNoiseDiagnostics.Singular(grid[fi]).Render());
                pt.Share.AsSpan().Fill(double.NaN); pt.Parts.AsSpan().Fill(double.NaN);
                Release(extractor, om, wOut);
                continue;
            }

            // 3. The output wave's row and its adjoint at the zero sideband: b = (√Re Z0 / Z0)·(V+ − V−).
            Complex z0 = pout.Z(wOut);
            Complex cOut = Math.Sqrt(Math.Max(0, z0.Real)) / z0;
            var part0 = extractor.LinearPartitionAt(wOut);
            int size = part0.Size;
            var r0 = new Complex[size];
            if (pout.Plus > 0)  r0[pout.Plus - 1]  += cOut;
            if (pout.Minus > 0) r0[pout.Minus - 1] -= cOut;
            var w0 = TransposedSolve(part0, r0, positive: true);

            // 4. q = Dᵀ·(e_0 ⊗ P·w), D = J_ss − diag(Y_s); then J_ssᵀ·p = q.
            var q = new Complex[nc];
            var y0 = ys[om[Z]];
            for (int m = 0; m < nInt; m++)
                for (int t = 0; t < S; t++)
                {
                    Complex acc = Complex.Zero;
                    for (int n = 0; n < nInt; n++)
                    {
                        int node = ifNodes[n];
                        if (node <= 0) continue;
                        Complex g = w0[node - 1];
                        Complex dnm = jss[(n * S + Z) * nc + m * S + t];
                        if (t == Z && n < y0.GetLength(0) && m < y0.GetLength(1)) dnm -= y0[n, m];
                        acc += g * dnm;
                    }
                    q[m * S + t] = acc;
                }
            var p = new Complex[nc];
            lu.Solve(q, p);

            // 5. Per sideband: z_s from A_sᵀ·z_s = Pᵀ·(Y_sᵀ·p_s), and a_s = δ_s0·w − z_s.
            var a = new Complex[S][];
            for (int s = 0; s < S; s++)
            {
                var y = ys[om[s]];
                var b = new Complex[size];
                for (int m = 0; m < nInt; m++)
                {
                    int node = ifNodes[m];
                    if (node <= 0) continue;
                    Complex acc = Complex.Zero;
                    for (int n = 0; n < nInt; n++) acc += p[n * S + s] * y[n, m];
                    b[node - 1] += acc;
                }
                var part = extractor.LinearPartitionAt(Math.Abs(om[s]));
                var z = TransposedSolve(part, b, positive: om[s] > 0);
                var row = new Complex[size];
                for (int i = 0; i < size; i++) row[i] = (s == Z ? w0[i] : Complex.Zero) - z[i];
                a[s] = row;
            }

            // 6. The output noise, source by source and sideband by sideband.
            Complex A(int s, int node) => node > 0 ? a[s][node - 1] : Complex.Zero;
            Complex Quad(int s, int t, int[] nodes, Mat<Complex> c)
            {
                Complex acc = Complex.Zero;
                for (int i = 0; i < nodes.Length; i++)
                {
                    Complex ai = A(s, nodes[i]);
                    if (ai == Complex.Zero) continue;
                    for (int j = 0; j < nodes.Length; j++) acc += ai * c[i, j] * Complex.Conjugate(A(t, nodes[j]));
                }
                return acc;
            }

            double total = 0;
            for (int k = 0; k < stationary.Count; k++)
                for (int s = 0; s < S; s++)
                {
                    var c = stat[k, s];
                    if (c.Nodes is null) continue;
                    double v = Quad(s, s, c.Nodes, c.C).Real;
                    pt.Parts[k] += v; pt.Share[s] += v; total += v;
                }
            for (int k = 0; k < devices.Count; k++)
            {
                var d = devices[k];
                for (int s = 0; s < S; s++)
                    for (int t = 0; t <= s; t++)
                    {
                        double fs = Math.Abs(om[s]) / (2 * Math.PI), ft = Math.Abs(om[t]) / (2 * Math.PI);
                        Complex term = Quad(s, t, d.Nodes, DeviceAt(d, smp, sb, s, t, fs, ft));
                        // (t, s) is the conjugate term: together 2·Re. Not split between sidebands: the cross terms of a
                        // device switched by its own waveform are large and cancel (a diode pumped on and off), so a
                        // per-sideband share of it means nothing. NFsb carries the sources that ARE per sideband.
                        double v = s == t ? term.Real : 2 * term.Real;
                        pt.Parts[stationary.Count + k] += v; total += v;
                    }
            }

            // 7. The input termination at T0 at every sideband.
            var nIn = new double[S];
            for (int s = 0; s < S; s++)
            {
                Complex zin = pin.Z(Math.Abs(om[s]));
                double g = zin == Complex.Zero ? 0 : Math.Max(0, (Complex.One / zin).Real);
                Complex h = A(s, pin.Plus) - A(s, pin.Minus);
                nIn[s] = 4 * NoiseMetrics.KT0 * g * (h.Real * h.Real + h.Imaginary * h.Imaginary);
                pt.Share[s] += nIn[s];
                total += nIn[s];
                if (s != sig) pt.Parts[^1] += nIn[s];
            }

            pt.Nout  = total;
            pt.GcLin = nIn[sig] / NoiseMetrics.KT0;
            pt.F     = total / nIn[sig];
            pt.Fdsb  = dsb ? total / (nIn[sig] + nIn[img]) : pt.F;
            for (int s = 0; s < S; s++) pt.Share[s] = 100 * pt.Share[s] / total;
            for (int k = 0; k < labels.Length; k++) pt.Parts[k] /= nIn[sig];

            Release(extractor, om, wOut);
        }

        AddCubes(ds, req, grid, points, dsb, S, sideValues, sideLabels, labels);
    }

    /// <summary>
    /// A Mixer's NF as a stationary current source across its RF port (brief NZ-5): referred to the port's own matched
    /// termination, so the mixer's equivalent input temperature is <c>T_e = T0·(F_DSB − 1)</c> and the density
    /// <c>4k·T_e/Zrf</c>. An ideal multiplier converts both sidebands equally, so <c>F_DSB = F_SSB/2</c> for an SSB figure;
    /// one below 3 dB SSB is below what a noiseless DSB mixer reaches and is taken as noiseless, with a warning.
    /// </summary>
    private static Func<double, NoiseCurrents> MixerSource(ElaboratedNetlist nl, ElaboratedComponent ec, MixerModel mx, double nfDb)
    {
        double f = Math.Pow(10, nfDb / 10);
        double fDsb = mx.NoiseFigureType == MixerNoiseFigureType.Dsb ? f : f / 2;
        if (fDsb < 1)
        {
            nl.AddWarningOnce($"hb.noise.mixer-nf-below-floor:{ec.InstancePath}",
                HbNoiseDiagnostics.MixerNfBelowFloor(ec.InstancePath, nfDb).Render());
            fDsb = 1;
        }
        double dens = ec.Multiplicity * 4 * NoiseMetrics.Boltzmann * NoiseMetrics.T0 * (fDsb - 1) * mx.RfConductance;
        int plus = ec.Nodes[0], minus = ec.Nodes.Length > 1 ? ec.Nodes[1] : 0;
        int[] nodes = [.. new[] { plus, minus }.Where(x => x > 0)];
        var c = new Mat<Complex>(nodes.Length, nodes.Length);
        int ia = Array.IndexOf(nodes, plus), ib = Array.IndexOf(nodes, minus);
        if (ia >= 0) c[ia, ia] += dens;
        if (ib >= 0) c[ib, ib] += dens;
        if (ia >= 0 && ib >= 0) { c[ia, ib] -= dens; c[ib, ia] -= dens; }
        var currents = new NoiseCurrents(nodes, c);
        return _ => currents;
    }

    private static Mat<Complex> Conj(Mat<Complex> m)
    {
        var c = new Mat<Complex>(m.RowCount, m.ColCount);
        for (int i = 0; i < m.RowCount; i++)
            for (int j = 0; j < m.ColCount; j++) c[i, j] = Complex.Conjugate(m[i, j]);
        return c;
    }

    /// <summary>
    /// <c>Aᵀ·x = b</c> for the partition at <c>±|ω|</c>. CSparse's transposed solve is Hermitian (<c>Aᴴ·y = b</c>,
    /// src/Engine/RESOLVED.md), so at <c>+|ω|</c> the answer is <c>conj(y)</c> for <c>conj(b)</c>, and at <c>−|ω|</c>, whose
    /// matrix is <c>conj(A)</c>, it is <c>y</c> itself.
    /// </summary>
    private static Complex[] TransposedSolve(HbLinearExtractor.LinearPartition part, Complex[] b, bool positive)
    {
        var x = new Complex[b.Length];
        if (!positive) { part.SolveHermitian(b, x); return x; }
        var bc = new Complex[b.Length];
        for (int i = 0; i < b.Length; i++) bc[i] = Complex.Conjugate(b[i]);
        part.SolveHermitian(bc, x);
        for (int i = 0; i < x.Length; i++) x[i] = Complex.Conjugate(x[i]);
        return x;
    }

    /// <summary>Drop one output point's sideband factorisations, as the WSProbe sweep does: none recurs.</summary>
    private static void Release(HbLinearExtractor extractor, double[] om, double wOut)
    {
        foreach (double w in om) if (Math.Abs(w) >= 1e-12) extractor.InvalidateLinear(Math.Abs(w));
        extractor.InvalidateLinear(wOut);
    }

    private static void AddCubes(DataSet ds, HbNoiseRequest req, double[] grid, Point[] pts, bool dsb, int sidebands,
        double[] sideValues, string[] sideLabels, string[] parts)
    {
        int nf = grid.Length;
        Axis Freq() => new("ssfreq", (double[])grid.Clone(), "Hz");
        const string One = RfCore.Export.ResultUnits.Dimensionless;
        double Db(double x) => 10 * Math.Log10(x);
        ds.Add("NF",   new DataCube([Freq()], [.. pts.Select(x => Db(x.F))])    { Unit = "dB" });
        ds.Add("Fn",   new DataCube([Freq()], [.. pts.Select(x => x.F)])        { Unit = One });
        if (dsb)
        {
            ds.Add("NFdsb", new DataCube([Freq()], [.. pts.Select(x => Db(x.Fdsb))]) { Unit = "dB" });
            ds.Add("Fndsb", new DataCube([Freq()], [.. pts.Select(x => x.Fdsb)])     { Unit = One });
        }
        ds.Add("Te",   new DataCube([Freq()], [.. pts.Select(x => NoiseMetrics.T0 * (x.F - 1))]) { Unit = "K" });
        ds.Add("Nout", new DataCube([Freq()], [.. pts.Select(x => x.Nout)])     { Unit = "W/Hz" });
        ds.Add("Gc",   new DataCube([Freq()], [.. pts.Select(x => Db(x.GcLin))]) { Unit = "dB" });

        var sbAxis = new Axis("sideband", (double[])sideValues.Clone(), "", sideLabels);
        var share = new double[nf * sidebands];
        for (int fi = 0; fi < nf; fi++)
            for (int s = 0; s < sidebands; s++) share[fi * sidebands + s] = pts[fi].Share.Length > s ? pts[fi].Share[s] : double.NaN;
        ds.Add("NFsb", new DataCube([Freq(), sbAxis], share) { Unit = "%" });

        if (!req.Contrib || parts.Length == 0) return;
        int np = parts.Length;
        Axis Part() => new("part", [.. Enumerable.Range(1, np).Select(k => (double)k)], "", parts);
        var nfc = new double[nf * np];
        var pct = new double[nf * np];
        for (int fi = 0; fi < nf; fi++)
            for (int k = 0; k < np; k++)
            {
                double c = pts[fi].Parts.Length > k ? pts[fi].Parts[k] : double.NaN;
                nfc[fi * np + k] = c;
                pct[fi * np + k] = 100 * c / (pts[fi].F - 1);
            }
        ds.Add("NFc",   new DataCube([Freq(), Part()], nfc) { Unit = One });
        ds.Add("NFpct", new DataCube([Freq(), Part()], pct) { Unit = "%" });
    }
}
