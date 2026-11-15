// brief-paa-6b — a fields analysis whose waves come from an envelope analysis (waves=ENV1): the modulated signal each named direction
// receives, each element's active reflection over the record, and one time-averaged pattern (docs/briefs/brief-paa-6b-envelope.md).
//
// THE WAVES ARE THE ENVELOPE'S OWN (D-6b-4). The run records the block's terminal voltages (its nodes join the envelope's recorded nodes)
// and its port branch currents (EnvelopeParams.Blocks, recorded beside the IProbes) at every step, so a wave is (V + Z0·I)/(2√Z0) from what
// the envelope solved, not I = Y·V at the carrier: across a modulation's band the block's current depends on its whole response.
//
// THE FIELD IS LINEAR IN THE WAVES, so it is a basis and a weight: every port's (or element's) stored pattern is read once, on the record's
// grid and in the named directions, and each step costs one weight vector and a dot product per direction. The time-averaged pattern is the
// basis's quadratic form against the weights' covariance over the measured window, R_kl = <w_k w_l*>, which is exactly the mean of |E|² over
// the steps without forming a pattern per step.
//
// THE METRICS ARE THE ENVELOPE'S OWN CODE. Each direction's co-polar envelope (Ludwig 3, about the record's reference) is handed to
// EnvelopeDataSet.Build as a "branch" whose ½·Re(V·I*) is the co-polar EIRP, so its PSD, ACLR, PAPR, CCDF and EVM are computed by the same
// functions an IProbe's are. The element pattern is held flat across the modulation's band (D-6b-3): it is taken at the record frequency the
// carrier harmonic falls on.

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Arrays;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Engine;
using CircuitRF.Engine.AntennaArray;
using CircuitRF.Engine.Envelope;
using CircuitRF.Engine.Mom;
using RfCore.Data;

namespace CircuitRF.Design.Em3d;

public static partial class FieldCosimulation
{
    /// <summary>The group of a run's radiated envelopes, one per named direction.</summary>
    public const string RadiatedGroup = "radiated";

    /// <summary>D-6b-1 — the most directions one run names.</summary>
    public const int MaxDirections = 16;

    /// <summary>The cubes an envelope's own branch measurements carry that mean nothing of a direction: a gain from the source's available power
    /// to an EIRP, an efficiency against no supply, and the instantaneous cloud built on them.</summary>
    private static readonly HashSet<string> NotOfADirection = new(StringComparer.Ordinal)
    {
        "Gt_dB", "DE_pct", "Gt_t_dB", "PM_t_deg", "Pin_inst_t_dBm", "Pin_avg_dBm", "Pdc_W", "I", "Pprobe_t_W",
    };

    /// <summary>A named direction: its label and its angles in degrees.</summary>
    private sealed record EnvDirection(string Label, double ThetaDeg, double PhiDeg);

