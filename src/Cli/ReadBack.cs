using CircuitRF.Diagnostics;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf read &lt;path&gt;</c> — the inverse of a run verb, and the only verb in this program
/// whose whole job is the direction <c>automation-architecture.md</c> R-aut-10 calls the expensive
/// one.
///
/// <para><b>Why it exists, and why it exists as a VERB.</b> The protocol adapter's tool table has a
/// <c>read</c> in it (brief-automation-5-protocol-adapter.md §3), and R-aut-13 says nothing may be
/// reachable through one adapter and not the other. A tool with no verb behind it would have broken
/// that on the day it landed, and the parity gate could not have been written for it at all — so the
/// capability is exposed on BOTH adapters in the same commit. That this was not already here is a
/// gap in the capability surface, not something <c>serve</c> invented; <c>src/Cli/RESOLVED.md</c>
/// records it as one.</para>
///
/// <para><b>It reads through the readers the GUI already reads through</b> — R-aut4-2's rule, applied
/// to loading rather than to validating. A <c>.npy</c> goes through <see cref="DataSetImporter"/> and
/// a Touchstone through <see cref="TouchstoneIO"/> plus <see cref="DataSetBuilder.FromSnp"/>, which
/// is exactly the pair <c>DataSourceEntryViewModel</c> uses to put a file in the Data Display's
/// source library. A second loader here would be a file the CLI and the GUI could disagree about.
/// </para>
///
/// <para><b>A design document comes back as its own bytes</b>, not as a re-serialization. The formats
/// ARE the interface (R-aut-5): a client authors by writing one of these files, so what it needs back
/// is the file. See <see cref="DocumentJson"/>.</para>
///
/// <para><b>It writes nothing</b>, for <c>check</c>'s reason (R-aut4-6): it must run on a read-only
/// tree and on a workspace another process has open.</para>
/// </summary>
internal static class ReadBack
{
    public static int Run(string[] args)
    {
        string? path = null;

        foreach (string a in args)
        {
            if (a.StartsWith('-')) return JsonRun.Fail(CliDiagnostics.ReadUnknownOption(a));
            if (path is not null)  return JsonRun.Fail(CliDiagnostics.ReadMultiplePaths());
            path = a;
        }

        if (path is null)
        {
            int code = JsonRun.Fail(CliDiagnostics.ReadPathRequired());
            Console.Error.WriteLine(
                "Usage: circuitrf read <file> [--only a,b] [--group g] [--at axis=value] "
              + "[--range axis=lo:hi] [--result summary]");
            return code;
        }

        JsonRun.InputPath = path;

        // A directory is refused rather than walked. `read` answers "what is IN this file"; what is
        // in a workspace is a question `check` and `explain` already answer, and walking one here
        // would hand a caller an unbounded document it did not ask for (R-aut-10).
        if (Directory.Exists(path)) return JsonRun.Fail(CliDiagnostics.ReadPathIsAFolder(path));
        if (!File.Exists(path))     return JsonRun.Fail(CliDiagnostics.ReadPathNotFound(path));

        // A result file first, because the extensions do not overlap and a result is what a caller
        // most often has just written.
        string ext = Path.GetExtension(path).ToLowerInvariant();

        if (ext == ".npy")                                    return ReadNpy(path);
        if (TouchstoneIO.ParsePortsFromExtension(path) is not null) return ReadTouchstone(path);

        var kind = DocumentKinds.Classify(path);
        return kind switch
        {
            DocumentKind.Unknown     => JsonRun.Fail(CliDiagnostics.ReadUnsupported(path, ext)),
            // An interchange file is BINARY as often as not (GDSII), and handing back a GDSII stream
            // as a JSON string would be an encoding decision this verb has no business making.
            // `convert` is the verb that reads those, and it is named rather than guessed at.
            DocumentKind.Interchange => JsonRun.Fail(
                CliDiagnostics.ReadInterchange(path, DocumentKinds.InterchangeFormat(path) ?? "interchange")),
            // The one document of circuitRF's OWN that is compiled rather than written. Reading it
            // as text produced a string of whatever the bytes decoded to, with nothing saying so
            // (R-aut10-5).
            DocumentKind.AssemblyRules => JsonRun.Fail(
                CliDiagnostics.ReadBinaryDocument(path, DocumentKinds.Name(kind))),
            // brief-img-2 R-im2-6 — named a picture since IM-2, where it used to fall to Unknown's refusal.
            DocumentKind.Picture     => JsonRun.Fail(CliDiagnostics.ReadPicture(path)),
            _                        => ReadDocument(path, kind),
        };
    }

    // ── result files ─────────────────────────────────────────────────────────

