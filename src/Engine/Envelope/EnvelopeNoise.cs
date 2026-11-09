// ================================================================
//  EnvelopeNoise.cs  —  thermal noise and source phase noise in an
//  envelope run (brief NZ-7)
//
//  Thermal (Noise=yes).  Every part the S-parameter noise analysis
//  makes noisy (SParameterNoise.Sources: passive parts at their own
//  temperature, S-parameter blocks with noise data, system amplifiers
//  with NF=) injects, at every step of every product, a complex
//  Gaussian current on its own nodes.  Its correlation is the part's
//  NoiseCurrents C at the product's centre frequency, scaled to the
//  envelope's sample bandwidth fs = 1/h:
//
//    product at f > 0:  I = sqrt(2/h) * L * z,   E|z|^2 = 1,  C = L L^H
//                       so E[I I^H] = 2C/h: a complex envelope of
//                       two-sided density 2C, which is a real signal of
//                       single-sided density C around f.
//    baseband (f = 0):  I = sqrt(1/(2h)) * L * g, g real, Re C = L L^T
//                       a real record of single-sided density Re C
//                       over 0 .. fs/2; C is read at fs/4, the middle.
//
//  A source is noiseless, as in the S-parameter noise analysis, unless
//  its line says Noise=yes: then a P1Tone's, PnTone's or MOD_Source's
//  own impedance is thermal at the source's temperature, 4kT Re(1/Z)
//  across its terminals, Z the band's own (Z[n], n = round(f / fc)).
//
//  The draws are white across one envelope band: a part whose noise
//  changes across the band is taken at the band centre.  A nonlinear
//  device's own noise varies with its state inside a step and is not
//  injected; such devices are named in one warning.
//
//  Phase noise (PhaseNoise= on a source).  The source's drive at a
//  product of frequency f is multiplied by exp(j (f/fs) phi(t)), fs its
//  own fundamental: exp(j phi) at the fundamental, exp(j k phi) at its
//  k-th harmonic.  phi is PhaseNoiseMask.Phase over the signal record,
//  periodic as the record is.  A MOD_Source's drive window is rotated
//  sample by sample; a CW source's static drive is split out of the
//  product's right-hand side and added back as (exp(j n phi) - 1) times
//  its own solution, so phi = 0 changes nothing.
//
//  Every draw is a function of (Seed, the part's path, the product,
//  the ABSOLUTE sample index): parallel segments and their warm-ups
//  draw what one sequential run draws at the same instant.
// ================================================================

using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using RfCore.Signals;

namespace CircuitRF.Engine.Envelope;

internal sealed class EnvelopeNoise
{
    private sealed record Part(int[] Rows, Complex[,] L, ulong Key);

    private readonly Part[][] _byProduct;
    private readonly double[] _scale;
    private readonly bool[] _real;

    private EnvelopeNoise(Part[][] byProduct, double[] scale, bool[] real)
    {
        _byProduct = byProduct;
        _scale = scale;
        _real = real;
    }

