// brief-em3d-9 R-em3d9-3 — the openEMS runs, one per port, and what is read back.
//
// Stage, run, watch, read. The executable is started from the ABSOLUTE path discovery resolved (brief 6),
// through Em3dProcessLauncher (the one door, which counts), in the port's own directory p<k>/, with its
// output logged beside the probe files it writes. Cancellation kills the process tree.
//
// ONE RUN PER PORT (R-em3d9-3a). openEMS excites one port per simulation and S needs every port excited,
// so an N-port is N runs, sequential, each using every core. The progress line says "port k of N".
//
// WHEN A RUN HAS CONVERGED (R-em3d9-3d, and em-3d.md §3 as F0 corrected it). openEMS's own criterion is
// the FIELD ENERGY left in the domain, and F0 found it unreachable on the commonest board geometry: a
// conductor floating in an open domain holds a static field no port drains and no absorber removes
// (case B: energy stuck at −0.4 dB after 38 ns while the port signals were 129 dB down after 1 ns). Its
// exit code is 0 whether or not the criterion was met (Q11). So circuitRF watches the PORTS: it reads
// every probe file as openEMS writes it (openEMS ends each sample's line with a flush), and when every
// port's voltage and current has fallen EndCriterionDb below its peak over the last pulse length, it
// creates the file ABORT in the run's directory — openEMS's own graceful stop, which ends the time loop
// at its next check and still post-processes. openEMS's energy criterion is written at the same level,
// as a second way to finish. A run that reaches MaxTimeSteps with neither is NOT converged: its result is
// written and carries a warning stating the level reached against the criterion.

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Engine;
using CircuitRF.Engine.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>What openEMS printed about one run.</summary>
/// <param name="TimeStepS">"FDTD timestep is: …" — the step openEMS chose.</param>
/// <param name="Cells">"FDTD simulation size: … --> N FDTD cells".</param>
/// <param name="StepsRun">"Time for N iterations …".</param>
/// <param name="EnergyCriterionMet">"end-criteria of … reached".</param>
/// <param name="MaxStepsReached">"Max. number of timesteps was reached …".</param>
/// <param name="Aborted">"Found file "ABORT"" — circuitRF stopped it on the ports' decay.</param>
/// <param name="EnergyDb">The last field-energy level it reported, dB below its peak (negative).</param>
public sealed record OpenEmsLogFacts(double? TimeStepS, long? Cells, long? StepsRun, bool EnergyCriterionMet,
                                     bool MaxStepsReached, bool Aborted, double? EnergyDb);

/// <summary>How one port's run ended, and — when it did — what it recorded.</summary>
/// <param name="DecayDb">How far below their peaks the port signals had fallen over the last pulse
/// length, dB (negative; −∞ when they reached zero; NaN when the run stopped before one pulse length
/// past the pulse, so there is no decay to measure).</param>
/// <param name="Converged">The ports decayed to the criterion, or openEMS's energy criterion was met.</param>
public sealed record OpenEmsPortRun(int Port, bool Ok, bool Cancelled, string? Message, OpenEmsLogFacts? Facts,
                                    double DecayDb, bool Converged, IReadOnlyList<FdtdPortProbes>? Probes);

public static class OpenEmsRun
{
    public const string LogFile   = "openems.log";
    public const string AbortFile = "ABORT";

    /// <summary>The directory port <paramref name="port"/>'s run works in, under the run directory.</summary>
    public static string PortDirectory(int port) => "p" + port.ToString(CultureInfo.InvariantCulture);

    private static long _runs;

    /// <summary>How many times this process has started openEMS.</summary>
    public static long Invocations => Interlocked.Read(ref _runs);

    /// <summary>Every openEMS process, as it starts — so a test can look for it after a cancellation.</summary>
    internal static event Action<Process>? ProcessStarted;

