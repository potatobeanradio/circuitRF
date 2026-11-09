// ================================================================
//  EnvelopeDataSet.cs  —  envelope results and modulated measurements
//
//  Name a net to see its voltage; put an IProbe where you want power
//  (brief ENV-MEAS-3). No impedance is assumed anywhere.
//
//  Node cubes, at every labeled net (node axis, ascending node index):
//    V          [time, node, harmonic|mixIndex]  complex peak phasors (SaveWaveforms)
//    VSD        [node, freq]            real   voltage spectral density, dBV/Hz (RMS V^2 per Hz),
//               absolute frequency. Single fundamental: the fundamental envelope; two
//               fundamentals: every carrier product and the IM3/IM5/IM2 products, summed onto
//               one ascending grid over the union of their spans (overlaps merge)
//    Idc        [time, supply]          real   DC supply branch currents (A, SaveWaveforms)
//  IProbe cubes (branch = its instance path; power flows from its first node to its second),
//  every power the power actually delivered through the probe:
//    I             [time, branch, harmonic|mixIndex] complex  probe current envelopes (SaveWaveforms)
//    Pprobe_W      [branch, harmonic|mixIndex]       real     time-averaged power per product (W)
//    Pprobe_fund_W [branch]                          real     the fundamental band: harmonic 1, or the carrier products
//    Pprobe_t_W    [time, branch]                    real     instantaneous fundamental-band power (SaveWaveforms)
//    PSD        [branch, freq]          dBm/Hz  1/2 Re of the V-I cross-spectrum, on VSD's grid
//    ACLR       [branch, carrier, metric]       Pch_dBm, ACPR1L, ACPR1U, ACPR2L, ACPR2U (dBc)
//    IMD        [branch, product]       mean delivered power (dBm) of each listed product
//    Pavg_dBm   [branch]                Pprobe_fund_W in dBm: at the output probe, Pout
//    PAPR_dB [branch]   CCDF [branch, prob]   of p(t) = 1/2 Re(V1 I1*) (dB above mean)
//    Gt_dB, DE_pct, NMSE_dB, Delay_s [branch]
//    Signal quality (brief NZ-8), each carrier read from the probe voltage's envelope, its delay taken out:
//    EVM_pct, EVM_dB, EVMpeak_pct, MER_dB [branch, carrier]   LTE / NR, and DBPSK at its bit centres; one gain fitted
//    Const [branch, carrier, symbol] complex  ConstRef [carrier, symbol] complex   the received points and the ideal
//               points there, at most ConstMaxSamples per carrier
//    FSKerr_pct, FreqDev_Hz, FreqOffset_Hz [branch, carrier]  FreqPts [branch, carrier, bit]  FreqRef [carrier, bit]
//               FSK: the frequency at each bit centre against the sent one
//    CSSerr_pct [branch, carrier]   CSS: the de-chirped error, one gain per chirp
//    With EvmSplit, each family's split (EVMdist_pct / EVMnoise_pct, FSKerrDist_pct / FSKerrNoise_pct,
//    CSSerrDist_pct / CSSerrNoise_pct) and SNR_dB [branch, carrier], the in-channel signal over what the noise added
//    AMAM / AMAM_min / AMAM_max / AMPM / AMPM_min / AMPM_max  [branch, Pin_inst]  binned on the
//               instantaneous available drive power; the min/max spread at one Pin is the memory
//    Pin_inst_t_dBm [time]  Gt_t_dB / PM_t_deg [time, branch]   the memory cloud: every AM-AM /
//               AM-PM sample, decimated to at most CloudMaxSamples (single fundamental)
//    A probe carrying no RF (mean fundamental-band power below NoRfFraction of the drive) is
//    NaN in Gt, DE, NMSE, Delay, ACLR, PAPR, CCDF, AM-AM / AM-PM, the cloud, EVM and the pulse cubes.
//  Scalars: Pin_avg_dBm (available drive), Pdc_W (every non-zero supply).
//    __EnvSnpFit [instance, metric]  order, rel_error, sigma_max of each SnP's
//               rational fit (only when the circuit has one)
//    A pulsed-CW source adds the pulse cubes of EnvelopePulse and drops
//    ACLR, CCDF, PAPR, AM-AM / AM-PM, NMSE and Delay_s: they are defined
//    for a modulated signal, and on a pulse train they print numbers that
//    look like results.
//  Measurements skip the first `SkipS` seconds (settling) when the run
//  started with no warm-up.
// ================================================================

using System.Numerics;
using RfCore.Data;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

public static class EnvelopeDataSet
{
    private static readonly string[] AclrLabels = ["Pch_dBm", "ACPR1L", "ACPR1U", "ACPR2L", "ACPR2U"];

    /// <summary>AM-AM / AM-PM instantaneous-input-power grid (dBm, 0.5 dB bins) and the samples a bin needs.</summary>
    public const double AmGridLoDbm = -40, AmGridHiDbm = 60;
    public const int AmMinSamples = 16;

    /// <summary>A probe whose mean fundamental-band power is below this fraction of the available drive (−60 dB) carries
    /// no RF: a supply or a bias probe. Its output rows are NaN.</summary>
    public const double NoRfFraction = 1e-6;

    /// <summary>The memory cloud keeps every m-th aligned sample, m the smallest that leaves at most this many.</summary>
    public const int CloudMaxSamples = 20_000;

    /// <summary>The lag search of NMSE / AM-AM alignment (steps).</summary>
    private const int MaxLag = 16;

    /// <summary>10·log10(1000/50): a VSD in dBV/Hz plus this is the power density into 50 Ω in dBm/Hz.</summary>
    public static readonly double VsdTo50OhmDb = 10 * Math.Log10(1000.0 / 50);