    /// <summary>
    /// The thermal injection for products at <paramref name="productHz"/> (|f|; 0 is the baseband) at step
    /// <paramref name="h"/>. Built BEFORE the run replaces any element (a line by its delay, an S-parameter block by its
    /// fit), so each part's noise comes from its own stamp. <paramref name="fcHz"/> is the run's band centre, which
    /// assigns a source's per-band impedance as the source does. Null when nothing is noisy.
    /// </summary>
    public static EnvelopeNoise? Thermal(ElaboratedNetlist nl, AnalysisSettings settings, double[] productHz, double h, ulong seed,
                                         double fcHz)
    {
        double bbHz = 0.25 / h;
        var evalHz = productHz.Select(f => f > 0 ? f : bbHz).ToArray();

        // A composed FET's parasitics are at its temperature at the operating point.
        double[]? dc = null;
        if (SParameterNoise.Table(nl, evalHz).Any(r => r.DeviceOf is not null))
        {
            var r = NonlinearDcEngine.Run(nl, settings);
            if (r.Converged) dc = r.NodeVoltages;
        }
        var devices = new List<string>();
        var sources = SParameterNoise.Sources(nl, evalHz, dc, (row, parts) =>
        {
            if (row.Kind != NoiseModelKind.DeviceSources) return false;
            devices.AddRange(parts.Select(ec => $"{ec.ComponentType}:{ec.InstancePath}"));
            return true;
        });
        if (devices.Count > 0)
            nl.AddWarningOnce(NoiseDiagnostics.EnvelopeDevicesKey, NoiseDiagnostics.EnvelopeDevices(devices).Render());
        var noisySources = nl.Components.Where(ec => ec.NoiseOn && SourceImpedance(ec, 0, fcHz) is not null).ToList();
        if (sources.Count == 0 && noisySources.Count == 0) return null;

        int P = productHz.Length;
        var byProduct = new Part[P][];
        var scale = new double[P];
        var real = new bool[P];
        for (int k = 0; k < P; k++)
        {
            real[k] = !(productHz[k] > 0);
            scale[k] = real[k] ? Math.Sqrt(0.5 / h) : Math.Sqrt(2.0 / h);
            var list = new List<Part>();
            foreach (var src in sources)
            {
                var nc = src.At(2 * Math.PI * evalHz[k]);
                if (nc.Nodes is not { Length: > 0 } nodes) continue;
                int m = nodes.Length;
                var c = new Complex[m, m];
                for (int i = 0; i < m; i++)
                    for (int j = 0; j < m; j++)
                        c[i, j] = real[k] ? nc.C[i, j].Real : nc.C[i, j];
                var l = Cholesky(c);
                if (l is null) continue;
                list.Add(new Part([.. nodes.Select(n => n - 1)], l,
                                  NoiseDraw.Key(seed, NoiseDraw.Hash($"thermal:{src.Label}") ^ NoiseDraw.Mix((ulong)k + 1))));
            }
            foreach (var ec in noisySources)
            {
                // A source's own impedance, thermal at the source's temperature: 4kT·Re(1/Z) across its two terminals.
                var z = SourceImpedance(ec, productHz[k], fcHz)!.Value;
                double kelvin = Temperature.ResolveDeviceC(ec.Parameters, nl.AmbientC) + Temperature.KelvinOffset;
                double g = (1 / z).Real;
                if (!(g > 0)) continue;
                double amp = Math.Sqrt(4 * Boltzmann * kelvin * g);
                int[] nodes = [.. new[] { ec.Nodes[0], ec.Nodes.Length > 1 ? ec.Nodes[1] : 0 }.Where(n => n > 0)];
                if (nodes.Length == 0) continue;
                var l = new Complex[nodes.Length, nodes.Length];
                l[0, 0] = ec.Nodes[0] > 0 ? amp : -amp;                // into the first terminal, out of the second
                if (nodes.Length == 2) l[1, 0] = -amp;
                list.Add(new Part([.. nodes.Select(n => n - 1)], l,
                                  NoiseDraw.Key(seed, NoiseDraw.Hash($"thermal:{ec.InstancePath}") ^ NoiseDraw.Mix((ulong)k + 1))));
            }
            byProduct[k] = [.. list];
        }
        return new EnvelopeNoise(byProduct, scale, real);
    }

    /// <summary>k, exactly (J/K): the S-parameter noise analysis's constant.</summary>
    private const double Boltzmann = 1.380649e-23;

    /// <summary>
    /// The impedance a source with one (P1Tone, PnTone, MOD_Source) presents in the band of a product at
    /// <paramref name="productHz"/>: a P1Tone's or PnTone's Z[n] with n = round-half-up(f / fc), Z[0] at baseband, as they
    /// assign it themselves; a MOD_Source's one Z. Null for anything else — an ideal source has no impedance to be noisy.
    /// </summary>
    internal static Complex? SourceImpedance(ElaboratedComponent ec, double productHz, double fcHz)
    {
        int n = productHz > 0 && fcHz > 0 ? Math.Max(0, (int)Math.Floor(productHz / fcHz + 0.5)) : 0;
        return ec.Model switch
        {
            ModSourceModel m => m.Z,
            P1ToneModel p => p.GetDeclaredZ(n),
            PnToneModel p => p.GetDeclaredZ(n),
            _ => null,
        };
    }

