using System.Globalization;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Matching;
using CircuitRF.Design.Schematic;
using CircuitRF.Engine;
using RfCore;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf filter --band B --order N --f1 F [--f2 F] [spec…] [--tech T] [--realiser R -o f.cnl [--to-schematic] [--em]]</c> —
/// the Filter Designer, headless (docs/design/filter-synthesis.md §6).
///
/// <para><b>It owns no synthesis.</b> The realisers are Core's (<see cref="FilterRealisers"/>), and the substrate, the
/// part and the bench are <see cref="FilterSynthesisService"/>'s, the calls the window makes. Without <c>--realiser</c> it
/// lists every realiser and whether it can build the specification; with one it writes the bench as a <c>.cnl</c>, and with
/// <c>--to-schematic</c> draws it through <c>netlist --to-schematic</c>'s own path, and with <c>--em</c> solves the part on Planar
/// MoM (<see cref="FilterEmService"/>, §6d) into <c>&lt;name&gt;.em.s2p</c> beside it.</para>
/// </summary>
internal static class FilterVerb
{
    public static int Run(string[] args) => Run(args, FilterRealisers.All);

    /// <summary>The verb over <paramref name="realisers"/>; a test hands its own.</summary>
    internal static int Run(string[] args, IReadOnlyList<IFilterRealiser> realisers)
    {
        string? tech = null, signal = null, ground = null, realiser = null, output = null;
        string? response = null, band = null, order = null, f1 = null, f2 = null, ripple = null, stop = null, z0 = null, zeros = null, points = null;
        bool toSchematic = false, em = false;
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--to-schematic") { toSchematic = true; continue; }
            if (a == "--em") { em = true; continue; }
            if (i + 1 >= args.Length || !a.StartsWith('-')) return JsonRun.Fail(CliDiagnostics.FilterUnknownOption(a));
            string v = args[++i];
            switch (a)
            {
                case "--response": response = v; break;
                case "--band": band = v; break;
                case "--order": order = v; break;
                case "--f1": f1 = v; break;
                case "--f2": f2 = v; break;
                case "--ripple": ripple = v; break;
                case "--stop": stop = v; break;
                case "--z0": z0 = v; break;
                case "--zeros": zeros = v; break;
                case "--tech": tech = v; break;
                case "--signal": signal = v; break;
                case "--ground": ground = v; break;
                case "--realiser": realiser = v; break;
                case "--points": points = v; break;
                case "-o" or "--output": output = v; break;
                default: return JsonRun.Fail(CliDiagnostics.FilterUnknownOption(a));
            }
        }

