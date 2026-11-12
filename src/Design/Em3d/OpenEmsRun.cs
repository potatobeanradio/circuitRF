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
                                    double DecayDb, bool Converged, IReadOnlyList<FdtdPortProbes>? Probes)
{
    /// <summary>brief-em3d-116 R-em3d116-2e — when the excited port is a wave-port terminal, its five probes in this run
    /// (U_a, U, U_c, I_a, I_b) and the voltage planes' spacing: what the line's own Z and ε_eff are measured from.</summary>
    public (FdtdProbe Ua, FdtdProbe U, FdtdProbe Uc, FdtdProbe Ia, FdtdProbe Ib, double SpacingM)? Line { get; init; }

    /// <summary>brief-emi-10 — the memory ceiling or the disk floor stopped openEMS (or refused to start it).</summary>
    public ProcessStop? Stopped { get; init; }
}

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
        // brief-em3d-65 R-em3d65-3b — each kernel solid's PLY beside every file naming it: openEMS runs in the port's own
        // directory and reads a PolyhedronReader's file relative to it.
        foreach (var kernel in lowering.KernelFiles ?? []) kernel.WriteInto(runDir);
        for (int k = 0; k < lowering.Ports.Count; k++)
        {
            string dir = Path.Combine(runDir, PortDirectory(lowering.Ports[k]));
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
            WriteText(Path.Combine(dir, CsxcadWriter.ModelFile), lowering.PortFiles[k]);
            foreach (var kernel in lowering.KernelFiles ?? []) kernel.WriteInto(dir);
        }
    }

    // brief-sar-8 — a voxelized phantom's DiscMaterial file is read the same way, and dropped the same way when it cannot be.
    private static readonly Regex PolyhedronReaderFile = new("<(?:PolyhedronReader FileName|DiscFile Type=\"\\d+\" File)=\"([^\"]*)\"",
                                                             RegexOptions.CultureInvariant);

    /// <summary>
    /// brief-em3d-65 R-em3d65-3c — null when every <c>PolyhedronReader</c> file <paramref name="xml"/> names exists in
    /// <paramref name="dir"/>, is not empty and hashes to what the writer wrote; otherwise the refusal, naming the solid and
    /// the file. F0 Q8: CSXCAD skips a file it cannot read with a warning and exit code 0, so openEMS would solve as if the
    /// solid were not there — which is why this is checked immediately before openEMS is started, not trusted.
    /// </summary>
    public static string? CheckKernelFiles(string dir, CsxcadLowering lowering, string xml)
    {
        foreach (Match m in PolyhedronReaderFile.Matches(xml))
        {
            string name = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            var written = lowering.KernelFiles?.FirstOrDefault(f => f.FileName == name);
            string solid = written is { Solid.Length: > 0 } w ? $"'{w.Solid}'" : "a kernel solid";
            string path = Path.Combine(dir, name);
            string? what =
                written is null ? "which circuitRF did not write for this run" :
                !File.Exists(path) ? "which is missing" :
                new FileInfo(path).Length == 0 ? "which is empty" :
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) != written.Sha256
                    ? "which is not what circuitRF wrote (it changed after it was written)" : null;
            if (what is not null)
                return $"{char.ToUpperInvariant(solid[0])}{solid[1..]} is read by openEMS from {path}, {what}. openEMS would " +
                       "solve without it and say nothing, so it was not started.";
        }
        return null;
    }

    // ── One run ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the file exciting <paramref name="lowering"/>'s port <paramref name="index"/> (0-based; one of
    /// <c>Ports.Count</c>), watching the probes and stopping openEMS when they have decayed
    /// <paramref name="endCriterionDb"/> below their peak over the last <paramref name="windowS"/>.
    /// <paramref name="threads"/> null lets openEMS choose (it measures which count is fastest).
    /// </summary>
    /// <param name="cells">brief-dfr27-5 R-dfr27-5-4 — the grid's cells, which a quiet openEMS is said to be building its operator
    /// for; 0 when unknown.</param>
    /// <param name="stepS">The time step openEMS is expected to take (the grid's Courant estimate): what turns a probe's time
    /// into a step when openEMS's own lines do not arrive; 0 when unknown.</param>
    public static OpenEmsPortRun RunPort(string runDir, CsxcadLowering lowering, int index, string openEms, int? threads,
                                         double endCriterionDb, double windowS, RunControl? control, CancellationToken ct,
                                         double settleS = 0, ProcessLimits? limits = null, long cells = 0, double stepS = 0)
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

        // brief-hzn-12 — a plane-wave run has no probe: openEMS's own energy criterion stops it.
        var tails = Names(lowering).Where(p => p.U.Count > 0)
                                   .SelectMany(p => new[] { new ProbeTail(Path.Combine(dir, p.U[0])),
                                                            new ProbeTail(Path.Combine(dir, p.I[0])) })
                                   .ToArray();
        // brief-emi-10 R-emi10-2 — the process loop every solver runs in, so openEMS is held to the same memory ceiling and
        // disk floor. Its own watching rides on it: the step count from each line, and on each tick the stage and, every half
        // second, the probes' decay — written as openEMS's own graceful stop, the ABORT file it checks every time step.
        long latestStep = 0, reported = 0, lineStep = 0;
        bool stoppedOnDecay = false;
        var decayWatch = Stopwatch.StartNew();
        // brief-dfr27-5 R-dfr27-5-4 — no silent stalls: a quiet openEMS is said to be quiet, once, with what it is probably
        // doing; and a step read from the probes when its own lines do not come (a child's buffered output looks the same).
        var sinceStep = Stopwatch.StartNew();
        bool quietSaid = false, bufferedSaid = false;
        void Say(string sentence) => control?.Notes?.Invoke(new RunNote(false, sentence));
        void OnLine(string text)
        {
            if (StepLine.Match(text) is { Success: true } m &&
                long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long s))
            {
                if (s > lineStep) sinceStep.Restart();
                lineStep = s;
                latestStep = Math.Max(latestStep, s);
            }
        }
        void OnTick()
        {
            if (latestStep > reported && control is not null)
            {
                control.TickStage(latestStep - reported);
                reported = latestStep;
            }
            if (!quietSaid && sinceStep.Elapsed >= QuietAfter)
            {
                quietSaid = true;
                Say(QuietSentence(index, n, lineStep, cells, QuietAfter));
            }
            if (stoppedOnDecay || decayWatch.ElapsedMilliseconds < 500) return;
            decayWatch.Restart();
            foreach (var tl in tails) tl.Poll();
            if (ProbeStep(tails.Select(tl => tl.LastTimeS), stepS) is { } probed && probed > latestStep && probed > lineStep)
            {
                latestStep = probed;
                if (!bufferedSaid && sinceStep.Elapsed >= QuietAfter)
                {
                    bufferedSaid = true;
                    Say(BufferedSentence);
                }
            }
            if (Decay(Pairs(tails), windowS, settleS) is { } level && level <= endCriterionDb)
            {
                try { WriteText(abort, "circuitRF: the port signals have decayed to the end criterion.\n"); stoppedOnDecay = true; }
                catch (IOException) { }
            }
        }
        var watch = PalaceRun.Watch(limits, OnLine, 0, runDir) with
        {
            OnTick = OnTick,
            OnStarted = p => { Interlocked.Increment(ref _runs); ProcessStarted?.Invoke(p); },
        };
        var run = PalaceRun.RunProcess(psi, logPath, Em3dProcessKind.Solver, control, ct, watch);
        TryDelete(abort);                    // a re-run by hand must not stop at its first step
        if (run.Cancelled) return Cancel();
        if (run.StartFailure is { } why) return Fail($"openEMS could not be started ({why}).");
        if (run.Stopped is { } stopped) return new(port, false, false, null, null, double.NaN, false, null) { Stopped = stopped };
        if (run.ExitCode != 0)
            return Fail($"openEMS failed on port {port}'s run (exit code {run.ExitCode}). Its own words: " +
                        $"{Quote([.. run.Tail])} The full log is {logPath}.");

        // The log is closed — and so flushed — before it is read: the writer above buffers.
        string logText = File.ReadAllText(logPath);
        var facts = ParseLog(logText);

        var probes = new List<FdtdPortProbes>();
        foreach (var names in Names(lowering).Where(n => n.U.Count > 0))
        {
            if (ReadPort(dir, names, out string? error) is not { } read) return Fail(error!);
            probes.Add(read);
        }

        // brief-em3d-116 R-em3d116-2e — the excited terminal's line, from its own five probes in its own run.
        var own = Names(lowering)[index];
        (FdtdProbe, FdtdProbe, FdtdProbe, FdtdProbe, FdtdProbe, double)? line = null;
        if (own.IsWave)
        {
            var ua = Mean(dir, own.Ua!, out string? e1);
            var um = Mean(dir, own.U, out string? e2);
            var uc = Mean(dir, own.Uc!, out string? e3);
            var ia = ReadProbe(Path.Combine(dir, own.I[0]), out string? e4);
            var ib = ReadProbe(Path.Combine(dir, own.I[1]), out string? e5);
            if ((e1 ?? e2 ?? e3 ?? e4 ?? e5) is { } lineError) return Fail(lineError);
            line = (ua!, um!, uc!, ia!, ib!, own.SpacingM);
        }

        double decay = Decay(probes, windowS, settleS) ?? double.NaN;      // NaN: stopped before a window past the pulse
        bool converged = facts.Aborted || facts.EnergyCriterionMet || decay <= endCriterionDb;
        return new OpenEmsPortRun(port, true, false, null, facts, decay, converged, probes) { Line = line };
    }

    /// <summary>The lowering's probe names per port; a lowering from before brief 116 (none recorded) is lumped throughout.</summary>
    private static IReadOnlyList<OpenEmsProbeNames> Names(CsxcadLowering lowering)
        => lowering.Probes.Count == lowering.Ports.Count ? lowering.Probes
           : [.. lowering.Ports.Select(p => new OpenEmsProbeNames(p, [CsxcadWriter.VoltageProbe(p)], [CsxcadWriter.CurrentProbe(p)]))];

    /// <summary>
    /// brief-em3d-116 R-em3d116-2d — one port's U and I in one run: U the mean of its voltage files (one, or a stripline's two
    /// halves), I the mean of its current files (one, or a terminal's two half-cell planes), each sample by sample. Files
    /// averaged together must share one time column; when they do not, the error names them.
    /// </summary>
    public static FdtdPortProbes? ReadPort(string dir, OpenEmsProbeNames names, out string? error)
    {
        var u = Mean(dir, names.U, out error);
        if (u is null) return null;
        var i = Mean(dir, names.I, out error);
        return i is null ? null : new FdtdPortProbes(u, i);
    }

    /// <summary>The sample-wise mean of probe files sharing one time column.</summary>
    private static FdtdProbe? Mean(string dir, IReadOnlyList<string> files, out string? error)
    {
        error = null;
        var read = new List<FdtdProbe>(files.Count);
        foreach (string f in files)
        {
            var p = ReadProbe(Path.Combine(dir, f), out error);
            if (p is null) return null;
            read.Add(p);
        }
        if (read.Count == 1) return read[0];
        var t = read[0].TimeS;
        for (int k = 1; k < read.Count; k++)
            if (read[k].TimeS.Length != t.Length || !read[k].TimeS.AsSpan().SequenceEqual(t))
            {
                error = $"openEMS's probe files {string.Join(" and ", files.Select(f => $"'{Path.Combine(dir, f)}'"))} do not share one " +
                        "time column, so they cannot be averaged sample by sample.";
                return null;
            }
        var v = new double[t.Length];
        foreach (var p in read)
            for (int n = 0; n < v.Length; n++) v[n] += p.Value[n];
        for (int n = 0; n < v.Length; n++) v[n] /= read.Count;
        return new FdtdProbe(t, v);
    }

    // ── The ports' decay ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How far below its peak the port signals have fallen: the largest |value| over the last
    /// <paramref name="windowS"/> of each probe, against the peak of that KIND (voltage or current)
    /// over every port — so a port that never saw much signal is judged on the excited port's scale,
    /// as openEMS's own criterion judges the whole domain's energy. In dB, 20·log10 of that ratio,
    /// which is the energy ratio's 10·log10. Null until every probe has run a window past the first,
    /// and until <paramref name="settleS"/>.
    ///
    /// <para><b>Why <paramref name="settleS"/>.</b> Two pulse lengths is not enough on a long structure:
    /// once the pulse has left the excited port of a matched line, every probe is quiet — the far port
    /// has not been reached yet — and the decay test passed with S21 ≈ 0, reported as converged. The
    /// caller passes the pulse plus twice the box's diagonal at the slowest wave speed in it, which is
    /// the longest any port can wait for the first arrival and its return.</para>
    /// </summary>
    public static double? Decay(IReadOnlyList<FdtdPortProbes> probes, double windowS, double settleS = 0)
    {
        if (probes.Count == 0) return null;
        double end = double.PositiveInfinity;
        foreach (var p in probes)
            foreach (var x in new[] { p.Voltage, p.Current })
            {
                if (x.TimeS.Length == 0) return null;
                end = Math.Min(end, x.TimeS[^1]);
            }
        if (!(end >= 2 * windowS) || !(end >= settleS)) return null;

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

    /// <summary>brief-dfr27-5 R-dfr27-5-4 — how long openEMS may report no step before the run says so.</summary>
    internal static TimeSpan QuietAfter { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// What a quiet openEMS is probably doing. Before its first step it builds its operator — a coefficient per cell and field
    /// component, in one thread — and says nothing while it does, which on a grid of tens of millions of cells takes minutes;
    /// after it, a step it does not report is usually a machine out of memory and swapping.
    /// </summary>
    internal static string QuietSentence(int index, int ports, long lastStep, long cells, TimeSpan quiet)
    {
        string who = $"openEMS (port {index + 1} of {ports}) has reported no time step for {quiet.TotalMinutes:0} minutes";
        string cancel = " Cancel stops it; nothing from this run is kept yet.";
        if (lastStep == 0)
            return who + (cells > 0
                       ? $": it is probably still building its operator for {cells / 1e6:0.#} million cells, which it does before " +
                         "the first step and without a word."
                       : ": it is probably still building its operator, which it does before the first step and without a word.") + cancel;
        return who + $" since step {lastStep:N0}: it is still running, and a step it does not report is usually a machine short of " +
               "memory and swapping." + cancel;
    }

    /// <summary>When openEMS's probes advance and its own lines do not arrive: its output is held back by the pipe.</summary>
    internal const string BufferedSentence =
        "openEMS's progress lines are not reaching circuitRF (its output is buffered), but its probes are advancing: the step " +
        "count shown is read from the probes instead.";

    /// <summary>The step openEMS has reached by its probes: the earliest last sample over every probe, over the time step.
    /// Null before every probe has a sample, or with no time step to divide by.</summary>
    internal static long? ProbeStep(IEnumerable<double> lastTimes, double stepS)
    {
        double t = double.PositiveInfinity;
        foreach (double x in lastTimes) t = Math.Min(t, x);
        return stepS > 0 && t > 0 && double.IsFinite(t) ? (long)(t / stepS) : null;
    }
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

        /// <summary>The time of the last sample read, or 0 before one.</summary>
        public double LastTimeS => _t.Count > 0 ? _t[^1] : 0;
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
