using System.Globalization;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Devices.Microstrip;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Diagnostics;

namespace CircuitRF.Design.Filters;

/// <summary>One pass of <see cref="FilterPolish.Run"/>: the part's stored values and what its bench extracts to.</summary>
public sealed record PolishStep(IReadOnlyDictionary<string, string> Parameters, ExtractedFilter Extraction);

/// <summary>
/// Polish (docs/design/filter-synthesis.md §6c): a realised MCRC of hairpins or open loops in a row, corrected until the
/// matrix its bench extracts to is the synthesised one. Each pass runs the bench the Filter Designer runs
/// (<see cref="FilterSynthesisService.Cnl"/> through the engine), extracts it (<see cref="FilterExtraction"/>), and
/// moves the targets the realiser's own curves are solved for (<see cref="CoupledResonatorDesign.FromCouplings"/>):
/// each k and the Qe by the ratio the extraction missed them by, then each resonator's length by its own frequency's
/// ratio to f0. The curves stand in for the bench, corrected at its output: output space mapping (J. W. Bandler et al.,
/// IEEE Trans. MTT 52(1), 2004, <see href="https://doi.org/10.1109/TMTT.2003.820904">10.1109/TMTT.2003.820904</see>).
/// </summary>
public static class FilterPolish
{
    /// <summary>The bench's points, as the Filter Designer sweeps.</summary>
    public const int Points = 401;

    /// <summary>
    /// <paramref name="realisation"/> (an MCRC from a hairpin or open-loop realiser for <paramref name="spec"/>, on
    /// <paramref name="substrate"/>) after up to <paramref name="passes"/> corrections: the pass whose worst return loss
    /// across the band is best, and every pass, the first being the realisation as given.
    /// </summary>
    public static (FilterRealisation Realisation, IReadOnlyList<PolishStep> History) Run(FilterSpec spec,
        FilterRealisation realisation, MicrostripSubstrate substrate, int passes = 3)
    {
        // The form FromCouplings solves on: the stored values less N and the lengths, gaps and tap it solves for.
        var form = new Dictionary<string, object>();
        foreach (var (key, text) in realisation.Parameters)
            if (!Regex.IsMatch(key, @"^(N|Tap|Sx|[LS]\d+)$"))
                form[key] = text.EndsWith("mm", StringComparison.Ordinal) && Number(text[..^2]) is { } mm ? mm * 1e-3
                          : Number(text) is { } x ? x : text;
        if (realisation.PartType != ComponentModelFactory.CoupledResonatorTypeName
            || form.GetValueOrDefault("Shape") is not ("Hairpin" or "OpenLoop")
            || form.GetValueOrDefault("Arrangement") is "Cross")
            throw new ParameterRefusalException(Diagnostic.Create("synthesis.polish.form", DiagnosticSeverity.Error,
                "Filter polish: a {type} {shape} is not hairpins or open loops in a row, whose gaps and feed are solved from "
                + "k and Qe.", ("type", realisation.PartType), ("shape", form.GetValueOrDefault("Shape"))));

        int n = spec.Order;
        double f0 = spec.CentreHz, fbw = spec.FractionalBandwidth;
        double[] g = LowpassPrototype.GValues(spec.Response, n, spec.RippleDb);
        double[] f = FilterSynthesisService.Frequencies(spec, Points);
        double[,] k = CoupledResonatorDesign.Couplings(g, fbw), aim = (double[,])k.Clone(), target = CouplingMatrix.AllPole(g);
        double qe = CoupledResonatorDesign.ExternalQ(g, fbw), qeAim = qe;
        double[] length = [.. Enumerable.Repeat(1.0, n)];
        var history = new List<PolishStep>();
        var part = realisation;
        while (true)
        {
            var snp = FilterSynthesisService.Response(FilterSynthesisService.Cnl(spec, part, "polish", substrate, null, Points, []),
                f, spec.Z0);
            var x = FilterExtraction.Extract(spec, f, [.. snp.Matrices.Select(s => s[0, 0])],
                [.. snp.Matrices.Select(s => s[1, 0])], target);
            history.Add(new PolishStep(part.Parameters, x));
            if (history.Count > passes) break;

            var r = x.Resonators!;
            for (int i = 0; i + 1 < n; i++)
                aim[i, i + 1] = aim[i + 1, i] = aim[i, i + 1] * k[i, i + 1] / Math.Abs(r.K[i, i + 1]);
            qeAim *= qe / Math.Sqrt(r.QeSource * r.QeLoad);
            for (int i = 0; i < n; i++) length[i] *= r.ResonantHz[i] / f0;
            var v = CoupledResonatorDesign.FromCouplings(form, aim, qeAim, f0, substrate);
            for (int i = 1; i <= n; i++) v[$"L{i}"] = PassivePlan.OnGrid((double)v[$"L{i}"] * length[i - 1]);
            part = part with { Parameters = CoupledResonatorRealiser.Stored(v) };
        }
        var best = history.MaxBy(h => h.Extraction.ReturnLossDb)!;
        double was = history[0].Extraction.ReturnLossDb, kept = best.Extraction.ReturnLossDb;
        string note = string.Create(CultureInfo.InvariantCulture, $"Polished in {history.Count - 1} passes: worst return "
            + $"loss across the band {was:F1} dB as realised, {kept:F1} dB kept.");
        return (realisation with { Parameters = best.Parameters, Notes = [.. realisation.Notes, note] }, history);
    }

    private static double? Number(string text)
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
}
