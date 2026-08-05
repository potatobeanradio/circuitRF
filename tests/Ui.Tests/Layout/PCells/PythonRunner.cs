using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Layout.PCells.Wire;

namespace CircuitRF.Ui.Tests.Layout.PCells;

/// <summary>
/// Runs a PCell generator script as a real subprocess and speaks the real wire format to it.
///
/// <para><b>This is a test harness, not B3's worker host.</b> It deliberately does none of what that
/// phase owns — no provider, no resolver, no interpreter discovery, no lifecycle beyond one
/// process for the duration of one test. What it does is the only honest way to check that the
/// Python package works: driving it the way the real host eventually will.</para>
/// </summary>
internal sealed class PythonRunner : IDisposable
{
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    private PythonRunner(Process process)
    {
        _process = process;
        // Nobody reading stderr fills the pipe and the script blocks forever inside a write —
        // presenting as a hang midway through, with no error anywhere. Same trap the device worker
        // already documents; drained on its own thread for the same reason.
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (_stderr) _stderr.AppendLine(e.Data); };
        _process.BeginErrorReadLine();
    }

    /// <summary>The interpreter to use, or null when there is none — which is a SKIP, never a
    /// failure: circuitRF must build and test on a machine with no Python on it.</summary>
    public static string? Interpreter { get; } = FindInterpreter();

    public static string PackageRoot { get; } = FindPackageRoot();

    public static PythonRunner Start(string scriptPath)
    {
        var info = new ProcessStartInfo(Interpreter!)
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = PackageRoot,
        };
        info.ArgumentList.Add(scriptPath);
        // How a kit's own modules import without the user configuring anything — the same thing the
        // manifest's pythonPath declares in production.
        info.Environment["PYTHONPATH"] = PackageRoot;

        var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start '{Interpreter}'.");
        return new PythonRunner(process);
    }

    // ── Exchanges ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes one frame and reads its reply, SERVICING whatever the script asks for along the way —
    /// the same loop <c>PCellWorkerProvider.ExchangeLocked</c> runs in production, so a test drives
    /// the real host behaviour rather than a simplified one that would pass where production hangs.
    /// </summary>
    public PCellWireFrame Exchange(in PCellWireFrame request)
    {
        PCellWireProtocol.WriteFrame(_process.StandardInput.BaseStream, request);
        try
        {
            while (true)
            {
                var frame = PCellWireProtocol.ReadFrame(_process.StandardOutput.BaseStream);
                if (!PCellWireHostServices.IsServiceRequest(frame, out string op))
                    return frame;

                ServiceCallCount++;
                PCellWireProtocol.WriteFrame(
                    _process.StandardInput.BaseStream, PCellWireHostServices.Serve(frame, op));
            }
        }
        catch (PCellWireException ex)
        {
            // The script's own error output is usually the only description of what went wrong.
            throw new PCellWireException($"{ex.Message}{StderrSuffix()}", ex);
        }
    }

    /// <summary>How many times the script asked circuitRF to do something for it.</summary>
    public int ServiceCallCount { get; private set; }

    public PCellWireDescribeReply Describe()
    {
        var frame = Exchange(PCellWireCodec.EncodeDescribe());
        var reply = JsonSerializer.Deserialize<PCellWireDescribeReply>(frame.Json, WireJsonOptions)
                    ?? throw new PCellWireException("The generator sent an empty describe reply.");

        if (!reply.Ok)
            throw new PCellWireException($"The generator refused describe: {reply.Error}{StderrSuffix()}");
        if (reply.WireVersion != PCellWireVersion.Current)
            throw new PCellWireException(
                $"The generator speaks wire version {reply.WireVersion}; this build speaks " +
                $"{PCellWireVersion.Current}.");
        return reply;
    }

    public PCellResult Generate(
        string generatorId,
        IReadOnlyDictionary<string, PCellValue> parametersInSi,
        IReadOnlyList<PCellWireParameterDecl> declarations,
        Technology? technology,
        PCellLayerSelection layerSelection,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron)
    {
        var request = PCellWireCodec.EncodeGenerate(
            generatorId, parametersInSi, declarations, technology, layerSelection, dbuPerMicron);
        try { return PCellWireCodec.DecodeGenerateReply(Exchange(request)); }
        catch (PCellWireException ex) when (!ex.Message.Contains("stderr", StringComparison.Ordinal))
        {
            throw new PCellWireException($"{ex.Message}{StderrSuffix()}", ex);
        }
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                PCellWireProtocol.WriteFrame(_process.StandardInput.BaseStream, PCellWireCodec.EncodeShutdown());
                if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true);
            }
        }
        catch { try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ } }
        finally { _process.Dispose(); }
    }

    private string StderrSuffix()
    {
        // WaitForExit() with NO timeout is the overload that also waits for the redirected readers to
        // reach end of stream; the timed one returns as soon as the process is gone and leaves output
        // in flight. Only ever called on a process that has already gone, under a bound.
        if (_process.HasExited)
        {
            var drained = new Thread(() => { try { _process.WaitForExit(); } catch { } }) { IsBackground = true };
            drained.Start();
            drained.Join(2000);
        }
        lock (_stderr)
            return _stderr.Length == 0 ? "" : $"\n--- generator stderr ---\n{_stderr}";
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    private static string? FindInterpreter()
    {
        foreach (string candidate in OperatingSystem.IsWindows()
                     ? ["python.exe", "python3.exe", "py.exe"]
                     : (string[])["python3", "python"])
        {
            try
            {
                using var probe = Process.Start(new ProcessStartInfo(candidate, "--version")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                });
                if (probe is null) continue;
                probe.WaitForExit(10_000);
                if (probe.ExitCode == 0) return candidate;
            }
            catch { /* not on PATH — try the next spelling */ }
        }
        return null;
    }

    private static string FindPackageRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, "tools", "pcell-python");
            if (Directory.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("tools/pcell-python not found above the test binary.");
    }

    private static readonly JsonSerializerOptions WireJsonOptions = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
