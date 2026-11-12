// A chain stage that IS a Touchstone file: measured or simulated S-parameters, read and interpolated exactly as the SnP
// block reads them (TouchstoneCache, cubic spline in magnitude and phase), at the frequency the stage sees — the chain's,
// or a mixer's output after one.
//
//   gain    |S(Out,In)|², the file renormalised to 50 Ω and every other port terminated in 50 Ω
//   noise   the file's noise data (a two-port read 1 → 2) at a 50 Ω source:
//               F = Fmin + 4·(Rn/R)·|Γs − Γopt|² / ((1 − |Γs|²)·|1 + Γopt|²),  Γs = (50 − R)/(50 + R)
//           or, with none and a passive network, its own thermal noise at T0 (Bosma), terminations included:
//               F = (1 − |S(Out,Out)|²) / |S(Out,In)|²
//           which is the noise the S-parameter noise analysis gives the placed block, so budget and bench agree.
//
// A file with gain and no noise data has no noise figure anyone can state for it, so the budget asks for NF rather than
// guessing. A stated NF or IIP3 is placed on the benches as an ideal Amp of 0 dB after the block, carrying the excess
// noise and the intercept, so the simulation still checks every number of the budget.
//
// The budget never extrapolates: a stage frequency outside the file's range is a refusal, and so is one outside its noise
// rows when the noise comes from them. A blocker outside the range is carried at the file's edge value, as the block
// itself clamps it in a simulation, and said.

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Elaboration;
using NumFlat;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Design.Chain;

/// <summary>An SnP stage, read at its frequency.</summary>
/// <param name="FullPath">The file, resolved.</param>
/// <param name="In">The port the chain enters at, 1-based.</param>
/// <param name="Out">The port it leaves from, 1-based.</param>
/// <param name="GainDb">|S(Out,In)|² at 50 Ω, dB.</param>
/// <param name="FileNoiseFactor">The noise factor the placed block has on its own in a simulation: from its noise data,
/// its thermal noise when passive, or 1 (noiseless) when it has gain and no noise data.</param>
/// <param name="NoiseFrom">Where <paramref name="FileNoiseFactor"/> comes from.</param>
/// <param name="NoiseFactor">The stage's noise factor: <paramref name="FileNoiseFactor"/>, or the NF stated.</param>
/// <param name="Iip3Dbm">The stated input intercept, +∞ when blank.</param>
public sealed record SnpStageRead(string FullPath, int Ports, int In, int Out, double GainDb, double FileNoiseFactor,
                                  SnpNoiseFrom NoiseFrom, double NoiseFactor, double Iip3Dbm, IReadOnlyList<string> Notes)
{
    /// <summary>The noise factor the ideal Amp after the block must add, referred to ITS input, for the stage to have
    /// <see cref="NoiseFactor"/>: 1 + (F − F_file)·G. 1 when nothing is stated beyond the file.</summary>
    public double AmpNoiseFactor => 1 + Math.Max(0, NoiseFactor - FileNoiseFactor) * Math.Pow(10, GainDb / 10);

    /// <summary>The benches place an Amp after the block: a stated NF above the file's, or a stated intercept.</summary>
    public bool NeedsAmp => AmpNoiseFactor > 1 + 1e-12 || double.IsFinite(Iip3Dbm);
}

/// <summary>Where an SnP stage's own noise comes from.</summary>
public enum SnpNoiseFrom { NoiseData, Passive, None }

public static class ChainSnp
{
    /// <summary>The reference impedance a chain's stages are matched to.</summary>
    public const double Z0 = 50;

    /// <summary>σ_max above which a file has gain: the SnP block's own passivity tolerance.</summary>
    private const double PassiveTol = 1e-6;

    /// <summary>Within this relative distance of an end of the file's range, a frequency is at that end.</summary>
    private const double EdgeTol = 1e-9;

