using System.Globalization;
using System.Numerics;
using System.Text;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;

/// <summary>One bias point: a voltage per pin, by pin name; a pin not named is at 0 V.</summary>
public sealed record ProtectBias(IReadOnlyDictionary<string, double> Volts)
{
    /// <summary><c>g=-0.5 d=5</c>, as the dialog and <c>--check-bias</c> spell it.</summary>
    public override string ToString()
        => Volts.Count == 0 ? "all pins at 0 V"
         : string.Join(" ", Volts.Select(kv => $"{kv.Key}={kv.Value.ToString("R", CultureInfo.InvariantCulture)}"));

    /// <summary>Reads <c>pin=volts</c> words, separated by spaces or commas; null with the reason when it does not.</summary>
    public static ProtectBias? Parse(string text, IReadOnlyList<string> pins, out string? why)
    {
        why = null;
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string word in text.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = word.IndexOf('=');
            if (eq <= 0) { why = $"'{word}' is not pin=volts."; return null; }
            string pin = word[..eq];
            if (!pins.Contains(pin, StringComparer.Ordinal)) { why = $"'{pin}' is not a pin of the cell; its pins are {string.Join(", ", pins)}."; return null; }
            if (!double.TryParse(word[(eq + 1)..].TrimEnd('V', 'v'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v))
            { why = $"'{word[(eq + 1)..]}' is not a voltage."; return null; }
            map[pin] = v;
        }
        return new ProtectBias(map);
    }

    /// <summary>The default set (§4): every pin at 0 V, then each pair of pins at +1 V and −1 V across them.</summary>
    public static IReadOnlyList<ProtectBias> Defaults(IReadOnlyList<string> pins)
    {
        var list = new List<ProtectBias> { new(new Dictionary<string, double>()) };
        for (int i = 0; i < pins.Count; i++)
            for (int j = i + 1; j < pins.Count; j++)
            {
                list.Add(new ProtectBias(new Dictionary<string, double> { [pins[i]] = 1, [pins[j]] = -1 }));
                list.Add(new ProtectBias(new Dictionary<string, double> { [pins[i]] = -1, [pins[j]] = 1 }));
            }
        return list;
    }
}

/// <summary>The proof's verdict: null <see cref="Refusal"/> when the compiled part matches the cell.</summary>
public sealed record ProtectCheckResult(double WorstError, string Worst, int Points, string? Refusal)
{
    public bool Passed => Refusal is null;

    /// <summary>One line for the dialog and the CLI.</summary>
    public string Summary => Refusal ?? $"The compiled part matches the cell at {Points} point{(Points == 1 ? "" : "s")}: worst {Worst}, " +
                                         $"relative difference {WorstError.ToString("G3", CultureInfo.InvariantCulture)}.";
}

/// <summary>
/// The proof (brief vendorlib-16 R-vl16-3, D5): the original cell and the wrapper with its freshly compiled module, each
/// placed with every pin biased through an inductor and an S-parameter port behind a DC block, run through the same
/// engine. At each bias point the DC terminal currents and the Y-parameters at three frequencies across the part's range
/// are compared — for a cell with a thermal pin, at two ambient temperatures as well. The limit is
/// <see cref="Tolerance"/> relative with absolute floors of 1e-12 A and 1e-12 S: the same equations in the same double
/// precision, so a real difference is a translation error, not rounding.
/// </summary>
public static class ProtectCheck
{
    public const double Tolerance = 1e-6;
    private const double CurrentFloor = 1e-12, AdmittanceFloor = 1e-12;
    private const double Z0 = 50;

    /// <summary>Two ambients, °C, for a cell with a thermal pin.</summary>
    public static readonly double[] Ambients = [25, 75];