    /// <param name="quiet">EvmSplit's second run, with every noise source and mask off (brief NZ-8); null without one.</param>
    public static DataSet Build(EnvelopeRunResult r, EnvelopeRunner.Plan plan, double skipS = 0, Action<string>? note = null,
                                EnvelopeRunResult? quiet = null)
    {
        var ds = new DataSet();
        var pulse = EnvelopePulse.Of(r);
        bool multi = r.MixTones is not null;
        int nt = r.Time.Length, np = r.ProbeNames.Length, nb = r.IProbeNames.Length;
        int nk = np > 0 ? r.Probe[0][0].Length : nb > 0 ? r.IProbeCurrent[0][0].Length : multi ? r.MixFreqHz!.Length : r.MaxHarm + 1;
        double fs = 1.0 / r.StepS;
        int t0 = Math.Min(nt - 16, (int)Math.Round(skipS / r.StepS));
        int nm = nt - t0;
        var nodeAxis = new Axis("node", Index(np), "", r.ProbeNames);
        var branchAxis = new Axis("branch", Index(nb), "", r.IProbeNames);

        if (plan.SaveWaveforms)
        {
            if (np > 0)
            {
                var v = new Complex[nt * np * nk];
                for (int t = 0; t < nt; t++)
                    for (int p = 0; p < np; p++)
                        for (int k = 0; k < nk; k++)
                            v[(t * np + p) * nk + k] = r.Probe[t][p][k];
                var third = multi
                    ? new Axis("mixIndex", r.MixFreqHz!, "Hz", r.MixLabels)
                    : new Axis("harmonic", Index(nk));
                ds.Add("V", new DataCube([new Axis("time", r.Time, "s"), nodeAxis, third], v));
            }
            if (r.SupplyNames.Length > 0)
            {
                var idc = new double[nt * r.SupplyNames.Length];
                for (int t = 0; t < nt; t++)
                    for (int s = 0; s < r.SupplyNames.Length; s++) idc[t * r.SupplyNames.Length + s] = r.SupplyCurrent[t][s];
                ds.Add("Idc", new DataCube([new Axis("time", r.Time, "s"),
                    new Axis("supply", Index(r.SupplyNames.Length), "", r.SupplyNames)], idc));
            }
        }

        // One product's envelope over the measured window (from t0). A lattice product at a negative frequency is read
        // conjugated, so its spectrum runs the right way round; a power or a magnitude is unchanged by it.
        Complex[] Window(Complex[][][] x, int i, int k)
        {
            var y = new Complex[nm];
            bool neg = multi && r.MixFreqHz![k] < 0;
            for (int t = 0; t < nm; t++) { var v = x[t0 + t][i][k]; y[t] = neg ? Complex.Conjugate(v) : v; }
            return y;
        }
        Complex[] Node(int p, int k) => Window(r.Probe, p, k);
        Complex[] ProbeV(int b, int k) => Window(r.IProbeVoltage, b, k);
        Complex[] ProbeI(int b, int k) => Window(r.IProbeCurrent, b, k);
        // the delivered power density at a probe, W/Hz: 1/2 Re of its voltage-current cross-spectrum (peak phasors)
        (double[] f, double[] d) Delivered(int b, int k)
        {
            var (f, csd) = SignalMeasure.CrossWelch(ProbeV(b, k), ProbeI(b, k), fs, Nseg());
            var d = new double[csd.Length];
            for (int i = 0; i < d.Length; i++) d[i] = 0.5 * csd[i].Real;
            return (f, d);
        }

        // Spectral resolution follows the CARRIERS' rate, not the envelope's: 8192 bins at the rate a
        // carrier group's own envelope runs at. A single envelope spanning widely spaced carriers
        // (Envelope=single) runs 8-16x faster, and a fixed 8192 bins then widened the bin to 0.5-2 MHz -
        // enough Hann leakage at a 20 MHz channel's edge to read ACPR1 up to 7 dB high.
        double narrow = 1.92e6, needNarrow = EnvelopeEngineMt.Plan(r.Sources).Max(g => g.needRate);
        while (narrow < needNarrow) narrow *= 2;
        int nseg = 8192 * (int)Math.Max(1, Math.Round(r.SignalRateHz / narrow));
        while (nseg > nm / 4 && nseg > 256) nseg /= 2;
        int Nseg() => nseg;

        // carriers: spec + product + offset
        var carSpecs = new List<CarrierSpec>();
        foreach (var s in r.Sources) foreach (var c in s.Signal.Carriers) carSpecs.Add(c.Spec);
        int nc = carSpecs.Count;

        // products shown in the spectrum (multi: tones + IM3/IM5/IM2); single: the fundamental
        var specProducts = new List<(int k, double fHz, string label)>();
        var imd = new List<(int k, string label)>();
        bool wideSingle = !multi && WideTwoCarrier(r, carSpecs, out _, out _, out _);
        if (!multi)
        {
            specProducts.Add((1, r.F0Hz, "fundamental"));
            if (wideSingle && nk > 1) specProducts.Insert(0, (0, 0.0, "baseband"));
        }
        else
        {
            int Find(int a, int b)
            {
                for (int m = 0; m < r.MixTones!.Length; m++)
                {
                    var t = r.MixTones[m];
                    if ((t[0] == a && t[1] == b) || (t[0] == -a && t[1] == -b)) return m;
                }
                return -1;
            }
            foreach (var (a, b, lab) in new[] { (1, 0, "C1 (f1)"), (0, 1, "C2 (f2)"), (2, -1, "IM3L (2f1-f2)"), (-1, 2, "IM3U (2f2-f1)"),
                                                 (3, -2, "IM5L (3f1-2f2)"), (-2, 3, "IM5U (3f2-2f1)"), (-1, 1, "IM2 (f2-f1)"),
                                                 (1, 1, "f1+f2"), (2, 0, "H2 (2f1)"), (0, 2, "H2 (2f2)") })
            {
                int m = Find(a, b);
                if (m < 0) continue;
                imd.Add((m, lab));
            }
            // The spectrum shows EVERY lattice product of the baseband and fundamental zones (|k1 + k2| <= 1):
            // the carriers, IM3/IM5/IM7, and the even-order baseband products at multiples of the spacing.
            // Listing only the labelled products left real simulated content out of the plot.
            for (int m = 0; m < r.MixTones!.Length; m++)
            {
                var t = r.MixTones[m];
                if (Math.Abs(t[0] + t[1]) > 1 || Math.Abs(r.MixFreqHz![m]) < 1) continue;
                specProducts.Add((m, Math.Abs(r.MixFreqHz[m]), $"({t[0]},{t[1]})"));
            }
            specProducts.Sort((x, y) => x.fHz.CompareTo(y.fHz));
        }

        // Every product's envelope spectrum sits on its own span fk ± fs/2. With two fundamentals the spans of
        // neighbouring products OVERLAP whenever fs exceeds their spacing (400 MHz apart at the standard 491.52 MHz
        // envelope rate), and appending them gave a frequency axis that ran backwards at each seam: a stitched-looking
        // spectrum with two crossing skirts. The physical spectrum at f is the sum of every product's contribution
        // there, so the spans are summed onto ONE ascending grid (linear interpolation of each product's Welch bins;
        // the cross-spectrum between products is neglected, which only matters in the far skirts where two products'
        // regrowth meets). VSD and PSD share the grid and every step of this, so they differ only in what is summed.
        double[]? gridF = null;
        double[] Spectrum(Func<int, (double[] f, double[] d)> density)
        {
            var spans = new List<(double fk, double[] f, double[] d)>();
            foreach (var (k, fHz, _) in specProducts)
            {
                var (f, d) = density(k);
                if (!multi && k == 0) for (int i = 0; i < d.Length; i++) d[i] *= 4;   // real baseband, one-sided
                spans.Add((fHz, f, d));
            }
            // A single envelope wider than the carrier (Envelope=single over widely spaced carriers) runs past
            // 0 Hz. Each harmonic envelope keeps only physical frequencies above 0 (the baseband one its
            // positive half); a mixing product (a,b) lives in envelope k = a+b, so the fundamental's envelope
            // holds IM3/IM5 even beyond +/- f0/2 and is not cropped there.
            if (!multi)
                for (int si = 0; si < spans.Count; si++)
                {
                    var (fk, f, d) = spans[si];
                    int lo = Array.FindIndex(f, x => fk + x > 0);
                    if (lo > 0) spans[si] = (fk, f[lo..], d[lo..]);
                }
            gridF ??= CompositeGrid(spans);
            var tot = new double[gridF.Length];
            foreach (var (fk, f, d) in spans)
            {
                double df = f[1] - f[0];
                for (int i = 0; i < gridF.Length; i++)
                {
                    double pos = (gridF[i] - fk - f[0]) / df;
                    if (pos < -1e-9 || pos > f.Length - 1 + 1e-9) continue;
                    int i0 = Math.Clamp((int)Math.Floor(pos), 0, f.Length - 1);
                    int i1 = Math.Min(i0 + 1, f.Length - 1);
                    double a = Math.Clamp(pos - i0, 0, 1);
                    tot[i] += d[i0] + (d[i1] - d[i0]) * a;
                }
            }
            return tot;
        }

        // ── node cubes: the voltage spectral density, RMS V^2/Hz (a peak phasor's density over 2) ──
        if (np > 0)
        {
            var vsd = new List<double>(np * 8192);
            for (int p = 0; p < np; p++)
            {
                int pp = p;
                foreach (double v in Spectrum(k => SignalMeasure.Welch(Node(pp, k), fs, nseg))) vsd.Add(10 * Math.Log10(Math.Max(v, 1e-300) / 2));
            }
            ds.Add("VSD", new DataCube([nodeAxis, new Axis("freq", gridF!, "Hz")], vsd.ToArray()) { Unit = "dBV/Hz" });
        }

        var ks = multi ? r.CarrierMix.Distinct().ToArray() : [1];
        var (pk, pf) = ProbePower(r, t0, ks);

        // ── drive and supplies: scalars, they never depended on an output ──
        var drive = new Complex[nm];
        double reZ = r.DriveRe[0];
        for (int t = 0; t < nm; t++) foreach (var d in r.Drive) drive[t] += d[t0 + t];
        double pin = 0;
        for (int t = 0; t < nm; t++) pin += drive[t].Real * drive[t].Real + drive[t].Imaginary * drive[t].Imaginary;
        pin /= nm;
        double pinW = pin / (8 * reZ);
        ds.Add("Pin_avg_dBm", DataCube.Scalar(10 * Math.Log10(pinW * 1e3)));
        double pdc = double.NaN;
        if (r.SupplyNames.Length > 0)
        {
            pdc = 0;
            for (int s = 0; s < r.SupplyNames.Length; s++)
            {
                double iav = 0;
                for (int t = t0; t < nt; t++) iav += r.SupplyCurrent[t][s];
                pdc += -r.SupplyVoltage[s] * iav / nm;      // branch current flows + node -> source; a supply delivers -V*I
            }
            ds.Add("Pdc_W", DataCube.Scalar(pdc));
        }

        if (nb > 0)
        {
            AddProbePower(ds, r, plan.SaveWaveforms, ks, branchAxis, pk, pf);
            // A probe carrying no RF is NaN in every output row, so the branch axis is the same at every sweep point.
            var noRf = pf.Select(x => !(x >= NoRfFraction * pinW)).ToArray();
            static double Dbm(double w) => w > 0 ? 10 * Math.Log10(w * 1e3) : double.NaN;

            var psd = new List<double>(nb * 8192);
            for (int b = 0; b < nb; b++)
            {
                int bb = b;
                foreach (double w in Spectrum(k => Delivered(bb, k))) psd.Add(Dbm(w));
            }
            ds.Add("PSD", new DataCube([branchAxis, new Axis("freq", gridF!, "Hz")], psd.ToArray()) { Unit = "dBm/Hz" });

            if (pulse is null)
            {
                var aclr = new double[nb * nc * AclrLabels.Length];
                var papr = new double[nb];
                var ccdf = new double[nb * 200];
                double[]? probAxis = null;
                for (int b = 0; b < nb; b++)
                {
                    for (int c = 0; c < nc; c++)
                    {
                        int k = r.CarrierMix.Length > c ? r.CarrierMix[c] : 1;
                        var (f, d) = Delivered(b, k);
                        var (mb, sp) = SignalMeasure.AclrBands(carSpecs[c]);
                        var a = SignalMeasure.Aclr(f, d, fs, r.CarrierOffsetHz.Length > c ? r.CarrierOffsetHz[c] : 0, mb, sp);
                        int o = (b * nc + c) * AclrLabels.Length;
                        aclr[o] = Dbm(a.ChannelPower);
                        aclr[o + 1] = a.Acpr1L; aclr[o + 2] = a.Acpr1U; aclr[o + 3] = a.Acpr2L; aclr[o + 4] = a.Acpr2U;
                        if (noRf[b]) Array.Fill(aclr, double.NaN, o, AclrLabels.Length);
                    }
                    // the instantaneous delivered power of the first carrier product
                    Complex[] v1 = ProbeV(b, ks[0]), i1 = ProbeI(b, ks[0]);
                    var pt = new double[nm];
                    for (int t = 0; t < nm; t++) pt[t] = 0.5 * (v1[t].Real * i1[t].Real + v1[t].Imaginary * i1[t].Imaginary);
                    papr[b] = noRf[b] ? double.NaN : SignalMeasure.PaprDb(pt, 1e-4);
                    var (db, pr) = SignalMeasure.Ccdf(pt, 200);
                    probAxis ??= pr;
                    if (noRf[b]) Array.Fill(db, double.NaN);
                    Array.Copy(db, 0, ccdf, b * 200, 200);
                }
                ds.Add("ACLR", new DataCube([branchAxis,
                    new Axis("carrier", Index(nc), "", r.CarrierLabels.Length == nc ? r.CarrierLabels : carSpecs.Select(c => c.Label).ToArray()),
                    new Axis("metric", Index(AclrLabels.Length), "", AclrLabels)], aclr));
                ds.Add("PAPR_dB", new DataCube([branchAxis], papr));
                ds.Add("CCDF", new DataCube([branchAxis, new Axis("prob", probAxis!)], ccdf));
                AddEvm(ds, r, quiet, plan, t0, branchAxis, carSpecs, noRf, ProbeV, note);
            }
            ds.Add("Pavg_dBm", new DataCube([branchAxis], [.. pf.Select(Dbm)]) { Unit = "dBm" });
            if (imd.Count > 0)
            {
                var im = new double[nb * imd.Count];
                for (int b = 0; b < nb; b++)
                    for (int i = 0; i < imd.Count; i++) im[b * imd.Count + i] = Dbm(pk[b * nk + imd[i].k]);
                ds.Add("IMD", new DataCube([branchAxis, new Axis("product", Index(imd.Count), "", imd.Select(x => x.label).ToArray())], im));
            }
            else if (!multi && SingleEnvelopeImd(r, carSpecs, nb, nk, fs, (b, k) => Delivered(b, k)) is { } sim)
                ds.Add("IMD", new DataCube([branchAxis, new Axis("product", Index(sim.labels.Length), "", sim.labels)], sim.dbm));

            ds.Add("Gt_dB", new DataCube([branchAxis], [.. pf.Select((x, b) => noRf[b] ? double.NaN : 10 * Math.Log10(x / pinW))]));
            if (r.SupplyNames.Length > 0)
                // NaN rather than absent when no DC power is drawn: every sweep point has the cube.
                ds.Add("DE_pct", new DataCube([branchAxis], [.. pf.Select((x, b) => pdc > 0 && !noRf[b] ? 100 * x / pdc : double.NaN)]));
            if (pulse is not null) note?.Invoke(EnvelopePulse.Add(ds, r, pulse, plan.Params.StartS, branchAxis, noRf));
            else if (!multi) AddAmAm(ds, r, t0, drive, reZ, branchAxis, noRf, ProbeV, ProbeI);
        }

        ds.Add("__EnvStep_s", DataCube.Scalar(r.StepS));
        ds.Add("__EnvF0_Hz", DataCube.Scalar(r.F0Hz));
        ds.Add("__EnvSignalRate_Hz", DataCube.Scalar(r.SignalRateHz));
        ds.Add("__EnvWall_s", DataCube.Scalar(r.WallSeconds));
        ds.Add("__EnvNewtonPerStep", DataCube.Scalar(r.MeanNewton));
        ds.Add("__EnvJacobians", DataCube.Scalar(r.JacobianBuilds));
        ds.Add("__EnvTangentError", DataCube.Scalar(r.WorstTangentError));
        ds.Add("__EnvMeasureFrom_s", DataCube.Scalar(t0 * r.StepS));
        ds.Add("__EnvFundamentals", DataCube.Scalar(multi ? r.ToneFreqsHz!.Length : 1));
        if (r.SnpFits.Count > 0)
        {
            string[] metrics = ["order", "rel_error", "sigma_max"];
            ds.Add("__EnvSnpFit", new DataCube([
                new Axis("instance", Index(r.SnpFits.Count), "", r.SnpFits.Select(f => f.Instance).ToArray()),
                new Axis("metric", Index(metrics.Length), "", metrics)],
                r.SnpFits.SelectMany(f => new[] { f.Order, f.RelativeError, f.SigmaMax }).ToArray()));
        }
        return ds;
    }

