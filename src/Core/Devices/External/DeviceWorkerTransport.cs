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

    private readonly Process       _process;
    private readonly Queue<string> _errorLines = new();
    private readonly Lock          _errorGate  = new();

    private ProcessDeviceWorkerTransport(Process process, string origin)
    {
        _process = process;
        Origin   = origin;

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

    /// <summary>
    /// Start a worker executable and connect to it.
    /// </summary>
    /// <param name="executablePath">Path to the worker binary. Supplied at runtime, never baked in.</param>
    /// <param name="arguments">Arguments the worker needs — typically which model library to load.</param>
    /// <param name="workingDirectory">Working directory, or null to inherit.</param>
    public static ProcessDeviceWorkerTransport Start(
        string               executablePath,
        IEnumerable<string>? arguments        = null,
        string?              workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

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

        Process process;
        try
        {
            process = Process.Start(info)
                   ?? throw new ExternalDeviceException(
                          $"The device worker '{executablePath}' could not be started.");
        }
        catch (Exception ex) when (ex is not ExternalDeviceException)
        {
            throw new ExternalDeviceException(
                $"The device worker '{executablePath}' could not be started: {ex.Message}", ex);
        }

        return new ProcessDeviceWorkerTransport(process, executablePath);
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
