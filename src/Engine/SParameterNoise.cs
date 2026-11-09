using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.Envelope;
using CSparse.Complex.Factorization;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Engine;

/// <summary>
/// One part's small-signal noise at one frequency: the correlation of the noise currents it injects INTO
/// <paramref name="Nodes"/> (circuit node indices, ground excluded), single-sided and absolute, A²/Hz, with
/// <c>C[a, b] = ⟨j_a·conj(j_b)⟩</c> for <c>Nodes[a]</c>, <c>Nodes[b]</c>. A default value contributes nothing.
/// </summary>
public readonly record struct NoiseCurrents(int[] Nodes, Mat<Complex> C);

/// <summary>One noisy entry of the S-parameter noise analysis: its label on the part axis and its noise currents at ω.</summary>
internal sealed record NoiseSource(string Label, Func<double, NoiseCurrents> At);

/// <summary>What the S-parameter noise analysis does with one part (<see cref="SParameterNoise.Table"/>).</summary>
public enum NoiseModelKind
{
    /// <summary>Thermal, by Bosma's theorem, at <see cref="NoiseModelRow.Kelvin"/>: a passive linear part.</summary>
    Thermal,
    /// <summary>The noise parameters in an S-parameter block's file.</summary>
    FileData,
    /// <summary>The noise sources an external device's model declares, at the DC operating point.</summary>
    DeviceSources,
    /// <summary>A noise figure stated on the part (<c>NF=</c> on the system amplifier): its four noise parameters.</summary>
    Specified,
    /// <summary><c>Noise=no</c> on the part.</summary>
    SwitchedOff,
    /// <summary>No noise, for <see cref="NoiseModelRow.Reason"/>.</summary>
    Noiseless,
}

/// <summary>
/// One row of the noise model table: a part, or a coupled group whose <paramref name="Label"/> joins its members' paths with
/// <c>+</c> (the label it carries on the noise cubes' part axis), its component types joined the same way, and what the
/// noise analysis makes of it. <paramref name="Kelvin"/> is set for <see cref="NoiseModelKind.Thermal"/>; the file and
/// the band of its noise rows (Hz) for <see cref="NoiseModelKind.FileData"/>; the source names for
/// <see cref="NoiseModelKind.DeviceSources"/>; the stated NF (dB, NaN when only NFopt was stated) and the four noise
/// parameters for <see cref="NoiseModelKind.Specified"/>; a reason for the other two. <paramref name="ByDefinition"/> marks a port or an
/// independent source, which the noiseless warning does not name.
/// </summary>
public sealed record NoiseModelRow(string Label, string Types, NoiseModelKind Kind)
{
    public double Kelvin { get; init; } = double.NaN;
    public string? File { get; init; }
    public double BandLoHz { get; init; } = double.NaN;
    public double BandHiHz { get; init; } = double.NaN;
    public IReadOnlyList<string>? SourceNames { get; init; }
    public double NfDb { get; init; } = double.NaN;
    public TwoPortNoise? Specified { get; init; }
    public string? Reason { get; init; }
    public bool ByDefinition { get; init; }
}

/// <summary>
/// The noise half of an S-parameter run (<c>Noise=yes</c>): the noise-wave correlation <c>Cs = ⟨b·bᴴ⟩</c> (W/Hz) of the
/// outgoing waves with every port terminated in its own noiseless Z0 — the network the wave path already factorises.
///
/// <para><b>Port waves.</b> With no drive, port k's current is <c>−V_k/Z0_k</c>, so the Kurokawa wave the S extraction
/// reads is <c>b_k = (V_k − Z0_k*·I_k)/(2√Re Z0_k) = (√Re Z0_k / Z0_k)·V_k</c>. The port-voltage row of <c>M⁻¹</c> is one
/// TRANSPOSED solve per port on the same factorisation, and CSparse's <c>SolveTranspose</c> is Hermitian (Mᴴy = u), so
/// the row is <c>conj(y)</c> (src/Engine/RESOLVED.md). Then <c>Cs = D·Σ H·C·Hᴴ·Dᴴ</c> over the parts' noise currents,
/// <c>H[k, a] = conj(y_k[node_a − 1])</c>, <c>D = diag(√Re Z0/Z0)</c>: N extra back-substitutions per frequency.</para>
///
/// <para><b>Passive parts</b> (<see cref="Bosma"/>) are thermal at their own temperature, a passive S-parameter block
/// included. A two-port S-parameter block with noise data uses that data instead, passive or not, and an external device
/// the noise sources its model declares, at the DC operating point. Everything else — built-in nonlinear devices, active
/// or black-box linear parts, S-parameter blocks with gain and no noise data, external devices that declare no sources —
/// is noiseless and named in one warning; ports and independent sources are noiseless by definition.</para>
/// </summary>
public static class SParameterNoise
{
    /// <summary>The pseudo-inverse's singular-value cut, relative to the largest: what reads as an ideal tie.</summary>
    private const double TieCut = 1e-10;

