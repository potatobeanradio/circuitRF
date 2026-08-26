using System.Diagnostics;
using System.Text;

namespace CircuitRF.Core.Devices.External;

// ─────────────────────────────────────────────────────────────────────────────
//  How a worker is reached.
//
//  A worker is a separate process that owns a compiled device model. Where that
//  process lives is the only thing that varies between deployments — beside the
//  application, or inside a virtual machine when the model is built for a
//  different operating system than the one circuitRF is running on. Both cases
//  are a pair of byte streams, so everything above this interface is written once.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// A duplex byte channel to a device worker, plus enough identity to write a comprehensible error
/// when it stops answering.
/// </summary>
public interface IDeviceWorkerTransport : IDisposable
{
    /// <summary>Stream the host writes command frames to.</summary>
    Stream Requests { get; }

    /// <summary>Stream the host reads reply frames from.</summary>
    Stream Replies { get; }

    /// <summary>
    /// Where this worker is, in words a user can act on. Appears verbatim in error messages, so it
    /// should name the thing that failed rather than describe it.
    /// </summary>
    string Origin { get; }

    /// <summary>
    /// False once the worker is known to be gone. A transport that cannot tell reports true — the
    /// authoritative signal is always a failed read, and this only ever improves a message.
    /// </summary>
    bool IsAlive { get; }

    /// <summary>
    /// Whatever the worker last wrote to its own error stream, or an empty string. A worker that
    /// dies during a solve usually explains itself there and nowhere else, so this is attached to
    /// the exception rather than left in a log the user will not think to open.
    /// </summary>
    string RecentErrorOutput { get; }
}

/// <summary>
/// A worker process about to be started: which provider it will serve (empty when the caller has no
/// name for it) and the executable being run. See <see cref="ProcessDeviceWorkerTransport.Starting"/>.
///
/// <para>Structured rather than a formed sentence: how this is worded belongs to whoever shows it,
/// and a headless host may want to log it in a different shape entirely.</para>
/// </summary>
/// <param name="ForDiscovery">
/// True when this worker exists only to be ASKED WHAT IT IMPLEMENTS and then shut down — the scan
/// that reads a folder of compiled models, which starts one worker per artefact and disposes each
/// immediately.
///
/// <para><b>Why the distinction has to reach the host.</b> The two kinds of start are indistinguishable
/// from the outside — same program, same provider name — but only one of them is a run waiting on
/// anything. A host that words the event as "the first device waits for it" says something false
/// once per artefact scanned, during a workspace OPEN with no run in sight, and the repetition reads
/// as the same worker starting twice. Reported against a kit shipping four compiled models.</para>
/// </param>
public readonly record struct DeviceWorkerStart(string Provider, string Command, bool ForDiscovery = false);

/// <summary>
/// One line a worker wrote to its error stream, with the provider it serves and the program it is.
/// See <see cref="ProcessDeviceWorkerTransport.Logged"/>.
/// </summary>
public readonly record struct DeviceWorkerLogLine(string Provider, string Command, string Line);

/// <summary>
/// A worker running as a local child process, spoken to over its standard input and output.
///
/// <para><b>Why stderr is drained on a thread.</b> A worker logs to its error stream. Nobody
/// reading it means the pipe fills, and the worker then blocks forever inside a write — presenting
/// as a hang partway through a long solve, with no error anywhere. The drain also gives
/// <see cref="RecentErrorOutput"/> something to report.</para>
/// </summary>
public sealed class ProcessDeviceWorkerTransport : IDeviceWorkerTransport
{
    /// <summary>How much of the worker's error output to keep for diagnostics.</summary>
    private const int RetainedErrorLines = 40;

    /// <summary>How many of a worker's lines to pass on when <see cref="MirrorErrorOutput"/> is on.
    /// A worker logs once per set-up step, so a real session is tens of lines — but a device refusing
    /// every point logs once per point, and a diagnostic that floods the thing it is meant to be read
    /// in is not one. Does not apply to <see cref="ErrorOutputFile"/>: a file is where the whole
    /// thing is wanted.</summary>
    private const int MirroredLineLimit = 500;

    private readonly Process       _process;
    private readonly Queue<string> _errorLines = new();
    private readonly Lock          _errorGate  = new();
    private readonly string        _provider;

    /// <summary>Lines already passed on for this worker, so a repeat is not passed on twice.</summary>
    private readonly HashSet<string> _seenLines = new(StringComparer.Ordinal);
    private readonly Lock            _mirrorGate = new();

    private int  _mirrored;
    private bool _saidRepeatsAreHidden;

    private ProcessDeviceWorkerTransport(Process process, string origin, string provider)
    {
        _process  = process;
        Origin    = origin;
        _provider = provider;

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_errorGate)
            {
                _errorLines.Enqueue(e.Data);
                while (_errorLines.Count > RetainedErrorLines) _errorLines.Dequeue();
            }

