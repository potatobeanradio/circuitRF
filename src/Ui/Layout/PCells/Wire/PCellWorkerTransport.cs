using System.Diagnostics;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>
/// A duplex byte channel to a PCell generator, plus enough identity to write a comprehensible error
/// when it stops answering.
///
/// <para>An interface rather than a concrete process for the same reason the device path has one:
/// <b>above this line, in-process or out, circuitRF cannot tell.</b> An embedded interpreter — or a
/// generator running somewhere else entirely — is a different implementation of these five members
/// and nothing else changes.</para>
/// </summary>
public interface IPCellWorkerTransport : IDisposable
{
    Stream Requests { get; }
    Stream Replies { get; }

    /// <summary>Where this generator is, in words a user can act on. Appears verbatim in errors, so
    /// it names the thing that failed rather than describing it.</summary>
    string Origin { get; }

    /// <summary>False once the generator is known to be gone. A transport that cannot tell reports
    /// true — the authoritative signal is always a failed read, and this only improves a message.</summary>
    bool IsAlive { get; }

    /// <summary>Whatever the generator last wrote to its own error stream. A script that dies
    /// usually explains itself there and nowhere else, so this is attached to the exception rather
    /// than left in a log the user will not think to open.</summary>
    string RecentErrorOutput { get; }
}

/// <summary>
/// A generator running as a local child process, spoken to over its standard input and output.
///
/// <para><b>stderr is drained on a thread, and that is a hang fix rather than a nicety.</b> A script
/// that logs — or simply hits a warning — fills the pipe if nobody reads it, and then blocks forever
/// inside a write. That presents as circuitRF freezing partway through opening a workspace, with no
/// error anywhere. The same trap the device worker already documents.</para>
/// </summary>
public sealed class ProcessPCellWorkerTransport : IPCellWorkerTransport
{
    private const int RetainedErrorLines = 40;

    private readonly Process _process;
    private readonly Queue<string> _errorLines = new();
    private readonly Lock _errorGate = new();
    private int _flushed;

    /// <summary>
    /// How many generator processes this build has started. Test/diagnostic only — the same shape as
    /// the geometry cache's own call counter, and for the same reason: <b>the claim worth gating is
    /// "how many processes", not "how many milliseconds"</b>, because a wall-clock assertion cannot
    /// survive the parallel-start burst of a full-solution run.
    /// </summary>
    public static int StartCount => Volatile.Read(ref _startCount);
    private static int _startCount;

