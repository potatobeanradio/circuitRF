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
/// included. A two-port S-parameter block with noise data uses that data instead, passive or not. Everything else —
/// nonlinear devices, active or black-box linear parts, external devices, S-parameter blocks with gain and no noise
/// data — is noiseless and named in one warning; ports and independent sources are noiseless by definition.</para>
/// </summary>
internal static class SParameterNoise
{
    /// <summary>The pseudo-inverse's singular-value cut, relative to the largest: what reads as an ideal tie.</summary>
    private const double TieCut = 1e-10;

    /// <summary>
    /// THE SEAM: every noisy part of <paramref name="nl"/> as a function of ω giving its <see cref="NoiseCurrents"/>.
    /// Built once per netlist; a later noise model (data-file noise, a device's own noise sources) is one more entry
    /// here. Every entry is evaluated BEFORE the frequency's assembly: a recorded stamp writes the model's branch index,
    /// and the assembly then writes the real one back.
    /// </summary>
    internal static List<Func<double, NoiseCurrents>> Sources(ElaboratedNetlist nl, double[] freqsHz)
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

        var members = new Dictionary<int, List<ElaboratedComponent>>();
        var order   = new List<int>();
        var silent  = new List<string>();
        for (int i = 0; i < n; i++)
        {
            var ec = comps[i];
            if (IsNoiselessByDefinition(ec)) continue;
            int g = Find(i);
            if (!members.TryGetValue(g, out var list)) { members[g] = list = []; order.Add(g); }
            list.Add(ec);
        }

        var sources = new List<Func<double, NoiseCurrents>>();
        foreach (int g in order)
        {
            var parts = members[g];
            // An S-parameter block with noise data is noisy by its own statement, passive or not.
            if (parts is [{ Model: SnpModel snp } block] && snp.NoiseData(block).Count > 0)
            {
                sources.Add(DataFile(block, snp, nl, freqsHz));
                continue;
            }
            if (!parts.All(ec => IsThermal(ec, freqsHz)))
            {
                silent.AddRange(parts.Select(ec => $"{ec.ComponentType}:{ec.InstancePath}"));
                continue;
            }
            // Non-mutual first, so a Mutual reads its inductors' branches of THIS recording.
            ElaboratedComponent[] seq = [.. parts.Where(ec => ec.Model is not MutualInductanceModel),
                                         .. parts.Where(ec => ec.Model is MutualInductanceModel)];
            // ponytail: a coupled group with members at different temperatures is taken at the hottest one; exact
            // would need each inductor's loss separated from the coupling, which the stamp does not expose.
            double kelvin = seq.Max(ec => Temperature.ResolveDeviceC(ec.Parameters, nl.AmbientC)) + Temperature.KelvinOffset;
            sources.Add(w => Bosma(EnvelopeTangentCheck.Recorded(r => { foreach (var ec in seq) ec.Stamp(r, w); }), kelvin));
        }

        if (silent.Count > 0)
            nl.AddWarningOnce(NoiseDiagnostics.NoiselessKey, NoiseDiagnostics.Noiseless(silent).Render());
        return sources;
    }

    /// <summary>Ports (the run's noiseless terminations, or inert when buried) and independent sources (zeroed).</summary>
    private static bool IsNoiselessByDefinition(ElaboratedComponent ec) =>
        ec.Model is PortModel or TermModel or VdcModel or IDriveScalable;   // IDriveScalable: P1Tone, ModSource, PnTone, tones

    /// <summary>A linear part with no dependent source: thermal noise at its own temperature. A passive S-parameter block
    /// with no noise data is one (its activity is measured on its file).</summary>
    private static bool IsThermal(ElaboratedComponent ec, double[] freqsHz) =>
        ec.Model.Kind == ModelKind.Linear
        && ec.Model is not ExternalDeviceModel
        && ec.ActivityFor(freqsHz) == Activity.Passive;

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
    /// one transposed solve per port, then every part's currents mapped onto the port waves.
    /// </summary>
    internal static Mat<Complex> Waves(
        SparseLU lu, int size, IReadOnlyList<(int Node0, int Node1, Complex Z0)> ports, NoiseCurrents[] currents)
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
        foreach (var c in currents)
        {
            if (c.Nodes is not { Length: > 0 } nodes) continue;
            var h = new Mat<Complex>(nPorts, nodes.Length);
            for (int k = 0; k < nPorts; k++)
            {
                Complex d = Math.Sqrt(ports[k].Z0.Real) / ports[k].Z0;
                for (int a = 0; a < nodes.Length; a++)
                    h[k, a] = d * Complex.Conjugate(rows[k][nodes[a] - 1]);
            }
            cs += h * c.C * h.ConjugateTranspose();
        }
        return cs;
    }

    /// <summary>
    /// The noise cubes: <c>Cs</c> [freq, i, j] on the S cube's own axes (W/Hz), and for a two-port <c>NF</c> (dB, port 1
    /// driven from its Z0), <c>NFmin</c> (dB), <c>Rn</c> (Ω) and <c>GammaOpt</c> (referenced to Re Z0 of port 1), each [freq].
    /// The names are what <see cref="DataSetBuilder.ToSnp(DataSet)"/> reads the Touchstone noise block from.
    /// </summary>
    internal static void AddCubes(DataSet ds, double[] freqsHz, Mat<Complex>[] s, Mat<Complex>[] cs, Complex[] z0)
    {
        int n = z0.Length, nf = freqsHz.Length;
        var data = new Complex[nf * n * n];
        for (int fi = 0; fi < nf; fi++)
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    data[(fi * n + i) * n + j] = cs[fi][i, j];
        ds.Add("Cs", new DataCube([.. ds["S"].Axes], data) { Unit = "W/Hz" });
        if (n != 2) return;

        var nfDb = new double[nf];
        var nfMin = new double[nf];
        var rn = new double[nf];
        var gOpt = new Complex[nf];
        for (int fi = 0; fi < nf; fi++)
        {
            var p = NoiseMetrics.FromWaves(s[fi], cs[fi], z0, z0[0].Real);
            nfDb[fi]  = 10 * Math.Log10(NoiseMetrics.NoiseFactorAtZ0(s[fi], cs[fi]));
            nfMin[fi] = p.NFminDb;
            rn[fi]    = p.Rn;
            gOpt[fi]  = p.GammaOpt;
        }
        Axis Freq() => new("freq", (double[])freqsHz.Clone(), "Hz");
        ds.Add("NF",       new DataCube([Freq()], nfDb)  { Unit = "dB" });
        ds.Add("NFmin",    new DataCube([Freq()], nfMin) { Unit = "dB" });
        ds.Add("Rn",       new DataCube([Freq()], rn)    { Unit = "Ohm" });
        ds.Add("GammaOpt", new DataCube([Freq()], gOpt)  { Unit = RfCore.Export.ResultUnits.Dimensionless });
    }
}
