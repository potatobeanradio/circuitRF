using System.Globalization;
using System.Text;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf filter</c> — the Filter Assistant with no window. The ladder and the netlist are
/// <see cref="FilterLadder"/>'s; a series snap is <see cref="FilterValues"/>'s.
/// </summary>
public static class FilterVerb
{
    /// <summary>Runs one specification and writes the ladder. Exit 0 when a ladder was produced.</summary>
    public static int Run(string[] args)
    {
        var spec = new Parsed();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (a)
            {
                case "--form":
                    if (!Enum.TryParse<NetworkForm>(Next(), true, out var form))
                        return Fail($"--form is lowpass, highpass, bandpass or bandstop.");
                    spec.Form = form;
                    break;
                case "--response":
                    if (!Enum.TryParse<FilterResponse>(Next(), true, out var response))
                        return Fail("--response is Butterworth, Chebyshev, InvChebyshev, Bessel, Gaussian or Elliptic.");
                    spec.Response = response;
                    break;
                case "--order":
                    if (!int.TryParse(Next(), NumberStyles.Integer, CultureInfo.InvariantCulture, out spec.Order))
                        return Fail("--order is a whole number.");
                    break;
                case "--first":
                    spec.First = Next().StartsWith("p", StringComparison.OrdinalIgnoreCase)
                        ? FilterFirstElement.Parallel : FilterFirstElement.Series;
                    break;
                case "--fc": spec.Fc = Hz(Next()); break;
                case "--f1": spec.F1 = Hz(Next()); break;
                case "--f2": spec.F2 = Hz(Next()); break;
                case "--fp": spec.Fc = Hz(Next()); break;
                case "--rs": spec.Rs = Num(Next()); break;
                case "--rl": spec.Rl = Num(Next()); break;
                case "--ap": spec.Ap = Num(Next()); break;
                case "--ripple": spec.Ap = Num(Next()); break;
                case "--series":
                    if (!Enum.TryParse<FilterSeriesKind>(Next(), true, out spec.Series))
                        return Fail("--series is exact, e6, e12, e24 or e96.");
                    break;
                case "-o":
                case "--out":
                    spec.Out = Next();
                    break;
                case "--help":
                case "-h":
                    Console.WriteLine(Help());
                    return 0;
                default:
                    return Fail($"Unrecognised argument '{a}'. filter --help lists them.");
            }
        }

        if (spec.Fc is null && spec.Form is NetworkForm.Lowpass or NetworkForm.Highpass)
            return Fail("A lowpass or highpass needs --fc (or --fp), a frequency with a unit.");
        if (spec.Form is NetworkForm.Bandpass or NetworkForm.Bandstop)
        {
            if (spec.F1 is null || spec.F2 is null)
                return Fail("A bandpass or bandstop needs --f1 and --f2, the cutoff edges.");
        }

        double fc = spec.Fc ?? 0, f1 = spec.F1 ?? 0, f2 = spec.F2 ?? 0;
        var built = FilterLadder.Synthesize(spec.Response, spec.Form, spec.Order, fc, f1, f2,
                                             spec.Ap > 0 ? spec.Ap : 0.1, spec.Rs, spec.First);
        if (!built.Ok)
        {
            Console.Error.WriteLine(built.Refusal!.Message);
            return 1;
        }

        var real = built.Realization!;
        var used = new List<FilterElement>(real.Elements.Count);
        var text = new StringBuilder();
        text.AppendLine($"# {spec.Response} {spec.Form} order {spec.Order}, first {spec.First}");
        text.AppendLine($"# source {real.SourceOhms:0.###} ohm, load {real.LoadOhms:0.###} ohm");
        foreach (var e in real.Elements)
        {
            double value = FilterValues.Snap(e.Inductor, e.Value, spec.Series);
            used.Add(e with { Value = value });
            text.Append(e.Name).Append('\t')
                .Append(e.Shunt ? "shunt" : "series").Append('\t')
                .Append(e.Inductor ? "L" : "C").Append('\t')
                .Append(e.Value.ToString("G6", CultureInfo.InvariantCulture)).Append('\t')
                .AppendLine(value.ToString("G6", CultureInfo.InvariantCulture));
        }
        Console.Write(text);

        if (!string.IsNullOrWhiteSpace(spec.Out))
        {
            var cnl = new StringBuilder();
            cnl.Append("* ").Append(spec.Response).Append(' ').Append(spec.Form)
               .Append(" order ").AppendLine(spec.Order.ToString(CultureInfo.InvariantCulture));
            cnl.Append(FilterLadder.ToCnl(used, real.SourceOhms, real.LoadOhms));
            File.WriteAllText(spec.Out, cnl.ToString());
            Console.Error.WriteLine($"Wrote {spec.Out}");
        }
        return 0;
    }

    private static string Help() => """
        circuitrf filter — an LC ladder from a filter specification.

          --form lowpass|highpass|bandpass|bandstop
          --response Butterworth|Chebyshev|InvChebyshev|Bessel|Gaussian|Elliptic
          --order N          prototype order (a bandpass or bandstop of order 3 has 6 elements)
          --first series|parallel
          --fc 1GHz          cutoff for lowpass and highpass (also --fp)
          --f1 0.8GHz --f2 1.25GHz
                             passband edges for a bandpass, stopband edges for a bandstop
          --rs 50 --rl 50    ohms. The ladder's load is the family's own ratio when that differs.
          --ap 0.1           passband ripple, dB, for Chebyshev and elliptic
          --series exact|e6|e12|e24|e96
          -o out.cnl         the ladder as a netlist

        Inverse Chebyshev and elliptic are refused: their ladders are tanks, and the ideal Filter
        block already has those responses. A frequency is a number and a unit (Hz, kHz, MHz, GHz).
        """;

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static double Num(string text) =>
        double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static double Hz(string text)
    {
        text = text.Trim();
        double scale = 1;
        if (text.EndsWith("GHz", StringComparison.OrdinalIgnoreCase)) { scale = 1e9; text = text[..^3]; }
        else if (text.EndsWith("MHz", StringComparison.OrdinalIgnoreCase)) { scale = 1e6; text = text[..^3]; }
        else if (text.EndsWith("kHz", StringComparison.OrdinalIgnoreCase)) { scale = 1e3; text = text[..^3]; }
        else if (text.EndsWith("Hz", StringComparison.OrdinalIgnoreCase)) text = text[..^2];
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) * scale;
    }

    private sealed class Parsed
    {
        public NetworkForm Form = NetworkForm.Lowpass;
        public FilterResponse Response = FilterResponse.Butterworth;
        public int Order = 3;
        public FilterFirstElement First = FilterFirstElement.Series;
        public double? Fc, F1, F2;
        public double Rs = 50, Rl = 50, Ap = 0.1;
        public FilterSeriesKind Series = FilterSeriesKind.Exact;
        public string? Out;
    }
}