    private static double[] Index(int n) => [.. Enumerable.Range(0, n).Select(i => (double)i)];

    /// <summary>The constellation keeps every m-th received point of a carrier, m the smallest that leaves at most this
    /// many (the Signal Designer draws as many).</summary>
    public const int ConstMaxSamples = 6000;

    /// <summary>How one carrier is measured: OFDM by <see cref="SignalEvm"/>, the others by <see cref="SignalQuality"/>.</summary>
    private enum Meter { None, Ofdm, Dbpsk, Fsk, Css }

    private static Meter MeterOf(CarrierStandard s) => s switch
    {
        CarrierStandard.Lte or CarrierStandard.Nr => Meter.Ofdm,
        CarrierStandard.Dbpsk => Meter.Dbpsk,
        CarrierStandard.Fsk => Meter.Fsk,
        CarrierStandard.Css => Meter.Css,
        _ => Meter.None,
    };

    /// <summary>One carrier at one probe: its error (EVM, FSK error or the de-chirped error), the FSK deviation and offset,
    /// the points to draw, and what was demodulated, for the SNR.</summary>
    private sealed record Reading(double Err, double Peak, double Dev, double Off, Complex[]? Points, Complex[]? Ref,
                                  double[]? Freq, double[]? FreqRef, object Raw);