    /// <summary>
    /// THE SEAM: every noisy part of <paramref name="nl"/> as a labelled function of ω giving its <see cref="NoiseCurrents"/>,
    /// in the order <see cref="Classify"/> lists them. Built once per netlist; a later noise model (a device's own noise
    /// sources, a system block's NF) is one more entry here. Every entry is evaluated BEFORE the frequency's assembly: a
    /// recorded stamp writes the model's branch index, and the assembly then writes the real one back.
    /// </summary>
    internal static List<NoiseSource> Sources(ElaboratedNetlist nl, double[] freqsHz, double[]? dcNodeVoltages)
    {
        var entries = Classify(nl, freqsHz);
        var sources = new List<NoiseSource>();
        var silent  = new List<string>();
        foreach (var (row, parts) in entries)
        {
            switch (row.Kind)
            {
                case NoiseModelKind.FileData:
                    sources.Add(new(row.Label, DataFile(parts[0], (SnpModel)parts[0].Model, nl, freqsHz)));
                    break;
                case NoiseModelKind.DeviceSources:
                    sources.Add(new(row.Label, DeviceSources(parts[0], (ExternalDeviceModel)parts[0].Model, freqsHz, dcNodeVoltages)));
                    break;
                case NoiseModelKind.Specified:
                    sources.Add(new(row.Label, Amplifier(parts[0], (AmplifierModel)parts[0].Model)));
                    break;
                case NoiseModelKind.Thermal:
                    // Non-mutual first, so a Mutual reads its inductors' branches of THIS recording.
                    ElaboratedComponent[] seq = [.. parts.Where(ec => ec.Model is not MutualInductanceModel),
                                                 .. parts.Where(ec => ec.Model is MutualInductanceModel)];
                    double kelvin = row.Kelvin;
                    sources.Add(new(row.Label, w => Bosma(EnvelopeTangentCheck.Recorded(r => { foreach (var ec in seq) StampLinear(ec, r, w); }), kelvin)));
                    break;
                case NoiseModelKind.Noiseless when !row.ByDefinition:
                    silent.AddRange(parts.Select(ec => $"{ec.ComponentType}:{ec.InstancePath}"));
                    break;
            }
        }

        if (silent.Count > 0)
            nl.AddWarningOnce(NoiseDiagnostics.NoiselessKey, NoiseDiagnostics.Noiseless(silent).Render());
        return sources;
    }

    /// <summary>
    /// What the noise analysis makes of every part of <paramref name="nl"/>, each listed once, in netlist order: the one
    /// classification <see cref="Sources"/> builds the noise from and <c>explain --analysis</c> reports, so the two cannot
    /// disagree. A <c>Mutual</c> and the inductors it couples are one entry, labelled with their paths joined by <c>+</c>:
    /// the coupled branches are one lossy network, and <c>Noise=no</c> on any member quiets all of it. Reads no file it does
    /// not have to and starts no device: a device's sources are counted from its descriptor.
    /// </summary>
    public static IReadOnlyList<NoiseModelRow> Table(ElaboratedNetlist nl, double[] freqsHz)
        => [.. Classify(nl, freqsHz).Select(e => e.Row)];

    private static List<(NoiseModelRow Row, List<ElaboratedComponent> Parts)> Classify(ElaboratedNetlist nl, double[] freqsHz)
    {
        var comps = nl.Components;
        int n = comps.Count;

        // A Mutual and the inductors it couples are one lossy network: one recording, in StampAll's two-phase order.
        var group = Enumerable.Range(0, n).ToArray();
        int Find(int i) => group[i] == i ? i : group[i] = Find(group[i]);
        var at = new Dictionary<ComponentModel, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < n; i++) at[comps[i].Model] = i;
        for (int i = 0; i < n; i++)
            if (comps[i].Model is MutualInductanceModel k)
                foreach (var l in new[] { k.Coupled.First, k.Coupled.Second })
                    if (l is ComponentModel m && at.TryGetValue(m, out int j)) group[Find(j)] = Find(i);

