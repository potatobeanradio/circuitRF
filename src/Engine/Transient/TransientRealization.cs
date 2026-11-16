// ================================================================
//  TransientRealization.cs  —  every element a transient run cannot stamp as
//  fixed G and C, replaced for the run by one it can, or refused by name
//  (docs/design/transient.md §5.3-§5.5; brief-trn-4)
//
//  - A two-conductor line (IUniformLine) whose delay alone reproduces its own
//    model within LineTolerance (|ΔS| in 50 Ω, DC to f_max) is stepped as that
//    delay (TransientLines). That covers every lossless, non-dispersive line: an
//    ideal TLIN, a physical TLIN with no loss, a lossless stripline, the ideal
//    Delay block.
//  - A lossy or dispersive line (brief TRN-4b) is the same delay with a rational
//    residual H~(f) = e^(−γl)·e^(j2πfτ) fitted directly, phase included, τ the
//    smallest phase delay in band less 1.5 periods of f_max (a residual at the
//    smallest delay is barely causal and fits badly), the order the lowest whose
//    realization meets 3e-4 among those with |H~| ≤ 1, accepted within |ΔS21|
//    1e-3 of the model. That works because the models' losses carry their phase
//    (CausalLoss); before they did, the causality correction alone moved S21 by
//    up to 0.22 and every lossy line was refused (src/Engine/RESOLVED.md, TRN-4,
//    TRN-4b). A line the fit cannot bring within 1e-3 is refused by name with
//    its error — unless it is electrically short and the generic fit of its own
//    stamp meets that fit's tolerance.
//  - A lossless coupled pair (CLIN) is stepped by its even and odd modes; every
//    other coupled line is lossy or dispersive and refused by name.
//  - An SnP file, a System Filter or Duplexer, and every other linear element the
//    affine test rejects is replaced by a rational (pole-residue) fit of its S
//    matrix over DC to f_max = 1/(2 MaxStep), realized with extra linear unknowns
//    exactly as the envelope realizes it (RationalSnpModel), checked for
//    passivity on a grid ten times denser than its samples and beyond them, and
//    made passive by the smallest residue change (RationalPassivity). Data
//    stopping short of DC is extrapolated from its two lowest points; data
//    stopping below f_max is held at its last value; each says so.
//  - A P1Tone/PnTone with harmonic terminations keeps its drive and has its
//    band-mapped impedance realized beside it by the same fit.
//
//  The substitution is scoped to one run: Dispose puts every element back.
// ================================================================

using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Engine.Envelope;
using CircuitRF.Engine.Modeling;
using RfCore;

namespace CircuitRF.Engine.Transient;

/// <summary>One frequency-defined element's wideband fit: its order and error over its own samples, σmax before
/// enforcement and after, and the enforcement's change of S (relative RMS over the samples; 0 when it was passive).</summary>
/// <summary>SlowestS is the fit's slowest pole's time constant, 1/min(−Re a).</summary>
public sealed record TransientFitReport(string Instance, string What, int Order, double RelativeError, double Tolerance,
                                        double SigmaBefore, double SigmaAfter, double Perturbation, double FMaxHz, double SlowestS);

/// <summary>One line stepped as its delay: its delays and how far that is from its own model up to f_max (|ΔS21|; for a
/// lossless line the larger of |ΔS11| and |ΔS21|). A lossy line (brief TRN-4b) adds its residual's order and |ΔS11|.</summary>
public sealed record TransientLineReport(string Instance, double[] DelaysS, double Error, double FMaxHz, int Order = 0, double ErrorS11 = 0);

public sealed class TransientRealization : IDisposable
{
    /// <summary>|ΔS| (50 Ω) up to f_max within which a line is stepped as its delay; the brief's proposed tolerance.</summary>
    public const double LineTolerance = 1e-3;

    /// <summary>A lossy line is offered to the wideband fit when f_max·τ is at most this (beyond it the fit's order runs
    /// away: a line turns once per 1/τ Hz).</summary>
    public const double ShortLineCycles = 2;

    /// <summary>Relative RMS error a fit of measured or generic data must reach over its own samples (the envelope's).</summary>
    public const double Tolerance = EnvelopeSnpFit.Tolerance;

    /// <summary>A System Filter or Duplexer is exactly rational: anything above round-off is an order not yet reached.</summary>
    public const double BlockTolerance = EnvelopeSnpFit.BlockTolerance;

    public const int MaxOrder = 60;

    /// <summary>The least damping a fitted pole may have, −Re(a)/|a| (a Q of 5·10⁵); an order with a pole below it is not taken.</summary>
    public const double MinDamping = 1e-6;

    private static readonly VectorFitSettings Settings = new() { Proportional = false };

    private readonly ElaboratedNetlist _nl;
    private readonly List<(int Index, ElaboratedComponent Original)> _swapped = [];
    private readonly List<ElaboratedComponent> _added = [];
    private readonly List<ComponentModel> _driveOnly = [];

    private TransientRealization(ElaboratedNetlist nl, double fMaxHz) { _nl = nl; FMaxHz = fMaxHz; }

    public double FMaxHz { get; }
    /// <summary>Every element the run cannot step, each a sentence naming it.</summary>
    public List<string> Problems { get; } = [];
    public List<TransientFitReport> Fits { get; } = [];
    public List<TransientLineReport> Lines { get; } = [];