        // ── the specification ──
        if (band is null || order is null || f1 is null)
            return JsonRun.Fail(CliDiagnostics.FilterSpecRefused("--band, --order and --f1 are required."));
        if (!Enum.TryParse(band, true, out FilterBand fb) || !Enum.IsDefined(fb))
            return JsonRun.Fail(CliDiagnostics.FilterBadValue("--band", band, "lowpass, highpass, bandpass or bandstop"));
        var fr = FilterResponse.Chebyshev;
        if (response is not null && (!Enum.TryParse(response, true, out fr) || !Enum.IsDefined(fr)))
            return JsonRun.Fail(CliDiagnostics.FilterBadValue("--response", response, string.Join(", ", Enum.GetNames<FilterResponse>())));
        if (!int.TryParse(order, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
            return JsonRun.Fail(CliDiagnostics.FilterBadValue("--order", order, "a whole number of at least 1"));
        if (Hz("--f1", f1) is not { } lo) return 1;
        bool isBand = fb is FilterBand.Bandpass or FilterBand.Bandstop;
        double hi = lo;
        if (isBand)
        {
            if (f2 is null) return JsonRun.Fail(CliDiagnostics.FilterSpecRefused($"a {band} needs --f2, its upper edge."));
            if (Hz("--f2", f2) is not { } h) return 1;
            if (!(h > lo)) return JsonRun.Fail(CliDiagnostics.FilterSpecRefused("--f2 must be above --f1."));
            hi = h;
        }
        if (Number("--ripple", ripple, 0.1) is not { } rippleDb || Number("--stop", stop, 40.0) is not { } stopDb) return 1;
        double zRef = 50.0;
        if (z0 is not null && (!MatchValueFormat.TryParseWithUnit(z0, MatchQuantity.Resistance, "Ω", out zRef, out _) || !(zRef > 0)))
            return JsonRun.Fail(CliDiagnostics.FilterBadValue("--z0", z0, "a resistance (50, 75 Ohm)"));
        var tz = new List<double>();
        foreach (string z in (zeros ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Hz("--zeros", z) is { } zHz) tz.Add(zHz); else return 1;
        int np = 401;
        if (points is not null && (!int.TryParse(points, NumberStyles.Integer, CultureInfo.InvariantCulture, out np) || np < 2))
            return JsonRun.Fail(CliDiagnostics.FilterBadValue("--points", points, "a whole number of at least 2"));
        var spec = new FilterSpec(fr, fb, n, lo, hi, rippleDb, stopDb, zRef, tz.Count > 0 ? tz : null);

        // ── the technology: --tech, else the workspace the CLI was given, else the one the output lands in ──
        Technology? technology;
        if (tech is not null) { if ((technology = Technology(tech)) is null) return 1; }
        else technology = CliEntry.HostWorkspace is { } host ? MicrostripSubstrateInjection.ResolveWorkspaceTechnology(host)
            : output is not null ? MicrostripSubstrateInjection.ResolveWorkspaceTechnology(Path.GetDirectoryName(Path.GetFullPath(output)))
            : null;
        MicrostripSubstrate? substrate = null;
        if (technology is not null && (substrate = FilterSynthesisService.Substrate(technology, signal, ground, out string? why)) is null)
            JsonRun.Report(CliDiagnostics.FilterNoSubstrate(why ?? "the technology has none"));

        Console.WriteLine($"Filter: {FilterSynthesisService.Describe(spec)}"
                        + (technology is null ? "" : $", on {technology.Name}")
                        + (substrate is { } s ? $" (H {s.HMeters * 1e3:0.####} mm, Er {s.EpsR:0.###})" : ""));

        // ── list, or realise one ──
        if (realiser is null)
        {
            if (realisers.Count == 0) Console.WriteLine("  no realiser is installed");
            foreach (var r in realisers)
            {
                bool can = r.Supports(spec, out string? reason);
                Console.WriteLine($"  {(can ? "yes" : "no "),-4}{r.Name}" + (can ? "" : $": {reason}"));
                JsonRun.Note(CliDiagnostics.FilterRealiserListed(r.Name, can, reason ?? ""));
            }
            return 0;
        }

        var chosen = realisers.FirstOrDefault(r => string.Equals(r.Name, realiser, StringComparison.OrdinalIgnoreCase))
                     ?? (realisers.Where(r => r.Name.Contains(realiser, StringComparison.OrdinalIgnoreCase)).ToList() is [var only] ? only : null);
        if (chosen is null)
            return JsonRun.Fail(CliDiagnostics.FilterRealiserUnknown(realiser, string.Join("; ", realisers.Select(r => r.Name))));
        if (!chosen.Supports(spec, out string? refusal))
            return JsonRun.Fail(CliDiagnostics.FilterRealiserRefused(chosen.Name, refusal ?? "it does not say why"));
        if (output is null) return JsonRun.Fail(CliDiagnostics.FilterOutputRequired());

        FilterRealisation realisation;
        try { realisation = chosen.Realise(spec, substrate); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { return JsonRun.Fail(CliDiagnostics.FilterRealiserRefused(chosen.Name, ex.Message)); }

        var notes = new List<string>(realisation.Notes);
        string cnl = FilterSynthesisService.Cnl(spec, realisation, chosen.Name, substrate, technology, np, notes);
        string path = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, cnl);
        foreach (string note in notes) { Console.WriteLine($"  note: {note}"); JsonRun.Note(CliDiagnostics.FilterNote(note)); }
        Console.WriteLine($"Wrote {path}: {realisation.PartType} and {realisation.Sites.Count} site part(s)");
        JsonRun.AddOutput("cnl", path);

        if (toSchematic && NetlistToSchematic.Run(path, DocumentKind.Netlist, Path.ChangeExtension(path, ".csch")) is var drawn and not 0)
            return drawn;
        return em ? Em(spec, realisation, substrate, cnl, path, points is null ? 11 : np) : 0;
    }

    /// <summary><c>--em</c>: the realisation on Planar MoM at <paramref name="points"/> frequencies across the bench's sweep,
    /// written as <c>&lt;name&gt;.em.s2p</c> beside the bench, and one line against the bench's own response.</summary>
    private static int Em(FilterSpec spec, FilterRealisation realisation, MicrostripSubstrate? substrate, string cnl, string path, int points)
    {
        if (substrate is null) return JsonRun.Fail(CliDiagnostics.FilterEmRefused("Planar EM needs the substrate a --tech gives."));
        var f = FilterSynthesisService.Frequencies(spec, points);
        string last = "";
        FilterEmService.Result run;
        try
        {
            run = FilterEmService.Run(spec, realisation, substrate, f, new Progress<RunProgress>(p =>
            {
                string line = $"[{p.Completed}/{p.Total}] {p.Stage}";
                if (line != last) Console.Error.WriteLine(last = line);
            }), RunHost.Cancellation);
        }
        catch (ParameterRefusalException ex) when (ex.Diagnostic is { } d) { return JsonRun.Fail(d); }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        { return JsonRun.Fail(CliDiagnostics.FilterEmRefused(ex.Message)); }

        string snp = Path.ChangeExtension(path, ".em.s2p");
        TouchstoneIO.WriteFile(run.S, snp);
        JsonRun.AddOutput("em", snp);
        var circuit = FilterSynthesisService.Response(cnl, f, spec.Z0);
        string edge = spec.Band is FilterBand.Lowpass or FilterBand.Highpass ? "edge" : "centre";
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"EM: {edge} {Centre(spec, run.S) / 1e9:0.####} GHz (circuit {Centre(spec, circuit) / 1e9:0.####}), worst in-band |S11| "
          + $"{WorstS11(spec, run.S):0.##} dB (circuit {WorstS11(spec, circuit):0.##}); wrote {snp}"));
        return 0;
    }

