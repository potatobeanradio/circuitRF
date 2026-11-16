// brief-pch-6 R-pch6-4 — what a run says of each Propagation Channel (design note §5.6), as cubes beside its results: the loss
// breakdown at the channel's reference frequency, the total path loss, the bulk delay and the common Doppler, and the rays.
// Named link:<instance>:<quantity>, the instance's own path, so a sweep stacks them as it stacks every other cube.
// brief-pch-7 R-pch7-6 adds the small scale: the RMS delay spread, the coherence bandwidth and time, f_D, the power-delay
// profile, the drop's Doppler spectrum, its tap gains over the record and (envelope) H(f, t) on the signal's band and the
// regime of note §2.4.

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using RfCore.Data;
using RfCore.Propagation;

namespace CircuitRF.Engine;

public static class PropagationChannelReport
{
    /// <summary>What an envelope run tells the report: the record's span (s, the run's own time axis), the signal's bandwidth
    /// and its subcarrier spacing (null when it is not OFDM).</summary>
    public sealed record EnvelopeSpan(double StartS, double StopS, double BandwidthHz, double? SubcarrierHz);

    /// <summary>
    /// Adds each channel's report to <paramref name="ds"/>, at the reference frequency of <paramref name="hz"/> (its f0, else
    /// the run's carrier, else <paramref name="hz"/>), and says once that harmonic balance holds the channel at TimeOffset with
    /// no Doppler shift when <paramref name="hb"/>.
    /// </summary>
    public static void Add(DataSet ds, ElaboratedNetlist nl, double hz, bool hb = false, EnvelopeSpan? env = null,
                           Func<int, Complex[]>? solution = null)
    {
        if (solution is not null) AddShares(ds, nl, hz, solution);
        foreach (var ec in nl.Components)
        {
            if (ec.Model is not PropagationChannelModel ch) continue;
            PropagationChannelModel.Realization r;
            try { r = ch.At(ch.ReferenceHz(hz)); }
            catch (ParameterRefusalException) { continue; }   // the run itself refused it, naming why
            string key = $"link:{ec.InstancePath}:";
            var terms = r.LargeScale.Terms;
            ds.Add(key + "PathLoss_dB", DataCube.Scalar(r.LargeScale.LossDb));
            if (terms.Count > 0)
                ds.Add(key + "Loss_dB", new DataCube([new Axis("term", [.. Enumerable.Range(0, terms.Count).Select(i => (double)i)], "",
                                                               [.. terms.Select(t => t.Name)])], [.. terms.Select(t => t.Db)]) { Unit = "dB" });
            ds.Add(key + "Delay0_s", DataCube.Scalar(r.BulkDelayS));
            ds.Add(key + "Doppler0_Hz", DataCube.Scalar(r.CommonDopplerHz));
            ds.Add(key + "Reference_Hz", DataCube.Scalar(r.ReferenceHz));
            int n = r.Rays.Count;
            var rayAxis = new Axis("ray", [.. Enumerable.Range(0, n).Select(i => (double)i)], "", [.. r.Rays.Select(x => x.Name)]);
            var terms0 = ch.Terms(r.ReferenceHz);
            ds.Add(key + "RayDelay_s", new DataCube([rayAxis], [.. r.Rays.Select(x => x.ExcessDelayS)]) { Unit = "s" });
            ds.Add(key + "RayDoppler_Hz", new DataCube([rayAxis], [.. r.Rays.Select(x => x.DopplerHz)]) { Unit = "Hz" });
            ds.Add(key + "RayGain", new DataCube([rayAxis], terms0.Length == n ? [.. terms0.Select(t => t.Amplitude)] : new Complex[n]));
            AddSmallScale(ds, key, ch, r, hz, env, nl, ec.InstancePath);
            if (r.Drop is { } drop) AddDrop(ds, key, ch, drop, env);
            if (ch.BeamTable is AntennaArray.BeamTable beam) AddBeam(ds, key, beam);
            if (hb && (r.Rays.Any(x => x.DopplerHz != 0) || r.Fading))
                nl.AddNoteOnce("hb-channel-doppler:" + ec.InstancePath,
                    $"Propagation Channel '{ec.InstancePath}': harmonic balance holds it at TimeOffset with no Doppler shift (a shifted tone would " +
                    "leave the harmonic grid) and its fading frozen there; an envelope run carries both.");
        }
    }