    /// <summary>Adds product <paramref name="k"/>'s noise currents at absolute sample <paramref name="sample"/> into the
    /// right-hand side <paramref name="rhs"/> (node rows; a current injected INTO a node is positive there).</summary>
    public void Add(int k, long sample, Complex[] rhs)
    {
        double s = _scale[k];
        foreach (var p in _byProduct[k])
        {
            int m = p.Rows.Length;
            var z = new Complex[m];
            for (int j = 0; j < m; j++)
                z[j] = _real[k] ? NoiseDraw.Real(p.Key, sample * m + j) : NoiseDraw.Complex(p.Key, sample * m + j);
            for (int i = 0; i < m; i++)
            {
                Complex acc = 0;
                for (int j = 0; j <= i; j++) acc += p.L[i, j] * z[j];
                rhs[p.Rows[i]] += s * acc;
            }
        }
    }

    /// <summary>
    /// The lower factor L of a Hermitian positive semi-definite <paramref name="c"/> (C = L·Lᴴ). A pivot at or below
    /// 1e-12 of the largest diagonal is a direction carrying no noise (two nodes of a resistor see one current), and its
    /// column is left zero. Null when the whole matrix is zero.
    /// </summary>
    internal static Complex[,]? Cholesky(Complex[,] c)
    {
        int m = c.GetLength(0);
        double big = 0;
        for (int i = 0; i < m; i++) big = Math.Max(big, c[i, i].Real);
        if (!(big > 0)) return null;
        double tol = 1e-12 * big;
        var l = new Complex[m, m];
        for (int j = 0; j < m; j++)
        {
            double d = c[j, j].Real;
            for (int p = 0; p < j; p++) d -= (l[j, p] * Complex.Conjugate(l[j, p])).Real;
            if (d <= tol) continue;
            double ljj = Math.Sqrt(d);
            l[j, j] = ljj;
            for (int i = j + 1; i < m; i++)
            {
                Complex acc = c[i, j];
                for (int p = 0; p < j; p++) acc -= l[i, p] * Complex.Conjugate(l[j, p]);
                l[i, j] = acc / ljj;
            }
        }
        return l;
    }
}

/// <summary>Every source's <c>PhaseNoise=</c> mask in an envelope run, as phase records over the signal record.</summary>
internal sealed class EnvelopePhaseNoise
{
    /// <summary>The key a source's phase-noise mask is stated under.</summary>
    public const string Key = "PhaseNoise";
    /// <summary>The key of the level beyond the mask's last point.</summary>
    public const string FloorKey = "PhaseNoiseFloor";

    /// <summary>One CW source with a mask: its component, its own fundamental (Hz) and its phase record.</summary>
    internal sealed record Tone(ElaboratedComponent Ec, IDriveScalable Model, double FundamentalHz, double[] Phi);

    private readonly Dictionary<ModSourceModel, double[]> _mod;
    public IReadOnlyList<Tone> Tones { get; }
    private readonly int _n;

    private EnvelopePhaseNoise(Dictionary<ModSourceModel, double[]> mod, List<Tone> tones, int n)
    {
        _mod = mod;
        Tones = tones;
        _n = n;
    }