    private static int ReadNpy(string path)
    {
        try
        {
            var (ds, _) = DataSetImporter.Import(path);
            if (IsYieldResult(ds)) return PublishYield(ds, path);
            return Publish(ds, path, "npy");
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ReadFileUnreadable(path, ex.Message)); }
    }

    private static int ReadTouchstone(string path)
    {
        try
        {
            var snp = TouchstoneIO.ReadFile(path);
            if (snp.IsEmpty) return JsonRun.Fail(CliDiagnostics.ReadFileUnreadable(path, "no frequency points"));
            return Publish(DataSetBuilder.FromSnp(snp), path, "touchstone");
        }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ReadFileUnreadable(path, ex.Message)); }
    }

    /// <summary>
    /// Hands the loaded set to the document, and prints the human form: what the file holds, one
    /// line per cube. Deliberately NOT the numbers — a swept loadpull <c>.npy</c> is megabytes of
    /// them, and <c>--json</c> with <c>--only</c>, <c>--at</c> or <c>--range</c> is how a caller asks
    /// for the ones it wants (R-aut5-6, AUT-9 R-aut9-9). The two forms read the same
    /// <see cref="DataSet"/>, so they cannot disagree — and the document's <c>result.shape</c> is
    /// this listing, with the units and the axis extents the terminal has no room for.
    /// </summary>
    private static int Publish(DataSet ds, string path, string kind)
    {
        JsonRun.Data = ds;

        Console.WriteLine($"{path}  ({kind})");
        foreach (string group in ds.Groups)
        {
            var cubes = ds.CubesIn(group);
            if (cubes.Count == 0) continue;
            Console.WriteLine($"  group {(group.Length == 0 ? "(default)" : group)}:");
            foreach (var (name, cube) in cubes.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                string axes = string.Join(" x ", cube.Axes.Select(a =>
                    $"{a.Name}[{a.Length}]{(string.IsNullOrEmpty(a.Unit) ? "" : $" {a.Unit}")}"));
                Console.WriteLine($"    {name,-24} {cube.DataKind,-8} {axes}");
            }
        }
        return 0;
    }

    // ── a Monte Carlo or yield result (brief-yield-5 R-ya5-6) ───────────────────

    private const string YieldGroup = "yield";

    /// <summary>A <c>&lt;design&gt;.yield.npy</c>: a <c>yield</c> group holding the run's summary.</summary>
    private static bool IsYieldResult(DataSet ds)
        => ds.ContainsGroup(YieldGroup) && ds.CubesIn(YieldGroup).ContainsKey("trials") && ds.CubesIn(YieldGroup).ContainsKey("mode");

    /// <summary>
    /// The summary group FIRST — in the listing and in the document's groups — then the cubes as for any result.
    /// It is the answer a caller opened the file for; the trial-stacked cubes are the evidence. The numbers are the
    /// group's own cubes, read back, never recomputed.
    /// </summary>
    private static int PublishYield(DataSet ds, string path)
    {
        var ordered = new DataSet();
        foreach (var (name, cube) in ds.CubesIn(YieldGroup)) ordered.AddToGroup(YieldGroup, name, cube);
        foreach (string group in ds.Groups.Where(g => g != YieldGroup))
            foreach (var (name, cube) in ds.CubesIn(group)) ordered.AddToGroup(group, name, cube);

        var y = ds.CubesIn(YieldGroup);
        double Num(string name) => y.TryGetValue(name, out var c) && c.Rank == 0 ? c.RealValues[0] : double.NaN;
        string Text(string name) => y.TryGetValue(name, out var c) && c.Rank == 1 && c.Axes[0].Labels is { Length: > 0 } l ? l[0] : "";
        static string Pct(double f) => double.IsFinite(f) ? (f * 100).ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + " %" : "—";

        Console.WriteLine($"{path}  ({Text("mode")} result)");
        Console.WriteLine($"  {Num("trials"):0} trials · {Num("did_not_evaluate"):0} did not evaluate · seed {Num("seed"):0} · " +
                          $"sampling {Text("sampling")} · {Text("stopped")}");
        if (double.IsFinite(Num("yield")))
            Console.WriteLine($"  yield {Pct(Num("yield"))} ({Pct(Num("confidence"))} interval {Pct(Num("lower"))} – {Pct(Num("upper"))})" +
                              (double.IsFinite(Num("target")) ? $" · target {Pct(Num("target"))}" : ""));
        foreach (var name in y.Keys.Where(k => k.StartsWith("goal:", StringComparison.Ordinal) && k.EndsWith(":yield", StringComparison.Ordinal)))
        {
            string prefix = name[..^"yield".Length];
            Console.WriteLine($"  {name["goal:".Length..^":yield".Length]}: {Pct(Num(name))} " +
                              $"({Pct(Num(prefix + "lower"))} – {Pct(Num(prefix + "upper"))})");
        }
        return Publish(ordered, path, "npy");
    }

    // ── circuitRF's own documents ────────────────────────────────────────────

    private static int ReadDocument(string path, DocumentKind kind)
    {
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) { return JsonRun.Fail(CliDiagnostics.ReadFileUnreadable(path, ex.Message)); }

        JsonRun.Document = new DocumentJson(path, DocumentKinds.Name(kind), text);

        // Verbatim on stdout too — Write, not WriteLine, so `circuitrf read x.csch > y.csch` is a
        // copy and not a copy with a newline added to it.
        Console.Out.Write(text);
        return 0;
    }
}