    /// <summary>
    /// brief-pch-10 R-pch10-1 — what the drop drew, so a sweep over Drop stacks each one's statistics: the shadowing (dB, added to the loss),
    /// the line-of-sight state (1 line of sight, 0 not; NaN when it was not drawn), the rain's time percentage (NaN when not drawn), a
    /// scenario drop's large-scale parameters, and P.681's state (1 GOOD, 0 BAD) at TimeOffset with, in an envelope run, the fraction of
    /// the record spent GOOD.
    /// </summary>
    private static void AddDrop(DataSet ds, string key, PropagationChannelModel ch, PropagationChannelModel.DropState d, EnvelopeSpan? env)
    {
        ds.Add(key + "Shadowing_dB", DataCube.Scalar(d.ShadowingDb));
        bool? los = d.Scenario?.Los ?? d.Los;
        ds.Add(key + "LosState", DataCube.Scalar(los is bool l ? (l ? 1 : 0) : double.NaN));
        ds.Add(key + "RainTime_pct", DataCube.Scalar(d.RainTimePercent ?? double.NaN));
        if (d.Scenario is { } sc)
        {
            ds.Add(key + "DS_s", DataCube.Scalar(sc.Ds));
            ds.Add(key + "ASD_deg", DataCube.Scalar(sc.Asd));
            ds.Add(key + "ASA_deg", DataCube.Scalar(sc.Asa));
            ds.Add(key + "ZSD_deg", DataCube.Scalar(sc.Zsd));
            ds.Add(key + "ZSA_deg", DataCube.Scalar(sc.Zsa));
            ds.Add(key + "K_dB", DataCube.Scalar(sc.KDb ?? double.NaN));
        }
        if (d.States is { } st)
        {
            double t = ch.FrozenTimeS;
            ds.Add(key + "State", DataCube.Scalar(st.Series.GoodAt(st.PositionM(t)) ? 1 : 0));
            double from = st.PositionM(ch.TimeOffsetS + (env?.StartS ?? 0)), to = st.PositionM(ch.TimeOffsetS + (env?.StopS ?? 0));
            ds.Add(key + "StateGood_frac", DataCube.Scalar(to > from ? Math.Clamp(GoodBetween(st.Series, from, to), 0, 1) : st.Series.GoodAt(from) ? 1 : 0));
        }
    }

    private static double GoodBetween(P681Series s, double from, double to)
        => (s.GoodFraction(to) * to - (from > 0 ? s.GoodFraction(from) * from : 0)) / (to - Math.Max(0, from));

    /// <summary>
    /// brief-pch-9 R-pch9-6 — the beam table: every scored (or visited) pair's metric (dB, the linear pattern model), its rank among distinct
    /// pairs (NaN: not distinct) and its two codebook entries (1-based), on a numeric pair axis strongest first — no label per pair, since an
    /// exhaustive search keeps up to 10⁶ of them; the codebooks as their own cubes, [beam, angle] in degrees with each beam's label. Then the
    /// chosen pair's position on the pair axis, its metric, the phase-only bound, the codebook's loss against it and the pairs scored.
    /// </summary>
    private static void AddBeam(DataSet ds, string key, AntennaArray.BeamTable t)
    {
        var axis = new Axis("pair", [.. Enumerable.Range(0, t.Pairs.Count).Select(i => (double)i)]);
        ds.Add(key + "Beam_dB", new DataCube([axis], [.. t.Pairs.Select(p => p.MetricDb)]) { Unit = "dB" });
        ds.Add(key + "BeamRank", new DataCube([axis], [.. t.Pairs.Select(p => p.Rank is int r ? (double)r : double.NaN)]));
        ds.Add(key + "BeamTx", new DataCube([axis], [.. t.Pairs.Select(p => (double)(p.Tx + 1))]));
        ds.Add(key + "BeamRx", new DataCube([axis], [.. t.Pairs.Select(p => (double)(p.Rx + 1))]));
        void Book(string name, IReadOnlyList<AntennaArray.BeamEntry> beams)
        {
            var beamAxis = Index(name == "TxCodebook_deg" ? "txBeam" : "rxBeam", beams.Select(b => b.Label));
            var values = new double[beams.Count * 2];
            for (int i = 0; i < beams.Count; i++) { values[2 * i] = beams[i].ThetaDeg; values[2 * i + 1] = beams[i].PhiDeg; }
            ds.Add(key + name, new DataCube([beamAxis, Index("angle", ["theta", "phi"])], values) { Unit = "deg" });
        }
        Book("TxCodebook_deg", t.TxBeams);
        Book("RxCodebook_deg", t.RxBeams);
        ds.Add(key + "BeamChosen", DataCube.Scalar(t.Pairs.ToList().IndexOf(t.Chosen)));
        ds.Add(key + "BeamChosen_dB", DataCube.Scalar(t.Chosen.MetricDb));
        ds.Add(key + "BeamBound_dB", DataCube.Scalar(t.BoundDb));
        ds.Add(key + "BeamCodebookLoss_dB", DataCube.Scalar(t.CodebookLossDb));
        ds.Add(key + "BeamPairsScored", DataCube.Scalar(t.PairsScored));
        if (t.TxSteered)
        {
            var e = t.TxBeams[t.Chosen.Tx];
            ds.Add(key + "TxBeam_deg", new DataCube([Index("angle", ["theta", "phi"])], [e.ThetaDeg, e.PhiDeg]) { Unit = "deg" });
        }
        if (t.RxSteered)
        {
            var e = t.RxBeams[t.Chosen.Rx];
            ds.Add(key + "RxBeam_deg", new DataCube([Index("angle", ["theta", "phi"])], [e.ThetaDeg, e.PhiDeg]) { Unit = "deg" });
        }
    }