    /// <summary>brief TRN-6 — the reference impedance of each S-matrix fit swapped in, by instance path (the SPICE deck writer
    /// realizes the same fit).</summary>
    public Dictionary<string, double> FitZ0 { get; } = new(StringComparer.Ordinal);
    internal List<TransientLineModel> LineModels { get; } = [];

    /// <summary>Whether the lines leave their delayed waves out of their stamps (the transient engine's own stamps).</summary>
    internal bool Stepping { set { foreach (var l in LineModels) l.Stepping = value; } }

    public void Dispose()
    {
        foreach (var l in LineModels) l.Stepping = false;
        for (int i = _swapped.Count - 1; i >= 0; i--) _nl.Components[_swapped[i].Index] = _swapped[i].Original;
        foreach (var a in _added) _nl.Components.Remove(a);
        foreach (var m in _driveOnly)
        {
            if (m is P1ToneModel p1) p1.DriveOnly = false;
            else if (m is PnToneModel pn) pn.DriveOnly = false;
        }
        _swapped.Clear(); _added.Clear(); _driveOnly.Clear(); LineModels.Clear();
    }

    private void Swap(int index, ElaboratedComponent replacement)
    {
        _swapped.Add((index, _nl.Components[index]));
        _nl.Components[index] = replacement;
    }

    /// <summary>
    /// Realizes every element of <paramref name="nl"/> a transient run stepping up to <paramref name="fMaxHz"/> needs
    /// realized, and collects the refusals (in netlist order) in <see cref="Problems"/>. Tone sources must already have
    /// their tone context.
    /// </summary>
    public static TransientRealization Apply(ElaboratedNetlist nl, double fMaxHz)
    {
        var r = new TransientRealization(nl, fMaxHz);
        int count = nl.Components.Count;
        for (int i = 0; i < count; i++)
        {
            var ec = nl.Components[i];
            try { r.One(i, ec); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                r.Problems.Add(TransientElements.NotYetSupported(ec, ex.Message.TrimEnd('.')));
            }
        }
        return r;
    }

    private void One(int i, ElaboratedComponent ec)
    {
        if (TransientElements.RefusalByKind(ec) is { } byKind) { Problems.Add(byKind); return; }
        if (ec.IsNonlinear)
        {
            if (ec.Model is SddModel sdd && sdd.ControlRefs.Length > 0)
                Problems.Add(TransientElements.NotYetSupported(ec, "its equations read a control current"));
            return;
        }
        switch (ec.Model)
        {
            case MutualInductanceModel: return;   // jωM between two inductor branches: affine by its stamp
            case IUniformLine line when line.Ends(ec.Nodes) is { } ends:
                RealizeLine(i, ec, line, ends.A, ends.B);
                return;
            case CLineModel cl when ec.Nodes.Length == 4:
            {
                var (ze, zo, te, to) = cl.Modes;
                double m = ec.Multiplicity;
                var model = TransientLineModel.Pair(ec.Model, ec.Nodes[0], ec.Nodes[1], ec.Nodes[2], ec.Nodes[3], ze / m, zo / m, te, to);
                Swap(i, new ElaboratedComponent(ec.ComponentType, ec.InstancePath, ec.Nodes, ec.Parameters, model));
                LineModels.Add(model);
                Lines.Add(new TransientLineReport(ec.InstancePath, [te, to], 0, FMaxHz));
                _nl.AddNoteOnce("tran-line:" + ec.InstancePath, string.Format(CultureInfo.InvariantCulture,
                    "{0} '{1}': stepped by its even and odd modes as delays of {2:G4} ns and {3:G4} ns",
                    ec.ComponentType, ec.InstancePath, te * 1e9, to * 1e9));
                return;
            }
            case MicrostripCoupledLineModel or StriplineBroadsideCoupledModel or MicrostripBroadsideCoupledModel:
                Problems.Add($"{ec.InstancePath} ({ec.ComponentType}): lossy or dispersive coupled lines are not supported in transient; " +
                             "S-parameter, harmonic balance and envelope analyses support them. An ideal coupled pair (CLIN) runs in transient by its even and odd modes.");
                return;
            case P1ToneModel { HasHarmonicTerminations: true } or PnToneModel { HasHarmonicTerminations: true }:
                RealizeTermination(ec);
                return;
            case SnpModel snp:
                RealizeFile(i, ec, snp);
                return;
            case FilterModel or DuplexerModel:
                RealizeBlock(i, ec, BlockTolerance, ec.Model is FilterModel ? "System Filter" : "Duplexer", null);
                return;
            case PortModel or TermModel or VdcModel or IdcModel or WaveformSourceModel or ToneSourceModelBase or P1ToneModel or PnToneModel:
                return;   // constant or affine by construction (a Port or Term is its termination; a source its drive)
        }
        if (TransientElements.AffineError(ec) is { } why) RealizeBlock(i, ec, Tolerance, null, why);
    }

    // ── lines ────────────────────────────────────────────────────────────

