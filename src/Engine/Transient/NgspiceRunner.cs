// ================================================================
//  NgspiceRunner.cs  —  a transient analysis solved by ngspice
//  (docs/design/transient.md §9.3; brief TRN-7 R-trn7-4..7)
//
//  The bench is written by SpiceDeckWriter (TRN-6) into a scratch folder with its name map, ngspice is run on it in batch
//  mode with a binary rawfile as its output, and the rawfile is read back (RawFileReader), its vectors renamed through the map
//  and resampled onto the output grid with the native engine's own interpolation. The DataSet has the native run's cubes.
//  ngspice is a separate process, never a linked library (D8): a crash in it is a failed run, refused with its last message.
// ================================================================

using System.Diagnostics;
using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist.Spice;
using RfCore.Data;
using RfCore.Spice;

namespace CircuitRF.Engine.Transient;

/// <summary>The ngspice a run uses: its program and the version <c>ngspice -v</c> printed.</summary>
public sealed record NgspiceProgram(string Path, string Version);

/// <summary>Runs a transient analysis on ngspice (brief TRN-7). The program comes from <see cref="Locate"/>; with none, the run is
/// refused with <see cref="NotFound"/> and never runs native (D2, R-trn7-7).</summary>
public static class NgspiceRunner
{
    /// <summary>The refusal when no usable ngspice is discovered: where to install one and the verb that does it (R-trn7-7).</summary>
    public const string NotFound =
        "Solver=ngspice, and no usable ngspice was found on this machine. Install it, or name the one you have, in Settings ▸ Solvers; " +
        "headless: circuitrf solver install ngspice. The run does not fall back to the native solver.";

    /// <summary>The CLI verb that installs ngspice, which <see cref="NotFound"/> names.</summary>
    public const string InstallVerb = "circuitrf solver install ngspice";

    /// <summary>Finds the ngspice to run. Installed by <c>src/Design</c> (its discovery, <c>SolverDiscovery.Ngspice</c>), read at the
    /// moment a run needs it so an install or a changed setting is seen with no restart. Null finds nothing.</summary>
    public static Func<NgspiceProgram?>? Locator { get; set; }

    private static readonly AsyncLocal<Func<NgspiceProgram?>?> Scoped = new();