    private static Axis Index(string name, IEnumerable<string> labels)
    {
        var l = labels.ToArray();
        return new Axis(name, [.. Enumerable.Range(0, l.Length).Select(i => (double)i)], "", l);
    }

    /// <summary>brief-pch-7 R-pch7-6 — the small-scale part of the report.</summary>
    private static void AddSmallScale(DataSet ds, string key, PropagationChannelModel ch, PropagationChannelModel.Realization r, double hz,
                                      EnvelopeSpan? env, ElaboratedNetlist nl, string instance)
    {
        var inv = CultureInfo.InvariantCulture;
        double rms = RayStatistics.RmsDelaySpread(r.Rays), bc = RayStatistics.CoherenceBandwidthHz(r.Rays), tc = r.CoherenceTimeS;
        ds.Add(key + "RmsDelaySpread_s", DataCube.Scalar(rms));
        ds.Add(key + "CoherenceBandwidth_Hz", DataCube.Scalar(bc));
        ds.Add(key + "fD_Hz", DataCube.Scalar(r.FdHz));
        ds.Add(key + "Tc_s", DataCube.Scalar(tc));

        // the power-delay profile: each delay's power (taps at one delay summed), in delay order
        var pdp = r.Rays.GroupBy(x => x.ExcessDelayS).OrderBy(g => g.Key)
                        .Select(g => (Delay: g.Key, P: g.Sum(x => x.Gain.Magnitude * x.Gain.Magnitude))).ToArray();
        ds.Add(key + "PowerDelayProfile_dB", new DataCube([new Axis("delay", [.. pdp.Select(x => x.Delay)], "s")],
                                                         [.. pdp.Select(x => x.P > 0 ? 10 * Math.Log10(x.P) : double.NaN)]) { Unit = "dB" });

        // the drop's Doppler spectrum: its lines binned across the band they occupy
        var (dhz, dp) = SmallScale.DopplerSpectrum(r.Rays, r.Fades, r.FdHz, 32);
        ds.Add(key + "DopplerSpectrum_dB", new DataCube([new Axis("doppler", dhz, "Hz")],
                                                       [.. dp.Select(p => p > 0 ? 10 * Math.Log10(p) : double.NaN)]) { Unit = "dB" });

        // the taps' gains (before the large scale and the antennas) at the record's instants, or at TimeOffset
        double[] times = env is { } e && e.StopS > e.StartS
            ? [.. Enumerable.Range(0, 256).Select(i => e.StartS + (e.StopS - e.StartS) * i / 255.0)]
            : [0];
        var gains = new Complex[r.Rays.Count * times.Length];
        for (int i = 0; i < r.Rays.Count; i++)
            for (int t = 0; t < times.Length; t++)
                gains[i * times.Length + t] = r.Rays[i].Gain * (i < r.Fades.Count ? r.Fades[i].Gain(ch.TimeOffsetS + times[t]) : Complex.One);
        ds.Add(key + "TapGain", new DataCube([Index("ray", r.Rays.Select(x => x.Name)), new Axis("time", times, "s")], gains));

        if (env is not { } en) return;
        // H(f, t) across the signal's band over the record, as the receive pin sees it
        int nf = 33, nt = 64;
        var offs = Enumerable.Range(0, nf).Select(i => en.BandwidthHz * (i / (nf - 1.0) - 0.5)).ToArray();
        var ht = Enumerable.Range(0, nt).Select(i => en.StartS + (en.StopS - en.StartS) * i / (nt - 1.0)).ToArray();
        double save = ch.FrozenTimeS;
        ch.FrozenTimeS = ch.TimeOffsetS;
        var h = new Complex[nt * nf];
        for (int t = 0; t < nt; t++)
            for (int f = 0; f < nf; f++) h[t * nf + f] = ch.H(hz + offs[f], ht[t]);
        ch.FrozenTimeS = save;
        ds.Add(key + "H", new DataCube([new Axis("time", ht, "s"), new Axis("offset", offs, "Hz")], h));

        // the regime (note §2.4): is the channel frozen over the record, does it turn within a symbol, is it flat over the band
        double record = en.StopS - en.StartS;
        ds.Add(key + "fDxTrecord", DataCube.Scalar(r.FdHz * record));
        if (en.SubcarrierHz is double scs) ds.Add(key + "fDoverSCS", DataCube.Scalar(r.FdHz / scs));
        if (!r.Fading) return;
        string time = double.IsInfinity(tc) ? "the fading never decorrelates to one half"
                      : tc > 4 * record ? string.Format(inv, "T_c = {0:G3} s is longer than the {1:G3} s record: the record sees a nearly frozen channel, and "
                                                             + "its statistics come from many drops", tc, record)
                      : string.Format(inv, "T_c = {0:G3} s, the record {1:G3} s: the channel turns within the record", tc, record);
        string ici = en.SubcarrierHz is double sc ? string.Format(inv, "; f_D/Δf_sc = {0:G3}{1}", r.FdHz / sc,
                         r.FdHz / sc > 0.01 ? " (inter-carrier interference grows with it)" : "") : "";
        string band = double.IsInfinity(bc) ? "one delay, flat across the band"
                      : bc < en.BandwidthHz ? string.Format(inv, "coherence bandwidth {0:G3} Hz under the signal's {1:G3} Hz: frequency selective", bc, en.BandwidthHz)
                      : string.Format(inv, "coherence bandwidth {0:G3} Hz over the signal's {1:G3} Hz: nearly flat", bc, en.BandwidthHz);
        nl.AddNoteOnce("env-channel-regime:" + instance, string.Format(inv,
            "Propagation Channel '{0}': f_D = {1:G4} Hz, f_D·T_record = {2:G3}; {3}{4}; {5}.", instance, r.FdHz, r.FdHz * record, time, ici, band));
    }