    private void RealizeLine(int i, ElaboratedComponent ec, IUniformLine line, int a, int b)
    {
        var inv = CultureInfo.InvariantCulture;
        double m = ec.Multiplicity;
        double zc = line.LineAt(0).Z0 / m;
        double tau = Math.Max(0, line.LineAt(FMaxHz).GammaLength.Imaginary / (2 * Math.PI * FMaxHz));
        double err = DelayError(line, m, zc, tau, FMaxHz);
        if (err <= LineTolerance)
        {
            var model = TransientLineModel.Single(ec.Model, a, b, zc, tau);
            Swap(i, new ElaboratedComponent(ec.ComponentType, ec.InstancePath, ec.Nodes, ec.Parameters, model));
            LineModels.Add(model);
            Lines.Add(new TransientLineReport(ec.InstancePath, [tau], err, FMaxHz));
            _nl.AddNoteOnce("tran-line:" + ec.InstancePath, string.Format(inv,
                "{0} '{1}': stepped as its delay ({2:G4} ns, Zc {3:G4} Ω); |ΔS| {4:G2} from its model up to {5}",
                ec.ComponentType, ec.InstancePath, tau * 1e9, zc, err, Hz(FMaxHz)));
            return;
        }
        // brief-trn-4b: lossy or dispersive — the delay and a rational residual, fitted directly
        string key = "line|" + string.Join(";", ec.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)) +
                     FormattableString.Invariant($"|{ec.ComponentType}|{m:R}|{FMaxHz:R}");
        var fit = LineCache.GetOrAdd(key, _ => new Lazy<LossyLine>(() => FitLossyLine(line, m, FMaxHz), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (fit.Residual is not null && fit.ErrorS21 <= LineTolerance)
        {
            var model = TransientLineModel.Single(ec.Model, a, b, fit.Zc, fit.Tau, fit.Residual);
            Swap(i, new ElaboratedComponent(ec.ComponentType, ec.InstancePath, ec.Nodes, ec.Parameters, model));
            LineModels.Add(model);
            Lines.Add(new TransientLineReport(ec.InstancePath, [fit.Tau], fit.ErrorS21, FMaxHz, fit.Order, fit.ErrorS11));
            _nl.AddNoteOnce("tran-line:" + ec.InstancePath, string.Format(inv,
                "{0} '{1}': stepped as its delay ({2:G4} ns) with its loss and dispersion as a rational residual of order {3}; " +
                "|ΔS21| {4:G2} and |ΔS11| {5:G2} from its model up to {6} (Zc held at {7:G4} Ω)",
                ec.ComponentType, ec.InstancePath, fit.Tau * 1e9, fit.Order, fit.ErrorS21, fit.ErrorS11, Hz(FMaxHz), fit.Zc));
            if (fit.ErrorS11 > ReflectionWarnAbove)
                _nl.AddWarningOnce("tran-line-s11:" + ec.InstancePath, string.Format(inv,
                    "transient: '{0}' reflects {1:G2} differently from its model up to {2}: its characteristic impedance varies with " +
                    "frequency and the transient holds it at {3:G4} Ω; its transmission is within {4:G2}",
                    ec.InstancePath, fit.ErrorS11, Hz(FMaxHz), fit.Zc, fit.ErrorS21));
            return;
        }
        string lossy = string.Format(inv,
            "{0} ({1}): this lossy or dispersive line cannot be realized in transient up to {2}: as a delay with a rational residual " +
            "it departs from its own model by |ΔS21| = {3:G2}, above the {4:G1} tolerance. S-parameter, harmonic balance and envelope " +
            "analyses support it; a smaller MaxStep's band, or a shorter line, may bring it within the tolerance.",
            ec.InstancePath, ec.ComponentType, Hz(FMaxHz), fit.ErrorS21, LineTolerance);
        if (FMaxHz * tau > ShortLineCycles) { Problems.Add(lossy); return; }
        // electrically short: its own stamp, fitted like any other frequency-defined element
        int before = Problems.Count;
        RealizeBlock(i, ec, Tolerance, null, "lossy line", quiet: true);
        if (Problems.Count > before) { Problems.RemoveRange(before, Problems.Count - before); Problems.Add(lossy); }
    }

    /// <summary>|ΔS11| of a lossy line's realization above which the run warns, naming it (its Zc is held).</summary>
    public const double ReflectionWarnAbove = 1e-2;

    /// <summary>The rotation (cycles at f_max) a lossy line's residual is left to carry: the delay is the smallest phase delay
    /// in band less this many periods of f_max. A residual at the smallest delay is barely causal and fits badly
    /// (src/Engine/RESOLVED.md, brief-trn-4b).</summary>
    public const double ResidualCycles = 1.5;

    /// <summary>The highest order a lossy line's residual is fitted to.</summary>
    public const int MaxLineOrder = 40;

    /// <summary>A lossy line's residual is taken at the lowest order whose realization reaches this |ΔS21|.</summary>
    public const double LineFitTarget = 3e-4;

    internal sealed record LossyLine(double Tau, double Zc, RationalModel? Residual, int Order, double ErrorS21, double ErrorS11);

    private static readonly ConcurrentDictionary<string, Lazy<LossyLine>> LineCache = new();

    private static RationalModel Scaled(RationalModel m, double k) => new()
    {
        Poles = m.Poles,
        Residues = [.. m.Residues.Select(r => r.Select(c => c * k).ToArray())],
        D = [.. m.D.Select(d => d * k)],
        E = m.E,
    };

    /// <summary>
    /// A lossy or dispersive line as a delay τ = max(0, τ_min − <see cref="ResidualCycles"/>/f_max) and a residual
    /// H~(f) = e^(−γl)·e^(j2πfτ) fitted directly (its phase included: dispersion is a phase with no magnitude), the order the
    /// lowest whose realization meets <see cref="LineFitTarget"/> on a dense grid among those that never gain (|H~| ≤ 1),
    /// Zc held at the middle of its range. Its errors are |ΔS21| and |ΔS11| in 50 Ω against the line's own model, DC to f_max.
    /// </summary>
    internal static LossyLine FitLossyLine(IUniformLine line, double mult, double fMax)
    {
        Complex G(double f) => line.LineAt(f).GammaLength;
        double tauMin = double.PositiveInfinity;
        for (int k = 1; k <= 4000; k++)
        {
            double f = fMax * k / 4000;
            tauMin = Math.Min(tauMin, G(f).Imaginary / (2 * Math.PI * f));
        }
        double tau = Math.Max(0, tauMin - ResidualCycles / fMax);
        var fs = Enumerable.Range(0, 401).Select(i => fMax * i / 400.0)
            .Concat(Enumerable.Range(0, 40).Select(k => fMax * Math.Pow(10, -6 + (6 - Math.Log10(400)) * k / 39)))
            .Distinct().OrderBy(x => x).ToArray();
        var resp = new[] { fs.Select(f => Complex.Exp(-G(f) + new Complex(0, 2 * Math.PI * f * tau))).ToArray() };
        var check = Enumerable.Range(1, 2000).Select(k => fMax * k / 2000.0)
            .Concat(Enumerable.Range(0, 60).Select(k => fMax * Math.Pow(10, -6 + 4.0 * k / 60))).ToArray();
        var model = check.Select(f => { var (z, gl) = line.LineAt(f); return LineS(z / mult, Complex.Exp(-gl)); }).ToArray();
        double zMin = check.Min(f => line.LineAt(f).Z0), zMax = check.Max(f => line.LineAt(f).Z0);
        double zc = 0.5 * (zMin + zMax) / mult;
        (double S21, double S11) Errors(RationalModel r)
        {
            double a = 0, b = 0;
            for (int k = 0; k < check.Length; k++)
            {
                double f = check[k];
                var (r11, r21) = LineS(zc, Complex.Exp(new Complex(0, -2 * Math.PI * f * tau)) * r.EvaluateAtHz(0, f));
                a = Math.Max(a, (model[k].S21 - r21).Magnitude);
                b = Math.Max(b, (model[k].S11 - r11).Magnitude);
            }
            return (double.IsFinite(a) ? a : double.PositiveInfinity, double.IsFinite(b) ? b : double.PositiveInfinity);
        }
        RationalModel? best = null;
        int bestOrder = 0;
        (double S21, double S11) bestErr = (double.PositiveInfinity, double.PositiveInfinity);
        var errAt = new double[MaxLineOrder + 1];
        Array.Fill(errAt, double.PositiveInfinity);
        for (int order = 1; order <= MaxLineOrder; order++)
        {
            // as Fit's: eight orders that have not halved the error are not converging
            if (order > 12 && !(bestErr.S21 < 0.5 * errAt[order - 9])) break;
            errAt[order - 1] = bestErr.S21;
            VectorFitResult r;
            try { r = VectorFit.Fit(fs, resp, order, Settings); }
            catch (Exception ex) when (ex is ArithmeticException or ArgumentException) { continue; }
            if (r.Model.Poles.Any(a => !(-a.Real >= MinDamping * a.Magnitude))) continue;
            // A line never gains: |H~| ≤ 1 everywhere, checked on RationalPassivity's grid (ten times denser than the samples,
            // beyond the band, at every pole and at s → ∞). An order whose fit rises above 1 by more than the target is a
            // spurious peak between samples and is not taken; one just above 1 is scaled down to 1, its error measured after.
            var (sig, _) = RationalPassivity.SigmaMax(r.Model, 1, RationalPassivity.Grid(r.Model, fs, fMax));
            if (!(sig <= 1 + LineFitTarget)) continue;
            var candidate = sig > 1 ? Scaled(r.Model, 1 / sig) : r.Model;
            var e = Errors(candidate);
            if (e.S21 < bestErr.S21) { bestErr = e; best = candidate; bestOrder = order; }
            if (e.S21 <= LineFitTarget) break;
        }
        Interlocked.Increment(ref _fitCount);
        return new LossyLine(tau, zc, best, bestOrder, bestErr.S21, bestErr.S11);
    }

    /// <summary>max |ΔS11|, |ΔS21| in 50 Ω between the line's own model and its delay with a constant Zc, DC to f_max.</summary>
    internal static double DelayError(IUniformLine line, double m, double zc, double tau, double fMaxHz)
    {
        double turns = fMaxHz * Math.Max(tau, line.LineAt(fMaxHz).GammaLength.Imaginary / (2 * Math.PI * fMaxHz));
        int n = (int)Math.Clamp(40 * turns + 200, 200, 20000);
        double worst = 0;
        for (int k = 1; k <= n; k++)
        {
            double f = fMaxHz * k / n;
            var (z, gl) = line.LineAt(f);
            var (s11, s21) = LineS(z / m, Complex.Exp(-gl));
            var (r11, r21) = LineS(zc, Complex.Exp(new Complex(0, -2 * Math.PI * f * tau)));
            worst = Math.Max(worst, Math.Max((s11 - r11).Magnitude, (s21 - r21).Magnitude));
            if (!double.IsFinite(worst)) return double.PositiveInfinity;
        }
        return worst;
    }

    private static (Complex S11, Complex S21) LineS(double zc, Complex h)
    {
        double g = (zc - 50) / (zc + 50);
        var d = 1 - g * g * h * h;
        return (g * (1 - h * h) / d, h * (1 - g * g) / d);
    }

    // ── fits ─────────────────────────────────────────────────────────────

    private sealed class Entry
    {
        public RationalModel? Model;
        public string? Refusal;
        public int Ports, Order, Points;
        public double Z0, Error, Tol;
        public PassivityReport? Passivity;
        public readonly List<string> Notes = [];
    }

    private static readonly ConcurrentDictionary<string, Lazy<Entry>> Cache = new();
    private static int _fitCount;

    /// <summary>Fits computed (cache misses) in this process.</summary>
    public static int FitCount => Volatile.Read(ref _fitCount);

    private void RealizeFile(int i, ElaboratedComponent ec, SnpModel snp)
    {
        if (snp.Inverse)
        {
            Problems.Add(TransientElements.NotYetSupported(ec, "a Deembed undoes a network, so it would have to advance what that network delays, which no causal model does"));
            return;
        }
        if (snp.OffsetsNotAppliedBy(ec.InstancePath, "the transient's rational fit") is { } offsets) { Problems.Add(offsets + "."); return; }
        if (string.IsNullOrWhiteSpace(snp.FilePath) || !File.Exists(snp.FilePath))
        {
            Problems.Add(TransientElements.NotYetSupported(ec, $"its file '{snp.FilePath}' was not found"));
            return;
        }
        string full = Path.GetFullPath(snp.FilePath);
        var fi = new FileInfo(full);
        string key = FormattableString.Invariant($"file|{full}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}|{FMaxHz:R}");
        var e = Cache.GetOrAdd(key, _ => new Lazy<Entry>(() => BuildFile(full, FMaxHz), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (e.Model is not null && e.Ports != snp.PortCount)
            e = new Entry { Refusal = $"its file has {e.Ports} port(s), the block {snp.PortCount}" };
        if (!Accept(ec, e, "SnP")) return;
        Swap(i, new ElaboratedComponent(ec.ComponentType, ec.InstancePath, ec.Nodes, ec.Parameters, new RationalSnpModel(e.Ports, e.Z0, e.Model!))
        {
            ReferenceNode = ec.ReferenceNode,
            Multiplicity = ec.Multiplicity,
        });
        FitZ0[ec.InstancePath] = e.Z0;
    }

    /// <summary>A System Filter, Duplexer or any other element whose stamp is not affine: its node-referenced S sampled from
    /// DC to f_max (EnvelopeTangentCheck.NodeS, as the envelope samples it) and fitted.</summary>
    private void RealizeBlock(int i, ElaboratedComponent ec, double tol, string? what, string? whyNotAffine, bool quiet = false)
    {
        var nodes = ec.Nodes.Where(n => n > 0).Distinct().ToArray();
        if (nodes.Length == 0) return;
        string shape = string.Join(",", ec.Nodes.Select(n => n == 0 ? "g" : Array.IndexOf(nodes, n).ToString(CultureInfo.InvariantCulture)));
        string key = "block|" + string.Join(";", ec.Parameters.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)) +
                     FormattableString.Invariant($"|{ec.ComponentType}|{ec.Multiplicity:R}|{shape}|{FMaxHz:R}|{tol:R}");
        var e = Cache.GetOrAdd(key, _ => new Lazy<Entry>(() => BuildBlock(ec, nodes, FMaxHz, tol), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (!Accept(ec, e, what ?? ec.ComponentType, whyNotAffine, quiet)) return;
        Swap(i, new ElaboratedComponent(ec.ComponentType, ec.InstancePath, nodes, ec.Parameters, new RationalSnpModel(e.Ports, e.Z0, e.Model!)));
        FitZ0[ec.InstancePath] = e.Z0;
    }

    /// <summary>The most harmonic bands a tone source's termination is realized over.</summary>
    internal const int MaxTerminationBands = 128;

    /// <summary>A tone source's band-mapped termination: its drive kept, its impedance fitted as a one-port between the external
    /// node and the drive node, matched at every harmonic band centre up to f_max.</summary>
    private void RealizeTermination(ElaboratedComponent ec)
    {
        double fc;
        Func<double, Complex> z;
        if (ec.Model is P1ToneModel p1) { fc = p1.BandCentreHz; z = p1.ImpedanceAt; }
        else { var pn = (PnToneModel)ec.Model; fc = pn.BandCentreHz; z = pn.ImpedanceAt; }
        if (!(fc > 0) || ec.Nodes.Length < 3 || ec.Nodes[2] <= 0)
        {
            Problems.Add(TransientElements.NotYetSupported(ec, "its harmonic terminations have no band centre to map"));
            return;
        }
        int bands = Math.Max(1, (int)Math.Ceiling(FMaxHz / fc));
        // One placed resonator per band, and a dense fit and passivity step over all of them: a 10 MHz IF beside a 2 GHz LO is
        // 2,000 bands, an effective hang with nothing checking for Stop. Refused by name past a bound no real termination needs.
        if (bands > MaxTerminationBands)
        {
            Problems.Add(TransientElements.NotYetSupported(ec, string.Format(CultureInfo.InvariantCulture,
                "its harmonic terminations span {0} bands of {1} up to the {2} this run resolves, more than the {3} a transient run "
                + "realizes; a larger MaxStep lowers that frequency", bands, Hz(fc), Hz(FMaxHz), MaxTerminationBands)));
            return;
        }
        const double z0 = 50;
        var gk = Enumerable.Range(0, bands + 1).Select(n => { var zz = z(2 * Math.PI * n * fc); return (zz - z0) / (zz + z0); }).ToArray();
        string key = FormattableString.Invariant($"term|{fc:R}|{FMaxHz:R}|") + string.Join(",", gk.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
        var e = Cache.GetOrAdd(key, _ => new Lazy<Entry>(() => BuildTermination(gk, fc, z0, FMaxHz), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        if (!Accept(ec, e, "harmonic terminations")) return;
        if (ec.Model is P1ToneModel q1) q1.DriveOnly = true; else ((PnToneModel)ec.Model).DriveOnly = true;
        _driveOnly.Add(ec.Model);
        var term = new ElaboratedComponent("RationalTermination", ec.InstancePath + ".Z", [ec.Nodes[0]], ec.Parameters, new RationalSnpModel(1, z0, e.Model!))
        {
            ReferenceNode = ec.Nodes[2],
        };
        _nl.Components.Add(term);
        _added.Add(term);
    }

    /// <summary>Takes a fit or refuses the element; reports either way.</summary>
    private bool Accept(ElaboratedComponent ec, Entry e, string what, string? whyNotAffine = null, bool quiet = false)
    {
        var inv = CultureInfo.InvariantCulture;
        string reason = e.Refusal ?? (!(e.Error <= e.Tol)
            ? string.Format(inv, "its rational fit from DC to {0} reached a relative error of {1:G2} at order {2}, against a tolerance of {3:G2} " +
                                 "(a transient run has no harmonic to approximate it around)", Hz(FMaxHz), e.Error, e.Order, e.Tol)
            : e.Passivity is { Passive: false } p
                ? string.Format(inv, "its rational fit could not be made passive (σmax(S) {0:0.######} at {1})", p.SigmaAfter, Hz(p.WorstHz))
                : "");
        if (reason.Length > 0)
        {
            if (!quiet) Problems.Add(TransientElements.NotYetSupported(ec, whyNotAffine is null ? reason : whyNotAffine + "; " + reason));
            else Problems.Add(reason);
            return false;
        }
        var pr = e.Passivity!;
        double slowest = e.Model!.Poles.Select(a => 1 / -a.Real).DefaultIfEmpty(0).Max();
        Fits.Add(new TransientFitReport(ec.InstancePath, what, e.Order, e.Error, e.Tol, pr.SigmaBefore, pr.SigmaAfter, pr.Perturbation, FMaxHz, slowest));
        string pass = pr.Perturbation == 0
            ? string.Format(inv, "passive as fitted (σmax(S) {0:0.######})", pr.SigmaBefore)
            : string.Format(inv, "made passive (σmax(S) {0:0.######} at {1} before) by a residue change of {2:G2} (relative RMS of S over its samples)",
                            pr.SigmaBefore, Hz(pr.WorstHz), pr.Perturbation);
        _nl.AddNoteOnce("tran-fit:" + ec.InstancePath, string.Format(inv,
            "{0} '{1}': rational fit of order {2} over {3} samples from DC to {4}, relative error {5:G2}; {6}{7}",
            what, ec.InstancePath, e.Order, e.Points, Hz(FMaxHz), e.Error, pass, e.Notes.Count == 0 ? "" : "; " + string.Join("; ", e.Notes)));
        if (pr.Perturbation > Tolerance)
            _nl.AddWarningOnce("tran-fit-passivity:" + ec.InstancePath, string.Format(inv,
                "transient: '{0}' was not passive (σmax(S) {1:0.####}) and was made passive by changing S by {2:G2} (relative RMS); " +
                "results differ from its data by about that much", ec.InstancePath, pr.SigmaBefore, pr.Perturbation));
        return true;
    }

    /// <summary>The damping of the resonator a termination places on each harmonic: a Q of 2.5, so the termination settles in a few
    /// cycles and neighbouring harmonics stay well conditioned.</summary>
    internal const double TerminationDamping = 0.2;

    /// <summary>
    /// A termination presenting <paramref name="gk"/>[n] (Γ in <paramref name="z0"/>) at every harmonic n·<paramref name="fc"/>:
    /// only those are read in a periodic steady state, and a fit asked for them alone puts undamped poles on them (the band
    /// function between them is a staircase no rational function follows, and a real-valued curve through them has zero
    /// phase, which no causal one has). So the poles are placed — one damped pair on each harmonic — and the residues and
    /// constant solved so the model takes each harmonic's value: 2N + 1 real equations, 2N + 1 unknowns. Then made passive.
    /// </summary>
    private static Entry BuildTermination(Complex[] gk, double fc, double z0, double fMax)
    {
        var e = new Entry { Ports = 1, Z0 = z0, Tol = Tolerance, Points = gk.Length };
        int N = gk.Length - 1;
        double zeta = TerminationDamping;
        var poles = new List<Complex>();
        for (int n = 1; n <= N; n++)
        {
            double w = 2 * Math.PI * n * fc;
            var a = new Complex(-zeta * w, w * Math.Sqrt(1 - zeta * zeta));
            poles.Add(a); poles.Add(Complex.Conjugate(a));
        }
        int K = 2 * N + 1;
        var rows = new List<(double[] Row, double Rhs)>();
        for (int m = 0; m <= N; m++)
        {
            var sj = new Complex(0, 2 * Math.PI * m * fc);
            var phi = new Complex[K];
            for (int n = 0; n < N; n++)
            {
                var a = poles[2 * n];
                var z1 = 1 / (sj - a); var z2 = 1 / (sj - Complex.Conjugate(a));
                phi[2 * n] = z1 + z2;
                phi[2 * n + 1] = Complex.ImaginaryOne * (z1 - z2);
            }
            phi[K - 1] = 1;
            rows.Add(([.. phi.Select(c => c.Real)], gk[m].Real));
            if (m > 0) rows.Add(([.. phi.Select(c => c.Imaginary)], gk[m].Imaginary));
        }
        var A = new double[rows.Count, K];
        var b = new double[rows.Count];
        for (int i = 0; i < rows.Count; i++) { for (int k = 0; k < K; k++) A[i, k] = rows[i].Row[k]; b[i] = rows[i].Rhs; }
        double[] x;
        try { x = LeastSquares.Solve(A, b); }
        catch (ArithmeticException ex) { e.Refusal = "its terminations could not be realized: " + ex.Message; return e; }
        var res = new Complex[2 * N];
        for (int n = 0; n < N; n++) { res[2 * n] = new Complex(x[2 * n], x[2 * n + 1]); res[2 * n + 1] = Complex.Conjugate(res[2 * n]); }
        var model = new RationalModel { Poles = [.. poles], Residues = [res], D = [x[K - 1]], E = [0.0] };
        Interlocked.Increment(ref _fitCount);
        var f = Enumerable.Range(0, N + 1).Select(n => n * fc).ToArray();
        double d2 = 0, s2 = 0;
        for (int m = 0; m <= N; m++)
        {
            var d = model.EvaluateAtHz(0, f[m]) - gk[m];
            d2 += d.Magnitude * d.Magnitude; s2 += gk[m].Magnitude * gk[m].Magnitude;
        }
        e.Order = poles.Count;
        e.Error = s2 > 0 ? Math.Sqrt(d2 / s2) : Math.Sqrt(d2);
        if (!(e.Error <= Tolerance)) { e.Model = model; return e; }
        var (passive, report) = RationalPassivity.Enforce(model, 1, f, RationalPassivity.Grid(model, f, fMax));
        e.Model = passive; e.Passivity = report;
        return e;
    }

    private static Entry BuildFile(string path, double fMax)
    {
        var e = new Entry { Tol = Tolerance };
        SNP snp;
        try { snp = TouchstoneCache.Get(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException) { e.Refusal = ex.Message; return e; }
        if (snp.FrequencyCount < 1) { e.Refusal = "it has no frequency points"; return e; }
        var s = snp.Type switch { MatrixType.Z => RFNetwork.ZToS(snp), MatrixType.Y => RFNetwork.YToS(snp), _ => snp };
        if (s.Z0.Imaginary != 0 || !(s.Z0.Real > 0)) { e.Refusal = $"its reference impedance {s.Z0} is not a positive resistance"; return e; }
        int n = s.Matrices[0].RowCount;
        e.Ports = n; e.Z0 = s.Z0.Real;
        var order = Enumerable.Range(0, s.FrequencyCount).OrderBy(k => s.Frequencies[k]).ToArray();
        double Fk(int k) => s.Frequencies[order[k]];
        Complex Sk(int k, int r) => s.Matrices[order[k]][r / n, r % n];
        // No point in (0, f_max]: the whole band would be a straight line extrapolated from two points outside it, which the
        // low-order fit then matches almost exactly and accepts — a fabricated response, refused instead.
        if (Fk(0) >= fMax)
        {
            e.Refusal = string.Format(CultureInfo.InvariantCulture,
                "its data starts at {0}, above the highest frequency this run resolves ({1}); a transient run needs the file's response "
                + "within that band (a smaller MaxStep raises it)", Hz(Fk(0)), Hz(fMax));
            return e;
        }
        // the band: every point up to f_max, and the first one past it so the band's edge is inside the data
        int last = 0;
        while (last + 1 < order.Length && Fk(last) < fMax) last++;
        var f = new List<double>(); var w = new List<double>(); var cnt = new List<bool>();
        var resp = Enumerable.Range(0, n * n).Select(_ => new List<Complex>()).ToArray();
        var inv = CultureInfo.InvariantCulture;
        if (Fk(0) > 0)
        {
            // extrapolated to DC from the two lowest points, real there (a real network's S at DC is real)
            f.Add(0); w.Add(1); cnt.Add(false);
            for (int r = 0; r < n * n; r++)
            {
                var v = order.Length > 1 ? Sk(0, r) - (Sk(1, r) - Sk(0, r)) * Fk(0) / (Fk(1) - Fk(0)) : Sk(0, r);
                resp[r].Add(v.Real);
            }
            e.Notes.Add(string.Format(inv, "its data starts at {0}; DC is extrapolated from its two lowest points", Hz(Fk(0))));
        }
        for (int k = 0; k <= last; k++)
        {
            f.Add(Fk(k)); w.Add(1); cnt.Add(true);
            for (int r = 0; r < n * n; r++) resp[r].Add(Sk(k, r));
        }
        e.Points = cnt.Count(c => c);
        if (Fk(last) < fMax)
        {
            // held at its last value up to f_max, as sparsely as the data's own spacing, weighted below the data
            double top = Fk(last);
            double step = last > 0 ? (top - Fk(0)) / last : top;
            int extra = (int)Math.Clamp(Math.Ceiling((fMax - top) / Math.Max(step, 1e-300)), 1, 200);
            for (int k = 1; k <= extra; k++)
            {
                f.Add(top + (fMax - top) * k / extra); w.Add(0.3); cnt.Add(false);
                for (int r = 0; r < n * n; r++) resp[r].Add(Sk(last, r));
            }
            e.Notes.Add(string.Format(inv, "its data stops at {0}; it is held at its last value up to {1}", Hz(top), Hz(fMax)));
        }
        Interlocked.Increment(ref _fitCount);
        var arr = resp.Select(r => r.ToArray()).ToArray();
        bool constant = arr.All(r => r.All(v => Math.Abs(v.Imaginary) <= 1e-12 && (v - r[0]).Magnitude <= 1e-12));
        if (constant)
        {
            e.Model = new RationalModel { Poles = [], Residues = [.. arr.Select(_ => Array.Empty<Complex>())], D = [.. arr.Select(r => r[0].Real)], E = new double[n * n] };
            e.Passivity = RationalPassivity.Enforce(e.Model, n, f, RationalPassivity.Grid(e.Model, f, fMax)).Report;
            return e;
        }
        Fit(e, [.. f], arr, [.. w], [.. cnt], Tolerance, fMax);
        return e;
    }

    private static Entry BuildBlock(ElaboratedComponent ec, int[] nodes, double fMax, double tol)
    {
        int n = nodes.Length;
        var e = new Entry { Ports = n, Z0 = EnvelopeTangentCheck.NodeSReference, Tol = tol };
        // a uniform grid to f_max, and a logarithmic one under its first step so the approach to DC is seen
        var grid = Enumerable.Range(0, 401).Select(k => fMax * k / 400.0)
            .Concat(Enumerable.Range(0, 30).Select(k => fMax / 400.0 * Math.Pow(10, -3 + 3.0 * k / 30)))
            .Distinct().OrderBy(x => x).ToArray();
        var f = new List<double>();
        var resp = Enumerable.Range(0, n * n).Select(_ => new List<Complex>()).ToArray();
        foreach (double hz in grid)
        {
            Complex[,] sm;
            try { sm = EnvelopeTangentCheck.NodeS(ec, 2 * Math.PI * hz, nodes); }
            catch (InvalidOperationException) { continue; }
            bool finite = true;
            foreach (var v in sm) finite &= double.IsFinite(v.Real) && double.IsFinite(v.Imaginary);
            if (!finite) continue;
            f.Add(hz);
            for (int m = 0; m < n * n; m++) resp[m].Add(sm[m / n, m % n]);
        }
        if (ec.Model is IReportsWarnings rw) rw.DrainWarnings();
        if (f.Count < 4) { e.Refusal = "its response could not be sampled"; return e; }
        e.Points = f.Count;
        Interlocked.Increment(ref _fitCount);
        Fit(e, [.. f], [.. resp.Select(r => r.ToArray())], [.. Enumerable.Repeat(1.0, f.Count)], [.. Enumerable.Repeat(true, f.Count)], tol, fMax);
        return e;
    }

    /// <summary>The lowest order whose relative RMS error over the counted samples meets <paramref name="tol"/> (else the best),
    /// then made passive.</summary>
    private static void Fit(Entry e, double[] f, Complex[][] resp, double[] w, bool[] counts, double tol, double fMax)
    {
        e.Tol = tol;
        if (e.Points == 0) e.Points = counts.Count(c => c);
        int top = Math.Min(MaxOrder, VectorFit.MaxOrder(f.Length, Settings));
        VectorFitResult? best = null;
        double bestErr = double.PositiveInfinity;
        var errAt = new double[top + 1];
        Array.Fill(errAt, double.PositiveInfinity);
        for (int order = 1; order <= top; order++)
        {
            // a fit that has not halved its error over the last eight orders is not converging (a long delay, a non-causal
            // response): stop there rather than walk every order to the cap
            if (order > 16 && !(bestErr < 0.5 * errAt[order - 9])) break;
            VectorFitResult r;
            try { r = VectorFit.Fit(f, resp, order, Settings, w); }
            catch (Exception ex) when (ex is ArithmeticException or ArgumentException) { continue; }
            double d2 = 0, s2 = 0;
            for (int m = 0; m < resp.Length; m++)
                for (int k = 0; k < f.Length; k++)
                {
                    if (!counts[k]) continue;
                    var d = r.Model.EvaluateAtHz(m, f[k]) - resp[m][k];
                    d2 += d.Magnitude * d.Magnitude;
                    s2 += resp[m][k].Magnitude * resp[m][k].Magnitude;
                }
            double err = s2 > 0 ? Math.Sqrt(d2 / s2) : Math.Sqrt(d2);
            if (!double.IsFinite(err)) continue;
            // a pole on the jω axis is an undamped resonance: it interpolates a sample and rings forever in time
            if (r.Model.Poles.Any(a => !(-a.Real >= MinDamping * a.Magnitude))) continue;
            if (err < bestErr) { bestErr = err; best = r; }
            errAt[order] = bestErr;
            if (err <= tol) break;
        }
        if (best is null) { e.Refusal = "the rational fit failed at every order"; return; }
        e.Order = best.Order; e.Error = bestErr;
        if (!(bestErr <= tol)) { e.Model = best.Model; return; }
        var sampled = f.Where((_, k) => counts[k]).ToArray();
        var (model, report) = RationalPassivity.Enforce(best.Model, e.Ports, sampled, RationalPassivity.Grid(best.Model, f, fMax));
        e.Model = model; e.Passivity = report;
    }

    internal static string Hz(double hz) => hz switch
    {
        >= 1e9 => (hz / 1e9).ToString("0.####", CultureInfo.InvariantCulture) + " GHz",
        >= 1e6 => (hz / 1e6).ToString("0.####", CultureInfo.InvariantCulture) + " MHz",
        >= 1e3 => (hz / 1e3).ToString("0.####", CultureInfo.InvariantCulture) + " kHz",
        _ => hz.ToString("0.####", CultureInfo.InvariantCulture) + " Hz",
    };
}
