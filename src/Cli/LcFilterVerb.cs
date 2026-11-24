using System.Globalization;
using System.Text;
using CircuitRF.Core.Matching;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf filter lc</c> — the LC Filter Designer with no window. The ladder and the netlist are
/// <see cref="FilterLadder"/>'s; a series snap is <see cref="FilterValues"/>'s.
/// </summary>
public static class LcFilterVerb
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
                case "--as":
                case "--astop": spec.As = Num(Next()); break;
                case "--elliptic-type":
                    if (!EllipticTypes.TryParse(Next(), out spec.EllipticType))
                        return Fail("--elliptic-type is auto, a, b or c.");
                    break;
                case "--series":
                    if (!Enum.TryParse<FilterSeriesKind>(Next(), true, out spec.Series)
                        || !Enum.IsDefined(spec.Series) || spec.Series == FilterSeriesKind.VendorLibrary)
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
                    return Fail($"Unrecognised argument '{a}'. filter lc --help lists them.");
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
                                             spec.Ap > 0 ? spec.Ap : 0.1, spec.Rs, spec.First,
                                             spec.As > 0 ? spec.As : 40, spec.EllipticType);
        if (!built.Ok)
            return JsonRun.Fail(CliDiagnostics.FilterSpecRefused(built.Refusal!.Message));

        var real = built.Realization!;
        var used = new List<FilterElement>(real.Elements.Count);
        var text = new StringBuilder();
        text.Append($"# {spec.Response} {spec.Form} order {spec.Order}, first {real.First}");
        if (spec.Response == FilterResponse.Elliptic)
            text.Append($", type {EllipticTypes.Letter(EllipticTypes.Resolve(spec.EllipticType, spec.Order))}");
        text.AppendLine();
        if (real.Note.Length > 0) JsonRun.Report(CliDiagnostics.FilterNote(real.Note));
        text.AppendLine($"# source {real.SourceOhms:0.###} ohm, load {real.LoadOhms:0.###} ohm");
        foreach (var e in real.Elements)
        {
            double value = FilterValues.Snap(e.Inductor, e.Value, spec.Series);
            // The snap multiplies a mantissa by a decade, so 6.8 pF arrives as 6.799999999999999e-12. A preferred value
            // has at most three significant digits, so twelve loses nothing and the netlist reads 6.8E-12.
            if (spec.Series != FilterSeriesKind.Exact)
                value = double.Parse(value.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            used.Add(e with { Value = value });
            text.Append(e.Name).Append('\t')
                .Append(e.Shunt ? "shunt" : "series").Append('\t')
                .Append(e.Inductor ? "L" : "C").Append('\t')
                .Append(e.Value.ToString("G6", CultureInfo.InvariantCulture)).Append('\t')
                .AppendLine(value.ToString("G6", CultureInfo.InvariantCulture));
        }
        Console.Write(text);
        JsonRun.LcFilter = Result(spec, real, used);

        if (!string.IsNullOrWhiteSpace(spec.Out))
        {
            // `;` is the netlist's comment; the `*` this once wrote is SPICE's, and the reader warned on it in every file.
            var cnl = new StringBuilder();
            cnl.Append("; ").Append(spec.Response).Append(' ').Append(spec.Form)
               .Append(" order ").AppendLine(spec.Order.ToString(CultureInfo.InvariantCulture));
            cnl.Append(FilterLadder.ToCnl(used, real.SourceOhms, real.LoadOhms));
            // A sweep across the band, so the file runs as written: the span the Filter Designer's own bench sweeps.
            var band = spec.Form switch
            {
                NetworkForm.Highpass => FilterBand.Highpass,
                NetworkForm.Bandpass => FilterBand.Bandpass,
                NetworkForm.Bandstop => FilterBand.Bandstop,
                _                    => FilterBand.Lowpass,
            };
            bool edges = band is FilterBand.Bandpass or FilterBand.Bandstop;
            var sweep = FilterSynthesisService.Frequencies(
                new FilterSpec(spec.Response, band, spec.Order, edges ? f1 : fc, edges ? f2 : 0), 401);
            cnl.Append("analysis SP1 type=sparam start=").Append(sweep[0].ToString("R", CultureInfo.InvariantCulture))
               .Append(" stop=").Append(sweep[^1].ToString("R", CultureInfo.InvariantCulture)).AppendLine(" npts=401 Unit=Hz");
            File.WriteAllText(spec.Out, cnl.ToString());
            Console.Error.WriteLine($"Wrote {spec.Out}");
            JsonRun.AddOutput("cnl", Path.GetFullPath(spec.Out));
        }
        return 0;
    }

    /// <summary>
    /// The ladder as <c>--json</c> carries it: what was asked, the terminations the family produced, and each
    /// element at its exact value and at the value written (the series snap; the same number with <c>--series exact</c>).
    /// </summary>
    private static System.Text.Json.Nodes.JsonObject Result(Parsed spec, FilterRealization real, IReadOnlyList<FilterElement> used)
    {
        var elements = new System.Text.Json.Nodes.JsonArray();
        for (int i = 0; i < used.Count; i++)
        {
            var e = used[i];
            elements.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"]   = e.Name,
                ["kind"]   = e.Component,
                ["arm"]    = e.Shunt ? "shunt" : "series",
                ["nodes"]  = new System.Text.Json.Nodes.JsonArray(e.NodeA, e.NodeB),
                ["exact"]  = real.Elements[i].Value,
                ["value"]  = e.Value,
                ["unit"]   = e.Inductor ? "H" : "F",
            });
        }
        var o = new System.Text.Json.Nodes.JsonObject
        {
            ["response"]   = spec.Response.ToString(),
            ["form"]       = spec.Form.ToString(),
            ["order"]      = spec.Order,
            ["first"]      = real.First.ToString(),
            ["series"]     = spec.Series.ToString(),
            ["sourceOhms"] = real.SourceOhms,
            ["loadOhms"]   = real.LoadOhms,
            ["elements"]   = elements,
        };
        if (spec.Fc is double fc) o["fcHz"] = fc;
        if (spec.F1 is double f1) o["f1Hz"] = f1;
        if (spec.F2 is double f2) o["f2Hz"] = f2;
        if (spec.Response is FilterResponse.Chebyshev or FilterResponse.Elliptic) o["rippleDb"] = spec.Ap > 0 ? spec.Ap : 0.1;
        if (spec.Response is FilterResponse.InvChebyshev or FilterResponse.Elliptic) o["stopDb"] = spec.As > 0 ? spec.As : 40;
        return o;
    }

    private static string Help() => """
        circuitrf filter lc — an LC ladder from a filter specification.

          --form lowpass|highpass|bandpass|bandstop
          --response Butterworth|Chebyshev|InvChebyshev|Bessel|Gaussian|Elliptic
          --order N          prototype order (a bandpass or bandstop of order 3 has 6 elements)
          --first series|parallel
          --fc 1GHz          cutoff for lowpass and highpass (also --fp)
          --f1 0.8GHz --f2 1.25GHz
                             the passband edges; a bandstop rejects between them
                             (inverse Chebyshev: the edges where --as is first met)
          --rs 50 --rl 50    ohms. The ladder's load is the family's own ratio when that differs.
          --ap 0.1           passband ripple, dB, for Chebyshev and elliptic
          --as 40            stopband floor, dB, for inverse Chebyshev and elliptic
          --elliptic-type auto|a|b|c
                             auto (default) is a at odd order and c at even order
          --series exact|e6|e12|e24|e96
          -o out.cnl         the ladder as a netlist, with an S-parameter sweep across the band

        Inverse Chebyshev and elliptic are ladders of tanks. An even-order inverse Chebyshev, or an
        even-order elliptic of type a, keeps a finite floor at infinite frequency, which a ladder
        cannot, and is refused. Elliptic types b and c are ladders at every even order: b with
        unequal terminations, c with equal ones.
        A frequency is a number and a unit (Hz, kHz, MHz, GHz).
        """;

    private static int Fail(string message)
    {
        JsonRun.Report(CliDiagnostics.FilterSpecRefused(message));
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
        public double Rs = 50, Rl = 50, Ap = 0.1, As = 40;
        public FilterSeriesKind Series = FilterSeriesKind.Exact;
        public EllipticType EllipticType = EllipticType.Auto;
        public string? Out;
    }
}