    /// <summary>Replaces <see cref="Locator"/> for this asynchronous flow until disposed: a test's stub, which other tests running
    /// beside it do not see.</summary>
    public static IDisposable Override(Func<NgspiceProgram?> locate)
    {
        var before = Scoped.Value;
        Scoped.Value = locate;
        return new Restore(() => Scoped.Value = before);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    /// <summary>The ngspice a run would use now, or null.</summary>
    public static NgspiceProgram? Locate() => (Scoped.Value ?? Locator)?.Invoke();

    /// <summary>The metadata cube a ngspice run's DataSet carries: its axis labels are <c>Ngspice.Version = 47</c> and
    /// <c>Ngspice.Deck = …</c> (R-trn7-6).</summary>
    public const string ProvenanceCube = "__Ngspice";

    /// <summary>The <c>key = value</c> lines of <see cref="ProvenanceCube"/>; empty when the DataSet was not solved by ngspice.</summary>
    public static IReadOnlyList<string> Provenance(DataSet data)
        => data.Contains(ProvenanceCube) && data[ProvenanceCube].Axes is [var a] && a.Labels is { } l ? l : [];

    /// <summary>Solves <paramref name="p"/> on <paramref name="nl"/> with ngspice. The deck, its map, ngspice's log and its rawfile
    /// are kept in a scratch folder under <paramref name="outputDirectory"/> (the system temp folder when null).</summary>
    public static DataSet Run(TransientParams p, ElaboratedNetlist nl, string? outputDirectory, RunControl? control = null,
                              string benchName = "", string version = "")
    {
        string who = p.Name.Length > 0 ? $"transient '{p.Name}'" : "transient";
        var a = p.Directive ?? throw new TransientRefusedException($"{who}: the ngspice solver needs the analysis directive.");
        var program = Locate() ?? throw new TransientRefusedException($"{who}: {NotFound}");
        control?.ThrowIfCancellationRequested();

        var deck = SpiceDeckWriter.Write(new SpiceDeckRequest
        {
            Netlist = nl, Analysis = a, BenchName = benchName, Version = version, Fits = new TransientSpiceFits(),
        });
        if (deck.Text is not { } text)
            throw new TransientRefusedException(
                $"{who}: the SPICE dialect has no exact translation of {deck.Refusals.Count} element{(deck.Refusals.Count == 1 ? "" : "s")}, " +
                "so ngspice was not run: " + string.Join("; ", deck.Refusals.Select(r => r.Message)));

        string dir = ScratchFolder(outputDirectory, p.Name);
        string deckPath = Path.Combine(dir, "deck" + SpiceDeckWriter.Extension);
        string mapPath = Path.Combine(dir, "names.txt");
        string rawPath = Path.Combine(dir, "out.raw");
        string logPath = Path.Combine(dir, "ngspice.log");
        File.WriteAllText(deckPath, text, new UTF8Encoding(false));
        File.WriteAllText(mapPath, MapText(deck), new UTF8Encoding(false));

        string tag = $"ngspice {program.Version}";
        var (exit, stdout, stderr) = Execute(program.Path, ["-b", "-n", "-r", rawPath, deckPath], dir, control, logPath);
        var said = stderr.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        // ngspice's own messages, verbatim, prefixed with its name and version (R-trn7-4)
        foreach (var line in said) nl.AddNoteOnce($"ngspice-msg:{line}", $"{tag}: {line}");
        string Last() => (said.Count > 0 ? said[^1]
                          : stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0)) ?? "it printed nothing";

        if (exit != 0)
            throw new TransientRefusedException($"{who}: {tag} stopped with exit code {exit}: {Last()} (its deck and log: {dir})");
        if (!File.Exists(rawPath))
            throw new TransientRefusedException($"{who}: {tag} wrote no rawfile: {Last()} (its deck and log: {dir})");

        RawPlot plot;
        try
        {
            plot = RawFileReader.Read(rawPath).LastOrDefault(pl => pl.Plotname.Contains("transient", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidDataException("it holds no transient plot");
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or OverflowException)
        {
            throw new TransientRefusedException($"{who}: {tag}'s rawfile could not be read: {ex.Message} (its deck and log: {dir})");
        }

        var ds = Pack(p, nl, deck, plot, who, tag, Last);
        ds.Add(ProvenanceCube, new DataCube([new Axis("field", [0, 1], "", [$"Ngspice.Version = {program.Version}", $"Ngspice.Deck = {deckPath}"])],
                                            [0.0, 1.0]));
        nl.AddNoteOnce($"ngspice-ran:{p.Name}", $"{who} was solved by {tag} ({program.Path}); its deck: {deckPath}");
        // the approximate translations, each labelled (R-trn7-6)
        foreach (var (_, sentence) in deck.Fits) nl.AddNoteOnce($"ngspice-fit:{sentence}", sentence);
        foreach (var l in deck.Lines) nl.AddNoteOnce($"ngspice-line:{l.Instance}", l.Sentence);
        return ds;
    }

    /// <summary>The DataSet of the run: the native run's cubes from ngspice's points.</summary>
    private static DataSet Pack(TransientParams p, ElaboratedNetlist nl, SpiceDeck deck, RawPlot plot, string who, string tag, Func<string> last)
    {
        double[] t = plot.RealOf("time") ?? throw new TransientRefusedException($"{who}: {tag}'s rawfile has no time vector: {last()}");
        // ngspice may mark a point's time negative in some builds' padding; the magnitude is the time
        for (int k = 0; k < t.Length; k++) t[k] = Math.Abs(t[k]);
        if (t.Length == 0) throw new TransientRefusedException($"{who}: {tag}'s rawfile has no points: {last()}");

        double[] Vector(string deckVector, string what)
            => plot.RealOf(deckVector) ?? throw new TransientRefusedException(
                $"{who}: {tag}'s rawfile has no vector {deckVector} ({what}): {last()}");

        var rec = TransientEngine.RecordedNodes(nl);
        var byNet = deck.Saved.Where(s => s.Vector.StartsWith("v(", StringComparison.OrdinalIgnoreCase))
                              .GroupBy(s => s.CircuitRf, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Vector, StringComparer.Ordinal);
        var nodeVec = rec.Select(r => byNet.TryGetValue(r.name, out var v) ? Vector(v, $"net {r.name}")
                                      : throw new TransientRefusedException($"{who}: the deck saves no voltage of net {r.name}.")).ToArray();

        var probes = nl.Components.Where(c => c.Model is IProbeModel && c.Nodes.Length >= 2).ToArray();
        var byPath = deck.Saved.Where(s => s.Vector.StartsWith("i(", StringComparison.OrdinalIgnoreCase))
                               .ToDictionary(s => s.CircuitRf, s => s.Vector, StringComparer.Ordinal);
        var probeVec = probes.Select(c => byPath.TryGetValue(c.InstancePath, out var v) ? Vector(v, $"IProbe {c.InstancePath}")
                                         : throw new TransientRefusedException($"{who}: the deck saves no current of {c.InstancePath}.")).ToArray();
        // the probe's first net, a recorded net (RecordedNodes adds it)
        var probeNodeCol = probes.Select(c => c.Nodes[0] > 0 ? Array.FindIndex(rec, r => r.row == c.Nodes[0] - 1) : -1).ToArray();

        int nRec = rec.Length, nPr = probes.Length;
        double[] Point(int k)
        {
            var s = new double[nRec + nPr];
            for (int i = 0; i < nRec; i++) s[i] = nodeVec[i][k];
            // the dialect's branch current through a source flows from its positive node to its negative inside the source: the
            // IProbe is written as a zero-volt source from its first net to its second, so that is circuitRF's own direction
            for (int i = 0; i < nPr; i++) s[nRec + i] = probeVec[i][k];
            return s;
        }

        var tOut = TransientEngine.OutputGrid(p, who);
        var outVals = Resample(t, Point, tOut);
        var result = new TransientRunResult
        {
            Time = tOut, NodeNames = [.. rec.Select(r => r.name)], ProbeNames = [.. probes.Select(c => c.InstancePath)],
            V = [.. outVals.Select(s => s[..nRec])], I = [.. outVals.Select(s => s[nRec..])],
            P = [.. outVals.Select(s => Enumerable.Range(0, nPr).Select(i => (probeNodeCol[i] >= 0 ? s[probeNodeCol[i]] : 0) * s[nRec + i]).ToArray())],
            VProbe = [.. outVals.Select(s => Enumerable.Range(0, nPr).Select(i => probeNodeCol[i] >= 0 ? s[probeNodeCol[i]] : 0).ToArray())],
            // ngspice's first point is the run's starting state (its operating point, or the held values under uic)
            OpV = Point(0)[..nRec],
            StepTime = p.SaveSteps ? (double[])t.Clone() : null,
            StepV = p.SaveSteps ? [.. Enumerable.Range(0, t.Length).Select(k => Point(k)[..nRec])] : null,
            AcceptedSteps = Math.Max(0, t.Length - 1),
            MaxStepS = p.MaxStepS,
        };
        var ds = TransientRunner.Pack(result, nl);
        // the native solver's own counters mean nothing here; the accepted steps are ngspice's points
        foreach (string c in new[] { "Tran.RejectedSteps", "Tran.NewtonIterations", "Tran.Factorizations", "Tran.DeviceEvaluations", "__TranMaxStep_s" })
            ds.RemoveFromGroup(DataSet.DefaultGroup, c);
        return ds;
    }

    /// <summary>Each output time's values from the points (<paramref name="t"/>, <paramref name="point"/>), through the native
    /// engine's interpolation: quadratic through the three points ending at the first at or after it, linear on the first interval
    /// and where two points share a time (a breakpoint), the first point's value before it.</summary>
    internal static double[][] Resample(double[] t, Func<int, double[]> point, double[] tOut)
    {
        var o = new double[tOut.Length][];
        int k = 0;   // the first point at or after tOut[j]
        for (int j = 0; j < tOut.Length; j++)
        {
            double tj = tOut[j];
            while (k < t.Length - 1 && t[k] < tj * (1 - 1e-12) - 1e-300) k++;
            if (k == 0 || tj <= t[0]) { o[j] = point(0); continue; }
            if (t[k] < tj) { o[j] = point(t.Length - 1); continue; }   // past the last point
            double[]? s0 = k >= 2 && t[k - 2] < t[k - 1] ? point(k - 2) : null;
            o[j] = TransientEngine.Interpolate(tj, k >= 2 ? t[k - 2] : 0, s0, t[k - 1], point(k - 1), t[k], point(k));
        }
        return o;
    }

    /// <summary>The scratch folder of one run: <c>ngspice/&lt;analysis&gt;-&lt;time&gt;</c> under the output directory.</summary>
    private static string ScratchFolder(string? outputDirectory, string analysis)
    {
        string root = outputDirectory is { Length: > 0 } ? Path.Combine(outputDirectory, "ngspice") : Path.Combine(Path.GetTempPath(), "circuitrf-ngspice");
        string safe = new string((analysis.Length > 0 ? analysis : "tran").Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_').ToArray());
        string dir = Path.Combine(root, $"{safe}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string MapText(SpiceDeck deck)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# deck name = circuitRF name");
        foreach (var (net, d) in deck.Names.Nets) sb.Append("net ").Append(d).Append(" = ").AppendLine(net);
        foreach (var (path, d) in deck.Names.Instances) sb.Append("instance ").Append(d).Append(" = ").AppendLine(path);
        foreach (var (v, c) in deck.Saved) sb.Append("vector ").Append(v).Append(" = ").AppendLine(c);
        return sb.ToString();
    }

    /// <summary>Runs ngspice to its end, or kills it (and everything it started) on cancel. Its output is written to
    /// <paramref name="logPath"/> as it arrives.</summary>
    private static (int Exit, string Stdout, string Stderr) Execute(string program, IReadOnlyList<string> args, string dir,
                                                                    RunControl? control, string logPath)
    {
        var psi = new ProcessStartInfo(program)
        {
            WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new Lock();
        using var proc = new Process { StartInfo = psi };
        proc.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (gate) { stdout.AppendLine(e.Data); log.WriteLine(e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is null) return; lock (gate) { stderr.AppendLine(e.Data); log.WriteLine(e.Data); } };
        try { proc.Start(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new TransientRefusedException($"ngspice at '{program}' could not be started: {ex.Message}");
        }
        // never outlive circuitRF
        EventHandler onExit = (_, _) => Kill(proc);
        AppDomain.CurrentDomain.ProcessExit += onExit;
        try
        {
            proc.StandardInput.Close();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            control?.BeginStage("ngspice");
            while (!proc.WaitForExit(50))
            {
                if (control is { } c && (c.Token.IsCancellationRequested || c.StopRequested))
                {
                    Kill(proc);
                    proc.WaitForExit();
                    throw new OperationCanceledException("the ngspice run was cancelled", c.Token);
                }
            }
            proc.WaitForExit();   // the no-timeout overload waits for the redirected readers to reach end of stream
            lock (gate) return (proc.ExitCode, stdout.ToString(), stderr.ToString());
        }
        finally { AppDomain.CurrentDomain.ProcessExit -= onExit; }
    }

    private static void Kill(Process proc)
    {
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }
}