    public static ProtectCheckResult Run(string sourceText, string sourceCell, string? sourceBase,
                                         string wrapperText, string wrapperCell, string wrapperBase,
                                         IReadOnlyList<string> pins, IReadOnlyList<ProtectBias> bias, double[] freqs,
                                         bool thermal, RunControl? control = null)
    {
        (double Error, string Where) worst = (0, "");
        int points = 0;
        double?[] ambients = thermal ? [.. Ambients.Select(a => (double?)a)] : [null];
        try
        {
            foreach (double? ambient in ambients)
                foreach (var b in bias)
                {
                    control?.Token.ThrowIfCancellationRequested();
                    var a = Bench(sourceText, sourceCell, sourceBase, pins, b, freqs, ambient);
                    var w = Bench(wrapperText, wrapperCell, wrapperBase, pins, b, freqs, ambient);
                    string at = $"at {b}" + (ambient is { } t ? $", {t.ToString(CultureInfo.InvariantCulture)} degC ambient" : "");
                    for (int k = 0; k < pins.Count; k++)
                    {
                        double e = Math.Abs(a.I[k] - w.I[k]) / Math.Max(Math.Max(Math.Abs(a.I[k]), Math.Abs(w.I[k])), CurrentFloor);
                        if (e > worst.Error) worst = (e, $"the DC current into {pins[k]} {at} (cell {G(a.I[k])} A, compiled {G(w.I[k])} A)");
                    }
                    for (int f = 0; f < freqs.Length; f++)
                        for (int i = 0; i < pins.Count; i++)
                            for (int j = 0; j < pins.Count; j++)
                            {
                                Complex ya = a.Y[f][i, j], yw = w.Y[f][i, j];
                                double e = (ya - yw).Magnitude / Math.Max(Math.Max(ya.Magnitude, yw.Magnitude), AdmittanceFloor);
                                if (e > worst.Error) worst = (e, $"Y({i + 1},{j + 1}) at {FormatHz(freqs[f])} {at} (cell {G(ya)} S, compiled {G(yw)} S)");
                            }
                    points++;
                }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProtectCheckResult(double.NaN, "", points, $"The proof could not run: {ex.Message}");
        }
        if (worst.Error > Tolerance)
            return new ProtectCheckResult(worst.Error, worst.Where, points,
                $"The compiled part does not match the cell: worst {worst.Where}, relative difference " +
                $"{worst.Error.ToString("G3", CultureInfo.InvariantCulture)} (allowed {Tolerance:G1}). Nothing was written to the library.");
        return new ProtectCheckResult(worst.Error, worst.Where, points, null);
    }

    /// <summary>Three frequencies log-spaced across the part's range: its ends and their geometric mean.</summary>
    public static double[] Frequencies(ModelPartFrequency? range)
    {
        double? lo = range is null ? null : VendorLibraryIndex.HzOf(range.Low);
        double? hi = range is null ? null : VendorLibraryIndex.HzOf(range.High);
        double high = hi is > 0 ? hi.Value : 10e9;
        double low = lo is > 0 && lo < high ? lo.Value : (hi is > 0 ? high / 1e4 : 1e6);
        return [low, Math.Sqrt(low * high), high];
    }

    private sealed record Result(double[] I, Complex[][,] Y);