    /// <summary>Where |S21| crosses 3 dB under its peak, linearly between samples: a lowpass's or highpass's first crossing,
    /// a band's midway between its first and last; NaN when it never crosses.</summary>
    private static double Centre(FilterSpec spec, SNP s)
    {
        var db = s.Matrices.Select(m => 20.0 * Math.Log10(m[1, 0].Magnitude)).ToArray();
        double level = db.Max() - 3.0;
        var x = new List<double>();
        for (int i = 1; i < db.Length; i++)
            if ((db[i - 1] - level) * (db[i] - level) < 0)
                x.Add(s.Frequencies[i - 1] + (level - db[i - 1]) / (db[i] - db[i - 1]) * (s.Frequencies[i] - s.Frequencies[i - 1]));
        return x.Count == 0 ? double.NaN : spec.Band is FilterBand.Lowpass or FilterBand.Highpass ? x[0] : 0.5 * (x[0] + x[^1]);
    }

    /// <summary>The largest |S11| in dB at the samples inside the passband; NaN when none is.</summary>
    private static double WorstS11(FilterSpec spec, SNP s)
    {
        bool In(double f) => spec.Band switch
        {
            FilterBand.Lowpass => f <= spec.F1Hz,
            FilterBand.Highpass => f >= spec.F1Hz,
            FilterBand.Bandpass => f >= spec.F1Hz && f <= spec.F2Hz,
            _ => f <= spec.F1Hz || f >= spec.F2Hz,
        };
        var db = s.Frequencies.Select((f, i) => (f, i)).Where(x => In(x.f))
            .Select(x => 20.0 * Math.Log10(s.Matrices[x.i][0, 0].Magnitude)).ToList();
        return db.Count == 0 ? double.NaN : db.Max();
    }

    /// <summary>A frequency with its unit (<c>2.4GHz</c>, <c>2.4 GHz</c>, or Hz bare), above zero; null with the refusal
    /// reported.</summary>
    private static double? Hz(string option, string text)
    {
        if (MatchValueFormat.TryParseWithUnit(text, MatchQuantity.Frequency, "Hz", out double hz, out _) && hz > 0) return hz;
        JsonRun.Fail(CliDiagnostics.FilterBadValue(option, text, "a frequency above zero (2.4GHz)"));
        return null;
    }

    private static double? Number(string option, string? text, double absent)
    {
        if (text is null) return absent;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v > 0 && double.IsFinite(v)) return v;
        JsonRun.Fail(CliDiagnostics.FilterBadValue(option, text, "a number of dB above zero"));
        return null;
    }

    /// <summary>The technology <c>--tech</c> names: a <c>.ctech</c>, a folder in a workspace (its technology), or a
    /// shipped id; null with the refusal reported.</summary>
    private static Technology? Technology(string text)
    {
        Technology? t = File.Exists(text) ? TechnologyResolver.LoadForPath(Path.GetFullPath(text))
            : Directory.Exists(text) ? MicrostripSubstrateInjection.ResolveWorkspaceTechnology(Path.GetFullPath(text))
            : ShippedTechnologies.All.FirstOrDefault(e => string.Equals(e.Id, text, StringComparison.OrdinalIgnoreCase)) is { } shipped
                ? ShippedTechnologies.Load(shipped) : null;
        if (t is null) JsonRun.Fail(CliDiagnostics.FilterTechNotFound(text, string.Join(", ", ShippedTechnologies.All.Select(e => e.Id))));
        return t;
    }
}
