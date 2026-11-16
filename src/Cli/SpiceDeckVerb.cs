using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Design.Circuit;
using CircuitRF.Engine.Transient;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf netlist &lt;bench&gt; -o out.cir</c> — the bench's transient analysis written as a SPICE deck (brief TRN-6
/// R-trn6-11; docs/design/transient.md §9.2, <c>cli.md</c> §46).
///
/// <para><b>It owns no translation.</b> Every byte comes from <see cref="SpiceDeckWriter"/>, the only writer of the dialect
/// in circuitRF, on the bench as a run would elaborate it. Exit 1 and nothing written on any refusal, every refusal listed
/// at once.</para>
/// </summary>
internal static class SpiceDeckVerb
{
    /// <summary>The bench a path names, read as a run reads it.</summary>
    internal static PreparedCircuit Prepare(string full, DocumentKind kind) => kind == DocumentKind.Schematic
        ? CircuitSource.PrepareSchematic(full)
        : PreparedCircuit.FromFile(full, Path.GetDirectoryName(full));

    /// <summary>The deck for <paramref name="tb"/>'s transient analysis <paramref name="analysis"/> (null: its one enabled
    /// transient analysis), or the sentence why there is none.</summary>
    internal static (SpiceDeck? Deck, string? Why) Deck(Library lib, TestBench tb, string sourcePath, string? analysis)
    {
        var trans = tb.Analyses.OfType<TransientAnalysis>().Where(t => t.Enabled).ToList();
        TransientAnalysis? a;
        if (analysis is not null)
        {
            a = trans.FirstOrDefault(t => t.Name.Equals(analysis, StringComparison.OrdinalIgnoreCase));
            if (a is null)
                return (null, $"'{analysis}' is not an enabled transient analysis of this bench" +
                              (trans.Count > 0 ? $" (it has {string.Join(", ", trans.Select(t => t.Name))})." : "."));
        }
        else if (trans.Count == 1) a = trans[0];
        else
            return (null, trans.Count == 0
                ? "the bench declares no enabled transient analysis (analysis <name> type=tran Stop=...), and a deck is written for one."
                : $"the bench declares {trans.Count} transient analyses ({string.Join(", ", trans.Select(t => t.Name))}); name one with --analysis.");

        var nl = Check.ElaborateReadOnly(lib, tb, sourcePath);
        using (nl)
            return (SpiceDeckWriter.Write(new SpiceDeckRequest
            {
                Netlist = nl, Analysis = a, BenchName = Path.GetFileNameWithoutExtension(sourcePath), Version = JsonRun.Version(),
                Bench = tb, Library = lib, Fits = new TransientSpiceFits(),
            }), null);
    }

    public static int Run(string path, DocumentKind kind, string? csch, string? analysis, string output)
    {
        string source = csch ?? path;
        string full = Path.GetFullPath(source);
        var circuit = Prepare(full, kind == DocumentKind.Netlist ? DocumentKind.Netlist : DocumentKind.Schematic);
        if (circuit.ReadError is { } readError || circuit.Tb is not { } tb || circuit.Lib is not { } lib)
            return JsonRun.Fail(CliDiagnostics.NetlistDeckFailed(path, circuit.ReadError ?? "the design could not be read"));

        SpiceDeck? deck;
        string? why;
        try { (deck, why) = Deck(lib, tb, full, analysis); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return JsonRun.Fail(CliDiagnostics.NetlistDeckFailed(path, ex.Message));
        }
        if (why is not null) return JsonRun.Fail(CliDiagnostics.NetlistDeckNoAnalysis(path, why));
        if (deck!.Text is not { } text)
            return JsonRun.Fail(CliDiagnostics.NetlistDeckRefused(
                path, deck.Refusals.Count, string.Join("\n", deck.Refusals.Select(r => "  " + r.Message))));

        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);
            File.WriteAllText(output, text, new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.NetlistWriteFailed(output, ex.Message)); }

        foreach (var l in deck.Lines) Console.Error.WriteLine($"[circuitRF] {l.Sentence}");
        foreach (var (_, s) in deck.Fits) Console.Error.WriteLine($"[circuitRF] {s}");
        JsonRun.AddOutput("cir", output);
        Console.WriteLine(output);
        Console.Error.WriteLine($"[circuitRF] wrote the SPICE deck of {Path.GetFileName(source)} -> {output}");
        return 0;
    }
}