    private static Result Bench(string cnl, string cell, string? baseDir, IReadOnlyList<string> pins, ProtectBias bias,
                                double[] freqs, double? ambient)
    {
        int n = pins.Count;
        var sb = new StringBuilder(cnl).Append('\n');
        if (ambient is { } t) sb.Append($"temp = {t.ToString("R", CultureInfo.InvariantCulture)}\n");
        for (int k = 1; k <= n; k++)
        {
            double v = bias.Volts.GetValueOrDefault(pins[k - 1]);
            sb.Append($"Vdc:PCV{k} __pc_v{k} 0 V={v.ToString("R", CultureInfo.InvariantCulture)}\n");
            sb.Append($"IProbe:PCI{k} __pc_v{k} __pc_b{k}\n");
            sb.Append($"L:PCL{k} __pc_b{k} __pc_n{k} L=1e-3\n");
            sb.Append($"Port:PCP{k} __pc_p{k} 0 Num={k} Z=50 Ohm\n");
            sb.Append($"C:PCC{k} __pc_p{k} __pc_n{k} C=1e-6\n");
        }
        sb.Append(cell).Append(":PCX1");
        for (int k = 1; k <= n; k++) sb.Append($" __pc_n{k}");
        sb.Append('\n');

        var (lib, tb) = new CnlReader().Read(sb.ToString(), "protect-check", baseDir);
        using var nl = new Elaborator(lib) { BaseDirectory = baseDir }.Elaborate(tb);
        var settings = new AnalysisSettings { NonlinearAbsTol = 1e-12 };
        var dc = NonlinearDcEngine.Run(nl, settings);
        if (!dc.Converged) throw new InvalidOperationException($"the DC solve at {bias} did not converge.");
        var current = new double[n];
        for (int k = 1; k <= n; k++) current[k - 1] = dc.ProbeCurrents[$"PCI{k}"];

        var s = SParameterEngine.Run(nl, freqs, settings)["S"].ComplexValues;
        var y = new Complex[freqs.Length][,];
        for (int f = 0; f < freqs.Length; f++)
        {
            var S = new Complex[n, n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) S[i, j] = s[f * n * n + i * n + j];
            y[f] = Admittance(S);
        }
        return new Result(current, y);
    }

    /// <summary>Y = (I − S)(I + S)⁻¹ / Z0.</summary>
    private static Complex[,] Admittance(Complex[,] s)
    {
        int n = s.GetLength(0);
        var a = new Complex[n, n];
        var b = new Complex[n, n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                a[i, j] = (i == j ? 1 : 0) + s[i, j];
                b[i, j] = (i == j ? 1 : 0) - s[i, j];
            }
        // Solve X·a = b for X by transposing: aᵀ·Xᵀ = bᵀ.
        var at = new Complex[n, n];
        var bt = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) { at[i, j] = a[j, i]; bt[i, j] = b[j, i]; }
        var xt = Solve(at, bt);
        var y = new Complex[n, n];
        for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) y[i, j] = xt[j, i] / Z0;
        return y;
    }

    private static Complex[,] Solve(Complex[,] a, Complex[,] b)
    {
        int n = a.GetLength(0), m = b.GetLength(1);
        for (int c = 0; c < n; c++)
        {
            int p = c;
            for (int r = c + 1; r < n; r++) if (a[r, c].Magnitude > a[p, c].Magnitude) p = r;
            if (a[p, c].Magnitude == 0) throw new InvalidOperationException("the bench's S-parameters are singular.");
            if (p != c)
            {
                for (int k = 0; k < n; k++) (a[c, k], a[p, k]) = (a[p, k], a[c, k]);
                for (int k = 0; k < m; k++) (b[c, k], b[p, k]) = (b[p, k], b[c, k]);
            }
            for (int r = 0; r < n; r++)
            {
                if (r == c) continue;
                Complex f = a[r, c] / a[c, c];
                if (f == Complex.Zero) continue;
                for (int k = c; k < n; k++) a[r, k] -= f * a[c, k];
                for (int k = 0; k < m; k++) b[r, k] -= f * b[c, k];
            }
        }
        var x = new Complex[n, m];
        for (int r = 0; r < n; r++) for (int k = 0; k < m; k++) x[r, k] = b[r, k] / a[r, r];
        return x;
    }

    private static string G(double x) => x.ToString("G6", CultureInfo.InvariantCulture);
    private static string G(Complex z) => $"{G(z.Real)}{(z.Imaginary < 0 ? "-" : "+")}j{G(Math.Abs(z.Imaginary))}";

    private static string FormatHz(double f) => f >= 1e9 ? $"{f / 1e9:G4} GHz" : f >= 1e6 ? $"{f / 1e6:G4} MHz" : $"{f / 1e3:G4} kHz";
}