    /// <summary>R-paa6b-1 — one envelope run of <paramref name="env"/> on <paramref name="nl"/> with the block recorded, and the fields analysis's cubes from it.</summary>
    private static DataSet FromEnvelope(FieldsAnalysis fa, ElaboratedNetlist nl, EnvelopeAnalysis env, FieldsHbContext ctx, List<string> notes,
                                        RunControl? control, Action<IFieldMetrics>? metrics, string? sweepPoint)
    {
        string who = $"Fields '{fa.Name}'";
        int k = HarmonicOf(fa, nl, who);
        var block = nl.Components.FirstOrDefault(c => string.Equals(c.InstancePath, fa.Block, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"{who}: the circuit has no instance '{fa.Block}'.");
        var (recordPath, pa) = block.Model switch
        {
            PhasedArrayModel { Feed: not null } => throw new InvalidOperationException(
                $"{who}: '{fa.Block}' is a {PhasedArrayModel.DisplayName} in Feed mode, whose elements are inside it; waves= reads an amplifier on every element's pin, which is Elements mode."),
            PhasedArrayModel arr => (arr.RecordPath, arr),
            SnpModel { RecordPath: { } rp } => (rp, (PhasedArrayModel?)null),
            SnpModel snp => throw new PlainSnpFieldsBlockException(who, fa.Block, snp.FilePath),
            _ => throw new InvalidOperationException($"{who}: '{fa.Block}' is a {block.ComponentType}, not an {EmFieldDataRecord.DisplayName} block or a {PhasedArrayModel.DisplayName}."),
        };
        var (far, refused) = PortFarFieldSet.Open(recordPath);
        if (far is null) throw new InvalidOperationException($"{who}: {refused}");
        string name = Path.GetFileName(recordPath);
        int ports = pa?.PortCount ?? far.PortCount;
        if (pa is null && far.PortCount != ((SnpModel)block.Model).PortCount)
            throw new InvalidOperationException($"{who}: '{fa.Block}' has {((SnpModel)block.Model).PortCount} ports and its far field {far.PortCount}.");
        var directions = DirectionsOf(fa, who);
        // refused before the run: a direction past the pattern's θ, or off the grid where the patterns are safe only on it
        foreach (var d in directions.Where(x => x.Label != "peak"))
        {
            if (d.ThetaDeg > far.Grid.ThetaDeg[^1] + 1e-9)
                throw new InvalidOperationException($"{who}: direction {d.Label} is at θ {G(d.ThetaDeg)}°, and the pattern of '{fa.Block}' reaches θ {G(far.Grid.ThetaDeg[^1])}°" +
                                                    (far.Hemisphere ? " (the upper hemisphere of a record over an infinite ground)." : "."));
            if ((pa is null || pa.Level == PhasedArrayLevel.Exact) && OnGrid(far.Grid, d.ThetaDeg, d.PhiDeg) < 0)
            {
                var g = far.Grid;
                int near = NearestGridDirection(g, d.ThetaDeg, d.PhiDeg);
                throw new InvalidOperationException($"{who}: direction {d.Label} is between the record's grid directions, and its stored patterns are safe only on their own grid. " +
                                                    $"The nearest is θ {G(g.ThetaDeg[near / g.PhiDeg.Count])}°, φ {G(g.PhiDeg[near % g.PhiDeg.Count])}°.");
            }
        }

        // the run, with the block's nodes and port currents recorded
        var plan = EnvelopeRunner.Resolve(env, nl.ResolvedGlobals);
        var nodeNames = block.Nodes.Take(ports).Append(block.ReferenceNode).Where(x => x > 0).Distinct().Select(nl.Nodes.NameOf).ToArray();
        plan = plan with { Params = plan.Params with { Probes = [.. plan.Params.Probes, .. nodeNames], Blocks = [fa.Block] } };
        double fc = nl.Components.Select(c => c.Model).OfType<ModSourceModel>().FirstOrDefault()?.FcHz
                    ?? throw new InvalidOperationException($"{who}: '{env.Name}' needs a MOD_Source, the modulated signal, and the circuit has none.");
        double hz = k * fc;
        int fi = far.IndexOf(hz);
        if (fi < 0)
        {
            // D-6b-3 — the nearest record frequency, which the pattern is then held at across the band
            fi = Enumerable.Range(0, far.FrequenciesHz.Count).MinBy(i => Math.Abs(far.FrequenciesHz[i] - hz));
            double off = Math.Abs(far.FrequenciesHz[fi] - hz) / hz;
            if (off > 0.02)
                throw new InvalidOperationException($"{who}: harmonic {k} of '{env.Name}''s carrier is {MaterialDispersion.Hz(hz)}, and '{name}' holds no pattern within 2 % of it: the " +
                                                    $"nearest frequency it holds is {MaterialDispersion.Hz(far.FrequenciesHz[fi])}. A pattern exists only at frequencies the run solved.");
        }
        double patternHz = far.FrequenciesHz[fi];
        bool first = true;
        object gate = new();
        ElaboratedNetlist Elaborate()
        {
            lock (gate)
            {
                if (first) { first = false; return nl; }
                return new Elaborator(ctx.Library) { BaseDirectory = ctx.BaseDirectory }.Elaborate(ctx.Bench);
            }
        }
        var (record, skipS, _) = EnvelopeRunner.Record(plan, Elaborate, null, control, null, fundamentals =>
        {
            if (fundamentals != 1)
                throw new InvalidOperationException($"{who}: '{env.Name}' runs on {fundamentals} fundamentals (its carriers are far apart, or a P1Tone is off them), and waves= " +
                                                    "forms the radiated envelope of one. Set Envelope=single on it where its carriers allow, so one envelope spans them.");
        });

        // the waves at every step
        int nt = record.Time.Length;
        int t0 = Math.Clamp((int)Math.Round(skipS / record.StepS), 0, Math.Max(0, nt - 1));
        Complex z0 = pa?.Z0 ?? far.Z0;
        Complex sq = RfCore.RFNetwork.ComplexSqrt(z0);
        int Probe(string node) => Array.IndexOf(record.ProbeNames, node);
        int[] vAt = [.. Enumerable.Range(0, ports).Select(p => block.Nodes[p] > 0 ? Probe(nl.Nodes.NameOf(block.Nodes[p])) : -1)];
        int vRef = block.ReferenceNode > 0 ? Probe(nl.Nodes.NameOf(block.ReferenceNode)) : -1;
        int[] iAt = [.. Enumerable.Range(0, ports).Select(p => Array.IndexOf(record.IProbeNames, $"{fa.Block}#{p + 1}"))];
        if (iAt.Any(i => i < 0) || vAt.Any(v => v < 0 && v != -1))
            throw new InvalidOperationException($"{who}: '{env.Name}' did not record '{fa.Block}''s ports.");
        var a = new Complex[nt][];
        var b = new Complex[nt][];
        for (int t = 0; t < nt; t++)
        {
            a[t] = new Complex[ports];
            b[t] = new Complex[ports];
            Complex vr = vRef >= 0 ? record.Probe[t][vRef][k] : Complex.Zero;
            for (int p = 0; p < ports; p++)
            {
                if (pa is not null && !pa.Lattice.IsOn(p)) continue;
                Complex v = (vAt[p] >= 0 ? record.Probe[t][vAt[p]][k] : Complex.Zero) - vr;
                Complex i = record.IProbeCurrent[t][iAt[p]][k];
                a[t][p] = (v + z0 * i) / (2 * sq);
                b[t][p] = (v - z0 * i) / (2 * sq);
            }
        }

        // the field: a basis per port (or element) and a weight per step
        var grid = far.Grid;
        var gridBasis = GridBasis(far, fi, pa, patternHz);
        Complex[] Weight(Complex[] at, Complex[] bt) => WeightsOf(far, fi, pa, at, bt);
        var w = new Complex[nt][];
        for (int t = 0; t < nt; t++) w[t] = Weight(a[t], b[t]);
        int nb = gridBasis.Theta.Length;
        int measured = nt - t0;
        var cov = new Complex[nb, nb];
        for (int t = t0; t < nt; t++)
            for (int p = 0; p < nb; p++)
                for (int q = 0; q < nb; q++) cov[p, q] += w[t][p] * Complex.Conjugate(w[t][q]);
        for (int p = 0; p < nb; p++) for (int q = 0; q < nb; q++) cov[p, q] /= Math.Max(1, measured);
        int nd = grid.DirectionCount;
        var avgTheta2 = new double[nd];
        var avgPhi2 = new double[nd];
        for (int d = 0; d < nd; d++)
        {
            Complex st = 0, sp = 0;
            for (int p = 0; p < nb; p++)
                for (int q = 0; q < nb; q++)
                {
                    st += gridBasis.Theta[p][d] * cov[p, q] * Complex.Conjugate(gridBasis.Theta[q][d]);
                    sp += gridBasis.Phi[p][d] * cov[p, q] * Complex.Conjugate(gridBasis.Phi[q][d]);
                }
            avgTheta2[d] = Math.Max(0, st.Real);
            avgPhi2[d] = Math.Max(0, sp.Real);
        }
        var averaged = FarFieldStage.Pattern(grid, [.. avgTheta2.Select(x => new Complex(Math.Sqrt(x), 0))], [.. avgPhi2.Select(x => new Complex(Math.Sqrt(x), 0))], 1, patternHz);
        int peak = 0;
        for (int d = 1; d < nd; d++) if (averaged.U[d] > averaged.U[peak]) peak = d;
        double peakTheta = grid.ThetaDeg[peak / grid.PhiDeg.Count], peakPhi = grid.PhiDeg[peak % grid.PhiDeg.Count];

        // the named directions
        var named = directions.Select(x => x.Label == "peak" ? x with { ThetaDeg = peakTheta, PhiDeg = peakPhi } : x).ToList();
        var dirBasis = NamedBasis(fa, who, far, fi, pa, patternHz, gridBasis, named);
        int nn = named.Count;
        var eTheta = new Complex[nt * nn];
        var ePhi = new Complex[nt * nn];
        for (int t = 0; t < nt; t++)
            for (int d = 0; d < nn; d++)
            {
                Complex st = 0, sp = 0;
                for (int p = 0; p < nb; p++) { st += w[t][p] * dirBasis.Theta[p][d]; sp += w[t][p] * dirBasis.Phi[p][d]; }
                eTheta[t * nn + d] = st;
                ePhi[t * nn + d] = sp;
            }

        // Ludwig-3 about the record's own reference, from the strongest step's pattern
        int strongest = Enumerable.Range(t0, measured).MaxBy(t => a[t].Sum(x => x.Magnitude * x.Magnitude));
        var (phi0, refNote) = ReferenceOf(far, gridBasis, w[strongest], patternHz, a[strongest], b[strongest]);

        var data = new DataSet();
        var time = new Axis("time", (double[])record.Time.Clone(), "s");
        var dirAxis = new Axis("direction", [.. Enumerable.Range(0, nn).Select(d => (double)d)], "", [.. named.Select(x => x.Label)]);
        data.Add($"{RadiatedGroup}.theta", new DataCube([dirAxis], [.. named.Select(x => x.ThetaDeg)]) { Unit = "deg" });
        data.Add($"{RadiatedGroup}.phi", new DataCube([dirAxis], [.. named.Select(x => x.PhiDeg)]) { Unit = "deg" });
        data.Add($"{RadiatedGroup}.Etheta", new DataCube([time, dirAxis], eTheta) { Unit = "V" });
        data.Add($"{RadiatedGroup}.Ephi", new DataCube([time, dirAxis], ePhi) { Unit = "V" });
        double eta = FarFieldStage.Eta0;
        var eirp = new double[nt * nn];
        for (int i = 0; i < eirp.Length; i++)
        {
            double pw = 4 * Math.PI * (eTheta[i].Magnitude * eTheta[i].Magnitude + ePhi[i].Magnitude * ePhi[i].Magnitude) / (2 * eta);
            eirp[i] = pw > 0 ? 10 * Math.Log10(pw * 1e3) : double.NegativeInfinity;
        }
        data.Add($"{RadiatedGroup}.EIRP_t_dBm", new DataCube([time, dirAxis], eirp) { Unit = "dBm" });
        // where the measured window starts: every average and metric here is over the record from this time on
        data.Add($"{RadiatedGroup}.MeasureFrom_s", DataCube.Scalar(t0 * record.StepS));

        // the co-polar envelope of each direction, measured by the envelope's own branch code
        if (k == 1)
        {
            double c = Math.Sqrt(4 * Math.PI / eta);
            int nk = record.MaxHarm + 1;
            var cur = new Complex[nt][][];
            for (int t = 0; t < nt; t++)
            {
                cur[t] = new Complex[nn][];
                for (int d = 0; d < nn; d++)
                {
                    double ang = (named[d].PhiDeg - phi0) * Math.PI / 180;
                    Complex co = eTheta[t * nn + d] * Math.Cos(ang) - ePhi[t * nn + d] * Math.Sin(ang);
                    cur[t][d] = new Complex[nk];
                    cur[t][d][1] = c * co;
                }
            }
            var branches = record.WithBranches([.. named.Select(x => x.Label)], cur, cur);
            var measures = EnvelopeDataSet.Build(branches, plan with { SaveWaveforms = false }, skipS);
            foreach (var (cubeName, cube) in measures.CubesIn(DataSet.DefaultGroup))
            {
                if (cubeName.StartsWith("__", StringComparison.Ordinal) || NotOfADirection.Contains(cubeName)) continue;
                if (cube.Axes.Count == 0 || cube.Axes[0].Name != "branch") continue;
                data.Add($"{RadiatedGroup}.{cubeName}", Renamed(cube, dirAxis));
            }
        }

        // the time-averaged pattern and what U alone gives
        var f = new Axis("freq", [patternHz], "Hz");
        var port = new Axis("port", [1.0], "");
        data.Add("farfield.U", new DataCube([f, new Axis("theta", [.. grid.ThetaDeg], "deg"), new Axis("phi", [.. grid.PhiDeg], "deg"), port], [.. averaged.U]) { Unit = "W/sr" });
        double prad = averaged.RadiatedPowerW;
        void Scalar(string n, string unit, double v) => data.Add($"farfield.{n}", new DataCube([f, port], [v]) { Unit = unit });
        Scalar("TrpDbm", "dBm", 10 * Math.Log10(prad * 1e3));
        Scalar("DirectivityDbi", "dBi", 10 * Math.Log10(4 * Math.PI * averaged.U[peak] / prad));
        Scalar("PeakEirpDbm", "dBm", 10 * Math.Log10(4 * Math.PI * averaged.U[peak] * 1e3));
        Scalar("DirectivityPeakThetaDeg", "deg", peakTheta);
        Scalar("DirectivityPeakPhiDeg", "deg", peakPhi);

        // each element's load over the record
        AddEnvelopeElements(data, time, a, b, t0, pa);

        double bytes = (double)nt * nn * 48 + (double)nt * ports * 56;
        var lines = new List<string>
        {
            $"Fields '{fa.Name}': the radiated envelope of '{fa.Block}' in {nn} {(nn == 1 ? "direction" : "directions")} ({string.Join("; ", named.Select(x => $"{x.Label} θ {G(x.ThetaDeg)}° φ {G(x.PhiDeg)}°"))}), " +
            $"from envelope analysis '{env.Name}''s own waves at {(k == 1 ? "the carrier" : $"harmonic {k}")} over {nt} steps of {G(record.StepS * 1e9)} ns, the first {t0} left out as the run's settle. " +
            $"The patterns are '{name}''s at {MaterialDispersion.Hz(patternHz)}, held flat across the modulation's band. farfield.U is the power-averaged pattern; " +
            $"its peak is θ {G(peakTheta)}°, φ {G(peakPhi)}°. About {(bytes / 1e6).ToString("0.#", CultureInfo.InvariantCulture)} MB of envelopes.",
            k == 1
                ? $"Fields '{fa.Name}': each direction's PSD, ACLR, PAPR, CCDF and modulation metrics are its Ludwig-3 co-polar component about φ₀ = {G(phi0)}° ({refNote}), as EIRP, measured as an IProbe's are."
                : $"Fields '{fa.Name}': at harmonic {k} the radiated envelopes are published, and the modulation metrics, which are the carrier's, are not formed.",
            $"Fields '{fa.Name}': ActiveGamma({fa.Name}, n) reads each element's power-weighted average reflection over the record; array.GammaActive holds it at every step.",
        };
        if (pa is not null) lines.Add(pa.LevelNote);
        // D-6b-2 — a direction whose co-polar component is weak against the cross-polar one has metrics of the weaker component
        for (int d = 0; d < nn; d++)
        {
            double ang = (named[d].PhiDeg - phi0) * Math.PI / 180, co2 = 0, cross2 = 0;
            for (int t = t0; t < nt; t++)
            {
                Complex et = eTheta[t * nn + d], ep = ePhi[t * nn + d];
                co2 += (et * Math.Cos(ang) - ep * Math.Sin(ang)).Magnitude is var c1 ? c1 * c1 : 0;
                cross2 += (et * Math.Sin(ang) + ep * Math.Cos(ang)).Magnitude is var c2 ? c2 * c2 : 0;
            }
            if (cross2 > 100 * co2)
                lines.Add($"Fields '{fa.Name}': in direction {named[d].Label} the co-polar component is {G(10 * Math.Log10(cross2 / Math.Max(co2, 1e-300)))} dB below the " +
                          "cross-polar one on average, so its metrics describe the weaker component.");
        }
        Interlocked.Increment(ref _runs);
        if (sweepPoint is not null)
        {
            nl.AddNoteOnce($"fields:{fa.Name}:env-sweep", lines[0]);
            foreach (string line in lines.Skip(1)) nl.AddNoteOnce($"fields:{fa.Name}:{line}", line);
        }
        else
        {
            for (int l = 0; l < lines.Count; l++) nl.AddNoteOnce($"fields:{fa.Name}:{l}", lines[l]);
            notes.Add($"Fields '{fa.Name}': {nn} radiated {(nn == 1 ? "envelope" : "envelopes")} from '{env.Name}', peak of the averaged pattern θ {G(peakTheta)}°, φ {G(peakPhi)}°");
        }
        metrics?.Invoke(new EnvelopeFieldsMetrics(fa, a, b, t0, pa));
        return data;
    }

    /// <summary>D-6b-1 — <c>dirs=</c>: <c>peak</c>, or <c>theta,phi</c> pairs in degrees separated by semicolons, at most <see cref="MaxDirections"/>.</summary>
    private static List<EnvDirection> DirectionsOf(FieldsAnalysis fa, string who)
    {
        string text = fa.DirsExpr.Trim();
        if (text.Length == 0) text = "peak";
        var items = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (items.Length > MaxDirections)
            throw new InvalidOperationException($"{who}: dirs= names {items.Length} directions, and a run names at most {MaxDirections}: each is a whole envelope of the far field and " +
                                                "its spectra. Name fewer, or read the time-averaged pattern, farfield.U, for the whole sphere.");
        var list = new List<EnvDirection>();
        foreach (string item in items)
        {
            if (item.Equals("peak", StringComparison.OrdinalIgnoreCase)) { list.Add(new EnvDirection("peak", double.NaN, double.NaN)); continue; }
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double th)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double ph))
                throw new InvalidOperationException($"{who}: dirs= item '{item}' is not a direction: write peak, or theta,phi in degrees (30,0), separated by semicolons.");
            // a negative theta is the same plane on the other side: theta -30 at phi 0 is theta 30 at phi 180
            if (th < 0) { th = -th; ph += 180; }
            ph = ((ph % 360) + 360) % 360;
            list.Add(new EnvDirection(item.Replace(" ", ""), th, ph));
        }
        if (list.Select(x => x.Label).Distinct(StringComparer.OrdinalIgnoreCase).Count() != list.Count)
            throw new InvalidOperationException($"{who}: dirs= names one direction twice.");
        return list;
    }

    /// <summary>The basis on the record's whole grid: one pattern per weight, θ and φ components.</summary>
    private sealed record Basis(Complex[][] Theta, Complex[][] Phi);

    private static Basis GridBasis(PortFarFieldSet far, int fi, PhasedArrayModel? pa, double hz)
    {
        int nd = far.Grid.DirectionCount;
        if (pa is null || pa.Level == PhasedArrayLevel.Exact)
        {
            var th = new Complex[far.PortCount][];
            var ph = new Complex[far.PortCount][];
            for (int p = 0; p < far.PortCount; p++)
            {
                th[p] = new Complex[nd];
                ph[p] = new Complex[nd];
                far.AddWeighted(fi, p, Complex.One, th[p], ph[p]);
            }
            return new Basis(th, ph);
        }
        return ArrayBasis(pa, far, hz, ArrayDirections.OnGrid(far.Grid));
    }

    /// <summary>An embedded-pattern array's basis: each element's referred pattern times its own array-factor term, in <paramref name="dirs"/>.</summary>
    private static Basis ArrayBasis(PhasedArrayModel pa, PortFarFieldSet far, double hz, ArrayDirections dirs)
    {
        var elements = Elements(pa, far, hz);
        int n = pa.PortCount;
        var th = new Complex[n][];
        var ph = new Complex[n][];
        for (int e = 0; e < n; e++)
        {
            var unit = new Complex[n];
            unit[e] = Complex.One;
            var field = ArrayPattern.Sum(pa.Lattice, unit, elements, dirs, hz);
            th[e] = field.ETheta;
            ph[e] = field.EPhi;
        }
        return new Basis(th, ph);
    }

    /// <summary>The weights of one step's waves against the basis: the record's own (PointOf's) for a block or an exact array, the incident waves
    /// for an embedded-pattern array.</summary>
    private static Complex[] WeightsOf(PortFarFieldSet far, int fi, PhasedArrayModel? pa, Complex[] a, Complex[] b)
    {
        if (pa is null) return far.Weights(fi, a, b, out _);
        if (pa.Level != PhasedArrayLevel.Exact) return a;
        var ra = new Complex[far.PortCount];
        var rb = new Complex[far.PortCount];
        for (int e = 0; e < pa.PortCount; e++) { ra[pa.PatternPort(e)] = a[e]; rb[pa.PatternPort(e)] = b[e]; }
        return far.Weights(fi, ra, rb, out _);
    }

    /// <summary>The basis in the named directions: read off the grid where a direction is on it; an embedded-pattern array's interpolated off it;
    /// anything else off it refused, naming the nearest grid direction.</summary>
    private static Basis NamedBasis(FieldsAnalysis fa, string who, PortFarFieldSet far, int fi, PhasedArrayModel? pa, double hz, Basis grid,
                                    List<EnvDirection> named)
    {
        var g = far.Grid;
        double maxTheta = g.ThetaDeg[^1];
        foreach (var d in named)
            if (d.ThetaDeg > maxTheta + 1e-9)
                throw new InvalidOperationException($"{who}: direction {d.Label} is at θ {G(d.ThetaDeg)}°, and the pattern of '{fa.Block}' reaches θ {G(maxTheta)}°" +
                                                    (far.Hemisphere ? " (the upper hemisphere of a record over an infinite ground)." : "."));
        int nb = grid.Theta.Length;
        int nn = named.Count;
        var th = new Complex[nb][];
        var ph = new Complex[nb][];
        for (int p = 0; p < nb; p++) { th[p] = new Complex[nn]; ph[p] = new Complex[nn]; }
        var off = new List<int>();
        for (int d = 0; d < nn; d++)
        {
            int at = OnGrid(g, named[d].ThetaDeg, named[d].PhiDeg);
            if (at < 0) { off.Add(d); continue; }
            for (int p = 0; p < nb; p++) { th[p][d] = grid.Theta[p][at]; ph[p][d] = grid.Phi[p][at]; }
        }
        if (off.Count > 0)
        {
            if (pa is null || pa.Level == PhasedArrayLevel.Exact)
            {
                var d = named[off[0]];
                int near = NearestGridDirection(g, d.ThetaDeg, d.PhiDeg);
                throw new InvalidOperationException($"{who}: direction {d.Label} is between the record's grid directions, and its stored patterns are safe only on their own grid. " +
                                                    $"The nearest is θ {G(g.ThetaDeg[near / g.PhiDeg.Count])}°, φ {G(g.PhiDeg[near % g.PhiDeg.Count])}°.");
            }
            var dirs = ArrayDirections.Of([.. off.Select(i => named[i].ThetaDeg * Math.PI / 180)], [.. off.Select(i => named[i].PhiDeg * Math.PI / 180)]);
            var extra = ArrayBasis(pa, far, hz, dirs);
            for (int j = 0; j < off.Count; j++)
                for (int p = 0; p < nb; p++) { th[p][off[j]] = extra.Theta[p][j]; ph[p][off[j]] = extra.Phi[p][j]; }
        }
        return new Basis(th, ph);
    }

    private static int OnGrid(PlanarFarFieldGrid g, double thetaDeg, double phiDeg)
    {
        int it = -1, ip = -1;
        for (int i = 0; i < g.ThetaDeg.Count; i++) if (Math.Abs(g.ThetaDeg[i] - thetaDeg) < 1e-6) it = i;
        for (int i = 0; i < g.PhiDeg.Count; i++) if (Math.Abs(g.PhiDeg[i] - phiDeg) < 1e-6) ip = i;
        // at θ = 0 every φ names one direction
        if (it == 0 && Math.Abs(thetaDeg) < 1e-9 && ip < 0) ip = 0;
        return it < 0 || ip < 0 ? -1 : g.IndexOf(it, ip);
    }

    private static int NearestGridDirection(PlanarFarFieldGrid g, double thetaDeg, double phiDeg)
    {
        var want = ArrayDirections.Vector(thetaDeg * Math.PI / 180, phiDeg * Math.PI / 180);
        int best = 0;
        double bestSep = double.MaxValue;
        for (int it = 0; it < g.ThetaDeg.Count; it++)
            for (int ip = 0; ip < g.PhiDeg.Count; ip++)
            {
                double sep = ArrayBeam.Separation(want, ArrayDirections.Vector(g.ThetaDeg[it] * Math.PI / 180, g.PhiDeg[ip] * Math.PI / 180));
                if (sep < bestSep) { bestSep = sep; best = g.IndexOf(it, ip); }
            }
        return best;
    }

    /// <summary>D-6b-2 — the Ludwig-3 reference angle: the one a published pattern of the strongest step would carry, through the same derivation.
    /// When none derives (no dominant linear axis), 0° is used and the note says so, and the co/cross split is the reader's to discount.</summary>
    private static (double PhiDeg, string Note) ReferenceOf(PortFarFieldSet far, Basis grid, Complex[] w, double hz, Complex[] a, Complex[] b)
    {
        int nd = far.Grid.DirectionCount;
        var et = new Complex[nd];
        var ep = new Complex[nd];
        for (int p = 0; p < w.Length; p++)
            for (int d = 0; d < nd; d++) { et[d] += w[p] * grid.Theta[p][d]; ep[d] += w[p] * grid.Phi[p][d]; }
        double inc = a.Sum(x => x.Magnitude * x.Magnitude), refl = b.Sum(x => x.Magnitude * x.Magnitude);
        double acc = Math.Max(1e-300, 0.5 * (inc - refl));
        var point = new Em3dPatternPoint(FarFieldStage.Pattern(far.Grid, et, ep, 1, hz), acc, new Complex(Math.Sqrt(Math.Max(0, refl / Math.Max(inc, 1e-300))), 0));
        var (verdict, reference) = PlanarPolarization.ReferenceAngle(Em3dRadiation.Context(far.Solver, point, far.ReferenceInputPowerDbm, far.Planar));
        return verdict.Ok && reference is not null
            ? (reference.PhiDeg, reference.Derived ? "derived from the strongest step's pattern, as a published pattern's is" : "named")
            : (0, "no reference derives from the pattern, so φ₀ = 0° is used and the co-polar component is read about it");
    }

    /// <summary>A cube with its first axis (an envelope's branch) replaced by the direction axis.</summary>
    private static DataCube Renamed(DataCube cube, Axis direction)
    {
        Axis[] axes = [direction, .. cube.Axes.Skip(1)];
        return cube.DataKind == DataKind.Complex
            ? new DataCube(axes, cube.ComplexValues) { Unit = cube.Unit }
            : new DataCube(axes, cube.RealValues) { Unit = cube.Unit };
    }

    /// <summary>R-paa6b-4 — the array group over time: a, b, the active reflection (NaN where the incident wave is below 1e-6 of its RMS) and the watts
    /// delivered, at every step; the power-weighted average reflection and the mean delivered power over the measured window.</summary>
    private static void AddEnvelopeElements(DataSet data, Axis time, Complex[][] a, Complex[][] b, int t0, PhasedArrayModel? pa)
    {
        int nt = a.Length, n = a[0].Length;
        var element = new Axis("element", [.. Enumerable.Range(1, n).Select(e => (double)e)], "");
        var ac = new Complex[nt * n];
        var bc = new Complex[nt * n];
        var g = new Complex[nt * n];
        var pd = new double[nt * n];
        var rms = new double[n];
        for (int e = 0; e < n; e++)
        {
            double s = 0;
            for (int t = t0; t < nt; t++) s += a[t][e].Magnitude * a[t][e].Magnitude;
            rms[e] = Math.Sqrt(s / Math.Max(1, nt - t0));
        }
        for (int t = 0; t < nt; t++)
            for (int e = 0; e < n; e++)
            {
                Complex ai = a[t][e], bi = b[t][e];
                ac[t * n + e] = ai;
                bc[t * n + e] = bi;
                bool on = pa?.Lattice.IsOn(e) ?? true;
                g[t * n + e] = on && ai.Magnitude > 1e-6 * rms[e] && rms[e] > 0 ? bi / ai : new Complex(double.NaN, double.NaN);
                pd[t * n + e] = 0.5 * (ai.Magnitude * ai.Magnitude - bi.Magnitude * bi.Magnitude);
            }
        data.Add($"{ArrayGroup}.{IncidentCube}", new DataCube([time, element], ac) { Unit = "sqrt(W)" });
        data.Add($"{ArrayGroup}.{ReflectedCube}", new DataCube([time, element], bc) { Unit = "sqrt(W)" });
        data.Add($"{ArrayGroup}.{ActiveReflectionCube}", new DataCube([time, element], g));
        data.Add($"{ArrayGroup}.{ElementDeliveredCube}", new DataCube([time, element], pd) { Unit = "W" });
        var avg = new Complex[n];
        var pAvg = new double[n];
        for (int e = 0; e < n; e++)
        {
            Complex num = 0;
            double den = 0, p = 0;
            for (int t = t0; t < nt; t++)
            {
                num += b[t][e] * Complex.Conjugate(a[t][e]);
                den += a[t][e].Magnitude * a[t][e].Magnitude;
                p += pd[t * n + e];
            }
            avg[e] = den > 0 && (pa?.Lattice.IsOn(e) ?? true) ? num / den : new Complex(double.NaN, double.NaN);
            pAvg[e] = p / Math.Max(1, nt - t0);
        }
        data.Add($"{ArrayGroup}.{ActiveReflectionCube}_avg", new DataCube([element], avg));
        data.Add($"{ArrayGroup}.{ElementDeliveredCube}_avg", new DataCube([element], pAvg) { Unit = "W" });
    }

    /// <summary>What a measurement reads of a run from an envelope: each element's power-weighted average reflection, and the worst VSWR over elements and steps.</summary>
    private sealed class EnvelopeFieldsMetrics(FieldsAnalysis fa, Complex[][] a, Complex[][] b, int t0, PhasedArrayModel? pa) : IFieldMetrics
    {
        public double Evaluate(FieldMetric metric, string? region, double? massGrams)
            => throw new ExpressionException($"'{FieldMetricFunctions.NameOf(metric)}' reads a field set, and fields analysis '{fa.Name}' forms radiated envelopes from an envelope analysis.");

        public double EvaluatePattern(FieldMetric metric, double? thetaDeg, double? phiDeg)
            => throw new ExpressionException($"{metric}({fa.Name}) reads one pattern, and fields analysis '{fa.Name}' forms one per envelope step. Read {fa.Name}.farfield.U, the " +
                                             $"time-averaged pattern, or {fa.Name}.{RadiatedGroup}.EIRP_t_dBm in a named direction.");

        public double EvaluateArray(FieldMetric metric, string? plane, double? theta0Deg, double? phi0Deg)
        {
            if (metric != FieldMetric.ActiveVswrMax)
                throw new ExpressionException($"'{FieldMetricFunctions.NameOf(metric)}' reads one beam, and fields analysis '{fa.Name}' has one per envelope step. Read its radiated group.");
            double worst = 0;
            int n = a[0].Length;
            var rms = new double[n];
            for (int e = 0; e < n; e++) rms[e] = Math.Sqrt(Enumerable.Range(t0, a.Length - t0).Sum(t => a[t][e].Magnitude * a[t][e].Magnitude) / Math.Max(1, a.Length - t0));
            for (int t = t0; t < a.Length; t++)
                for (int e = 0; e < n; e++)
                    if ((pa?.Lattice.IsOn(e) ?? true) && a[t][e].Magnitude > 1e-6 * rms[e] && rms[e] > 0) worst = Math.Max(worst, (b[t][e] / a[t][e]).Magnitude);
            return worst >= 1 ? double.PositiveInfinity : (1 + worst) / (1 - worst);
        }

        public Complex ActiveGamma(int element)
        {
            int n = a[0].Length;
            if (element < 1 || element > n) throw new ExpressionException($"ActiveGamma({fa.Name}, {element}): '{fa.Block}' has elements 1 to {n}.");
            if (pa is not null && !pa.Lattice.IsOn(element - 1)) throw new ExpressionException($"ActiveGamma({fa.Name}, {element}): element {element} is switched off, so it has no active reflection.");
            Complex num = 0;
            double den = 0;
            for (int t = t0; t < a.Length; t++) { num += b[t][element - 1] * Complex.Conjugate(a[t][element - 1]); den += a[t][element - 1].Magnitude * a[t][element - 1].Magnitude; }
            return den > 0 ? num / den : throw new ExpressionException($"ActiveGamma({fa.Name}, {element}): no wave reaches element {element} from the circuit, so it has no active reflection.");
        }
    }
}
