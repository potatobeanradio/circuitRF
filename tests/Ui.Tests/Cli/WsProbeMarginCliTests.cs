// ================================================================
//  WsProbeMarginCliTests.cs — brief-wsprobe-9's gates (j) and (l).
//
//    (l) the run summary line and the --json fields of R-wsp9-2, from the REAL CLI as a separate
//        process, against the in-process DataSet; the MarginThreshold diagnostic on stderr; and
//        `explain --analysis` reporting the effective threshold (R-wsp9-4);
//    (j) the RETIREMENT of D-12's placeholders — a source scan of src/ and docs/design/ finds none
//        of the four names, and `wsp_stability_margin` evaluates to a Real cube instead of refusing.
//
//  The margin's own arithmetic is gated in tests/RfCore.Tests/Stability/WspMarginTests.cs and its
//  behaviour on real circuits in tests/Engine.Tests/Linear/WspMarginEngineTests.cs; what this file
//  holds is that the VERBS report what the engine produced.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using CircuitRF.Engine;
using RfCore.Data;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Cli;

public sealed class WsProbeMarginCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-wsp9-cli-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private static string Fixture(string name) => Path.Combine(RepoRoot(), "testdata", "wsprobe", name);

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    // ══ (l) — the summary line and the --json fields ═════════════════════════

    /// <summary>
    /// R-wsp9-2. Each probe's line gains the minimum of each margin over the sweep with its
    /// frequency, in dB; the <c>--json</c> row gains the same four numbers <b>linear</b>, because dB
    /// is a display convention (<c>20·log10</c>, overview D-16) and a document carries the number.
    /// Both are checked against the in-process <c>DataSet</c>, so the verb cannot report one thing
    /// while the engine computed another.
    /// </summary>
    [Fact]
    public void Sparam_ReportsEachMarginsMinimumOnTheLine_AndCarriesItLinearInTheJson()
    {
        string outPath = Path.Combine(Dir("summary"), "split.npy");
        var run = RunCli("sparam", Fixture("margin_split_resonator.cnl"),
                         "-o", outPath, "--json", "--result", "summary");
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        output.WriteLine(run.StdOut);

        // The in-process truth.
        var (lib, tb) = CnlReader.ReadFile(Fixture("margin_split_resonator.cnl"));
        using var nl = new Elaborator(lib).Elaborate(tb);
        var spa   = tb.Analyses.OfType<SParameterAnalysis>().First();
        var freqs = spa.Expand(nl.ResolvedGlobals);
        var ds    = SParameterEngine.Run(nl, freqs);
        var (yMin, yHz) = Minimum(ds["SM_Y0:P"].RealValues, freqs);
        var (hMin, hHz) = Minimum(ds["SM_H0:P"].RealValues, freqs);

        using var doc = JsonDocument.Parse(run.StdOut);
        var probe = doc.RootElement.GetProperty("result").GetProperty("wsprobes")[0];
        Assert.Equal("P", probe.GetProperty("label").GetString());
        Assert.Equal(yMin, probe.GetProperty("smY0Min").GetDouble(),   15);
        Assert.Equal(yHz,  probe.GetProperty("smY0MinHz").GetDouble(), 0);
        Assert.Equal(hMin, probe.GetProperty("smH0Min").GetDouble(),   15);
        Assert.Equal(hHz,  probe.GetProperty("smH0MinHz").GetDouble(), 0);

        // The human line carries the SAME numbers, in dB.
        var text = RunCli("sparam", Fixture("margin_split_resonator.cnl"),
                          "-o", Path.Combine(Dir("summary2"), "split.npy"));
        Assert.True(text.ExitCode == 0, text.StdErr + text.StdOut);
        string line = Assert.Single(text.StdOut.Split('\n'), l => l.StartsWith("WSProbe P "));
        output.WriteLine(line);
        Assert.Contains($"SM_Y0 min {Db(yMin)}", line);
        Assert.Contains($"SM_H0 min {Db(hMin)}", line);
        Assert.Contains($"@ {yHz / 1e9:G6} GHz", line);
        Assert.Contains($"@ {hHz / 1e9:G6} GHz", line);
    }

    /// <summary>
    /// R-wsp9-3 through the verb: the Info diagnostic reaches the CLI through the existing warnings
    /// channel, names the probe, both minima and the −12 dB rule; and <c>MarginThreshold=none</c> on
    /// the directive silences it without changing anything else the run reports.
    /// </summary>
    [Fact]
    public void Sparam_ReportsTheMarginThresholdDiagnostic_AndMarginThresholdNoneSilencesIt()
    {
        var run = RunCli("sparam", Fixture("margin_split_resonator.cnl"),
                         "-o", Path.Combine(Dir("note"), "split.npy"));
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        string all = run.StdOut + run.StdErr;
        output.WriteLine(all);
        Assert.Contains("WSProbe 'P': stability margin below", all);
        Assert.Contains("SM_Y0 = −18.1 dB at 1.59125 GHz", all);
        Assert.Contains("SM_H0 = −19.8 dB at 1.73375 GHz", all);
        Assert.Contains("negative resistance", all);
        Assert.Contains("EuMIC 2024", all);

        // The same design with MarginThreshold=none: silent, and the margin cubes still written.
        string dir  = Dir("quiet");
        string path = Path.Combine(dir, "quiet.cnl");
        File.WriteAllText(path, File.ReadAllText(Fixture("margin_split_resonator.cnl"))
            .Replace("npts=2001 Unit=GHz", "npts=2001 Unit=GHz MarginThreshold=none"));
        var quiet = RunCli("sparam", path, "-o", Path.Combine(dir, "quiet.npy"));
        Assert.True(quiet.ExitCode == 0, quiet.StdErr + quiet.StdOut);
        Assert.DoesNotContain("stability margin below", quiet.StdOut + quiet.StdErr);
        Assert.Contains("SM_Y0 min", quiet.StdOut);          // the numbers are still reported
    }

    /// <summary>
    /// R-wsp9-4. <c>explain --analysis</c> lists the effective <c>MarginThreshold</c> beside the
    /// probe list, in dB or as the word <c>none</c> — the default is not written in the document, so
    /// it is reported rather than left to be assumed.
    /// </summary>
    [Fact]
    public void Explain_ListsTheEffectiveMarginThreshold()
    {
        var run = RunCli("explain", Fixture("margin_split_resonator.cnl"), "--analysis");
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        output.WriteLine(run.StdOut);
        Assert.Contains("MarginThreshold: -15 dB", run.StdOut);

        string dir  = Dir("explain");
        string path = Path.Combine(dir, "quiet.cnl");
        File.WriteAllText(path, File.ReadAllText(Fixture("margin_split_resonator.cnl"))
            .Replace("npts=2001 Unit=GHz", "npts=2001 Unit=GHz MarginThreshold=none"));
        var quiet = RunCli("explain", path, "--analysis", "--json");
        Assert.True(quiet.ExitCode == 0, quiet.StdErr + quiet.StdOut);
        using var doc = JsonDocument.Parse(quiet.StdOut);
        var analyses = doc.RootElement.GetProperty("result").GetProperty("explain").GetProperty("analyses");
        Assert.Equal("none", analyses[0].GetProperty("marginThreshold").GetString());
    }

    /// <summary>
    /// The directive key survives a <c>.cnl</c> round trip — the standing check of
    /// <c>src/Core/CLAUDE.md</c>: a field <c>CnlWriter</c> cannot say is silently absent from every
    /// run, because the GUI's own Simulate writes a netlist and reads it back.
    /// </summary>
    [Fact]
    public void MarginThreshold_SurvivesTheCnlRoundTrip()
    {
        foreach (string spelling in (string[])["-25", "none"])
        {
            string text = File.ReadAllText(Fixture("margin_split_resonator.cnl"))
                .Replace("npts=2001 Unit=GHz", $"npts=2001 Unit=GHz MarginThreshold={spelling}");
            var (lib, tb) = new CnlReader().Read(text, "tb", null);
            var read = tb.Analyses.OfType<SParameterAnalysis>().Single();
            Assert.Equal(spelling, read.MarginThresholdExpr);

            string written = CnlWriter.Write(tb, lib);
            Assert.Contains($"MarginThreshold={spelling}", written);
            var (_, tb2) = new CnlReader().Read(written, "tb", null);
            Assert.Equal(spelling, tb2.Analyses.OfType<SParameterAnalysis>().Single().MarginThresholdExpr);
        }

        // The default is not written, so a document that never mentioned it round-trips unchanged.
        var (lib0, tb0) = CnlReader.ReadFile(Fixture("margin_split_resonator.cnl"));
        Assert.DoesNotContain("MarginThreshold", CnlWriter.Write(tb0, lib0));
    }

    // ══ (j) — the retirement of D-12's placeholders ══════════════════════════

    /// <summary>
    /// Gate (j). <c>wsp_nZ</c>, <c>wsp_nY</c>, <c>NormalizedLocus</c> and
    /// <c>margin-not-transcribed</c> are gone from <c>src/</c> and <c>docs/design/</c> — two
    /// normalised stability quantities beside each other, one Winslow's and one ours, is exactly the
    /// confusion the notation rule of overview §1 exists to prevent, and D-12 promised the
    /// placeholders would go the day the margin was published.
    ///
    /// <para>Comments are stripped before the scan, so a note SAYING these were retired does not
    /// fail the gate that they were.</para>
    /// </summary>
    [Fact]
    public void TheD12Placeholders_AreGoneFromTheSourceAndTheDesignNotes()
    {
        string root = RepoRoot();
        string[] names = ["wsp_nZ", "wsp_nY", "NormalizedLocus", "margin-not-transcribed",
                          "MarginNotTranscribed"];
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            string stripped = StripComments(File.ReadAllText(file));
            foreach (string n in names)
                if (stripped.Contains(n, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(root, file)}: {n}");
        }

        // The design notes carry no comment syntax to strip; a mention there IS a mention.
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "docs", "design"), "*.md"))
            foreach (string n in names)
                if (File.ReadAllText(file).Contains(n, StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(root, file)}: {n}");

        Assert.True(offenders.Count == 0,
            "D-12's placeholders survive:\n    " + string.Join("\n    ", offenders));
    }

    /// <summary>
    /// The other half of (j): the name D-12 reserved now ANSWERS. Through the CLI, over a real run,
    /// as a Real cube — and the retired spellings are an unknown-function error rather than a
    /// second normalised quantity.
    /// </summary>
    [Fact]
    public void WspStabilityMargin_Answers_AndTheRetiredNamesDoNot()
    {
        string dir  = Dir("retire");
        string path = Path.Combine(dir, "retired.cnl");
        File.WriteAllText(path, File.ReadAllText(Fixture("margin_split_resonator.cnl")) +
            "\nmeasure sm = wsp_stability_margin(SP1.wsp, 1)\n");
        string outPath = Path.Combine(dir, "retired.npy");
        var run = RunCli("sparam", path, "-o", outPath);
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);

        var (ds, _) = RfCore.Export.DataSetImporter.Import(outPath);
        var sm = ds["measurements.sm"];
        Assert.Equal(DataKind.Real, sm.DataKind);
        Assert.Equal(2001, sm.RealValues.Length);
        foreach (double v in sm.RealValues) Assert.InRange(v, 0.0, 1.0);

        // wsp_nZ is not a function any more.
        string bad = Path.Combine(dir, "bad.cnl");
        File.WriteAllText(bad, File.ReadAllText(Fixture("margin_split_resonator.cnl")) +
            "\nmeasure nz = wsp_nZ(SP1.wsp, 1)\n");
        var badRun = RunCli("sparam", bad, "-o", Path.Combine(dir, "bad.npy"));
        string msg = badRun.StdOut + badRun.StdErr;
        output.WriteLine(msg);
        Assert.Contains("wsp_nZ", msg);
        Assert.DoesNotContain("normalised", msg, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The <b>harmonic-balance</b> verb prints the same probe line, over <c>ssfreq</c> instead of
    /// <c>freq</c> — brief-wsprobe-5's "the run summary prints the per-operating-point minimum",
    /// and R-wsp1-12(a)'s label ↔ idx map, which a caller cannot guess because <c>idx</c> depends
    /// on the other probes. Both were absent from <c>hb</c>: the run wrote every <c>wsp</c> cube
    /// and no way to read them by, and the margin minimum the threshold note is measured against
    /// was nowhere on stdout or in <c>--json</c>.
    /// </summary>
    [Fact]
    public void Hb_ReportsTheSameProbeLineOverSsfreq_AndCarriesItInTheJson()
    {
        string dir  = Dir("hb");
        string path = Path.Combine(dir, "pumped.cnl");
        File.WriteAllText(path, File.ReadAllText(Fixture("hb_pumped_two_port.cnl"))
            .Replace("Tol=1e-10", "Tol=1e-10 SSStart=0.35 SSStop=3.15 SSNpts=8 SSUnit=GHz"));

        var run = RunCli("hb", path, "--json", "--result", "summary");
        Assert.True(run.ExitCode == 0, run.StdErr + run.StdOut);
        using var doc = JsonDocument.Parse(run.StdOut);
        var probes = doc.RootElement.GetProperty("result").GetProperty("wsprobes");
        Assert.Equal(2, probes.GetArrayLength());
        Assert.Equal("GATE",  probes[0].GetProperty("label").GetString());
        Assert.Equal(1,       probes[0].GetProperty("idx").GetInt32());
        Assert.Equal("DRAIN", probes[1].GetProperty("label").GetString());
        Assert.Equal(2,       probes[1].GetProperty("idx").GetInt32());

        // The frequency the minimum is reported at is a point of the SSFREQ grid, not of the
        // tone grid — the axis the summary reads is the probe's own.
        double yHz = probes[0].GetProperty("smY0MinHz").GetDouble();
        Assert.InRange(yHz, 0.35e9, 3.15e9);

        var text = RunCli("hb", path);
        Assert.True(text.ExitCode == 0, text.StdErr + text.StdOut);
        string line = Assert.Single(text.StdOut.Split('\n'), l => l.StartsWith("WSProbe GATE "));
        output.WriteLine(line);
        Assert.Contains("idx=1", line);
        Assert.Contains($"SM_Y0 min {Db(probes[0].GetProperty("smY0Min").GetDouble())}", line);
        Assert.Contains($"@ {yHz / 1e9:G6} GHz", line);

        // R-wsp1-12: an HB run with no small-signal sweep carries no probe cubes and the line is
        // silent, so nothing an unprobed or un-swept run printed before has changed.
        var bare = RunCli("hb", Fixture("hb_pumped_two_port.cnl"));
        Assert.True(bare.ExitCode == 0, bare.StdErr + bare.StdOut);
        Assert.DoesNotContain("WSProbe GATE idx=", bare.StdOut);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static (double Min, double Hz) Minimum(double[] v, double[] freqs)
    {
        int best = 0;
        for (int k = 1; k < v.Length; k++) if (v[k] < v[best]) best = k;
        return (v[best], freqs[best]);
    }

    /// <summary>The dB spelling the summary line uses: <c>20·log10</c>, one decimal, typographic
    /// minus (overview D-16).</summary>
    private static string Db(double linear)
    {
        string s = $"{20.0 * Math.Log10(linear):F1}";
        return s.StartsWith('-') ? "−" + s[1..] : s;
    }

    /// <summary>Line and block comments removed, so a source scan measures the CODE. String
    /// literals are left alone — a retired name inside one would still be a live reference.</summary>
    private static string StripComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"^\s*///?.*$", "", RegexOptions.Multiline);
    }

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(WsProbeMarginCliTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path), $"the CLI was not built beside these tests: {path}");
        return path;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }
}