    private static Reading? Read(Meter meter, CarrierRecord rec, double record, Complex[] y, double fs, long start, double off,
                                 out string? why)
    {
        if (meter == Meter.Ofdm)
        {
            var d = SignalEvm.Demodulate(rec, record, y, fs, start, off, out why);
            if (d is null) return null;
            var e = SignalEvm.Evaluate(d);
            return new Reading(e.RmsPercent, e.PeakPercent, double.NaN, double.NaN, e.Received, e.Ideal, null, null, d);
        }
        var w = SignalQuality.Receive(rec, record, y, fs, start, off, out why);
        if (w is null) return null;
        var q = SignalQuality.Evaluate(w);
        return new Reading(q.ErrorPercent, q.PeakPercent, q.DeviationHz, q.OffsetHz, q.Points, q.Reference, q.FreqHz, q.FreqRefHz, w);
    }

    private static double Snr(object noisy, object quiet) => noisy switch
    {
        ReceivedSymbols n => SignalEvm.SignalToNoise(n, (ReceivedSymbols)quiet),
        ReceivedWaveform n => SignalQuality.SignalToNoise(n, (ReceivedWaveform)quiet),
        _ => double.NaN,
    };

    /// <summary>
    /// The received signal's quality at every IProbe for every carrier that carries symbols (brief NZ-8), each read from
    /// the probe VOLTAGE's envelope in the product that carries it, its delay fitted against the record the source played:
    /// LTE / NR and DBPSK as EVM, MER and a constellation; FSK as the FSK error, the deviation and the offset, with the
    /// frequency at each bit centre; CSS as the de-chirped error. With EvmSplit, each error's distortion / noise split and
    /// the in-channel SNR. A family is written only when a carrier of it is there, and nothing when no carrier carries
    /// symbols; a carrier the run cannot measure is NaN, said once. A probe carrying no RF is NaN, as in ACLR.
    /// </summary>
    private static void AddEvm(DataSet ds, EnvelopeRunResult r, EnvelopeRunResult? quiet, EnvelopeRunner.Plan plan, int t0,
                               Axis branchAxis, List<CarrierSpec> carSpecs, bool[] noRf, Func<int, int, Complex[]> probeV,
                               Action<string>? note)
    {
        var meters = carSpecs.Select(c => MeterOf(c.Standard)).ToArray();
        if (meters.All(m => m == Meter.None)) return;
        var recs = r.Sources.SelectMany(s => s.Signal.Carriers.Select(c => (rec: c, record: s.Signal.RecordSeconds))).ToList();
        int nb = branchAxis.Length, nc = recs.Count;
        double fs = 1.0 / r.StepS;
        long start = (long)Math.Round(plan.Params.StartS / r.StepS) + t0;
        string[] labels = r.CarrierLabels.Length == nc ? r.CarrierLabels : carSpecs.Select(c => c.Label).ToArray();

        // the quiet run's window, read as the first run's is
        Complex[] QuietV(int b, int k)
        {
            int nm = quiet!.Time.Length - t0;
            var y = new Complex[nm];
            bool neg = r.MixFreqHz is { } mf && mf[k] < 0;
            for (int t = 0; t < nm; t++) { var v = quiet.IProbeVoltage[t0 + t][b][k]; y[t] = neg ? Complex.Conjugate(v) : v; }
            return y;
        }

        var err = Nan(nb * nc); var peak = Nan(nb * nc); var dev = Nan(nb * nc); var offs = Nan(nb * nc);
        double[]? dist = quiet is null ? null : Nan(nb * nc), noise = quiet is null ? null : Nan(nb * nc), snr = quiet is null ? null : Nan(nb * nc);
        var points = new Complex[nb, nc][]; var refs = new Complex[nc][];
        var freq = new double[nb, nc][]; var freqRef = new double[nc][];
        var why = new string?[nc];
        var clamped = new HashSet<Meter>();
        for (int c = 0; c < nc; c++)
        {
            if (meters[c] == Meter.None) continue;
            int k = r.CarrierMix.Length > c ? r.CarrierMix[c] : 1;
            double off = r.CarrierOffsetHz.Length > c ? r.CarrierOffsetHz[c] : 0;
            for (int b = 0; b < nb; b++)
            {
                var e = Read(meters[c], recs[c].rec, recs[c].record, probeV(b, k), fs, start, off, out why[c]);
                if (e is null) break;
                int n = e.Points?.Length ?? e.Freq?.Length ?? 0;
                int stride = Math.Max(1, (n + ConstMaxSamples - 1) / ConstMaxSamples);
                int kept = (n + stride - 1) / stride;
                if (e.Ref is { } er) refs[c] ??= [.. Enumerable.Range(0, kept).Select(i => er[i * stride])];
                if (e.FreqRef is { } fr) freqRef[c] ??= [.. Enumerable.Range(0, kept).Select(i => fr[i * stride])];
                if (noRf[b]) continue;
                int at = b * nc + c;
                err[at] = e.Err; peak[at] = e.Peak; dev[at] = e.Dev; offs[at] = e.Off;
                if (e.Points is { } ep) points[b, c] = [.. Enumerable.Range(0, kept).Select(i => ep[i * stride])];
                if (e.Freq is { } ef) freq[b, c] = [.. Enumerable.Range(0, kept).Select(i => ef[i * stride])];
                if (quiet is null) continue;
                var q = Read(meters[c], recs[c].rec, recs[c].record, QuietV(b, k), fs, start, off, out _);
                if (q is null) continue;
                double diff = e.Err * e.Err - q.Err * q.Err;
                dist![at] = q.Err;
                if (diff < 0) clamped.Add(meters[c]);
                noise![at] = Math.Sqrt(Math.Max(0, diff));
                snr![at] = 10 * Math.Log10(Snr(e.Raw, q.Raw));
            }
        }
        var missing = Enumerable.Range(0, nc).Where(c => why[c] is not null).ToList();
        if (missing.Count > 0)
            note?.Invoke($"Envelope '{plan.Name}': no signal quality for " +
                         string.Join("; ", missing.Select(c => $"{labels[c]} ({why[c]})")) + ". NMSE remains the waveform measure for them.");
        if (clamped.Count > 0)
            note?.Invoke($"Envelope '{plan.Name}': on at least one carrier the noiseless run read a larger error than the noisy one " +
                         "(the record's statistics), so the noise part is 0 there.");

        var carrierAxis = new Axis("carrier", Index(nc), "", labels);
        double[] Only(double[] v, params Meter[] kinds) => [.. v.Select((x, i) => kinds.Contains(meters[i % nc]) ? x : double.NaN)];
        void Family(string prefix, string unitErr, Meter[] kinds, string distName, string noiseName)
        {
            ds.Add(prefix, new DataCube([branchAxis, carrierAxis], Only(err, kinds)) { Unit = unitErr });
            if (dist is null) return;
            ds.Add(distName, new DataCube([branchAxis, carrierAxis], Only(dist, kinds)) { Unit = "%" });
            ds.Add(noiseName, new DataCube([branchAxis, carrierAxis], Only(noise!, kinds)) { Unit = "%" });
        }

        Meter[] qam = [Meter.Ofdm, Meter.Dbpsk];
        if (meters.Any(qam.Contains))
        {
            Family("EVM_pct", "%", qam, "EVMdist_pct", "EVMnoise_pct");
            var evm = Only(err, qam);
            ds.Add("EVM_dB", new DataCube([branchAxis, carrierAxis], [.. evm.Select(v => 20 * Math.Log10(v / 100))]) { Unit = "dB" });
            ds.Add("EVMpeak_pct", new DataCube([branchAxis, carrierAxis], Only(peak, qam)) { Unit = "%" });
            ds.Add("MER_dB", new DataCube([branchAxis, carrierAxis], [.. evm.Select(v => -20 * Math.Log10(v / 100))]) { Unit = "dB" });
            // the constellation: one symbol axis as long as the longest carrier's, the rest NaN
            int ns = Math.Max(1, refs.Max(x => x?.Length ?? 0));
            var nan = new Complex(double.NaN, double.NaN);
            var cons = new Complex[nb * nc * ns];
            Array.Fill(cons, nan);
            var cref = new Complex[nc * ns];
            Array.Fill(cref, nan);
            for (int c = 0; c < nc; c++)
            {
                if (refs[c] is { } rf) Array.Copy(rf, 0, cref, c * ns, rf.Length);
                for (int b = 0; b < nb; b++)
                    if (points[b, c] is { } pt) Array.Copy(pt, 0, cons, (b * nc + c) * ns, pt.Length);
            }
            var symbolAxis = new Axis("symbol", Index(ns));
            ds.Add("Const", new DataCube([branchAxis, carrierAxis, symbolAxis], cons));
            ds.Add("ConstRef", new DataCube([carrierAxis, symbolAxis], cref));
        }
        if (meters.Contains(Meter.Fsk))
        {
            Family("FSKerr_pct", "%", [Meter.Fsk], "FSKerrDist_pct", "FSKerrNoise_pct");
            ds.Add("FreqDev_Hz", new DataCube([branchAxis, carrierAxis], Only(dev, Meter.Fsk)) { Unit = "Hz" });
            ds.Add("FreqOffset_Hz", new DataCube([branchAxis, carrierAxis], Only(offs, Meter.Fsk)) { Unit = "Hz" });
            int ns = Math.Max(1, freqRef.Max(x => x?.Length ?? 0));
            var pts = Nan(nb * nc * ns);
            var fref = Nan(nc * ns);
            for (int c = 0; c < nc; c++)
            {
                if (freqRef[c] is { } rf) Array.Copy(rf, 0, fref, c * ns, rf.Length);
                for (int b = 0; b < nb; b++)
                    if (freq[b, c] is { } pt) Array.Copy(pt, 0, pts, (b * nc + c) * ns, pt.Length);
            }
            var bitAxis = new Axis("bit", Index(ns));
            ds.Add("FreqPts", new DataCube([branchAxis, carrierAxis, bitAxis], pts) { Unit = "Hz" });
            ds.Add("FreqRef", new DataCube([carrierAxis, bitAxis], fref) { Unit = "Hz" });
        }
        if (meters.Contains(Meter.Css))
            Family("CSSerr_pct", "%", [Meter.Css], "CSSerrDist_pct", "CSSerrNoise_pct");
        if (snr is not null)
            ds.Add("SNR_dB", new DataCube([branchAxis, carrierAxis], snr) { Unit = "dB" });
    }