        // Entries in the order their first member appears; a part noiseless by definition is never grouped.
        var members = new Dictionary<int, List<ElaboratedComponent>>();
        var order   = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int g = IsNoiselessByDefinition(comps[i]) ? -1 - i : Find(i);
            if (!members.TryGetValue(g, out var list)) { members[g] = list = []; order.Add(g); }
            list.Add(comps[i]);
        }

        var result = new List<(NoiseModelRow, List<ElaboratedComponent>)>(order.Count);
        foreach (int g in order)
        {
            var parts = members[g];
            string label = string.Join("+", parts.Select(ec => ec.InstancePath));
            string types = string.Join("+", parts.Select(ec => ec.ComponentType));
            NoiseModelRow Row(NoiseModelKind kind) => new(label, types, kind);

            if (g < 0)
            {
                result.Add((Row(NoiseModelKind.Noiseless) with { ByDefinition = true, Reason = WhyNoiselessByDefinition(parts[0]) }, parts));
                continue;
            }
            // The user's statement, not a gap: listed, never warned about.
            if (parts.FirstOrDefault(ec => ec.NoiseOff) is { } off)
            {
                result.Add((Row(NoiseModelKind.SwitchedOff) with
                {
                    Reason = parts.Count == 1 ? "Noise=no" : $"Noise=no on {off.InstancePath}, which quiets the coupled group",
                }, parts));
                continue;
            }
            // An S-parameter block with noise data is noisy by its own statement, passive or not.
            if (parts is [{ Model: SnpModel snp } block] && snp.NoiseData(block) is { Count: > 0 } rows)
            {
                result.Add((Row(NoiseModelKind.FileData) with
                {
                    File = snp.FilePath, BandLoHz = rows[0].Hz, BandHiHz = rows[^1].Hz,
                }, parts));
                continue;
            }
            // A system amplifier with a stated noise figure: its four noise parameters, through its own Y.
            if (parts is [{ Model: AmplifierModel { NoiseParameters: { } spec, HasAdmittance: true } amp }])
            {
                result.Add((Row(NoiseModelKind.Specified) with
                {
                    NfDb = amp.NoiseSpec!.NfDb ?? double.NaN, Specified = spec,
                }, parts));
                continue;
            }
            // An external device whose model declares noise sources; one that declares none is named below.
            if (parts is [{ Model: ExternalDeviceModel ed }] && ed.NoiseSources.Count > 0)
            {
                result.Add((Row(NoiseModelKind.DeviceSources) with { SourceNames = [.. ed.NoiseSources.Select(x => x.Name)] }, parts));
                continue;
            }
            if (parts.FirstOrDefault(ec => !IsThermal(ec, freqsHz)) is { } loud)
            {
                result.Add((Row(NoiseModelKind.Noiseless) with { Reason = WhyNoNoiseModel(loud, freqsHz) }, parts));
                continue;
            }
            // A coupled group with members at different temperatures is taken at the hottest one; exact would need
            // each inductor's loss separated from the coupling, which the stamp does not expose.
            double kelvin = parts.Max(ec => Temperature.ResolveDeviceC(ec.Parameters, nl.AmbientC)) + Temperature.KelvinOffset;
            result.Add((Row(NoiseModelKind.Thermal) with { Kelvin = kelvin }, parts));
        }
        return result;
    }

    /// <summary>Why a part that is noiseless by definition is, in a few words.</summary>
    private static string WhyNoiselessByDefinition(ElaboratedComponent ec) => ec.Model switch
    {
        PortModel or TermModel => "a port: the run's own noiseless termination",
        _                      => "an independent source, zeroed in a small-signal run",
    };

    /// <summary>Why a part the analysis would like to give noise to has none.</summary>
    private static string WhyNoNoiseModel(ElaboratedComponent ec, double[] freqsHz) => ec.Model switch
    {
        ExternalDeviceModel => "its model declares no noise sources",
        AmplifierModel { NoiseParameters: not null } =>
            "an amplifier whose reverse loop gain is unity has no admittance to write its noise through",
        AmplifierModel => "a system amplifier with no NF=",
        MixerModel mx => "a mixer has no conversion path without an LO, so it stays out of the small-signal noise as it does " +
                         "out of the small-signal S" + (mx.NoiseFigureDb is { } nf
                             ? $"; its NF={nf.ToString("G6", System.Globalization.CultureInfo.InvariantCulture)} dB " +
                               $"({(mx.NoiseFigureType == MixerNoiseFigureType.Dsb ? "DSB" : "SSB")}) is for harmonic-balance and envelope noise"
                             : ""),
        IdealSBlockModel when ec.ActivityFor(freqsHz) == Activity.BlackBox =>
            $"a system block whose S-matrix is not passive ({ec.Model.PassivationNote})",
        SnpModel            => "an S-parameter block that is not passive and has no noise data in its file",
        _ when ec.Model.Kind != ModelKind.Linear => "a built-in nonlinear model, which has no noise sources",
        _ => ec.ActivityFor(freqsHz) switch
        {
            Activity.ActiveExact or Activity.ActiveUserScaled => "a linear part with a dependent source or a negative resistance",
            _                                                 => "a linear part whose activity cannot be measured",
        },
    };

    /// <summary>
    /// Whether <see cref="Elaborator.NoiseParamName"/> on this part can ever do anything: false for a port, an independent
    /// source and an ideal element (a probe, a short), which have no noise to switch off — what <c>check</c> reports as INFO.
    /// </summary>
    public static bool CanBeNoisy(ElaboratedComponent ec) =>
        !IsNoiselessByDefinition(ec) && ec.Model is not (IProbeModel or ShortModel or WSProbeModel);

    /// <summary>Ports (the run's noiseless terminations, or inert when buried) and independent sources (zeroed).</summary>
    private static bool IsNoiselessByDefinition(ElaboratedComponent ec) =>
        ec.Model is PortModel or TermModel or VdcModel or IDriveScalable;   // IDriveScalable: P1Tone, ModSource, PnTone, tones

    /// <summary>A linear part with no dependent source: thermal noise at its own temperature. A passive S-parameter block
    /// with no noise data is one (its activity is measured on its file), and so is a passive system block carrying a
    /// passive intermod: the overlay is a nonlinearity and adds no noise, so the block is thermal on its linear S.</summary>
    private static bool IsThermal(ElaboratedComponent ec, double[] freqsHz) =>
        (ec.Model.Kind == ModelKind.Linear || ec.Model is IdealSBlockModel) && ec.ActivityFor(freqsHz) == Activity.Passive;

    /// <summary>The linear stamp a thermal part's noise is recorded from: the part's own, or a system block's linear S when
    /// a passive intermod makes it nonlinear (its <c>Stamp</c> then stands down). A system block allocates branch unknowns,
    /// so it carries no multiplier, and calling the model directly loses none.</summary>
    private static void StampLinear(ElaboratedComponent ec, IMnaContext r, double w)
    {
        if (ec.Model is IdealSBlockModel { Kind: ModelKind.Nonlinear } block) block.StampLinearS(r, ec, w);
        else ec.Stamp(r, w);
    }

    /// <summary>
    /// A system amplifier's stated noise (<see cref="AmplifierModel.NoiseParameters"/>): the chain correlation of its four
    /// noise parameters as port currents, <c>C_Y = T⁻¹·C_A·T⁻ᴴ</c>, with Y the power-wave admittance of the S it stamps at
    /// its own reference impedances — the same route an S-parameter block's file noise takes (<see cref="DataFile"/>), so
    /// the block is exactly a two-port with noise data. Each port's current is across its own ± pair (the family's 2N-net
    /// convention). Frequency-flat, as the block is: computed once.
    /// </summary>
    private static Func<double, NoiseCurrents> Amplifier(ElaboratedComponent ec, AmplifierModel amp)
    {
        var s = new Mat<Complex>(2, 2);
        var sAt = amp.SAt(0.0);
        for (int i = 0; i < 2; i++)
            for (int j = 0; j < 2; j++)
                s[i, j] = sAt[i, j];
        var y  = NoiseMetrics.PowerWaveAdmittance(s, [.. amp.Z0At(0.0)]);
        var cy = NoiseMetrics.AdmittanceFromChain(NoiseMetrics.ChainCorrelation(amp.NoiseParameters!.Value), y);
        var currents = AcrossPairs(new Complex(ec.Multiplicity, 0) * cy, [(ec.Nodes[0], ec.Nodes[1]), (ec.Nodes[2], ec.Nodes[3])]);
        return _ => currents;
    }

    /// <summary>
    /// Port currents' correlation <paramref name="cy"/> as node injections when each port has its own reference: port k's
    /// current enters at <c>Plus</c> and leaves at <c>Minus</c>. Ground rows are dropped.
    /// </summary>
    private static NoiseCurrents AcrossPairs(Mat<Complex> cy, (int Plus, int Minus)[] ports)
    {
        int n = ports.Length;
        var nodes = ports.SelectMany(p => new[] { p.Plus, p.Minus }).Where(x => x > 0).Distinct().ToArray();
        if (nodes.Length == 0) return default;
        var t = new Mat<Complex>(nodes.Length, n);
        for (int k = 0; k < n; k++)
        {
            int a = Array.IndexOf(nodes, ports[k].Plus), b = Array.IndexOf(nodes, ports[k].Minus);
            if (a >= 0) t[a, k] += 1;
            if (b >= 0) t[b, k] -= 1;
        }
        return new NoiseCurrents(nodes, t * cy * t.ConjugateTranspose());
    }

    /// <summary>
    /// An external device's own noise sources (<see cref="ExternalDeviceModel.ReadNoiseAt"/>), read once, here, at the DC
    /// operating point the run linearises the device about (<see cref="SParameterEngine.BuildBias"/>) for every sweep
    /// frequency. Source s between device nodes (a, b) with density p is a current noise source: on circuit nodes
    /// (na, nb) it adds <c>p·[[1, −1], [−1, 1]]</c>, or p on na alone when b is ground. Device node k is circuit node
    /// <c>Nodes[2k]</c>, internal nodes included; a node collapsed to ground is 0 and drops out, and a pair collapsed
    /// onto one node cancels. Times the multiplicity, m independent copies; the provider never applies it.
    /// </summary>
    private static Func<double, NoiseCurrents> DeviceSources(
        ElaboratedComponent ec, ExternalDeviceModel ed, double[] freqsHz, double[]? dcNodeVoltages)
    {
        var psd = ed.ReadNoiseAt(SParameterEngine.BuildBias(ec, dcNodeVoltages).Voltages, freqsHz)!;
        int Circuit(int k) => k < 0 || 2 * k >= ec.Nodes.Length ? 0 : ec.Nodes[2 * k];
        var pairs = ed.NoiseSources.Select(s => (A: Circuit(s.NodeA), B: Circuit(s.NodeB))).ToArray();
        int[] nodes = [.. pairs.SelectMany(p => new[] { p.A, p.B }).Where(n => n > 0).Distinct()];
        if (nodes.Length == 0) return _ => default;

        // Keyed by ω exactly as RunRange computes it (2π·f), so the lookup is an equality of the same double.
        var at = new Dictionary<double, NoiseCurrents>();
        for (int k = 0; k < freqsHz.Length; k++)
        {
            var c = new Mat<Complex>(nodes.Length, nodes.Length);
            for (int s = 0; s < pairs.Length; s++)
            {
                double p = ec.Multiplicity * psd[k, s];
                int a = Array.IndexOf(nodes, pairs[s].A), b = Array.IndexOf(nodes, pairs[s].B);
                if (a >= 0) c[a, a] += p;
                if (b >= 0) c[b, b] += p;
                if (a >= 0 && b >= 0) { c[a, b] -= p; c[b, a] -= p; }
            }
            at[2.0 * Math.PI * freqsHz[k]] = new NoiseCurrents(nodes, c);
        }
        return w => at[w];
    }

    /// <summary>
    /// A two-port S-parameter block's own noise data (<see cref="SnpModel.NoiseAt"/>): the chain correlation of its noise
    /// parameters, as absolute densities at T0 whatever the ambient, turned into the short-circuit currents across its
    /// ports, <c>C_Y = T⁻¹·C_A·T⁻ᴴ</c>, with Y the power-wave admittance of the S it stamps at the file's R, times its
    /// multiplicity (m independent copies in parallel). Rows outside the sweep's band and data that is not physical are
    /// each reported once, here, and the data is used as given.
    /// </summary>
    private static Func<double, NoiseCurrents> DataFile(
        ElaboratedComponent ec, SnpModel snp, ElaboratedNetlist nl, double[] freqsHz)
    {
        var rows = snp.NoiseData(ec);
        string who = $"{ec.ComponentType}:{ec.InstancePath}";
        double lo = rows[0].Hz, hi = rows[^1].Hz;
        if (freqsHz.Length > 0 && (freqsHz.Min() < lo || freqsHz.Max() > hi))
            nl.AddWarningOnce($"{NoiseDiagnostics.DataBandKey}:{ec.InstancePath}",
                              NoiseDiagnostics.DataOutsideBand(who, lo, hi, freqsHz.Min(), freqsHz.Max()).Render());
        // Measured data rounded to 0.01 dB misses the bound by up to ~1e-3 in the factor; that is not worth a warning.
        var bad = rows.Where(r => !NoiseMetrics.IsRealizable(r.Noise, 1e-3)).ToList();
        if (bad.Count > 0)
            nl.AddWarningOnce($"{NoiseDiagnostics.DataUnphysicalKey}:{ec.InstancePath}",
                              NoiseDiagnostics.DataUnphysical(who, bad[0].Hz, bad.Count, rows.Count).Render());

        int[] ports = [ec.Nodes[0], ec.Nodes[1]];
        return w =>
        {
            double hz = w / (2 * Math.PI);
            var p  = snp.NoiseAt(ec, hz)!.Value;
            var y  = RFNetwork.SToY(snp.SAt(ec, hz), new Complex(p.R, 0));
            var cy = NoiseMetrics.AdmittanceFromChain(NoiseMetrics.ChainCorrelation(p), y);
            return AcrossPorts(new Complex(ec.Multiplicity, 0) * cy, ports, ec.ReferenceNode);
        };
    }

    /// <summary>
    /// Port currents' correlation <paramref name="cy"/> as node injections: port k's current enters at
    /// <paramref name="ports"/>[k] and leaves at <paramref name="reference"/>, so the reference carries minus their sum.
    /// Ground rows are dropped (<see cref="NoiseCurrents"/> excludes ground).
    /// </summary>
    private static NoiseCurrents AcrossPorts(Mat<Complex> cy, int[] ports, int reference)
    {
        int n = ports.Length;
        int[] nodes = [.. ports, reference];
        int[] kept = [.. Enumerable.Range(0, n + 1).Where(i => nodes[i] > 0)];
        if (kept.Length == 0) return default;
        var t = new Mat<Complex>(kept.Length, n);
        for (int r = 0; r < kept.Length; r++)
            for (int k = 0; k < n; k++)
                t[r, k] = kept[r] == n ? -1 : kept[r] == k ? 1 : 0;
        return new NoiseCurrents([.. kept.Select(i => nodes[i])], t * cy * t.ConjugateTranspose());
    }

    /// <summary>
    /// Bosma/Twiss for one recorded stamp at <paramref name="kelvin"/>. Every node is tied to ground through R
    /// (<see cref="EnvelopeTangentCheck.NodeSReference"/>); the terminated node impedances <c>Z_t</c> come from the whole
    /// recorded system, so internal branches are eliminated. The terminated network at T has open-circuit noise
    /// <c>2kT(Z_t + Z_tᴴ)</c>, of which the terminations' own <c>4kT/R</c> currents give <c>(4kT/R)·Z_t·Z_tᴴ</c>; the rest
    /// is the part's, <c>C_V = kT[2(Z_t + Z_tᴴ) − (4/R)·Z_t·Z_tᴴ]</c>, and its node currents are
    /// <c>C_j = Z_t⁺·C_V·Z_t⁺ᴴ</c>. Where the part has an admittance this is <c>2kT(Y + Yᴴ)</c>; where an ideal tie
    /// (a short, an ideal transformer) leaves <c>Z_t</c> singular, the pseudo-inverse drops only injections that drive no
    /// node voltage, which the network cannot see either. A lossless part comes out at round-off.
    /// </summary>
    internal static NoiseCurrents Bosma(Dictionary<long, Complex> stamp, double kelvin)
    {
        var ids = EnvelopeTangentCheck.Ids(stamp.Keys);
        int[] rows = [.. Enumerable.Range(0, ids.Count).Where(i => ids[i] < EnvelopeTangentCheck.Recorder.B)];
        if (rows.Length == 0) return default;

        var zFull = EnvelopeTangentCheck.Terminated(stamp, ids);
        int m = rows.Length;
        var zt = new Mat<Complex>(m, m);
        for (int i = 0; i < m; i++)
            for (int j = 0; j < m; j++)
                zt[i, j] = zFull[rows[i], rows[j]];

        double r  = EnvelopeTangentCheck.NodeSReference;
        double kt = NoiseMetrics.Boltzmann * kelvin;
        var zh = zt.ConjugateTranspose();
        var cv = new Complex(2 * kt, 0) * (zt + zh) - new Complex(4 * kt / r, 0) * (zt * zh);
        var p  = PseudoInverse(zt);
        return new NoiseCurrents([.. rows.Select(i => (int)ids[i])], p * cv * p.ConjugateTranspose());
    }

    /// <summary>Moore–Penrose inverse through the SVD, singular values below <see cref="TieCut"/> of the largest dropped.</summary>
    private static Mat<Complex> PseudoInverse(Mat<Complex> a)
    {
        var svd = a.Svd();
        int n = a.RowCount;
        double cut = TieCut * svd.S[0];
        var p = new Mat<Complex>(n, n);
        for (int k = 0; k < n; k++)
        {
            double s = svd.S[k];
            if (!(s > cut)) continue;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    p[i, j] += Complex.Conjugate(svd.VT[k, i]) * Complex.Conjugate(svd.U[j, k]) / s;
        }
        return p;
    }

    /// <summary>
    /// <c>Cs</c> (W/Hz) against the wave path's own factorisation <paramref name="lu"/> of the terminated network:
    /// one transposed solve per port, then every part's currents mapped onto the port waves. With <paramref name="parts"/>,
    /// each entry's own share <c>D·H·C_part·Hᴴ·Dᴴ</c> is kept there too, in <paramref name="currents"/>' order: the very
    /// terms <c>Cs</c> sums, so their sum is <c>Cs</c> bit for bit, at no extra solve.
    /// </summary>
    internal static Mat<Complex> Waves(
        SparseLU lu, int size, IReadOnlyList<(int Node0, int Node1, Complex Z0)> ports, NoiseCurrents[] currents,
        Mat<Complex>[]? parts = null)
    {
        int nPorts = ports.Count;
        var rows = new Complex[nPorts][];
        var u = new Complex[size];
        for (int k = 0; k < nPorts; k++)
        {
            Array.Clear(u);
            if (ports[k].Node0 > 0) u[ports[k].Node0 - 1] += 1;
            if (ports[k].Node1 > 0) u[ports[k].Node1 - 1] -= 1;
            rows[k] = new Complex[size];
            lu.SolveTranspose(u, rows[k]);          // Mᴴ·y = u, so row k of M⁻¹ is conj(y)
        }

        var cs = new Mat<Complex>(nPorts, nPorts);
        for (int e = 0; e < currents.Length; e++)
        {
            var c = currents[e];
            if (parts is not null) parts[e] = new Mat<Complex>(nPorts, nPorts);
            if (c.Nodes is not { Length: > 0 } nodes) continue;
            var h = new Mat<Complex>(nPorts, nodes.Length);
            for (int k = 0; k < nPorts; k++)
            {
                Complex d = Math.Sqrt(ports[k].Z0.Real) / ports[k].Z0;
                for (int a = 0; a < nodes.Length; a++)
                    h[k, a] = d * Complex.Conjugate(rows[k][nodes[a] - 1]);
            }
            var share = h * c.C * h.ConjugateTranspose();
            cs += share;
            if (parts is not null) parts[e] = share;
        }
        return cs;
    }

    /// <summary>
    /// The noise cubes: <c>Cs</c> [freq, i, j] on the S cube's own axes (W/Hz), and for a two-port, each [freq], with port
    /// 1 the input and the names Qucs and ngspice use where they have one: <c>NF</c> (dB, port 1 driven from its Z0) and
    /// <c>Fn</c>, the same noise factor linear (Qucs's <c>F</c>; <c>SP1.F("probe")</c> is already the WSProbe's return
    /// difference); <c>NFmin</c> (dB) and <c>Fmin</c>; <c>Te</c> and <c>Tmin</c> (K), the equivalent input noise
    /// temperatures T0·(F − 1) and T0·(Fmin − 1); <c>Rn</c> (Ω); <c>Sopt</c>, Γopt on R = Re Z0 of port 1, and
    /// <c>Zopt</c> = R·(1 + Sopt)/(1 − Sopt) (Ω). <see cref="DataSetBuilder.ToSnp(DataSet)"/> reads the Touchstone noise
    /// block from NFmin, Sopt and Rn.
    ///
    /// <para><b>Each part's share</b>, on a <c>part</c> axis labelled by instance path in <see cref="Sources"/>' order (a
    /// coupled group's members joined by <c>+</c>), when any part is noisy: <c>NCs</c> [freq, part, i, j] (W/Hz), each
    /// part's term of <c>Cs</c>, only when <paramref name="o"/> was asked for it (<c>NoiseContrib=yes</c>: it is the
    /// cube whose size grows with parts × frequencies × ports²); and for a two-port always <c>NFc</c> [freq, part], the
    /// part's own output noise at port 2 over the source's, <c>NCs[f, p, 2, 2]/(kT0·|S21|²)</c>, so that
    /// <c>F − 1 = Σ NFc</c>, and <c>NFpct</c>, the same as a percentage of <c>F − 1</c>.</para>
    /// </summary>
    internal static void AddCubes(DataSet ds, double[] freqsHz, Mat<Complex>[] s, NoiseOutput o, Complex[] z0)
    {
        var cs = o.Cs;
        int n = z0.Length, nf = freqsHz.Length;
        var data = new Complex[nf * n * n];
        for (int fi = 0; fi < nf; fi++)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    data[(fi * n + i) * n + j] = cs[fi][i, j];
        ds.Add("Cs", new DataCube([.. ds["S"].Axes], data) { Unit = "W/Hz" });

        int np = o.Labels.Length;
        Axis Part() => new("part", [.. Enumerable.Range(1, np).Select(k => (double)k)], "", o.Labels);
        if (o.Parts is { } parts && o.WriteNcs && np > 0)
        {
            var nc = new Complex[nf * np * n * n];
            for (int fi = 0; fi < nf; fi++)
                for (int p = 0; p < np; p++)
                    for (int i = 0; i < n; i++)
                        for (int j = 0; j < n; j++)
                            nc[((fi * np + p) * n + i) * n + j] = parts[fi][p][i, j];
            var axes = ds["S"].Axes;
            ds.Add("NCs", new DataCube([axes[0], Part(), axes[1], axes[2]], nc) { Unit = "W/Hz" });
        }
        if (n != 2) return;

        double r = z0[0].Real;
        double[] nfDb = new double[nf], f = new double[nf], nfMin = new double[nf], fMin = new double[nf],
                 te = new double[nf], tMin = new double[nf], rn = new double[nf];
        Complex[] sOpt = new Complex[nf], zOpt = new Complex[nf];
        for (int fi = 0; fi < nf; fi++)
        {
            var p = NoiseMetrics.FromWaves(s[fi], cs[fi], z0, r);
            f[fi]     = NoiseMetrics.NoiseFactorAtZ0(s[fi], cs[fi]);
            nfDb[fi]  = 10 * Math.Log10(f[fi]);
            te[fi]    = NoiseMetrics.T0 * (f[fi] - 1);
            fMin[fi]  = p.Fmin;
            nfMin[fi] = p.NFminDb;
            tMin[fi]  = NoiseMetrics.T0 * (p.Fmin - 1);
            rn[fi]    = p.Rn;
            sOpt[fi]  = p.GammaOpt;
            zOpt[fi]  = r * (1 + p.GammaOpt) / (1 - p.GammaOpt);
        }
        Axis Freq() => new("freq", (double[])freqsHz.Clone(), "Hz");
        const string One = RfCore.Export.ResultUnits.Dimensionless;
        ds.Add("NF",    new DataCube([Freq()], nfDb)  { Unit = "dB" });
        ds.Add("Fn",    new DataCube([Freq()], f)     { Unit = One });
        ds.Add("NFmin", new DataCube([Freq()], nfMin) { Unit = "dB" });
        ds.Add("Fmin",  new DataCube([Freq()], fMin)  { Unit = One });
        ds.Add("Te",    new DataCube([Freq()], te)    { Unit = "K" });
        ds.Add("Tmin",  new DataCube([Freq()], tMin)  { Unit = "K" });
        ds.Add("Rn",    new DataCube([Freq()], rn)    { Unit = "Ohm" });
        ds.Add("Sopt",  new DataCube([Freq()], sOpt)  { Unit = One });
        ds.Add("Zopt",  new DataCube([Freq()], zOpt)  { Unit = "Ohm" });

        if (o.Parts is not { } shares || np == 0) return;
        var nfc = new double[nf * np];
        var pct = new double[nf * np];
        for (int fi = 0; fi < nf; fi++)
        {
            // The source's available noise kT0 reaches port 2 as kT0·|S21|²: F − 1 is Cs22 over it, and so is each share.
            Complex s21 = s[fi][1, 0];
            double source = NoiseMetrics.KT0 * (s21.Real * s21.Real + s21.Imaginary * s21.Imaginary);
            for (int p = 0; p < np; p++)
            {
                double c = shares[fi][p][1, 1].Real / source;
                nfc[fi * np + p] = c;
                pct[fi * np + p] = 100 * c / (f[fi] - 1);
            }
        }
        ds.Add("NFc",   new DataCube([Freq(), Part()], nfc) { Unit = One });
        ds.Add("NFpct", new DataCube([Freq(), Part()], pct) { Unit = "%" });
    }

    /// <summary>
    /// The size in bytes of <c>NCs</c> for <paramref name="parts"/> noisy parts, <paramref name="ports"/> ports and
    /// <paramref name="freqs"/> frequencies: one complex double per entry. What <c>explain --analysis</c> states before a
    /// <c>NoiseContrib=yes</c> run writes it.
    /// </summary>
    public static long ContributionBytes(int freqs, int parts, int ports) => 16L * freqs * parts * ports * ports;
}

/// <summary>
/// A run's noise result, filled per frequency (each index by exactly one worker): <c>Cs</c>, and each noisy part's share of
/// it when the cubes need them — <c>NCs</c> on request, <c>NFc</c>/<c>NFpct</c> for any two-port.
/// </summary>
internal sealed class NoiseOutput(int freqCount, string[] labels, bool keepParts, bool writeNcs)
{
    public Mat<Complex>[]    Cs     { get; } = new Mat<Complex>[freqCount];
    public Mat<Complex>[][]? Parts  { get; } = keepParts && labels.Length > 0 ? new Mat<Complex>[freqCount][] : null;
    public string[]          Labels { get; } = labels;
    public bool              WriteNcs { get; } = writeNcs;
}