    /// <summary>
    /// Every source of <paramref name="nl"/> stating a mask, its phase drawn over <paramref name="recordSamples"/> samples
    /// at <paramref name="fsHz"/>. A mask point above the envelope's bandwidth (fs/2) is reported once per source. Null
    /// when no source states one.
    /// </summary>
    public static EnvelopePhaseNoise? Of(ElaboratedNetlist nl, int recordSamples, double fsHz, ulong seed)
    {
        Dictionary<ModSourceModel, double[]>? mod = null;
        List<Tone>? tones = null;
        foreach (var ec in nl.Components)
        {
            if (Mask(ec) is not { } mask) continue;
            if (mask.FirstOffsetAbove(0.5 * fsHz) is var above && !double.IsNaN(above))
                nl.AddWarningOnce($"envelope-phase-noise-band:{ec.InstancePath}",
                    NoiseDiagnostics.PhaseNoiseBeyondBand($"{ec.ComponentType}:{ec.InstancePath}", Hz(0.5 * fsHz), Hz(above)).Render());
            var phi = mask.Phase(recordSamples, fsHz, NoiseDraw.Key(seed, NoiseDraw.Hash($"phase:{ec.InstancePath}")));
            if (ec.Model is ModSourceModel ms) (mod ??= new())[ms] = phi;
            else if (ec.Model is IDriveScalable d && FundamentalOf(ec) is > 0 and var f0)
                (tones ??= []).Add(new Tone(ec, d, f0, phi));
        }
        return mod is null && tones is null ? null : new EnvelopePhaseNoise(mod ?? new(), tones ?? [], recordSamples);
    }

    /// <summary>The mask <paramref name="ec"/> states, or null. A mask that does not read is an error naming the source.</summary>
    public static PhaseNoiseMask? Mask(ElaboratedComponent ec)
    {
        if (!ec.Parameters.TryGetValue(Key, out var v)) return null;
        string text = v.Kind == Core.Expressions.ValueKind.String ? v.AsString() : v.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        double floor = ec.Parameters.TryGetValue(FloorKey, out var fl) && fl.Kind == Core.Expressions.ValueKind.Real ? fl.AsReal() : double.NaN;
        try { return PhaseNoiseMask.Parse(text, floor); }
        catch (FormatException ex) { throw new InvalidOperationException($"{ec.ComponentType}:{ec.InstancePath}: {ex.Message}"); }
    }

    /// <summary>A CW source's own fundamental: its lowest positive tone frequency.</summary>
    private static double FundamentalOf(ElaboratedComponent ec) => ec.Model switch
    {
        P1ToneModel => ec.Parameters.TryGetValue("Freq", out var f) && f.Kind == Core.Expressions.ValueKind.Real ? f.AsReal() : ((P1ToneModel)ec.Model).FreqHz,
        PnToneModel pn => pn.ToneFreqsHz.Where(x => x > 0).DefaultIfEmpty(0).Min(),
        ToneSourceModelBase t => t.ToneFreqsHz.Where(x => x > 0).DefaultIfEmpty(0).Min(),
        _ => 0,
    };

    /// <summary>Rotates <paramref name="drive"/> (a MOD_Source's window from absolute sample <paramref name="i0"/>) by
    /// that source's phase, when it states a mask.</summary>
    public void Apply(ModSourceModel src, Complex[] drive, long i0)
    {
        if (!_mod.TryGetValue(src, out var phi)) return;
        for (int i = 0; i < drive.Length; i++)
        {
            double p = phi[Wrap(i0 + i)];
            drive[i] *= new Complex(Math.Cos(p), Math.Sin(p));
        }
    }

    /// <summary>(exp(j·n·φ) − 1) for tone <paramref name="j"/> at a product of |f| = <paramref name="productHz"/>, at
    /// absolute sample <paramref name="sample"/>: what multiplies the tone's own solution at that product.</summary>
    public Complex ToneFactor(int j, double productHz, long sample)
    {
        var t = Tones[j];
        double p = productHz / t.FundamentalHz * t.Phi[Wrap(sample)];
        return new Complex(Math.Cos(p) - 1, Math.Sin(p));
    }

    private int Wrap(long i) => (int)(((i % _n) + _n) % _n);

    private static string Hz(double hz) => hz >= 1e9 ? $"{hz / 1e9:G4} GHz" : hz >= 1e6 ? $"{hz / 1e6:G4} MHz"
                                         : hz >= 1e3 ? $"{hz / 1e3:G4} kHz" : $"{hz:G4} Hz";
}