    private static double[] Nan(int n)
    {
        var a = new double[n];
        Array.Fill(a, double.NaN);
        return a;
    }

    /// <summary>
    /// AM-AM and AM-PM at every IProbe (brief ENV-MEAS-3 §4-5), on the fixed <c>Pin_inst</c> grid. X is the instantaneous
    /// available drive, |V_drive|²/(8 Re Z_s); the probe node's fundamental envelope is aligned to the drive
    /// (<see cref="SignalMeasure.Align"/>); the gain is the delivered power ½·Re(V₁·I₁*) over the available drive, exact for
    /// any load, and the phase is V₁'s relative to the drive less the mean gain phase. The cloud is the same samples on
    /// the drive's time axis, every m-th of them.
    /// </summary>
    private static void AddAmAm(DataSet ds, EnvelopeRunResult r, int t0, Complex[] drive, double reZ, Axis branchAxis, bool[] noRf,
                                Func<int, int, Complex[]> probeV, Func<int, int, Complex[]> probeI)
    {
        int nm = drive.Length, nb = branchAxis.Length;
        // A FIXED absolute grid (AmGridLoDbm..AmGridHiDbm, 0.5 dB), the same at every sweep point: a data-dependent grid
        // made a swept AM-AM stack every point against the FIRST point's axis. A bin with fewer than AmMinSamples samples
        // is NaN (plots skip it) rather than noise. The cubes are emitted at EVERY point, however few bins hold data: a
        // sweep point without them (a CW drive fills one bin) could not be stacked against the others, and the whole
        // sweep was refused.
        int b0 = (int)Math.Round(AmGridLoDbm / 0.5), b1 = (int)Math.Round(AmGridHiDbm / 0.5);
        int nbin = b1 - b0;
        var pinAxis = new Axis("Pin_inst", Enumerable.Range(b0, nbin).Select(b => (b + 0.5) * 0.5).ToArray(), "dBm");
        // the cloud keeps every m-th aligned sample; m depends on Stop and the step alone, so a swept run stacks
        int m = Math.Max(1, (nm + CloudMaxSamples - 1) / CloudMaxSamples);
        int nc = (nm + m - 1) / m;
        var pinT = new double[nc];
        for (int c = 0; c < nc; c++)
        {
            var d = drive[c * m];
            double pi = (d.Real * d.Real + d.Imaginary * d.Imaginary) / (8 * reZ);
            pinT[c] = pi > 0 ? 10 * Math.Log10(pi * 1e3) : double.NaN;
        }
        var gtT = new double[nc * nb]; var pmT = new double[nc * nb];
        Array.Fill(gtT, double.NaN); Array.Fill(pmT, double.NaN);
        string[] names = ["AMAM", "AMAM_min", "AMAM_max", "AMPM", "AMPM_min", "AMPM_max"];
        var cols = names.Select(_ => new double[nb * nbin]).ToArray();
        var nmse = new double[nb]; var delay = new double[nb];
        for (int b = 0; b < nb; b++)
        {
            var y = probeV(b, 1); var cur = probeI(b, 1);
            var (g, lag, e) = SignalMeasure.Align(drive, y, MaxLag);
            nmse[b] = noRf[b] ? double.NaN : e;
            delay[b] = noRf[b] ? double.NaN : lag * r.StepS;
            double gphase = Math.Atan2(g.Imaginary, g.Real);
            var bins = new SortedDictionary<int, (double sum, double min, double max, double ph, double phMin, double phMax, int n)>();
            for (int t = 0; t + lag < nm && !noRf[b]; t++)
            {
                double pi = (drive[t].Real * drive[t].Real + drive[t].Imaginary * drive[t].Imaginary) / (8 * reZ);
                if (pi <= 0) continue;
                Complex yo = y[t + lag], io = cur[t + lag];
                double po = 0.5 * (yo.Real * io.Real + yo.Imaginary * io.Imaginary);
                if (po <= 0) continue;
                double gt = 10 * Math.Log10(po / pi);
                double ph = (Complex.Log(yo / drive[t]).Imaginary - gphase) * 180 / Math.PI;
                ph = ((ph + 540) % 360) - 180;
                int bin = (int)Math.Floor(10 * Math.Log10(pi * 1e3) / 0.5);
                bins[bin] = bins.TryGetValue(bin, out var q)
                    ? (q.sum + gt, Math.Min(q.min, gt), Math.Max(q.max, gt), q.ph + ph, Math.Min(q.phMin, ph), Math.Max(q.phMax, ph), q.n + 1)
                    : (gt, gt, gt, ph, ph, ph, 1);
                if (t % m == 0) { gtT[t / m * nb + b] = gt; pmT[t / m * nb + b] = ph; }
            }
            for (int i = 0; i < nbin; i++)
            {
                bool ok = bins.TryGetValue(b0 + i, out var q) && q.n >= AmMinSamples;
                double[] v = ok ? [q.sum / q.n, q.min, q.max, q.ph / q.n, q.phMin, q.phMax] : [.. names.Select(_ => double.NaN)];
                for (int c = 0; c < names.Length; c++) cols[c][b * nbin + i] = v[c];
            }
        }
        ds.Add("NMSE_dB", new DataCube([branchAxis], nmse));
        ds.Add("Delay_s", new DataCube([branchAxis], delay));
        for (int c = 0; c < names.Length; c++) ds.Add(names[c], new DataCube([branchAxis, pinAxis], cols[c]));
        var timeAxis = new Axis("time", [.. Enumerable.Range(0, nc).Select(c => r.Time[t0 + c * m])], "s");
        ds.Add("Pin_inst_t_dBm", new DataCube([timeAxis], pinT) { Unit = "dBm" });
        ds.Add("Gt_t_dB", new DataCube([timeAxis, branchAxis], gtT) { Unit = "dB" });
        ds.Add("PM_t_deg", new DataCube([timeAxis, branchAxis], pmT) { Unit = "deg" });
    }