    /// <summary>The stage's <c>File</c> as written, unquoted and trimmed.</summary>
    public static string FileText(ChainStage s)
    {
        string t = s.Get("File").Trim();
        return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1].Trim() : t;
    }

    /// <summary>The file a reference names: rooted as written, relative from <paramref name="baseDirectory"/> by the
    /// rule a schematic's SnP <c>File</c> resolves by; null for a relative reference with no directory to resolve it
    /// from.</summary>
    public static string? Resolve(string reference, string? baseDirectory)
    {
        if (reference.Length == 0) return null;
        if (Path.IsPathRooted(reference)) return Path.GetFullPath(reference);
        return string.IsNullOrEmpty(baseDirectory) ? null : Elaborator.ResolveFileReference(reference, baseDirectory);
    }

    /// <summary>A file as a reference from <paramref name="baseDirectory"/>: relative, with '/' separators, when the file
    /// is under it; the full path otherwise.</summary>
    public static string Reference(string fullPath, string? baseDirectory)
    {
        fullPath = Path.GetFullPath(fullPath);
        if (string.IsNullOrEmpty(baseDirectory)) return fullPath;
        string rel = Path.GetRelativePath(Path.GetFullPath(baseDirectory), fullPath);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? fullPath : rel.Replace('\\', '/');
    }

    /// <summary>The stage's budget; an all-zero one when it cannot be read, which <see cref="ChainDesign.Refusals"/> has
    /// already stopped the chain for.</summary>
    public static StageBudget Budget(ChainStage s, ChainStageContext c)
    {
        if (Read(s, c, []) is not { } r) return new(0, 0, 0, double.PositiveInfinity, double.PositiveInfinity, 1, []);
        double nf = 10 * Math.Log10(r.NoiseFactor);
        double ip1 = double.IsFinite(r.Iip3Dbm) ? r.Iip3Dbm - ChainParts.TanhP1dbBelowIip3 : double.PositiveInfinity;
        return new(r.GainDb, r.GainDb, nf, r.Iip3Dbm, ip1, 1, r.Notes);
    }

    /// <summary>The stage read at <see cref="ChainStageContext.FrequencyHz"/>, or null with every reason it cannot be
    /// added to <paramref name="refusals"/>, each naming the stage. Never throws.</summary>
    public static SnpStageRead? Read(ChainStage s, ChainStageContext c, List<string> refusals)
    {
        void No(string why) => refusals.Add($"Stage '{s.Name}': {why}");
        string reference = FileText(s);
        if (reference.Length == 0)
        {
            No("no Touchstone file is named. Set File to an .sNp file.");
            return null;
        }
        if (Resolve(reference, c.BaseDirectory) is not { } full)
        {
            No($"File '{reference}' is a relative path, and the chain is not saved anywhere it could be read from. Save " +
               "the chain, or give the file's full path.");
            return null;
        }
        if (Directory.Exists(full)) { No($"File '{reference}' names a folder, not a Touchstone file."); return null; }
        if (!File.Exists(full)) { No($"File '{reference}': there is no file at '{full}'."); return null; }

        SNP snp;
        SnpInterpolator interp;
        try
        {
            snp = TouchstoneCache.Get(full);
            if (snp.IsEmpty) { No($"File '{reference}' holds no frequency points."); return null; }
            interp = TouchstoneCache.GetInterpolator(full, InterpolationMethod.CubicSpline, InterpolationFormat.MagPhase,
                                                     MatrixType.S, OutOfRangePolicy.WarnClamp);
        }
        catch (Exception ex)
        {
            No($"File '{reference}' could not be read as Touchstone: {ex.Message}");
            return null;
        }

        int ports = snp.Ports;
        bool portsOk = true;
        int Port(string key)
        {
            string text = s.Get(key).Trim();
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                || v != Math.Round(v) || v < 1 || v > ports)
            {
                No($"{key} = '{text}' is not a port of '{Path.GetFileName(full)}', which has {ports} " +
                   (ports == 1 ? "port: a one-port has no way through." : $"ports: give 1 to {ports}."));
                portsOk = false;
                return 0;
            }
            return (int)v;
        }
        int pin = Port("In"), pout = Port("Out");
        if (!portsOk) return null;
        if (pin == pout) { No($"In and Out are both port {pin}: the chain needs a way through."); return null; }

        double f = c.FrequencyHz;
        double lo = interp.MinFrequency, hi = interp.MaxFrequency;
        if (!(f >= lo * (1 - EdgeTol) && f <= hi * (1 + EdgeTol)))
        {
            No($"the stage is at {Hz(f)}, outside '{Path.GetFileName(full)}', which holds {Hz(lo)} to {Hz(hi)}. " +
               "The budget does not extrapolate S-parameters: use a file that covers it, or a chain frequency it covers.");
            return null;
        }

        var notes = new List<string>();
        Mat<Complex> s50;
        try { s50 = At50(interp.Evaluate(Math.Clamp(f, lo, hi)), interp.Source.Z0); }
        catch (Exception ex) { No($"File '{reference}' could not be interpolated at {Hz(f)}: {ex.Message}"); return null; }

        int i = pin - 1, o = pout - 1;
        Complex t = s50[o, i];
        double g = t.Magnitude * t.Magnitude;
        if (!(g > 0) || !double.IsFinite(g))
        {
            No($"'{Path.GetFileName(full)}' has no transmission from port {pin} to port {pout} at {Hz(f)} " +
               $"(S{pout}{pin} = {t.Magnitude:G3}).");
            return null;
        }

        // The noise the placed block has on its own.
        double fFile;
        SnpNoiseFrom from;
        bool passive = RFNetwork.Passivity(s50) <= 1 + PassiveTol;
        var rows = snp.NoiseParameters.OrderBy(p => p.FrequencyHz).ToList();
        bool useRows = rows.Count > 0 && ports == 2 && pin == 1 && pout == 2;
        if (rows.Count > 0 && !useRows)
            notes.Add($"{s.Name}: the noise data in '{Path.GetFileName(full)}' is the two-port's from port 1 to port 2, " +
                      $"and this stage reads it from port {pin} to port {pout}, so it is not used.");
        bool nfBlank = s.Get("NF").Trim().Length == 0;
        if (useRows)
        {
            double nlo = rows[0].FrequencyHz, nhi = rows[^1].FrequencyHz;
            if (!(f >= nlo * (1 - EdgeTol) && f <= nhi * (1 + EdgeTol)) && nfBlank)
            {
                No($"the stage is at {Hz(f)}, outside the noise data in '{Path.GetFileName(full)}', which covers " +
                   $"{Hz(nlo)} to {Hz(nhi)}. Give NF, or use a frequency the noise data covers.");
                return null;
            }
            double r = snp.Z0.Real;
            var p = NoiseAt(rows, f, r);
            Complex gs = new((Z0 - r) / (Z0 + r), 0);
            fFile = NoiseMetrics.NoiseFactor(p, gs);
            from = SnpNoiseFrom.NoiseData;
            if (!NoiseMetrics.IsRealizable(p, 1e-3))
                notes.Add($"{s.Name}: the noise data in '{Path.GetFileName(full)}' at {Hz(f)} is not physical " +
                          "(Fmin − 1 above 4·Rn·Gopt); it is used as given.");
        }
        else if (passive)
        {
            Complex so = s50[o, o];
            fFile = (1 - so.Magnitude * so.Magnitude) / g;
            from = SnpNoiseFrom.Passive;
        }
        else
        {
            fFile = 1;
            from = SnpNoiseFrom.None;
        }

        double fStage = fFile;
        if (!nfBlank)
        {
            double nfDb = ChainValue.Or(s.Get("NF"), 0);
            fStage = Math.Pow(10, nfDb / 10);
            if (fStage < fFile * (1 - 1e-9))
            {
                No($"NF = {s.Get("NF").Trim()} dB is below the {10 * Math.Log10(fFile):0.###} dB '{Path.GetFileName(full)}' " +
                   $"itself gives at {Hz(f)} ({(from == SnpNoiseFrom.NoiseData ? "its noise data" : "a passive network's own loss")}). " +
                   "Leave NF blank to use the file's.");
                return null;
            }
            if (from == SnpNoiseFrom.None)
                notes.Add($"{s.Name}: '{Path.GetFileName(full)}' has gain and no noise data, so its NF is the {s.Get("NF").Trim()} dB stated.");
        }
        else if (from == SnpNoiseFrom.None)
        {
            No($"'{Path.GetFileName(full)}' has gain at {Hz(f)} and holds no noise data, so its noise figure is unknown. " +
               "Give NF.");
            return null;
        }

        double iip3 = s.Get("IIP3").Trim().Length == 0 ? double.PositiveInfinity : ChainValue.Or(s.Get("IIP3"), double.PositiveInfinity);
        if (Math.Abs(snp.Z0.Real - Z0) > 1e-9 || Math.Abs(snp.Z0.Imaginary) > 1e-9)
            notes.Add($"{s.Name}: '{Path.GetFileName(full)}' is referenced to {snp.Z0.Real:G4} Ω; the budget reads it renormalised to 50 Ω.");
        return new SnpStageRead(full, ports, pin, pout, 10 * Math.Log10(g), fFile, from, fStage, iip3, notes);
    }

    /// <summary>
    /// |S(Out,In)| at another frequency, dB, for a blocker: the file's own edge value outside its range, as the block
    /// clamps it in a simulation (<paramref name="outside"/> says so). Null when the stage cannot be read.
    /// </summary>
    public static double? GainDbAt(ChainStage s, string? baseDirectory, double fHz, out bool outside)
    {
        outside = false;
        try
        {
            if (Resolve(FileText(s), baseDirectory) is not { } full || !File.Exists(full)) return null;
            var interp = TouchstoneCache.GetInterpolator(full, InterpolationMethod.CubicSpline, InterpolationFormat.MagPhase,
                                                         MatrixType.S, OutOfRangePolicy.WarnClamp);
            outside = fHz < interp.MinFrequency || fHz > interp.MaxFrequency;
            var m = At50(interp.Evaluate(Math.Clamp(fHz, interp.MinFrequency, interp.MaxFrequency)), interp.Source.Z0);
            int i = (int)ChainValue.Or(s.Get("In"), 1) - 1, o = (int)ChainValue.Or(s.Get("Out"), 2) - 1;
            if (i < 0 || o < 0 || i >= m.RowCount || o >= m.RowCount) return null;
            double mag = m[o, i].Magnitude;
            return mag > 0 ? 20 * Math.Log10(mag) : -300;
        }
        catch (Exception) { return null; }
    }

    /// <summary>S renormalised from the file's reference to 50 Ω; the same matrix when it already is.</summary>
    private static Mat<Complex> At50(Mat<Complex> s, Complex z0)
        => z0 == new Complex(Z0, 0) ? s : RFNetwork.SToS(s, z0, new Complex(Z0, 0));

    /// <summary>The noise parameters at <paramref name="hz"/> as the SnP block takes them: linear between rows in Fmin
    /// (a factor), Γopt and Rn, exact at a row, the end row held outside.</summary>
    private static TwoPortNoise NoiseAt(List<NoiseParameterPoint> rows, double hz, double r)
    {
        TwoPortNoise P(NoiseParameterPoint p) => new(Math.Pow(10, p.NFminDb / 10), p.GammaOpt, p.RnNormalized * r, r);
        if (hz <= rows[0].FrequencyHz) return P(rows[0]);
        if (hz >= rows[^1].FrequencyHz) return P(rows[^1]);
        int k = 1;
        while (rows[k].FrequencyHz < hz) k++;
        var a = P(rows[k - 1]); var b = P(rows[k]);
        double t = (hz - rows[k - 1].FrequencyHz) / (rows[k].FrequencyHz - rows[k - 1].FrequencyHz), u = 1 - t;
        return new TwoPortNoise(u * a.Fmin + t * b.Fmin, u * a.GammaOpt + t * b.GammaOpt, u * a.Rn + t * b.Rn, r);
    }

    /// <summary>A frequency as a person writes it: "2.45 GHz", "350 MHz".</summary>
    public static string Hz(double hz)
    {
        static string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        double a = Math.Abs(hz);
        return a >= 1e9 ? $"{N(hz / 1e9)} GHz" : a >= 1e6 ? $"{N(hz / 1e6)} MHz" : a >= 1e3 ? $"{N(hz / 1e3)} kHz" : $"{N(hz)} Hz";
    }
}