    private ProcessPCellWorkerTransport(Process process, string origin)
    {
        _process = process;
        Origin = origin;

        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_errorGate)
            {
                _errorLines.Enqueue(e.Data);
                while (_errorLines.Count > RetainedErrorLines) _errorLines.Dequeue();
            }
        };
        _process.BeginErrorReadLine();
    }

    /// <summary>Start an interpreter on a generator script and connect to it.</summary>
    /// <param name="interpreter">The interpreter to run. Supplied at runtime, never baked in.</param>
    /// <param name="scriptPath">The script that calls the package's <c>run()</c>.</param>
    /// <param name="pythonPath">Directories added to <c>PYTHONPATH</c> so a kit's own modules import
    /// without the user configuring anything.</param>
    /// <param name="interpreterArguments">Arguments that must precede the script. Empty for a direct
    /// interpreter; the Windows launcher needs <c>-3</c>, because it is a dispatcher that will
    /// otherwise choose a version of its own.</param>
    public static ProcessPCellWorkerTransport Start(
        string interpreter, string scriptPath, IReadOnlyList<string>? pythonPath = null,
        IReadOnlyList<string>? interpreterArguments = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interpreter);
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);

        var info = new ProcessStartInfo
        {
            FileName               = interpreter,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
            WorkingDirectory       = Path.GetDirectoryName(scriptPath) ?? "",
        };
        foreach (string a in interpreterArguments ?? []) info.ArgumentList.Add(a);
        info.ArgumentList.Add(scriptPath);

        // ── Keep Python's bytecode cache OUT of the application bundle ────────────────────────
        //
        // On macOS the generator scripts we ship live at
        // <app>.app/Contents/MacOS/pcell-python/, INSIDE the signed bundle. Python's default is to
        // write __pycache__/*.pyc beside every module it imports — so the first workspace that runs
        // a PCell generator adds 14 files to a sealed bundle and BREAKS ITS CODE SIGNATURE.
        // Measured on a real Developer ID install, 2026-08-25: `codesign --verify --deep --strict
        // /Applications/circuitRF.app` then reports "a sealed resource is missing or invalid".
        //
        // Nothing visibly failed, which is why it went unnoticed: the app carries no quarantine
        // attribute once it is installed, so Gatekeeper never assesses it and it launches fine. But
        // `spctl` refuses it, and anything that ever does verify the installed bundle would refuse
        // it too. (The updater deliberately does not — PayloadVerifier READS the running app's Team
        // ID rather than verifying its seal, and verifies the STAGED bundle instead, precisely so
        // this cannot disable updates on every machine that has opened a kit.)
        //
        // PYTHONPYCACHEPREFIX rather than PYTHONDONTWRITEBYTECODE: the cache is a real startup
        // saving on a kit with dozens of modules, so it is REDIRECTED rather than turned off.
        info.Environment["PYTHONPYCACHEPREFIX"] = AppDataRoot.SubDir("pcell-cache");

        if (pythonPath is { Count: > 0 })
        {
            // Prepended to whatever the user already has rather than replacing it — a developer with
            // their own PYTHONPATH must not have it silently dropped by opening a workspace.
            string existing = Environment.GetEnvironmentVariable("PYTHONPATH") ?? "";
            string joined = string.Join(Path.PathSeparator, pythonPath);
            info.Environment["PYTHONPATH"] =
                existing.Length > 0 ? joined + Path.PathSeparator + existing : joined;
        }

        Process process;
        try
        {
            process = Process.Start(info)
                   ?? throw new PCellWireException($"The PCell generator '{scriptPath}' could not be started.");
        }
        catch (Exception ex) when (ex is not PCellWireException)
        {
            throw new PCellWireException(
                $"The PCell generator '{scriptPath}' could not be started with '{interpreter}': {ex.Message}", ex);
        }

        Interlocked.Increment(ref _startCount);
        return new ProcessPCellWorkerTransport(process, scriptPath);
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

    /// <summary>
    /// Waits for what an exited generator wrote to actually arrive, before it is reported.
    ///
    /// <para>Error lines come over a background reader, so a script that dies FAST — a syntax error,
    /// a missing import — can be reported before a single line has been delivered, leaving a bare
    /// "closed its output" in exactly the case where its own traceback is the only useful thing.
    /// <c>WaitForExit()</c> with NO timeout is the only overload that also waits for the redirected
    /// readers to reach end of stream; the timed one returns as soon as the process is gone and
    /// leaves output in flight. Run off-thread under a bound of its own, so a grandchild holding the
    /// pipe open cannot wedge the path that is trying to explain a failure.</para>
    ///
    /// <para><b>It does NOT skip the wait when the process has not exited yet, and that is the
    /// difference from the device worker's otherwise-identical version.</b> Gating on
    /// <c>HasExited</c> looks safe and is the race: a script that dies fast is usually mid-exit and
    /// not yet reaped at the moment the failure is reported, so the check says "still alive", the
    /// wait is skipped, and the stderr that explains everything is dropped. Caught by
    /// <c>AScriptThatDiesImmediately_…</c> failing once in a full-solution run and passing every
    /// time in isolation — the load-dependent shape this repo has already been bitten by. The device
    /// path can afford the gate because its <c>RecentErrorOutput</c> is also read on a
    /// success-adjacent path (an in-band point failure from a live worker); this one is reached ONLY
    /// from <c>Failed(...)</c>, so the wait is bounded, bounded short, and only ever paid when
    /// something has already gone wrong.</para>
    /// </summary>
    private void FlushErrorOutput()
    {
        if (Interlocked.Exchange(ref _flushed, 1) != 0) return;

        try { Task.Run(() => { try { _process.WaitForExit(); } catch { } }).Wait(FlushTimeout); }
        catch { /* the diagnostic is a courtesy; it must never become the failure */ }
    }

    /// <summary>Ample for a pipe already at end of stream, short enough that a failure path cannot
    /// stall on a generator that is merely wedged rather than gone.</summary>
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                // Closing stdin is the ordinary shutdown: the script's read loop sees end of stream
                // and returns. Killing is for one wedged inside a generator.
                try { _process.StandardInput.Close(); } catch { /* already gone */ }
                if (!_process.WaitForExit(2000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* teardown must not throw over a process that is already gone */ }
        finally { _process.Dispose(); }
    }
}

/// <summary>
/// A transport over streams the caller already has, so the provider and the decoding can be tested
/// against a generator that is not a process. The parts worth testing are the framing and the
/// decoding, and neither needs an interpreter.
/// </summary>
public sealed class StreamPCellWorkerTransport(
    Stream requests, Stream replies,
    string origin = "in-memory PCell generator", bool leaveOpen = false) : IPCellWorkerTransport
{
    public Stream Requests => requests;
    public Stream Replies => replies;
    public string Origin => origin;
    public bool IsAlive => true;
    public string RecentErrorOutput => string.Empty;

    public void Dispose()
    {
        if (leaveOpen) return;
        requests.Dispose();
        replies.Dispose();
    }
}