    /// <summary>
    /// Per IProbe, per product, ½·Re(Vₖ·Iₖ*) at a step (V₀·I₀ at DC) is the exact power the product carries: the cross terms
    /// between products average out over a carrier period. Averaged from <paramref name="t0"/>, the built-in scalars'
    /// window. The fundamental band is <paramref name="fund"/>: harmonic 1, or the lattice's carrier products.
    /// </summary>
    private static (double[] pk, double[] pf) ProbePower(EnvelopeRunResult r, int t0, int[] fund)
    {
        int nb = r.IProbeNames.Length, nt = r.Time.Length;
        if (nb == 0) return ([], []);
        int nk = r.IProbeCurrent[0][0].Length, nm = nt - t0;
        var pk = new double[nb * nk];
        var pf = new double[nb];
        for (int b = 0; b < nb; b++)
        {
            for (int k = 0; k < nk; k++)
            {
                double sum = 0;
                for (int t = t0; t < nt; t++) sum += P(r, t, b, k);
                pk[b * nk + k] = sum / nm;
            }
            foreach (int k in fund) pf[b] += pk[b * nk + k];
        }
        return (pk, pf);
    }

    private static double P(EnvelopeRunResult r, int t, int b, int k)
    {
        Complex v = r.IProbeVoltage[t][b][k], i = r.IProbeCurrent[t][b][k];
        return k == 0 ? v.Real * i.Real : 0.5 * (v.Real * i.Real + v.Imaginary * i.Imaginary);
    }

