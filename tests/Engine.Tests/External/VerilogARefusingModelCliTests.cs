using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Xunit;

namespace CircuitRF.Engine.Tests.External;

/// <summary>
/// Brief vendorlib-15 R-vl15-2, headless half: a compiled model that refuses to run stops a <c>dc</c> run as ONE
/// refusal — exit 1, the model's own sentence on stderr, no node table — within a bounded time. The device is the fake
/// library's <c>crf_refuse</c>; its <c>mode</c> model parameter picks how it refuses, and a .cnl places it the way a
/// user would. The provider-level proof is <c>Core.Tests</c>' <c>OsdiRefusingModelTests</c>.
///
/// <para>Runs the BUILT <c>CircuitRF.Cli.dll</c>, never <c>dotnet run</c> (a nested MSBuild inside <c>dotnet test</c>
/// holds this repository's build locks and does not finish).</para>
/// </summary>
public sealed class VerilogARefusingModelCliTests
{
    private const string WorkerRel = "tools/osdi-worker/osdi-worker";
    private const string ModelRel  = "tools/fake-osdi-model/fake_osdi.osdi";
    private const string HowTo     = "run tools/osdi-worker/build.sh (needs a C compiler)";

    [FixtureTheory(ModelRel, HowTo)]
    [InlineData(1, "TSTFET100 licence check failed: this build expired on 2020-01-01.")]
    [InlineData(2, "TSTFET100 stopped: this build is locked to another machine.")]
    [InlineData(3, "TSTFET100 cannot start: no licence file was found beside the model.")]
    public void ARefusingModelEndsTheRunWithItsOwnSentence(int mode, string sentence)
    {
        string repo = RepoRoot();
        string cliDir = CliDir();
        EnsureWorkerBeside(cliDir, Path.Combine(repo, WorkerRel));

        string dir = Directory.CreateTempSubdirectory("crf-refuse-").FullName;
        try
        {
            string cnl = Path.Combine(dir, "refuse.cnl");
            File.WriteAllText(cnl,
                "Vdc:V1 in 0 Vdc=1\n" +
                "R:R1 in mid R=100 Ohm\n" +
                $"VerilogA:X1 mid 0 File=\"{Path.Combine(repo, ModelRel)}\" Model=crf_refuse mode={mode}\n" +
                "analysis DC1 type=dc\n");

            var (exit, stdout, stderr) = RunCli(repo, Path.Combine(cliDir, "CircuitRF.Cli.dll"), "dc", cnl);

            Assert.True(exit == 1, $"exit {exit}\nstderr:\n{stderr}\nstdout:\n{stdout}");
            // The FIRST line carries the model's own sentence; nothing reads as an operating point.
            string first = stderr.Split('\n').First(l => l.Trim().Length > 0);
            Assert.Contains(sentence, first);
            Assert.DoesNotContain("Node voltages:", stdout);
            Assert.DoesNotContain("DID NOT CONVERGE", stderr);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    /// <summary>The CLI hosts a compiled model through the worker BESIDE ITSELF, and nothing in <c>src/Cli</c>'s build
    /// puts one there (the application's build does, for its own output). The checkout's freshly built worker is moved
    /// in by rename — never copied over in place, which would rewrite a file another test's process may be running.</summary>
    private static void EnsureWorkerBeside(string cliDir, string worker)
    {
        Assert.True(File.Exists(worker), $"the worker is not built: {worker} ({HowTo})");
        string target = Path.Combine(cliDir, "osdi-worker");
        if (File.Exists(target) && File.ReadAllBytes(target).AsSpan().SequenceEqual(File.ReadAllBytes(worker))) return;
        string staged = target + "." + Guid.NewGuid().ToString("N")[..8];
        File.Copy(worker, staged);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(staged, File.GetUnixFileMode(worker));
        File.Move(staged, target, overwrite: true);
    }

    private static (int Exit, string StdOut, string StdErr) RunCli(string repo, string dll, params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        // A bound, not a timing assertion: a refusing model must never leave the run waiting.
        if (!proc.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            Assert.Fail("the run did not finish within 60 s");
        }
        proc.WaitForExit();
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDir()
    {
        string cliDir = typeof(VerilogARefusingModelCliTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "CliDir").Value!;
        string full = Path.GetFullPath(cliDir);
        Assert.True(File.Exists(Path.Combine(full, "CircuitRF.Cli.dll")), $"the CLI was not built beside these tests: {full}");
        return full;
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