    // ── Staging ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the shared model into <paramref name="runDir"/> and each port's file into its own
    /// directory, removing anything an earlier or a killed run left there — a probe file from another
    /// run is never read as this one's.
    /// </summary>
    public static void Stage(string runDir, CsxcadLowering lowering)
    {
        ArgumentNullException.ThrowIfNull(lowering);
        if (!lowering.Ok) throw new ArgumentException(lowering.Refusal, nameof(lowering));
        Directory.CreateDirectory(runDir);
        WriteText(Path.Combine(runDir, CsxcadWriter.ModelFile), lowering.Model!);
        for (int k = 0; k < lowering.Ports.Count; k++)
        {
            string dir = Path.Combine(runDir, PortDirectory(lowering.Ports[k]));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            WriteText(Path.Combine(dir, CsxcadWriter.ModelFile), lowering.PortFiles[k]);
        }
    }

    // ── One run ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the file exciting <paramref name="lowering"/>'s port <paramref name="index"/> (0-based; one of
    /// <c>Ports.Count</c>), watching the probes and stopping openEMS when they have decayed
    /// <paramref name="endCriterionDb"/> below their peak over the last <paramref name="windowS"/>.
    /// <paramref name="threads"/> null lets openEMS choose (it measures which count is fastest).
    /// </summary>
    public static OpenEmsPortRun RunPort(string runDir, CsxcadLowering lowering, int index, string openEms, int? threads,
                                         double endCriterionDb, double windowS, RunControl? control, CancellationToken ct)
    {
        int port = lowering.Ports[index];
        int n = lowering.Ports.Count;
        string dir = Path.Combine(runDir, PortDirectory(port));
        string abort = Path.Combine(dir, AbortFile);
        string logPath = Path.Combine(dir, LogFile);
        if (File.Exists(abort)) File.Delete(abort);

        OpenEmsPortRun Fail(string why) => new(port, false, false, why, null, double.NaN, false, null);
        OpenEmsPortRun Cancel() => new(port, false, true, null, null, double.NaN, false, null);
        if (ct.IsCancellationRequested) return Cancel();

        var args = new List<string> { CsxcadWriter.ModelFile };
        if (threads is { } t) args.Add("--numThreads=" + t.ToString(CultureInfo.InvariantCulture));

        var psi = new ProcessStartInfo(openEms)
        {
            WorkingDirectory       = dir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        control?.BeginStage($"solving (openEMS), port {index + 1} of {n}", lowering.MaxTimeSteps, "steps");

        var tails = lowering.Ports.SelectMany(p => new[] { new ProbeTail(Path.Combine(dir, CsxcadWriter.VoltageProbe(p))),
                                                           new ProbeTail(Path.Combine(dir, CsxcadWriter.CurrentProbe(p))) })
                                  .ToArray();
        var gate = new Lock();
        var tail = new Queue<string>();
        long latestStep = 0;
        var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        void Line(string? text)
        {
            if (text is null) return;
            lock (gate)
            {
                log.WriteLine(text);
                tail.Enqueue(text);
                if (tail.Count > 60) tail.Dequeue();
                if (StepLine.Match(text) is { Success: true } m &&
                    long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long s))
                    latestStep = s;
            }
        }

        Process? p;
        try { p = Em3dProcessLauncher.Start(psi, Em3dProcessKind.Solver); }
        catch (Exception e) { log.Dispose(); return Fail($"openEMS could not be started ({e.Message})."); }
        if (p is null) { log.Dispose(); return Fail("openEMS could not be started (the operating system started nothing)."); }
        Interlocked.Increment(ref _runs);

        bool stoppedOnDecay = false;
        using (log)
        using (p)
        {
            ProcessStarted?.Invoke(p);
            p.OutputDataReceived += (_, e) => Line(e.Data);
            p.ErrorDataReceived  += (_, e) => Line(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            long reported = 0;
            var watch = Stopwatch.StartNew();
            while (!p.WaitForExit(100))
            {
                if (ct.IsCancellationRequested) { Kill(p); TryDelete(abort); return Cancel(); }
                long now;
                lock (gate) now = latestStep;
                if (now > reported && control is not null)
                {
                    try { control.TickStage(now - reported); }
                    catch (OperationCanceledException) { Kill(p); TryDelete(abort); return Cancel(); }
                    reported = now;
                }
                if (!stoppedOnDecay && watch.ElapsedMilliseconds >= 500)
                {
                    watch.Restart();
                    foreach (var tl in tails) tl.Poll();
                    if (Decay(Pairs(tails), windowS) is { } level && level <= endCriterionDb)
                    {
                        // openEMS's own graceful stop: it checks for this file every time step.
                        try { WriteText(abort, "circuitRF: the port signals have decayed to the end criterion.\n"); stoppedOnDecay = true; }
                        catch (IOException) { }
                    }
                }
            }
            p.WaitForExit();                 // drains the asynchronous readers
            TryDelete(abort);                // a re-run by hand must not stop at its first step
            if (ct.IsCancellationRequested) return Cancel();

            string[] lastLines;
            lock (gate) lastLines = [.. tail];
            if (p.ExitCode != 0)
                return Fail($"openEMS failed on port {port}'s run (exit code {p.ExitCode}). Its own words: " +
                            $"{Quote(lastLines)} The full log is {logPath}.");
        }

        // The log is closed — and so flushed — before it is read: the writer above buffers.
        string logText = File.ReadAllText(logPath);
        var facts = ParseLog(logText);

        var probes = new List<FdtdPortProbes>();
        foreach (int q in lowering.Ports)
        {
            var u = ReadProbe(Path.Combine(dir, CsxcadWriter.VoltageProbe(q)), out string? eu);
            var i = ReadProbe(Path.Combine(dir, CsxcadWriter.CurrentProbe(q)), out string? ei);
            if (eu is not null || ei is not null) return Fail((eu ?? ei)!);
            probes.Add(new FdtdPortProbes(u!, i!));
        }

        double decay = Decay(probes, windowS) ?? double.NaN;      // NaN: stopped before a window past the pulse
        bool converged = facts.Aborted || facts.EnergyCriterionMet || decay <= endCriterionDb;
        return new OpenEmsPortRun(port, true, false, null, facts, decay, converged, probes);
    }

    // ── The ports' decay ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How far below its peak the port signals have fallen: the largest |value| over the last
    /// <paramref name="windowS"/> of each probe, against the peak of that KIND (voltage or current)
    /// over every port — so a port that never saw much signal is judged on the excited port's scale,
    /// as openEMS's own criterion judges the whole domain's energy. In dB, 20·log10 of that ratio,
    /// which is the energy ratio's 10·log10. Null until every probe has run a window past the first.
    /// </summary>
    public static double? Decay(IReadOnlyList<FdtdPortProbes> probes, double windowS)
    {
        if (probes.Count == 0) return null;
        double end = double.PositiveInfinity;
        foreach (var p in probes)
            foreach (var x in new[] { p.Voltage, p.Current })
            {
                if (x.TimeS.Length == 0) return null;
                end = Math.Min(end, x.TimeS[^1]);
            }
        if (!(end >= 2 * windowS)) return null;

        double level = 0;
        foreach (bool voltage in new[] { true, false })
        {
            double peak = 0, recent = 0;
            foreach (var p in probes)
            {
                var x = voltage ? p.Voltage : p.Current;
                int count = Math.Min(x.TimeS.Length, x.Value.Length);
                for (int k = 0; k < count; k++)
                {
                    double a = Math.Abs(x.Value[k]);
                    peak = Math.Max(peak, a);
                    if (x.TimeS[k] >= end - windowS && x.TimeS[k] <= end) recent = Math.Max(recent, a);
                }
            }
            if (peak > 0) level = Math.Max(level, recent / peak);
        }
        return level > 0 ? 20 * Math.Log10(level) : double.NegativeInfinity;
    }

    private static List<FdtdPortProbes> Pairs(ProbeTail[] tails)
    {
        var list = new List<FdtdPortProbes>(tails.Length / 2);
        for (int k = 0; k + 1 < tails.Length; k += 2) list.Add(new FdtdPortProbes(tails[k].Snapshot(), tails[k + 1].Snapshot()));
        return list;
    }

    // ── Reading back ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One probe file: <c>%</c> header lines, then <c>t/s value</c> pairs (F0 Q11). Its own time column is
    /// kept (overview §1f). A missing file, or a row that is not two numbers, is an error naming the file.
    /// </summary>
    public static FdtdProbe? ReadProbe(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path))
        {
            error = $"openEMS finished but wrote no probe file '{path}'.";
            return null;
        }
        var t = new List<double>();
        var x = new List<double>();
        int row = 0;
        foreach (string raw in File.ReadLines(path))
        {
            row++;
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '%') continue;
            if (!TryRow(line, out double tt, out double xx))
            {
                error = $"Line {row} of openEMS's probe file '{path}' is not a time and a value.";
                return null;
            }
            t.Add(tt);
            x.Add(xx);
        }
        if (t.Count == 0)
        {
            error = $"openEMS's probe file '{path}' holds no samples.";
            return null;
        }
        return new FdtdProbe([.. t], [.. x]);
    }

    private static bool TryRow(string line, out double t, out double x)
    {
        t = x = 0;
        var cells = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return cells.Length >= 2 &&
               double.TryParse(cells[0], NumberStyles.Float, CultureInfo.InvariantCulture, out t) &&
               double.TryParse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x);
    }

    private static readonly Regex StepLine    = new(@"Timestep:\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex TimeStep    = new(@"FDTD timestep is:\s*([-+0-9.eE]+)\s*s", RegexOptions.Compiled);
    private static readonly Regex SizeLine    = new(@"FDTD simulation size:.*-->\s*(\d+)\s*FDTD cells", RegexOptions.Compiled);
    private static readonly Regex TimeFor     = new(@"Time for\s+(\d+)\s+iterations", RegexOptions.Compiled);
    private static readonly Regex EnergyLine  = new(@"Energy:\s*~\s*[-+0-9.eE]+\s*\(\s*-\s*([0-9.]+)\s*dB\)", RegexOptions.Compiled);
    private static readonly Regex EndReached  = new(@"end-criteria of\s*-?[0-9.]+dB reached after\s+\d+\s+timesteps\s*\(\s*-\s*([0-9.]+)\s*dB\)", RegexOptions.Compiled);

    /// <summary>What openEMS printed about a run — the formats of the pinned version's log (F0 Q11).</summary>
    public static OpenEmsLogFacts ParseLog(string text)
    {
        double? D(Match m) => m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;
        long? L(Match m) => m.Success && long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : null;

        var energies = EnergyLine.Matches(text);
        var end = EndReached.Match(text);
        double? energy = end.Success ? -D(end) : energies.Count > 0 ? -D(energies[^1]) : null;
        return new OpenEmsLogFacts(
            D(TimeStep.Match(text)),
            L(SizeLine.Match(text)),
            L(TimeFor.Match(text)),
            end.Success,
            text.Contains("Max. number of timesteps was reached", StringComparison.Ordinal),
            text.Contains("Found file \"ABORT\"", StringComparison.Ordinal),
            energy);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A probe file read as openEMS writes it: only complete lines, from where the last read stopped.</summary>
    private sealed class ProbeTail(string path)
    {
        private long _position;
        private string _pending = "";
        private readonly List<double> _t = [];
        private readonly List<double> _x = [];

        public void Poll()
        {
            try
            {
                if (!File.Exists(path)) return;
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < _position) { _position = 0; _pending = ""; _t.Clear(); _x.Clear(); }
                fs.Seek(_position, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
                string chunk = reader.ReadToEnd();
                _position = fs.Length;
                string text = _pending + chunk;
                int last = text.LastIndexOf('\n');
                if (last < 0) { _pending = text; return; }
                _pending = text[(last + 1)..];
                foreach (string raw in text[..last].Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '%') continue;
                    if (TryRow(line, out double t, out double x)) { _t.Add(t); _x.Add(x); }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public FdtdProbe Snapshot() => new([.. _t], [.. _x]);
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        try { p.WaitForExit(10_000); } catch (InvalidOperationException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>openEMS's last lines worth quoting: its errors when it printed any, else its last few.</summary>
    private static string Quote(IReadOnlyList<string> tail)
    {
        var lines = tail.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var marked = lines.Where(l => l.Contains("rror", StringComparison.Ordinal) || l.Contains("failed", StringComparison.Ordinal))
                          .TakeLast(5).ToList();
        var chosen = marked.Count > 0 ? marked : lines.TakeLast(8).ToList();
        return chosen.Count == 0 ? "(it printed nothing)" : "“" + string.Join(" | ", chosen) + "”";
    }

    private static void WriteText(string path, string text) => File.WriteAllText(path, text, new UTF8Encoding(false));
}