    /// <summary>Power through every IProbe (brief ENV-MEAS-1): the per-product and fundamental-band averages always, the
    /// probe currents and the instantaneous power with the waveforms.</summary>
    private static void AddProbePower(DataSet ds, EnvelopeRunResult r, bool saveWaveforms, int[] fund, Axis branchAxis, double[] pk, double[] pf)
    {
        int nb = r.IProbeNames.Length, nt = r.Time.Length;
        int nk = r.IProbeCurrent[0][0].Length;
        var third = r.MixTones is not null
            ? new Axis("mixIndex", r.MixFreqHz!, "Hz", r.MixLabels)
            : new Axis("harmonic", Index(nk));
        ds.Add("Pprobe_W", new DataCube([branchAxis, third], pk));
        ds.Add("Pprobe_fund_W", new DataCube([branchAxis], pf));
        if (saveWaveforms)
        {
            var ic = new Complex[nt * nb * nk];
            var pt = new double[nt * nb];
            for (int t = 0; t < nt; t++)
                for (int b = 0; b < nb; b++)
                {
                    for (int k = 0; k < nk; k++) ic[(t * nb + b) * nk + k] = r.IProbeCurrent[t][b][k];
                    foreach (int k in fund) pt[t * nb + b] += P(r, t, b, k);
                }
            ds.Add("I", new DataCube([new Axis("time", r.Time, "s"), branchAxis, third], ic));
            ds.Add("Pprobe_t_W", new DataCube([new Axis("time", r.Time, "s"), branchAxis], pt));
        }
        ds.Add("__ProbeBranches", new DataCube([new Axis("probe", Index(nb), "", r.IProbeNames)], new double[nb]));
    }