    /// <summary>
    /// brief-pch-8b R-pch8b-3 — each channel's own share at an array's feed, from a single-tone harmonic-balance solution
    /// (<paramref name="solution"/>: harmonic → the linear solution vector): the wave its ray unknowns send from the feed into a matched load,
    /// through the receive collapse's coefficients, as <c>link:&lt;channel&gt;:FeedPower_dBm</c>; and for an array several channels reach,
    /// <c>link:&lt;array&gt;:SIR_dB[channel]</c>, each one's power over the sum of the others'. A channel whose receive array is not on the fast
    /// path reads NaN, and the run says why.
    /// </summary>
    private static void AddShares(DataSet ds, ElaboratedNetlist nl, double f0, Func<int, Complex[]> solution)
    {
        var powers = new Dictionary<string, List<(string Channel, double Watts)>>(StringComparer.Ordinal);
        Complex[]? x = null;
        foreach (var ec in nl.Components)
        {
            if (ec.Model is not PropagationChannelModel ch || ch.RxArray is not { } rx) continue;
            double watts = double.NaN;
            if (ch.Shares.TryGetValue(2 * Math.PI * f0, out var share) && share.Coefficients.Length == ch.RayBranches.Count)
            {
                x ??= solution(1);
                Complex b = 0;
                for (int r = 0; r < share.Coefficients.Length; r++)
                {
                    var (yt, yp) = ch.RayBranches[r];
                    if (yt < x.Length) b += share.Coefficients[r].Theta * x[yt];
                    if (yp < x.Length) b += share.Coefficients[r].Phi * x[yp];
                }
                watts = 0.5 * (b.Real * b.Real + b.Imaginary * b.Imaginary);
            }
            else
                nl.AddNoteOnce("channel-share:" + ec.InstancePath, $"Propagation Channel '{ec.InstancePath}': its power at '{rx.Ec.InstancePath}''s feed is not " +
                    $"reported, since the array is solved element by element ({ch.CollapseSentence ?? "no collapse"}).");
            ds.Add($"link:{ec.InstancePath}:FeedPower_dBm", DataCube.Scalar(watts > 0 ? 10 * Math.Log10(watts * 1000) : double.NaN));
            if (!powers.TryGetValue(rx.Ec.InstancePath, out var list)) powers[rx.Ec.InstancePath] = list = [];
            list.Add((ec.InstancePath, watts));
        }
        foreach (var (array, list) in powers)
        {
            if (list.Count < 2) continue;
            var sir = list.Select(c =>
            {
                double others = list.Where(o => o.Channel != c.Channel).Sum(o => o.Watts);
                return c.Watts > 0 && others > 0 ? 10 * Math.Log10(c.Watts / others) : double.NaN;
            }).ToArray();
            ds.Add($"link:{array}:SIR_dB", new DataCube([Index("channel", list.Select(c => c.Channel))], sir) { Unit = "dB" });
            nl.AddNoteOnce("channel-sir:" + array, $"'{array}': SIR_dB is each channel's power at the feed over the sum of the other channels'; the array's own " +
                           "noise at the feed is the S-parameter noise analysis's (SystemTempK), not in this ratio.");
        }
    }
}