            Mirror(e.Data);
        };
        _process.BeginErrorReadLine();
    }

    /// <summary>
    /// Whether to pass every line a worker writes to <see cref="Logged"/> as it arrives. Off unless
    /// <c>CRF_WORKER_LOG</c> is set in the environment; settable so a host can offer its own switch.
    ///
    /// <para><b>What this is for, and why the existing capture is not enough.</b> A worker's log is
    /// the only account of the things it MEASURES rather than is told — which nodes it found to be
    /// free unknowns, which pins carry a temperature, whether the model's own Jacobian agrees with
    /// its currents. Those measurements decide how the device is stamped, and a wrong one is
    /// invisible from the host: the device stamps cleanly, every number is finite, and the only
    /// symptom is a solve that will not converge. <see cref="RecentErrorOutput"/> holds the same
    /// lines, but it is read only where something THREW — and this failure mode never throws, so the
    /// one description of what happened was unreachable in exactly the case that needs it.</para>
    ///
    /// <para><b>REPEATS ARE SHOWN ONCE.</b> A worker's account is per DEVICE INSTANCE, and a design
    /// placing the same part five times produces five identical accounts — the same node roles, the
    /// same Jacobian comparison — which is five times the reading for none of the information. What
    /// is worth seeing is what DIFFERS between instances, and that is exactly what survives showing
    /// each distinct line once. Full fidelity, in order and with the repeats, is what
    /// <see cref="ErrorOutputFile"/> is for.</para>
    ///
    /// <para>Off by default because it is per-line and a worker under a failing solve is chatty.</para>
    /// </summary>
    public static bool MirrorErrorOutput { get; set; } = ReadSwitch(out _);

    /// <summary>
    /// A file every line is written to verbatim — no de-duplication, no cap, and nothing shown in
    /// the host — or null. Set <c>CRF_WORKER_LOG</c> to a PATH rather than to <c>1</c>.
    ///
    /// <para>This is the form to reach for when handing the log to somebody else: a complete
    /// account, in order, of a run that may have produced thousands of lines, without any of it
    /// landing in the window the user is trying to read.</para>
    /// </summary>
    public static string? ErrorOutputFile { get; set; } = ReadSwitchFile();

    /// <summary>
    /// Reads <c>CRF_WORKER_LOG</c>: <c>1</c>/<c>on</c>/<c>true</c>/<c>yes</c> asks for the host's own
    /// log, anything else non-empty is taken as a file path.
    /// </summary>
    private static bool ReadSwitch(out string? file)
    {
        file = null;
        string? v = Environment.GetEnvironmentVariable("CRF_WORKER_LOG")?.Trim();
        if (string.IsNullOrEmpty(v)) return false;

        if (v is "1" or "on" or "true" or "yes" or "ON" or "TRUE" or "YES" or "On" or "True" or "Yes")
            return true;

        file = v;
        return false;
    }

    private static string? ReadSwitchFile() { ReadSwitch(out string? f); return f; }

    /// <summary>
    /// One line a worker wrote to its error stream, as it arrives. Only raised while
    /// <see cref="MirrorErrorOutput"/> is on.
    /// </summary>
    public static event Action<DeviceWorkerLogLine>? Logged;

    private void Mirror(string line)
    {
        WriteToFile(line);

        if (!MirrorErrorOutput) return;

        // Each DISTINCT line once. What a worker says per instance is identical across instances of
        // the same part, so the repeats carry nothing the first one did not; the lines that differ
        // between them are the ones worth reading, and those all survive.
        string text;
        lock (_mirrorGate)
        {
            if (!_seenLines.Add(line))
            {
                if (_saidRepeatsAreHidden) return;
                _saidRepeatsAreHidden = true;
                text = "(this worker is repeating itself for each device instance; "
                     + "identical lines are shown once. Set CRF_WORKER_LOG to a file path for all of them)";
            }
            else
            {
                int n = ++_mirrored;
                if (n > MirroredLineLimit) return;
                text = n == MirroredLineLimit
                    ? $"(further output from this worker is not being shown; {MirroredLineLimit} lines is the limit)"
                    : line;
            }
        }

        // A host's own reporting must never be the reason a worker fails — the same rule Starting
        // follows, and for the same reason: the failure would be attributed to the kit.
        try { Logged?.Invoke(new DeviceWorkerLogLine(_provider, Origin, text)); }
        catch { /* ignored */ }
    }

    private static readonly Lock FileGate = new();

    /// <summary>
    /// Appends one line to <see cref="ErrorOutputFile"/>, tagged with the worker it came from so two
    /// workers writing one file stay separable. Silent on failure: a diagnostic that cannot be
    /// written must not become the failure being diagnosed.
    /// </summary>
    private void WriteToFile(string line)
    {
        string? path = ErrorOutputFile;
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            lock (FileGate)
                File.AppendAllText(path,
                    (string.IsNullOrWhiteSpace(_provider) ? "" : _provider + ": ") + line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            /* nothing to say that would not itself be noise */
        }
    }

    /// <summary>
    /// Raised immediately BEFORE a worker process is created, once per process.
    ///
    /// <para><b>Why a host wants this.</b> Starting a worker is the one step in evaluating an
    /// external model that a user waits on and cannot see: the library is loaded, its device types
    /// are described, and on a Mac the whole thing happens inside a virtual machine that has to boot
    /// first. Until it finishes, a run that is working normally is indistinguishable from one that
    /// has hung. Announcing it costs one line and is only ever emitted once per provider, because the
    /// registry keeps what it resolved (<see cref="ExternalDeviceRegistry.Find"/>) — every device
    /// after the first uses the worker already running.</para>
    ///
    /// <para>Raised BEFORE the process is created rather than after, because the wait is the whole
    /// point of raising it. A subscriber that throws is ignored: a host's own reporting must never be
    /// the reason a worker fails to start.</para>
    /// </summary>
    public static event Action<DeviceWorkerStart>? Starting;

    /// <summary>
    /// Start a worker executable and connect to it.
    /// </summary>
    /// <param name="executablePath">Path to the worker binary. Supplied at runtime, never baked in.</param>
    /// <param name="arguments">Arguments the worker needs — typically which model library to load.</param>
    /// <param name="workingDirectory">Working directory, or null to inherit.</param>
    /// <param name="forProvider">
    /// Which provider this worker will serve, for <see cref="Starting"/>. Empty when the caller has
    /// no name to give — a message can still be written, it just says less.
    /// </param>
    /// <param name="forDiscovery">
    /// True when this worker is started only to be asked what it implements and is then disposed —
    /// see <see cref="DeviceWorkerStart.ForDiscovery"/> for why a host has to be able to tell.
    /// </param>
    public static ProcessDeviceWorkerTransport Start(
        string               executablePath,
        IEnumerable<string>? arguments        = null,
        string?              workingDirectory = null,
        string?              forProvider      = null,
        bool                 forDiscovery     = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        // THE CONSENT GATE, and it is here rather than at the callers because this is the line that
        // starts a process. A kit's device-provider.json names a command that resolves against the
        // kit's own folder, so this is where somebody else's executable becomes a running one — and
        // the three paths that reach it (a simulation resolving a kit provider, a PDK import
        // describing a compiled model, and src/Cli) must not each carry their own copy of the check.
        // Default is ALLOWED: DeviceWorkerPolicy has no opinion until an application installs one.
        if (DeviceWorkerPolicy.RefusalReason(forProvider ?? "") is { } refused)
            throw new ExternalDeviceException(refused);

        var info = new ProcessStartInfo
        {
            FileName               = executablePath,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        foreach (string a in arguments ?? []) info.ArgumentList.Add(a);
        if (!string.IsNullOrEmpty(workingDirectory)) info.WorkingDirectory = workingDirectory;

        try { Starting?.Invoke(new DeviceWorkerStart(forProvider ?? "", executablePath, forDiscovery)); }
        catch { /* a host's own reporting must never stop a worker from starting */ }

        Process process;
        try
        {
            process = Process.Start(info)
                   ?? throw new ExternalDeviceException(
                          $"The device worker '{executablePath}' could not be started.");
        }
        catch (Exception ex) when (ex is not ExternalDeviceException)
        {
            throw new ExternalDeviceException(WhyItDidNotStart(executablePath, ex), ex);
        }

        return new ProcessDeviceWorkerTransport(process, executablePath, forProvider ?? "");
    }

    /// <summary>
    /// Why a worker did not start, said in terms of what is actually missing.
    ///
    /// <para>The operating system's own message for a program that is not there names the file and
    /// the working directory and stops — and the working directory is a red herring, because a bare
    /// name was never going to be looked for there. What the reader needs instead is that this
    /// particular program is <b>circuitRF's own optional component</b>, built beside the application
    /// rather than shipped by the kit: a build made where no C compiler was present skips it, warns,
    /// and succeeds. Nothing else in the run says so, and from the message alone the natural reading
    /// is that the kit is broken — which it is not.</para>
    ///
    /// <para>Only ever added to a genuine "no such file", never to a failure that got further than
    /// that. A permission or format error means the program IS there, and telling someone to go and
    /// build one would send them off to fix the wrong thing.</para>
    /// </summary>
    private static string WhyItDidNotStart(string executablePath, Exception ex)
    {
        string plain = $"The device worker '{executablePath}' could not be started: {ex.Message}";

        bool notFound = ex is System.ComponentModel.Win32Exception w
                     && w.NativeErrorCode is 2 or 3;     // ENOENT / ERROR_FILE_NOT_FOUND / _PATH_
        if (!notFound) return plain;

        bool bareName = !Path.IsPathRooted(executablePath)
                     && !executablePath.Contains(Path.DirectorySeparatorChar)
                     && !executablePath.Contains('/');

        return plain + (bareName
            ? $" It is not in circuitRF's own tools folder ('{DeviceWorkerManifest.ToolsDirectory}') " +
              "or anywhere on this system's program path. That program is circuitRF's own component " +
              "and is built alongside the application, not shipped by the kit — a build made on a " +
              "machine with no C compiler skips it with a warning and still succeeds, which is the " +
              "state this message describes. A kit whose devices are compiled models needs it."
            : " There is no file at that path.");
    }

    public Stream Requests => _process.StandardInput.BaseStream;
    public Stream Replies  => _process.StandardOutput.BaseStream;
    public string Origin   { get; }

    public bool IsAlive
    {
        get { try { return !_process.HasExited; } catch { return false; } }
    }

    public string RecentErrorOutput
    {
        get
        {
            FlushErrorOutput();
            lock (_errorGate) return string.Join(Environment.NewLine, _errorLines);
        }
    }

    private int _flushed;

    /// <summary>How long to wait for a dead worker's last words. Ample for a pipe already at end of
    /// stream; short enough that a failure path cannot stall on one.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Waits for what an exited worker wrote to actually arrive, before it is reported.
    ///
    /// <para><b>The race, and why it is easy to talk yourself out of.</b> Error lines arrive on a
    /// background reader, so a worker that dies FAST — during start-up, or the instant it is handed
    /// something it cannot use — can be reported before a single line has been delivered. The
    /// failure then reads as "the connection failed (Broken pipe)" and nothing else, in exactly the
    /// case where the worker's own message is the only description of what went wrong. A worker that
    /// dies slowly reports fine, so the diagnostic looks healthy almost all of the time.</para>
    ///
    /// <para>It is also RARE — about one run in seventeen on the machine this was found on. Twelve
    /// consecutive passes were taken as evidence the race did not exist, and that was wrong; it
    /// surfaced in a full-solution run afterwards. <c>AWorkerThatDiesImmediately_…</c> is the guard,
    /// and it must be run repeatedly, not once, to mean anything.</para>
    ///
    /// <para><c>WaitForExit()</c> with NO timeout is the only overload that also waits for the
    /// redirected readers to reach end of stream; the timed one returns as soon as the process is
    /// gone and leaves whatever it wrote still in flight. So it is run off-thread under a bound of
    /// its own — a grandchild holding the pipe open must not wedge the path trying to explain a
    /// failure.</para>
    ///
    /// <para>Only ever waits on a process that has ALREADY exited, so nothing is slowed down while a
    /// worker is alive and merely refusing a request in-band.</para>
    /// </summary>
    private void FlushErrorOutput()
    {
        bool exited;
        try { exited = _process.HasExited; } catch { return; }

        if (!exited) return;
        if (Interlocked.Exchange(ref _flushed, 1) != 0) return;

        try { Task.Run(() => { try { _process.WaitForExit(); } catch { } }).Wait(FlushTimeout); }
        catch { /* the diagnostic is a courtesy; it must never become the failure */ }
    }

    /// <summary>
    /// Ends the worker. A worker that has already been asked to shut down will have exited on its
    /// own; one that is wedged inside a model evaluation will not, and is killed rather than left
    /// behind as an orphan holding a model library open.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                try { _process.StandardInput.Close(); } catch { /* already gone */ }
                if (!_process.WaitForExit(milliseconds: 2000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* teardown must not throw over a process that is already gone */ }
        finally { _process.Dispose(); }
    }
}

/// <summary>
/// A transport over streams the caller already has. Exists so the protocol, the provider and the
/// evaluation path can all be tested against a worker that is not a process — the parts worth
/// testing are the framing and the decoding, and neither needs a real model to exercise.
/// </summary>
public sealed class StreamDeviceWorkerTransport(
    Stream  requests,
    Stream  replies,
    string  origin       = "in-memory device worker",
    bool    leaveOpen    = false) : IDeviceWorkerTransport
{
    public Stream Requests          => requests;
    public Stream Replies           => replies;
    public string Origin            => origin;
    public bool   IsAlive           => true;
    public string RecentErrorOutput => string.Empty;

    public void Dispose()
    {
        if (leaveOpen) return;
        requests.Dispose();
        replies.Dispose();
    }
}