    /// <summary>
    /// The IMD cube of a SINGLE envelope that spans two widely spaced carriers (Envelope=single): the
    /// same products the lattice lists, each read as the delivered power of the harmonic envelope that holds it
    /// (k = a + b for the product a f1 + b f2) integrated over +/- half the carrier spacing around its offset F - k f0. That
    /// is the lattice's per-product mean power by another route, so the two modes compare directly.
    /// <paramref name="density"/> is a probe's delivered power density (W/Hz) in one harmonic envelope.
    /// Null unless there are exactly two carrier frequencies more than 3x the bandwidth apart.
    /// </summary>
    private static (string[] labels, double[] dbm)? SingleEnvelopeImd(EnvelopeRunResult r, List<CarrierSpec> carSpecs,
        int nb, int nk, double fs, Func<int, int, (double[] f, double[] d)> density)
    {
        if (!WideTwoCarrier(r, carSpecs, out double f1, out double f2, out double sp)) return null;
        var prods = new[] { (1, 0, "C1 (f1)"), (0, 1, "C2 (f2)"), (2, -1, "IM3L (2f1-f2)"), (-1, 2, "IM3U (2f2-f1)"),
                            (3, -2, "IM5L (3f1-2f2)"), (-2, 3, "IM5U (3f2-2f1)"), (-1, 1, "IM2 (f2-f1)"),
                            (1, 1, "f1+f2"), (2, 0, "H2 (2f1)"), (0, 2, "H2 (2f2)") };
        var keep = new List<(string lab, int k, double off)>();
        foreach (var (a, b, lab) in prods)
        {
            double F = a * f1 + b * f2;
            if (F <= 0) continue;
            // A product (a,b) is generated in harmonic envelope k = a+b (the fast-time transform treats the
            // envelopes as frozen over a carrier period). round(F/f0) is NOT that: IM5 of carriers at f0 -/+ s
            // sits at f0 -/+ 5s, which for s = f0/10 is exactly 0.5 f0 and 1.5 f0, and banker's rounding sent
            // IM5L to the baseband envelope and IM5U to the second harmonic's.
            int k = a + b;
            if (k < 0) continue;
            double off = F - k * r.F0Hz;
            if (k > nk - 1 || Math.Abs(off) + sp / 2 > fs / 2) continue;     // outside this envelope's band
            keep.Add((lab, k, off));
        }
        if (keep.Count == 0) return null;
        var dbm = new double[nb * keep.Count];
        for (int p = 0; p < nb; p++)
        {
            var welch = new Dictionary<int, (double[] f, double[] d)>();
            for (int i = 0; i < keep.Count; i++)
            {
                var (_, k, off) = keep[i];
                if (!welch.TryGetValue(k, out var w)) welch[k] = w = density(p, k);
                double pw = SignalMeasure.BandPower(w.f, w.d, off, sp);
                // the DC envelope is the physical baseband itself: a line at +F appears at +F and -F of its
                // two-sided spectrum, and its power is not halved the way Re(V e^{jwt}) is
                if (k == 0) pw = 2 * (pw + SignalMeasure.BandPower(w.f, w.d, -off, sp));
                dbm[p * keep.Count + i] = pw > 0 ? 10 * Math.Log10(pw * 1e3) : double.NaN;
            }
        }
        return (keep.Select(x => x.lab).ToArray(), dbm);
    }

    /// <summary>Exactly two carrier frequencies more than 3x the widest bandwidth apart (one envelope over them).</summary>
    private static bool WideTwoCarrier(EnvelopeRunResult r, List<CarrierSpec> carSpecs, out double f1, out double f2, out double sp)
    {
        f1 = f2 = sp = 0;
        var cf = r.CarrierOffsetHz.Select(o => r.F0Hz + o).Distinct().OrderBy(x => x).ToArray();
        if (cf.Length != 2 || carSpecs.Count == 0) return false;
        double bw = carSpecs.Max(c => Math.Max(c.BandwidthHz, 1e6));
        f1 = cf[0]; f2 = cf[1]; sp = f2 - f1;
        return sp > 3 * bw;
    }

    /// <summary>One ascending frequency grid over the union of the products' spans fk + f (Welch
    /// offsets, uniform step): overlapping spans merge into one run of points, and a gap between
    /// spans that do not meet is simply not sampled.</summary>
    private static double[] CompositeGrid(List<(double fk, double[] f, double[] d)> spans)
    {
        double df = spans[0].f[1] - spans[0].f[0];
        var iv = spans.Select(s => (lo: s.fk + s.f[0], hi: s.fk + s.f[^1])).OrderBy(x => x.lo).ToList();
        var merged = new List<(double lo, double hi)>();
        foreach (var x in iv)
        {
            if (merged.Count > 0 && x.lo <= merged[^1].hi + df)
                merged[^1] = (merged[^1].lo, Math.Max(merged[^1].hi, x.hi));
            else merged.Add(x);
        }
        var g = new List<double>();
        foreach (var (lo, hi) in merged)
        {
            int n = (int)Math.Floor((hi - lo) / df + 1e-9) + 1;
            for (int i = 0; i < n; i++) g.Add(lo + i * df);
        }
        return g.ToArray();
    }
}
